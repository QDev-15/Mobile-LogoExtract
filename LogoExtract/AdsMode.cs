namespace LogoExtract;

/// <summary>Which LevelPlay ad units this build requests. Same shape as DocScanner/PdfReader's AdsMode. AppKey +
/// production ad unit ids are real (see LevelPlayConfig.cs/RealAdsConfig.cs) -- a <c>-p:AdsMode=Pro</c> Release
/// build resolves to real production units.</summary>
public static class AdsMode
{
#if ADS_MODE_PRO
    public const bool IsPro = true;
#else
    public const bool IsPro = false;
#endif

    public static (string Banner, string Interstitial, string Source)? Resolve()
    {
        if (IsPro && RealAdsConfig.HasRealIds)
            return (RealAdsConfig.BannerAdUnitId, RealAdsConfig.InterstitialAdUnitId, "production");
        if (LevelPlayConfig.HasSandboxIds)
            return (LevelPlayConfig.SandboxBannerAdUnitId, LevelPlayConfig.SandboxInterstitialAdUnitId, "sandbox");
#if DEBUG
        if (RealAdsConfig.HasRealIds)
            return (RealAdsConfig.BannerAdUnitId, RealAdsConfig.InterstitialAdUnitId, "production units on a Debug build (test device)");
#endif
        return null;
    }
}
