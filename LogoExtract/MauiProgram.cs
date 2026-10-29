using LogoExtract.AdsService;
using LogoExtract.Core;
using LogoExtract.Core.Licensing;
using LogoExtract.Core.Segmentation;
using LogoExtract.Services;
using LogoExtract.ViewModels;
using LogoExtract.Views;
using Microsoft.Extensions.Logging;
using SkiaSharp.Views.Maui.Controls.Hosting;

namespace LogoExtract;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		(string Banner, string Interstitial, string Source)? adUnits = AdsMode.Resolve();
		Android.Util.Log.Info("LogoExtractPerf", $"ads: LevelPlay app {LevelPlayConfig.AppKey}, mode {(AdsMode.IsPro ? "Pro" : "Dev")}, units: {adUnits?.Source ?? "NONE (ads off for this build)"}");

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseLevelPlayAds(new LevelPlayOptions(
				AppKey: LevelPlayConfig.AppKey,
				BannerAdUnitId: adUnits?.Banner ?? "",
				InterstitialAdUnitId: adUnits?.Interstitial ?? "",
				TestMode: LevelPlayConfig.TestMode)
			{
				IsAdsEnabled = LevelPlayConfig.IsAdsEnabled && adUnits != null,
			})
			.UseSkiaSharp()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				fonts.AddFont("MaterialIcons-Regular.ttf", "Icons");
			});

		builder.Services.AddSingleton(_ => new ItemStore(Path.Combine(FileSystem.AppDataDirectory, "items")));
		// Lazy<T>, not a plain Singleton factory: copying the 4.5MB model out of the APK and building/optimizing
		// the ONNX Runtime session graph is real I/O + CPU work (found on a real device, 2026-10-27: the whole
		// UI froze on first opening the segment screen, well past a minute, no ANR because nothing was waiting
		// on input at the time). A Singleton factory runs the FIRST time anything resolves this service, which
		// for EditViewModel's own constructor injection (merged segment+edit screen, 2026-10-29 -- formerly
		// SegmentViewModel's) is Shell's own page-construction step during navigation -- on the UI thread,
		// before EditViewModel.LoadAsync's own Task.Run ever gets a chance to push it off that thread. Lazy<T>
		// defers the actual work to the first .Value access, which EditViewModel now makes from inside its own
		// Task.Run instead of the constructor.
		builder.Services.AddSingleton(_ => new Lazy<ISegmentationService>(() =>
		{
			// Console.WriteLine does NOT reach logcat on this Release build (confirmed on-device, 2026-10-27:
			// Android.Util.Log.Info does, Console.WriteLine never showed up at all, even hours later) --
			// Android.Util.Log.Info used here instead for that reason alone, not a style preference.
			Android.Util.Log.Info("LogoExtractPerf", "[U2Net] Lazy<ISegmentationService> factory starting");
			var sw = System.Diagnostics.Stopwatch.StartNew();
			string modelPath = SegmentationProviderResolver.EnsureModelCopied();
			Android.Util.Log.Info("LogoExtractPerf", $"[U2Net] EnsureModelCopied: {sw.ElapsedMilliseconds}ms");
			sw.Restart();
			// Owner's request, 2026-10-29: pick Cpu vs Nnapi per-device (benchmarked once and cached, or the
			// owner's own override from Settings) instead of hardcoding one -- see
			// SegmentationProviderResolver's own doc comment for why neither is safe to assume universally
			// faster. On a first-ever resolve with no cached benchmark yet, this runs that benchmark inline
			// (two real ONNX sessions, synthetic input) before returning, which is why this whole factory
			// already assumes real I/O+CPU cost and defers to the first .Value access off the UI thread (see
			// this field's own comment above).
			SegmentationProvider provider = SegmentationProviderResolver.Resolve(modelPath);
			var service = new U2NetSegmentationService(modelPath, provider);
			Android.Util.Log.Info("LogoExtractPerf", $"[U2Net] new InferenceSession ({provider}): {sw.ElapsedMilliseconds}ms");
			return service;
		}));

		// One dialog, one overlay, singleton: every page that drops an AppDialogOverlay into its own root Grid
		// binds to this SAME instance (mobile-app-design.md §5.5), so there is only ever one confirm/alert on
		// screen regardless of which page triggered it.
		builder.Services.AddSingleton<AppDialog>();

		// Singleton: owner's request, 2026-10-29, "thêm chức năng mua bản quyền mua pro giống như các app khác"
		// -- same Plugin.InAppBilling-backed pattern as the sibling PdfReader/Minesweeper apps' own
		// ILicenseService. App's own constructor wires this to IAdsClient.AreAdsEnabled (see that file's own
		// comment) so a Pro purchase actually hides the ad banner, not just flips a flag nothing reads.
		builder.Services.AddSingleton<ILicenseService, LicenseService>();

		// Singleton: its own background worker loop must outlive any one page (ImportViewModel enqueues a
		// background segmentation run and immediately navigates away; the run has to keep going after that
		// transient ViewModel is gone), and ItemsViewModel/EditViewModel (merged segment+edit screen,
		// 2026-10-29) both need to observe/await the SAME in-flight queue, not each get their own.
		builder.Services.AddSingleton<SegmentationCoordinator>();

		builder.Services.AddTransient<ItemsViewModel>();
		// ImportViewModel/ImportPage retired (2026-10-29) -- CaptureCommand/PickCommand/ImportOneAsync moved onto
		// ItemsViewModel itself, driven by HomePage's new inline FAB instead of a separate navigated page.
		// SegmentViewModel/SegmentPage retired (merged into EditViewModel/EditPage, 2026-10-29) -- EditViewModel
		// now takes the same services SegmentViewModel used to (Lazy<ISegmentationService>, SegmentationCoordinator)
		// alongside what it already took (ItemStore, AppDialog).
		builder.Services.AddTransient<EditViewModel>();
		builder.Services.AddTransient<SettingsViewModel>();
		builder.Services.AddTransient<HomePage>();
		builder.Services.AddTransient<EditPage>();
		builder.Services.AddTransient<SettingsPage>();
		// AboutPage/PrivacyPolicyPage have no ViewModel (plain static/display pages, same shape as PdfReader's
		// and Minesweeper's own About/Privacy pages) -- only the page itself needs a DI registration.
		builder.Services.AddTransient<AboutPage>();
		builder.Services.AddTransient<PrivacyPolicyPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		MauiApp app = builder.Build();
		return app;
	}
}
