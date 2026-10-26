using LogoExtract.AdsService.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;

namespace LogoExtract.AdsService;

/// <summary>Shared wiring every provider's own <c>UseXxxAds(options)</c> extension
/// (LogoExtract.AdsService.AdMob's <c>UseAdMobAds</c>, .AppLovin's <c>UseAppLovinAds</c>, .LevelPlay's
/// <c>UseLevelPlayAds</c>) calls into: registers the generic <see cref="IAdsClient"/> singleton around
/// whichever <see cref="IAdProvider"/> factory that provider passes in, and the <see cref="AdBannerSurface"/>
/// handler. Not meant to be called directly by the app -- construct one of the provider-specific Options
/// records and call that provider's own <c>UseXxxAds</c> instead, which is the one thing the app needs to
/// know about.
///
/// When <see cref="AdsProviderOptions.IsAdsEnabled"/> is false the real provider is NOT registered (so its SDK is
/// never initialised and nothing can load an ad); a no-op <see cref="DisabledAdProvider"/> stands in, and
/// everything else in the app (banner surface, app-level services) keeps working unchanged.</summary>
public static class AdsServiceCoreExtensions
{
    public static MauiAppBuilder UseAdsCore(this MauiAppBuilder builder, bool isAdsEnabled, Func<IServiceProvider, IAdProvider> providerFactory)
    {
        // The provider is a FACTORY, invoked lazily by AdsClient the first time ads are actually allowed -- never at startup
        // for a Pro user (see AdsClient). With the master switch off the factory is a no-op stand-in that never loads anything.
        Func<IServiceProvider, IAdProvider> factory = isAdsEnabled ? providerFactory : _ => new DisabledAdProvider();

        builder.Services.AddSingleton(sp => new AdsClient(() => factory(sp), isAdsEnabled));
        builder.Services.AddSingleton<IAdsClient>(sp => sp.GetRequiredService<AdsClient>());
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<AdBannerSurface, AdBannerSurfaceHandler>());
        return builder;
    }
}
