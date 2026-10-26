using View = Android.Views.View;

namespace LogoExtract.AdsService.Internal;

/// <summary>Stand-in provider used when <see cref="AdsProviderOptions.IsAdsEnabled"/> is false (see
/// <c>UseAdsService</c>): no ad SDK is initialised, no network request is ever made, and the banner view is an empty
/// <c>Gone</c> view. Because the real provider is never constructed, EVERY load path (banner create/load/retry,
/// interstitial prepare/show) is impossible, in every build configuration -- not merely hidden.</summary>
internal sealed class DisabledAdProvider : IAdProvider
{
    public bool IsBannerLoaded => false;

    public event Action? BannerStateChanged { add { } remove { } }

    public View CreateBannerView(Android.Content.Context context) =>
        new(context) { Visibility = Android.Views.ViewStates.Gone };

    public bool IsInterstitialReady => false;

    public void PrepareInterstitial() { }

    public void ShowInterstitialIfReady() { }
}
