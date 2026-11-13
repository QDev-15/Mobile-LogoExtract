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
/// problem and the banner not showing at all.
///
/// <para><b>Known limitation, found on BlueStacks 2026-11-13, NOT fixed (see below for why)</b>: the
/// underlying native ad view is a single shared instance that gets silently re-parented
/// (<c>IAdProvider.CreateBannerView</c>'s own "if already attached elsewhere, RemoveView first" logic) to
/// whichever page's <c>AdBannerSurfaceHandler</c> most recently called <c>CreatePlatformView</c> -- which
/// normally only happens once, the first time this element's <see cref="Loaded"/> event fires. That is
/// harmless for a page that gets a brand-new <see cref="AdBannerSurface"/> instance on every visit (EditPage/
/// SettingsPage/AboutPage: each pushed via <c>Shell.Current.GoToAsync</c>, each registered <c>AddTransient</c>
/// in MauiProgram.cs, so a fresh element is constructed -- and fresh-Loaded -- every single time). It is NOT
/// harmless for HomePage specifically: Shell caches its sole <c>ShellContent</c> page for the whole app session,
/// so its one-and-only AdBannerSurface is Loaded exactly once, ever. The first time the user visits any other
/// page, that page's own banner steals the shared native view away, and because Home is never re-Loaded on
/// return, Home's banner row stays empty for the rest of that session (it comes back the next time the app is
/// relaunched). Net effect: a free user occasionally misses a banner impression on Home after having visited
/// Settings/About/Edit at least once in the same session -- a real but minor revenue/cosmetic gap, not a
/// crash or a correctness bug.
///
/// Two fix attempts were tried and BOTH rejected after on-device testing, because each one broke something far
/// worse than the gap it was meant to close -- Shell's own Back navigation stopped working AT ALL, permanently,
/// for the rest of the process, confirmed reproducible even from a freshly launched process:
/// 1. Clearing <see cref="VisualElement.Handler"/> and re-attaching via <c>ToHandler</c> (the standard MAUI way
///    to force a handler to be recreated on demand) DID call <c>CreatePlatformView</c> again, but the parent
///    Grid's native ViewGroup never actually swapped in the new platform view -- nothing tells a parent to do
///    that just because a child's Handler changed out from under it in isolation.
/// 2. Removing and re-inserting this element in its parent Layout's Children collection (the same mechanism a
///    CollectionView uses to recycle cells, which DOES correctly reattach the native view) broke Back navigation
///    outright -- even deferred to the next UI-thread tick via MainThread.BeginInvokeOnMainThread, to run after
///    OnAppearing and Shell's own page-transition work were done. Isolated by testing a diagnostic no-op version
///    of this same method: Back navigation was reliably fine with it as a no-op, reliably broken with either
///    real implementation, repeatably, across multiple fresh app launches.
/// Given a missed banner impression is a minor revenue gap and broken Back navigation would make the app
/// unusable, this class intentionally ships WITHOUT any reclaim mechanism. If this is worth fixing properly
/// later, the next idea to try is reproducing it on a real device first (BlueStacks' own Shell/animation timing
/// may not be representative) and/or filing it as a MAUI Shell navigation issue with a minimal repro.</para></summary>
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
