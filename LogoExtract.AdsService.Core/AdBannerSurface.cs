using Microsoft.Extensions.DependencyInjection;

namespace LogoExtract.AdsService;

/// <summary>Cross-platform placeholder for the free-tier ad banner: a MAUI View with a custom Handler
/// (<c>AdBannerSurfaceHandler</c>) that hands every page the SAME native ad view instead of building a fresh
/// one each time, regardless of which provider is configured. Drop this at the bottom of any page -- no
/// <c>HeightRequest</c>, it sizes itself to the real ad view while shown, and NOTHING (not even a reserved
/// strip) while there is no ad to show, via <see cref="IAdsClient.AreAdsEnabled"/> and
/// <see cref="IAdsClient.IsBannerLoaded"/>, kept live for as long as this instance is on screen.
///
/// Migrated unchanged (behaviourally) from the app's own former <c>Views/AdBannerSurface.cs</c>
/// (2026-10-07) -- see that history for why a shared instance through a real Handler, rather than a fresh
/// native view per page or a hand-wired Activity-level one, is what actually fixes both the performance
/// problem and the banner not showing at all.</summary>
public sealed class AdBannerSurface : View
{
    private IAdsClient? _ads;

    public AdBannerSurface()
    {
        IsVisible = false; // nothing to show until proven otherwise, not even for one frame
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        _ads ??= IPlatformApplication.Current?.Services.GetService<IAdsClient>();
        if (_ads == null) return;
        _ads.BannerStateChanged += Apply;
        Apply();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (_ads != null) _ads.BannerStateChanged -= Apply;
    }

    private void Apply() => IsVisible = _ads is { AreAdsEnabled: true, IsBannerLoaded: true };
}
