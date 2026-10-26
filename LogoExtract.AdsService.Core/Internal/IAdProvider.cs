using View = Android.Views.View;

namespace LogoExtract.AdsService.Internal;

/// <summary>What each ad network's wrapper (<c>AdMobProvider</c> in LogoExtract.AdsService.AdMob,
/// <c>AppLovinProvider</c> in LogoExtract.AdsService.AppLovin, <c>UnityLevelPlayProvider</c> in
/// LogoExtract.AdsService.LevelPlay) must implement. <c>AdsClient</c> is the single <see cref="IAdsClient"/>
/// exposed to the app; it creates the matching provider LAZILY (only once ads are actually allowed -- a Pro
/// user's process never constructs one, so the ad SDK is never initialised and nothing is ever loaded) and
/// forwards to it. Internal: consumers of this library only ever see <see cref="IAdsClient"/>.
///
/// Public (not internal, despite living under the Internal/ namespace convention): a provider project
/// (LogoExtract.AdsService.AdMob / .AppLovin / .LevelPlay) implements this from a SEPARATE assembly (2026-10-12
/// split, replacing the single LogoExtract.AdsService project + its AdProviderKind switch -- see
/// LogoExtract.AdsService.Core.csproj's comment), which a C# interface must be at least public to allow.</summary>
public interface IAdProvider
{
    /// <summary>True once the banner has a creative on screen; drives <see cref="IAdsClient.IsBannerLoaded"/>.</summary>
    bool IsBannerLoaded { get; }

    /// <summary>Raised whenever <see cref="IsBannerLoaded"/> changes.</summary>
    event Action? BannerStateChanged;

    /// <summary>Creates (or returns the already-shared) native banner view for this provider, parented into
    /// whichever page's <c>AdBannerSurfaceHandler</c> asked for it first -- one native ad view for the whole app's
    /// lifetime regardless of provider, reparented as pages come and go.</summary>
    View CreateBannerView(Android.Content.Context context);

    bool IsInterstitialReady { get; }

    void PrepareInterstitial();

    void ShowInterstitialIfReady();

    /// <summary>Called when the app's own ad-free condition flips AFTER this provider already exists (e.g. a Pro purchase
    /// mid-session): true = stop everything that loads/refreshes ads and hide the banner; false = resume. Default is a
    /// no-op for providers that have nothing to pause; a Pro user at startup never gets this far (no provider at all).</summary>
    void SetSuspended(bool suspended) { }
}
