namespace LogoExtract;

/// <summary>
/// Unity LevelPlay settings for DEV builds. Same split as DocScanner/PdfReader (LevelPlayConfig.cs / RealAdsConfig.cs /
/// AdsMode.cs): sandbox here, production ids only in <see cref="RealAdsConfig"/>, used only by a
/// <c>-p:AdsMode=Pro</c> build.
///
/// <para>LogoExtract has its own app entry + real AppKey on the LevelPlay dashboard (owner confirmed 2026-11-12,
/// production ad unit ids live in <see cref="RealAdsConfig"/>). No sandbox-specific ad unit ids have been created
/// here -- <see cref="AdsMode.Resolve"/> falls through to the Debug-build "production units on a test device"
/// branch instead (see its own comment), which is fine as long as the dev device is registered as a LevelPlay
/// test device.</para>
/// </summary>
public static class LevelPlayConfig
{
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
