using Microsoft.Extensions.DependencyInjection;

namespace LogoExtract;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		// Light is the app's default theme regardless of the device's own system theme (owner's explicit
		// request 2026-10-27), overriding MAUI's normal "follow the system" behavior. The dark theme the
		// design mockup was originally built in still exists (AppThemeBinding Dark= everywhere) and is reachable
		// later via a settings toggle if one gets added; this just decides which one a fresh install starts on.
		UserAppTheme = AppTheme.Light;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// The start screen first; it builds the real shell (AppShell) while it animates, then hands over --
		// see Views/SplashPage.xaml.cs. Same technique as the sibling PdfReader/DocScanner repos (added
		// 2026-10-28, this app didn't have this second in-app splash layer before).
		return new Window(new Views.SplashPage(() => new AppShell()));
	}
}