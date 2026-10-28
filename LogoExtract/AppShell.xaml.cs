using LogoExtract.Views;

namespace LogoExtract;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute(nameof(ImportPage), typeof(ImportPage));
		// SegmentPage retired (merged into EditPage, 2026-10-29) -- Home now navigates straight to EditPage,
		// which both runs/shows segmentation progress and does color-adjust/vectorize/export.
		Routing.RegisterRoute(nameof(EditPage), typeof(EditPage));
		Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
		Routing.RegisterRoute(nameof(AboutPage), typeof(AboutPage));
		Routing.RegisterRoute(nameof(PrivacyPolicyPage), typeof(PrivacyPolicyPage));
	}
}
