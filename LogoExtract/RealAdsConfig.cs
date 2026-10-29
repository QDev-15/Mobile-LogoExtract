namespace LogoExtract;

/// <summary>
/// PRODUCTION ad unit ids. PLACEHOLDER (2026-10-27): owner has not created LogoExtract as its own app on the
/// LevelPlay dashboard yet (needed before any real AppKey/ad unit ids exist for this app -- see
/// LevelPlayConfig.cs's own comment). <see cref="HasRealIds"/> stays false until these are filled in for real,
/// so a <c>-p:AdsMode=Pro</c> build of this placeholder state safely falls back to "no ads" rather than
/// requesting a bogus id.
/// </summary>
public static class RealAdsConfig
{
    public const string BannerAdUnitId = "m88tg202d2vxj6fl";
    public const string InterstitialAdUnitId = "k7dtg04ni0zoddal";

    public static bool HasRealIds => !BannerAdUnitId.Contains("REPLACE_ME") && !InterstitialAdUnitId.Contains("REPLACE_ME")
        && BannerAdUnitId.Length > 0 && InterstitialAdUnitId.Length > 0;
}
