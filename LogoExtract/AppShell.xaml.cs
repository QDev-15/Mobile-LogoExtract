using LogoExtract.Views;

namespace LogoExtract;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute(nameof(ImportPage), typeof(ImportPage));
		Routing.RegisterRoute(nameof(SegmentPage), typeof(SegmentPage));
		Routing.RegisterRoute(nameof(EditPage), typeof(EditPage));
	}
}
