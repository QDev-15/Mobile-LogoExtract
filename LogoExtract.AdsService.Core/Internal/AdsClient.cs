using Microsoft.Maui.ApplicationModel;
using View = Android.Views.View;

namespace LogoExtract.AdsService.Internal;

/// <summary>The one <see cref="IAdsClient"/> implementation, registered as a singleton by <c>UseAdsService</c>.
///
/// The real <see cref="IAdProvider"/> (and with it the ad network's SDK) is created LAZILY, the first time something
/// actually needs to show or load an ad while ads are allowed. Ads are allowed only when BOTH the build-level master
/// switch (<see cref="IsAdsEnabled"/>) and the app's own condition (<see cref="AreAdsEnabled"/> setter, e.g. not Pro) say
/// so. A user who already owns Pro (cached flag, known instantly at startup) therefore never causes SDK initialisation,
/// a banner view, a network request or a preload -- nothing ad-related runs at all. If Pro is bought later in the
/// session, an existing provider is suspended (refresh/loading stopped, banner hidden).</summary>
internal sealed class AdsClient(Func<IAdProvider> providerFactory, bool isAdsEnabled) : IAdsClient
{
    private readonly Lock _lock = new();
    private IAdProvider? _provider;
    private bool _appAllowsAds = true;

    public bool IsAdsEnabled { get; } = isAdsEnabled;

    /// <summary>Reads true only if BOTH the build-level master switch and the app's own (e.g. Pro) condition allow ads,
    /// so existing consumers (the banner surface, the app-level services) honour both with no changes.</summary>
    public bool AreAdsEnabled
    {
        get => IsAdsEnabled && _appAllowsAds;
        set
        {
            if (_appAllowsAds == value) return;
            _appAllowsAds = value;
            if (!IsAdsEnabled) return;
            // Only an already-created provider has anything to pause/resume -- never create one just to suspend it.
            // Ads flipping back ON does not eagerly create one either: the next PrepareInterstitial / page does.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _provider?.SetSuspended(!value);
                BannerStateChanged?.Invoke(); // surfaces re-evaluate visibility right away (e.g. banner vanishes on a Pro purchase)
            });
        }
    }

    public bool IsBannerLoaded => AreAdsEnabled && _provider is { IsBannerLoaded: true };

    public event Action? BannerStateChanged;

    public bool IsInterstitialReady => AreAdsEnabled && _provider is { IsInterstitialReady: true };

    public void PrepareInterstitial()
    {
        if (!AreAdsEnabled) return;
        EnsureProvider().PrepareInterstitial();
    }

    public void ShowInterstitialIfReady()
    {
        if (AreAdsEnabled) _provider?.ShowInterstitialIfReady();
    }

    /// <summary>The shared native banner for a page's handler, or null while ads are not allowed (the handler then shows
    /// nothing). Creating the banner view is also what first initialises the provider for a free user.</summary>
    internal View? GetBannerView(Android.Content.Context context) =>
        AreAdsEnabled ? EnsureProvider().CreateBannerView(context) : null;

    private IAdProvider EnsureProvider()
    {
        lock (_lock)
        {
            if (_provider is { } existing) return existing;
            IAdProvider created = providerFactory();
            created.BannerStateChanged += () => BannerStateChanged?.Invoke();
            _provider = created;
            return created;
        }
    }
}
