using LogoExtract.AdsService;
using LogoExtract.Core;
using LogoExtract.Core.Segmentation;
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
			});

		builder.Services.AddSingleton(_ => new ItemStore(Path.Combine(FileSystem.AppDataDirectory, "items")));
		// Lazy<T>, not a plain Singleton factory: copying the 4.5MB model out of the APK and building/optimizing
		// the ONNX Runtime session graph is real I/O + CPU work (found on a real device, 2026-10-27: the whole
		// UI froze on first opening the segment screen, well past a minute, no ANR because nothing was waiting
		// on input at the time). A Singleton factory runs the FIRST time anything resolves this service, which
		// for SegmentViewModel's own constructor injection is Shell's own page-construction step during
		// navigation -- on the UI thread, before SegmentViewModel.LoadAsync's own Task.Run ever gets a chance to
		// push it off that thread. Lazy<T> defers the actual work to the first .Value access, which
		// SegmentViewModel now makes from inside its own Task.Run instead of the constructor.
		builder.Services.AddSingleton(_ => new Lazy<ISegmentationService>(() =>
		{
			// Console.WriteLine does NOT reach logcat on this Release build (confirmed on-device, 2026-10-27:
			// Android.Util.Log.Info does, Console.WriteLine never showed up at all, even hours later) --
			// Android.Util.Log.Info used here instead for that reason alone, not a style preference.
			Android.Util.Log.Info("LogoExtractPerf", "[U2Net] Lazy<ISegmentationService> factory starting");
			var sw = System.Diagnostics.Stopwatch.StartNew();
			string modelPath = EnsureModelCopied();
			Android.Util.Log.Info("LogoExtractPerf", $"[U2Net] EnsureModelCopied: {sw.ElapsedMilliseconds}ms");
			sw.Restart();
			var service = new U2NetSegmentationService(modelPath);
			Android.Util.Log.Info("LogoExtractPerf", $"[U2Net] new InferenceSession: {sw.ElapsedMilliseconds}ms");
			return service;
		}));

		builder.Services.AddTransient<ItemsViewModel>();
		builder.Services.AddTransient<ImportViewModel>();
		builder.Services.AddTransient<SegmentViewModel>();
		builder.Services.AddTransient<EditViewModel>();
		builder.Services.AddTransient<HomePage>();
		builder.Services.AddTransient<ImportPage>();
		builder.Services.AddTransient<SegmentPage>();
		builder.Services.AddTransient<EditPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		MauiApp app = builder.Build();
		return app;
	}

	/// <summary>Raw assets (Resources/Raw) ship inside the APK, not as a plain file on disk -- ONNX Runtime's
	/// InferenceSession needs an actual file path (or a byte[], not used here to keep this one-line simple), so
	/// the model is copied to AppDataDirectory once on first run and reused after that.</summary>
	private static string EnsureModelCopied()
	{
		string dest = Path.Combine(FileSystem.AppDataDirectory, "u2netp.onnx");
		if (!File.Exists(dest))
		{
			using Stream source = FileSystem.OpenAppPackageFileAsync("Models/u2netp.onnx").GetAwaiter().GetResult();
			using FileStream destStream = File.Create(dest);
			source.CopyTo(destStream);
		}
		return dest;
	}
}
