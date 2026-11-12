using LogoExtract.AdsService;
using LogoExtract.Core.Licensing;
using LogoExtract.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LogoExtract;

public partial class App : Application
{
	private readonly ILicenseService _license;
	private readonly AppDialog _dialog;

	public App(ILicenseService license, IAdsClient ads, AppDialog dialog)
	{
		InitializeComponent();
		_license = license;
		_dialog = dialog;
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

		// Owner's request, 2026-10-29, "thêm chức năng mua bản quyền mua pro giống như các app khác": the single
		// place that connects ILicenseService (pure Play Billing/Pro-status concern) to IAdsClient.AreAdsEnabled
		// (pure "should the banner show" concern) -- IAdsClient's own doc comment deliberately keeps it unaware
		// of what "Pro" even means, so this composition has to happen somewhere in the app layer, not inside
		// either service. Set once immediately from License's locally-cached (instant, no network) guess so the
		// very first frame is already correct for a returning Pro owner, then kept live via Changed for the
		// moment a purchase/restore/refresh actually completes.
		ads.AreAdsEnabled = !license.State.IsPro;
		license.Changed += () => MainThread.BeginInvokeOnMainThread(() => ads.AreAdsEnabled = !license.State.IsPro);
		_ = license.RefreshAsync(); // re-verify with Play on every launch (e.g. a purchase made on another device)
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// The start screen first; it builds the real main page (either AppShell directly, or the terms gate in
		// front of it -- see CreateMainPage below) while it animates, then hands over -- see
		// Views/SplashPage.xaml.cs. Same technique as the sibling PdfReader/DocScanner repos (added 2026-10-28,
		// this app didn't have this second in-app splash layer before).
		return new Window(new Views.SplashPage(CreateMainPage));
	}

	/// <summary>Owner's request, 2026-11-12 ("thêm popup trước khi vào app, yêu cầu đồng ý điều khoản quảng cáo
	/// thu thập thông tin cá nhân, hoặc mua Pro"): decides, synchronously and with no network wait, whether the
	/// very first screen after Splash is AppShell directly or the new terms gate in front of it. Runs on
	/// SplashPage's own background Task.Run right after its fade-in starts (see SplashPage.OnAppearing's
	/// "Page main = _main();" line) -- even a slow synchronous read here costs no visible frame, but there is
	/// none to pay anyway (Preferences.Default.Get and ILicenseService.State are both plain in-memory/local-disk
	/// reads, no await).
	///
	/// Two independent reasons to SKIP the gate and go straight to AppShell:
	/// 1. <see cref="Views.TermsGatePage.TermsAcceptedKey"/> is already true -- this install has agreed once
	///    before. Deliberately versioned (_v1): if this gate's actual legal/product copy ever changes
	///    meaningfully later, bumping the key to _v2 naturally re-prompts everyone, same idiom
	///    LicenseService.CachedIsProKey already uses for its own (unrelated) flag in this exact codebase.
	/// 2. <c>license.State.IsPro</c> is already true from the LOCAL CACHE (populated synchronously in
	///    LicenseService's own constructor, no await needed -- same cached value this very method's sibling,
	///    the ads.AreAdsEnabled line above, already reads) -- a returning Pro owner on a fresh app-data-wipe
	///    (which would also have cleared the "accepted" flag above) should not be re-gated just because that
	///    ONE flag got cleared; their Pro status alone already answers the question this gate is asking.
	///
	/// If neither applies, the gate IS shown; TermsGatePage.OnAppearing kicks off its own short live
	/// RefreshAsync (see TermsGateViewModel.RefreshInBackgroundAsync) in case a Pro purchase completed on
	/// another device since this cache was last refreshed -- that path, and the "Mua Pro ngay"/"Đồng ý &amp;
	/// Tiếp tục" buttons, are what actually gets the user from the gate to <c>createShell()</c>
	/// (<c>() =&gt; new AppShell()</c>, same factory shape SplashPage itself already takes).</summary>
	private Page CreateMainPage()
	{
		bool accepted = Preferences.Default.Get(Views.TermsGatePage.TermsAcceptedKey, false);
		if (accepted || _license.State.IsPro) return new AppShell();
		return new Views.TermsGatePage(_license, _dialog, () => new AppShell());
	}
}