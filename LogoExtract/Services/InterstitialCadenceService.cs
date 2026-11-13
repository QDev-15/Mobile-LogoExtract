using LogoExtract.AdsService;

namespace LogoExtract.Services;

/// <summary>Owner's spec (2026-11-13): show the full-screen interstitial every 5 minutes while the app is
/// running, restarting the 5-minute countdown right after each attempt -- independent of any particular user
/// action (unlike DocScanner's own "every 5th PDF export" cadence, which this app has no equivalent trigger
/// for). A no-op for a Pro user: <see cref="IAdsClient.ShowInterstitialIfReady"/> already checks
/// <see cref="IAdsClient.AreAdsEnabled"/> itself, and <see cref="IAdsClient.PrepareInterstitial"/> is a no-op
/// too (see <c>AdsClient.PrepareInterstitial</c>) -- so this loop can just keep running unconditionally for the
/// lifetime of the process without its own Pro-awareness; it costs nothing while ads are off.</summary>
public sealed class InterstitialCadenceService(IAdsClient ads)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? _cts;

    /// <summary>Call once at startup. Kicks off the first interstitial preload immediately (so it has the full
    /// 5 minutes to finish loading, same reasoning as <c>AdsService.PrepareInterstitial</c> in the sibling
    /// DocScanner app -- "one kept ready at all times, so the Nth trigger rarely has to skip") and starts the
    /// countdown loop.</summary>
    public void Start()
    {
        if (_cts != null) return;
        ads.PrepareInterstitial();
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;

            // Show-if-ready, then immediately queue the next one -- whether this attempt actually displayed an
            // ad (preloaded in time) or silently skipped (still loading, or ads off for a Pro user), the
            // countdown restarts the same way either side of that, exactly as the owner's spec describes
            // ("sau khi hiển thị thì reset lại countdown").
            ads.ShowInterstitialIfReady();
            ads.PrepareInterstitial();
        }
    }
}
