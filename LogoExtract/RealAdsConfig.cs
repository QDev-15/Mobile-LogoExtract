namespace LogoExtract;

/// <summary>
/// PRODUCTION ad unit ids, real since 2026-11-02 (owner confirmed the LogoExtract app + ad units exist for real
/// on the LevelPlay dashboard, 2026-11-12). <see cref="HasRealIds"/> is the guard that still protects a
/// <c>-p:AdsMode=Pro</c> build against ever requesting a blank/placeholder id if these were ever cleared again.
/// </summary>
public static class RealAdsConfig
{
    public const string BannerAdUnitId = "m88tg202d2vxj6fl";
    public const string InterstitialAdUnitId = "k7dtg04ni0zoddal";

    public static bool HasRealIds => !BannerAdUnitId.Contains("REPLACE_ME") && !InterstitialAdUnitId.Contains("REPLACE_ME")
        && BannerAdUnitId.Length > 0 && InterstitialAdUnitId.Length > 0;
}
