namespace LogoExtract.AdsService;

/// <summary>Base for a provider's connection details (ad unit IDs, keys, test-mode flag...). Each provider
/// project (LogoExtract.AdsService.AdMob / .AppLovin / .LevelPlay) defines its own sealed record deriving from
/// this -- AdMobOptions / AppLovinOptions / LevelPlayOptions -- and a matching UseXxxAds extension that is the
/// only thing the app calls. Switching ad networks means referencing a different provider project and calling
/// its UseXxxAds instead; nothing else in the app changes. Replaces the former AdProviderKind enum + Kind
/// property (2026-10-12 split): which provider is active is now which project is referenced, not which enum
/// value a record returns.</summary>
public abstract record AdsProviderOptions
{
    /// <summary>MASTER switch for ads in this build -- true by default, applies to every build configuration (Debug,
    /// Release, APK, AAB). When false, the matching UseXxxAds registers a no-op provider instead of the real one: the
    /// ad SDK is never initialised, nothing is loaded or shown, and the banner never appears. Distinct from the runtime
    /// <see cref="IAdsClient.AreAdsEnabled"/> (the app's own ad-free condition, e.g. Pro); both must be true for ads to
    /// show. Set it with an object initialiser: <c>new LevelPlayOptions(...) { IsAdsEnabled = false }</c>.</summary>
    public bool IsAdsEnabled { get; init; } = true;
}
