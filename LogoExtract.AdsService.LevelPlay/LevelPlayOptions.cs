namespace LogoExtract.AdsService;

/// <param name="AppKey">Unity LevelPlay app key, from the LevelPlay dashboard (the SDK's own term; equivalent
/// role to AppLovin's SdkKey / AdMob's App ID). One key per app.</param>
/// <param name="BannerAdUnitId">LevelPlay banner ad unit ID.</param>
/// <param name="InterstitialAdUnitId">LevelPlay interstitial ad unit ID.</param>
/// <param name="TestMode">Turns on adapter debug logging (<c>LevelPlay.setAdaptersDebug</c>) for this
/// device/build. Like AppLovin, LevelPlay has no separate "test ad unit ID" concept -- test creatives come from
/// marking the device/ad unit as test in the dashboard, not a different ID here.</param>
public sealed record LevelPlayOptions(
    string AppKey,
    string BannerAdUnitId,
    string InterstitialAdUnitId,
    bool TestMode = false
) : AdsProviderOptions;
