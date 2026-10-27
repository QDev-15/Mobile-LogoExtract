using LogoExtract.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LogoExtract.Views;

/// <summary>Self-resolves <see cref="AppDialog"/> from DI (same idiom as AdsService's AdBannerSurface already
/// uses in this app) so every page that drops this control in gets the SAME singleton, no constructor params
/// to thread through Shell's own page-construction.</summary>
public partial class AppDialogOverlay : ContentView
{
    public AppDialogOverlay()
    {
        InitializeComponent();
        BindingContext = IPlatformApplication.Current?.Services.GetService<AppDialog>();
    }
}
