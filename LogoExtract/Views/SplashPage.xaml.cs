namespace LogoExtract.Views;

/// <summary>
/// The start screen, right after the native OS splash hands off (same dark-brown gradient as that native
/// splash, see SplashPage.xaml's own comment, so the two join up with no visible color jump): the mark grows
/// in while the real app (AppShell) is built behind the scenes, then this page is replaced with a hard cut (no
/// fade, a fading page would show the window's plain background behind it) once both are done (at least
/// <see cref="MinimumTime"/>, so the screen does not just flash by). Same technique as the sibling PdfReader/
/// DocScanner repos' own SplashPage. Also carries the author/version info the native splash SVG itself cannot
/// show (that asset is a static image baked at build time, no live text) -- added 2026-10-28 per owner request
/// ("chỉnh lại splash, hiển thị các thông tin tác giả và version - version code").
/// </summary>
public partial class SplashPage : ContentPage
{
	private static readonly TimeSpan MinimumTime = TimeSpan.FromMilliseconds(1100);

	private readonly Func<Page> _main;
	private bool _started;

	public SplashPage(Func<Page> main)
	{
		InitializeComponent();
		_main = main;
		// AppInfo.Current.BuildString is the native build number (Android versionCode, i.e. this app's own
		// LogoExtract.csproj ApplicationVersion) -- "bản dựng" (build) -- distinct from VersionString
		// (ApplicationDisplayVersion, e.g. "1.0") -- "phiên bản" (version).
		VersionLabel.Text = $"Phiên bản {AppInfo.Current.VersionString} (bản dựng {AppInfo.Current.BuildString})";
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_started) return;
		_started = true;
		var shown = Task.Delay(MinimumTime);

		// FadeTo/ScaleTo (Microsoft.Maui.Controls.ViewExtensions), not "...Async" -- they already return
		// Task<bool>, there is no separate Async-suffixed overload. Confirmed real gotcha from building this
		// exact page in the sibling PdfReader repo: calling non-existent "...Async" names here still compiled,
		// but left the page permanently blank at runtime with no error either way.
		await Task.WhenAll(
			Mark.FadeTo(1, 350, Easing.CubicOut),
			Mark.ScaleTo(1, 450, Easing.SpringOut));

		await Task.Yield();
		Page main = _main(); // build the main screen while the minimum-time delay above finishes
		await shown;
		if (Window != null) Window.Page = main;
	}
}
