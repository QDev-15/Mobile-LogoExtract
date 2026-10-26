using Com.Unity3d.Mediation;
using Com.Unity3d.Mediation.Banner;
using Com.Unity3d.Mediation.Interstitial;
using Microsoft.Maui.ApplicationModel;
using FrameLayout = Android.Widget.FrameLayout;
using GravityFlags = Android.Views.GravityFlags;
using View = Android.Views.View;
using ViewGroup = Android.Views.ViewGroup;

namespace LogoExtract.AdsService.Internal;

/// <summary>Unity LevelPlay (ironSource) wrapper, built 2026-10-08 the same way as <see cref="AppLovinProvider"/>:
/// the Java API below was read directly off the real <c>mediation-sdk-9.6.1.aar</c> bytecode with <c>javap</c>,
/// not guessed. Uses the modern "LevelPlay" API (<c>com.unity3d.mediation.*</c>), not the older static
/// <c>com.ironsource.mediationsdk.IronSource</c> facade which the same AAR also ships: <c>LevelPlay.Init(context,
/// request, listener)</c>, <c>LevelPlayBannerAdView(context, adUnitId).SetBannerListener(...)</c>,
/// <c>LevelPlayInterstitialAd(adUnitId).SetListener(...)</c>. Showing an interstitial needs a live
/// <see cref="Android.App.Activity"/> (no parameterless overload, unlike AdMob/AppLovin), taken from
/// <see cref="Platform.CurrentActivity"/> -- the same helper the rest of this app already uses for
/// activity-scoped platform calls (photo/PDF pickers, camera).
///
/// Needed a Metadata.xml fix to build at all: the real AAR has hundreds of R8-flattened, 1-2 character obfuscated
/// classes sitting directly in the top-level "com.ironsource" package (not under any "impl" sub-package), plus an
/// adapter-facing SPI tree (<c>com.ironsource.mediationsdk.adunit.adapter.*</c>, <c>com.ironsource.mediationsdk.
/// sdk.I*SmashListener</c>) that referenced them and failed to bind for the same reason -- none of it is public
/// API, see <c>Transforms/Metadata.xml</c> for the exact exclusions.</summary>
internal sealed class UnityLevelPlayProvider(LevelPlayOptions options) : IAdProvider
{
    // CreateBannerView's own View, not the LevelPlayBannerAdView itself: LevelPlayAdSize.CreateAdaptiveAdSize()
    // has the exact same "SDK must be initialized first" constraint as LoadAd() (confirmed on-device 2026-10-09:
    // calling it before init crashes the app outright, SetAdSize(null) throwing on the Java side -- worse than
    // LoadAd's merely-rejected-with-an-error-callback behavior). CreateBannerView must return a View synchronously
    // and LevelPlayBannerAdView has no way to change its ad size after construction, so the real ad view can only
    // be built once init is ready; this stable, empty container is what every page actually gets back and reuses.
    private FrameLayout? _container;
    private LevelPlayBannerAdView? _shared;
    private LevelPlayInterstitialAd? _interstitial;

    // Init() is async (~1s on a real device): calling LoadAd() before its callback fires is rejected outright
    // (errorCode 625 "Load must be called after init success callback"), not queued or retried by the SDK itself
    // -- confirmed on-device 2026-10-09, the actual reason ads never showed despite every other piece (dependency,
    // dashboard config, credentials) being correct. CreateBannerView/PrepareInterstitial can legitimately be
    // called that early (banner view is created as soon as the first page appears), so every LoadAd() call goes
    // through RunAfterInit instead of being issued directly.
    private readonly Lock _lock = new();
    private bool _initDone;
    private readonly List<Action> _pendingActions = [];

    public bool IsBannerLoaded { get; private set; }

    public event Action? BannerStateChanged;

    public bool IsInterstitialReady => _interstitial?.IsAdReady ?? false;

    public UnityLevelPlayProvider Initialize(Android.Content.Context context)
    {
        if (options.TestMode) global::Com.Unity3d.Mediation.LevelPlay.SetAdaptersDebug(true);
        _context = context;
        StartInit();
        return this;
    }

    private Android.Content.Context? _context;
    private int _initAttempt;

    // Init() itself can go silent -- neither OnInitSuccess nor OnInitFailed ever firing (observed on-device
    // 2026-10-13 after many rapid relaunches in a short test session: no callback for 45+ seconds, everything
    // downstream -- banner, interstitial -- stayed queued in RunAfterInit forever). Unity's own sample code
    // comments "recommended to initialize again" inside OnInitFailed, but that only helps when the SDK actually
    // calls it; a true hang needs its own watchdog. Same capped-exponential-backoff shape as ScheduleBannerRetry
    // below, for the same reason (do not hammer a struggling backend), just a shorter base delay since init is
    // expected to resolve faster than a banner refresh.
    private void StartInit()
    {
        int attempt = Interlocked.Increment(ref _initAttempt);
        AdLog($"init attempt #{attempt}");
        LevelPlayInitRequest request = new LevelPlayInitRequest.Builder(options.AppKey)!.Build()!;
        global::Com.Unity3d.Mediation.LevelPlay.Init(_context!, request, new InitListener(this));
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, attempt - 1)));
        _ = Task.Delay(timeout).ContinueWith(_ =>
        {
            // _initDone, or a newer attempt already in flight (a real OnInitFailed retried before this timeout
            // fired): this stale watchdog has nothing to do.
            if (_initDone || attempt != _initAttempt) return;
            AdLog($"init attempt #{attempt}: no callback within {timeout.TotalSeconds:0}s, retrying");
            MainThread.BeginInvokeOnMainThread(StartInit);
        });
    }

    public View CreateBannerView(Android.Content.Context context)
    {
        if (_container is { } existing)
        {
            // Already shown on another (still-alive, e.g. back-stack) page: an Android View can only have one
            // parent, so take it back before this page's own container tries to add it.
            (existing.Parent as ViewGroup)?.RemoveView(existing);
            return existing;
        }

        var container = new FrameLayout(context);
        _container = container;
        RunAfterInit(() =>
        {
            // Adaptive banner (as wide as the screen) instead of the fixed 320x50 a no-config constructor would
            // default to -- see the field comment on _container for why this has to wait until here.
            LevelPlayBannerAdView.Config config = new LevelPlayBannerAdView.Config.Builder()
                .SetAdSize(LevelPlayAdSize.CreateAdaptiveAdSize(context)!)
                .Build();
            var view = new LevelPlayBannerAdView(context, options.BannerAdUnitId, config);
            view.BannerListener = new BannerListener(this); // getter+setter pair binds as a C# property, not a method
            _shared = view;
            // WrapContent + CenterHorizontal: without an explicit Gravity, FrameLayout.AddView defaults to
            // top-start, so a banner even a few dp narrower than its container (safe-area insets, rounding
            // between the adaptive width calculation and MAUI's own arranged width) sits flush left instead of
            // centered. Same fix PdfReader's AdBannerSurfaceHandler already applies at its own wrapping layer.
            container.AddView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.CenterHorizontal,
            });
            if (!_suspended) view.LoadAd();
            StartBannerWatchdog();
        });
        return container;
    }

    /// <summary>Pro bought mid-session: stop the banner refreshing and retrying, hide it; resume on false. (A Pro user at
    /// startup never reaches here: the provider is never even created for them, see AdsClient.)</summary>
    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_container is { } container) container.Visibility = suspended ? Android.Views.ViewStates.Gone : Android.Views.ViewStates.Visible;
            if (_shared is not { } view) return;
            if (suspended)
            {
                view.PauseAutoRefresh();
                SetBannerLoaded(false);
            }
            else
            {
                view.ResumeAutoRefresh();
                view.LoadAd();
            }
        });
    }

    public void PrepareInterstitial()
    {
        LevelPlayInterstitialAd ad = _interstitial ??= CreateInterstitial();
        RunAfterInit(ad.LoadAd);
    }

    private void RunAfterInit(Action action)
    {
        lock (_lock)
        {
            if (!_initDone) { _pendingActions.Add(action); return; }
        }
        action();
    }

    private void OnInitFinished()
    {
        List<Action> pending;
        lock (_lock)
        {
            _initDone = true;
            pending = [.. _pendingActions];
            _pendingActions.Clear();
        }
        // The queued actions touch Views (container.AddView for the banner): MainThread.BeginInvokeOnMainThread
        // is a cheap no-op if this callback already arrived on the UI thread (observed on-device so far), and
        // the safety net if a future SDK version ever delivers it off-thread.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            foreach (Action action in pending) action();
        });
    }

    public void ShowInterstitialIfReady()
    {
        if (_interstitial is not { IsAdReady: true } ad) return;
        if (Platform.CurrentActivity is { } activity) ad.ShowAd(activity);
    }

    private LevelPlayInterstitialAd CreateInterstitial()
    {
        var ad = new LevelPlayInterstitialAd(options.InterstitialAdUnitId);
        ad.SetListener(new InterstitialListener());
        return ad;
    }

    /// <summary>Always-on (Release too) one-line ad diagnostics: the SDK itself is silent in Release, so without this a
    /// missing banner can not be told apart from a failed load. Read with `adb logcat -s DocScanAds`.</summary>
    internal static void AdLog(string message) => Android.Util.Log.Info("DocScanAds", message);

    // LevelPlay auto-refreshes a loaded banner (~every 15s), but after a failed load (e.g. 509 "Mediation No fill", seen
    // on-device 2026-10-11 after ~10 refreshes) it stops on its own -- observed: no further callback for 5+ minutes, so the
    // banner (hidden on failure) never came back. Retry with backoff: 20s, 40s, ... capped at 5 min; reset on success.
    private bool _suspended; // set by SetSuspended: the app turned ads off mid-session (e.g. a Pro purchase)
    private int _bannerRetries;
    private int _bannerFailures;

    // One failed refresh while an ad is already on screen keeps that ad (a transient no-fill should not blank a banner
    // that is still perfectly displayable); a second consecutive failure, or a failure with nothing ever loaded, hides it.
    private void OnBannerLoadFailed()
    {
        int failures = Interlocked.Increment(ref _bannerFailures);
        if (!IsBannerLoaded || failures >= 2) SetBannerLoaded(false);
    }
    private bool _bannerRetryPending;

    private void ScheduleBannerRetry()
    {
        lock (_lock)
        {
            if (_bannerRetryPending) return;
            _bannerRetryPending = true;
        }
        int attempt = Interlocked.Increment(ref _bannerRetries);
        TimeSpan delay = TimeSpan.FromSeconds(Math.Min(300, 20 * Math.Pow(2, attempt - 1)));
        AdLog($"banner retry #{attempt} in {delay.TotalSeconds:0}s");
        _ = Task.Delay(delay).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() =>
        {
            lock (_lock) _bannerRetryPending = false;
            if (!_suspended) _shared?.LoadAd();
        }));
    }

    private void SetBannerLoaded(bool loaded)
    {
        if (IsBannerLoaded == loaded) return;
        IsBannerLoaded = loaded;
        BannerStateChanged?.Invoke();
    }

    // Recurring safety net, independent of OnAdLoadFailed above: LevelPlay auto-refreshes a loaded banner every
    // ~15s, but if a LoadAd() call goes out and NEITHER OnAdLoaded NOR OnAdLoadFailed ever fires back (a
    // genuinely silent stuck load, not merely a reported failure) nothing currently notices or retries. Started
    // once, right after the first LoadAd(); runs for the provider's whole lifetime and simply no-ops on each
    // tick while a creative is already showing or ads are suspended.
    private CancellationTokenSource? _watchdogCts;

    private void StartBannerWatchdog()
    {
        if (_watchdogCts != null) return; // already running; CreateBannerView only ever calls this once per provider
        var cts = _watchdogCts = new CancellationTokenSource();
        _ = BannerWatchdogLoopAsync(cts.Token);
    }

    private async Task BannerWatchdogLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested || _suspended || IsBannerLoaded) continue;
            AdLog("banner watchdog: no ad after the usual refresh window, forcing a reload");
            MainThread.BeginInvokeOnMainThread(() => { if (!_suspended && !IsBannerLoaded) _shared?.LoadAd(); });
        }
    }

    private sealed class InitListener(UnityLevelPlayProvider owner) : Java.Lang.Object, ILevelPlayInitListener
    {
        public void OnInitSuccess(LevelPlayConfiguration? configuration) { AdLog("init success"); owner.OnInitFinished(); }

        // Unity's own sample code: "recommended to initialize again" here. StartInit() increments _initAttempt,
        // which also retires the previous attempt's timeout watchdog (see StartInit) -- queued LoadAd() calls
        // stay queued through the retry instead of being flushed to fail against an un-initialized SDK.
        public void OnInitFailed(LevelPlayInitError? error) { AdLog($"init FAILED: {error}, retrying"); MainThread.BeginInvokeOnMainThread(owner.StartInit); }
    }

    private sealed class BannerListener(UnityLevelPlayProvider owner) : Java.Lang.Object, ILevelPlayBannerAdViewListener
    {
        public void OnAdLoaded(LevelPlayAdInfo? adInfo) { AdLog($"banner loaded: {adInfo}"); Interlocked.Exchange(ref owner._bannerRetries, 0); Interlocked.Exchange(ref owner._bannerFailures, 0); owner.SetBannerLoaded(true); }
        public void OnAdLoadFailed(LevelPlayAdError? error) { AdLog($"banner load FAILED: {error}"); owner.OnBannerLoadFailed(); owner.ScheduleBannerRetry(); }
        public void OnAdDisplayed(LevelPlayAdInfo? adInfo) { }
        public void OnAdDisplayFailed(LevelPlayAdInfo? adInfo, LevelPlayAdError? error) { }
        public void OnAdClicked(LevelPlayAdInfo? adInfo) { }
        public void OnAdExpanded(LevelPlayAdInfo? adInfo) { }
        public void OnAdCollapsed(LevelPlayAdInfo? adInfo) { }
        public void OnAdLeftApplication(LevelPlayAdInfo? adInfo) { }
    }

    private sealed class InterstitialListener : Java.Lang.Object, ILevelPlayInterstitialAdListener
    {
        public void OnAdLoaded(LevelPlayAdInfo? adInfo) => AdLog($"interstitial loaded: {adInfo}");
        public void OnAdLoadFailed(LevelPlayAdError? error) => AdLog($"interstitial load FAILED: {error}");
        public void OnAdDisplayed(LevelPlayAdInfo? adInfo) { }
        public void OnAdDisplayFailed(LevelPlayAdError? error, LevelPlayAdInfo? adInfo) { }
        public void OnAdClicked(LevelPlayAdInfo? adInfo) { }
        public void OnAdClosed(LevelPlayAdInfo? adInfo) { }
        public void OnAdInfoChanged(LevelPlayAdInfo? adInfo) { }
    }
}
