using LogoExtract.AdsService.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using View = Android.Views.View;

namespace LogoExtract.AdsService;

/// <summary>Maps <see cref="AdBannerSurface"/> to ONE shared native ad view for the whole app, whichever
/// provider is configured -- migrated from the app's own former <c>AdBannerSurfaceHandler</c> (2026-10-07),
/// generalised to ask the registered <see cref="IAdProvider"/> for the native view instead of constructing an
/// AdMob <c>AdView</c> directly. See the app's history for why a shared instance through a real Handler, rather
/// than a fresh native view per page or a hand-wired Activity-level one, is what actually fixes both the
/// performance problem and the banner not showing at all.</summary>
internal sealed class AdBannerSurfaceHandler : ViewHandler<AdBannerSurface, View>
{
    public static readonly IPropertyMapper<AdBannerSurface, AdBannerSurfaceHandler> Mapper =
        new PropertyMapper<AdBannerSurface, AdBannerSurfaceHandler>(ViewHandler.ViewMapper);

    public AdBannerSurfaceHandler() : base(Mapper)
    {
    }

    protected override View CreatePlatformView()
    {
        AdsClient client = IPlatformApplication.Current?.Services.GetService<AdsClient>()
            ?? throw new InvalidOperationException("LogoExtract.AdsService: MauiAppBuilder.UseAdsService(...) was never called.");
        // null while ads are not allowed (Pro user / master switch off): an empty Gone view, the ad SDK never touched.
        return client.GetBannerView(Context) ?? new View(Context) { Visibility = Android.Views.ViewStates.Gone };
    }

    /// <summary>Never tears down the shared native view just because the page hosting it right now is going
    /// away -- the next page's handler will simply reparent it (<see cref="CreatePlatformView"/>).</summary>
    protected override void DisconnectHandler(View platformView)
    {
    }
}
