namespace LogoExtract;

/// <summary>
/// Unity LevelPlay settings for DEV builds. Same split as DocScanner/PdfReader (LevelPlayConfig.cs / RealAdsConfig.cs /
/// AdsMode.cs): sandbox/placeholder here, production ids only in <see cref="RealAdsConfig"/>, used only by a
/// <c>-p:AdsMode=Pro</c> build.
///
/// <para><b>AppKey is a PLACEHOLDER (2026-10-27)</b>: LogoExtract is a brand-new app, not yet registered as its own
/// app on the LevelPlay dashboard (that is a separate app entry per Unity/ironSource account app, cannot reuse
/// DocScanner's or PdfReader's AppKey). Until the owner creates it there and pastes the real AppKey + ad unit ids
/// here, <see cref="AdsMode.Resolve"/> returns null (no ads requested at all, in every build type) -- the ad SDK
/// still initializes safely with a placeholder key but every LoadAd() call will just fail quietly, exactly the
/// same "ads off until configured" behavior as a Dev build with no sandbox ids on the sibling apps.</para>
/// </summary>
public static class LevelPlayConfig
{
    /// <summary>PLACEHOLDER -- replace once this app exists on the LevelPlay dashboard.</summary>
    public const string AppKey = "29199e055";

    public const string SandboxBannerAdUnitId = "";
    public const string SandboxInterstitialAdUnitId = "";

    public static bool HasSandboxIds => SandboxBannerAdUnitId.Length > 0 && SandboxInterstitialAdUnitId.Length > 0;

#if ADS_DISABLED
    public const bool IsAdsEnabled = false;
#else
    public const bool IsAdsEnabled = true;
#endif

#if DEBUG
    public const bool TestMode = true;
#else
    public const bool TestMode = false;
#endif
}
