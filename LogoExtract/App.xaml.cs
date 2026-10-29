using LogoExtract.Services;
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

		// Owner's request, 2026-10-29, "chạy benchmark đánh giá máy để recommend ... chỉ được chạy 1 lần khi
		// cài app": kicked off here, at process start, so it's very likely already finished (or at least well
		// under way) by the time the owner gets through the splash animation, imports a photo, and triggers
		// their first real segmentation -- rather than that first real segmentation also paying for the
		// benchmark's own ~1-2s. No-ops instantly on every launch after the first (see
		// SegmentationProviderResolver.WarmUpInBackground's own doc comment) and is itself fire-and-forget on a
		// background thread, so it adds zero latency to app startup/the splash sequence either way.
		_ = Task.Run(SegmentationProviderResolver.WarmUpInBackground);
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// The start screen first; it builds the real shell (AppShell) while it animates, then hands over --
		// see Views/SplashPage.xaml.cs. Same technique as the sibling PdfReader/DocScanner repos (added
		// 2026-10-28, this app didn't have this second in-app splash layer before).
		return new Window(new Views.SplashPage(() => new AppShell()));
	}
}