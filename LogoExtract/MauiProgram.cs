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
		builder.Services.AddSingleton<ISegmentationService>(_ =>
		{
			string modelPath = EnsureModelCopied();
			return new U2NetSegmentationService(modelPath);
		});

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
