using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core.Segmentation;
using LogoExtract.Services;
using LogoExtract.Views;
using Microsoft.Maui.ApplicationModel;

namespace LogoExtract.ViewModels;

/// <summary>SettingsPage's view model. No Pro tier / IAP and no theme or language picker (unlike the sibling
/// PdfReader/Minesweeper apps), so the "ỨNG DỤNG" section stays as thin as before; this one now also owns the
/// segmentation execution-provider picker (owner's request, 2026-10-29: "setting dùng 1 trong 2... user có thể
/// chỉnh được trong setting" -- see SegmentationProviderResolver's own doc comment for the full reasoning/the
/// on-device numbers that motivated a per-device choice instead of one hardcoded option).</summary>
public partial class SettingsViewModel : ObservableObject
{
    public string VersionText => $"Logo Extract · Phiên bản {AppInfo.Current.VersionString} (bản dựng {AppInfo.Current.BuildString})";

    public SettingsViewModel()
    {
        selectedOverride = SegmentationProviderResolver.UserOverride;
        RefreshBenchmarkText();
    }

    // AllowConcurrentExecutions = false: same double-tap-navigates-twice guard as ItemsViewModel.OpenAsync.
    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task OpenAboutAsync() => Shell.Current.GoToAsync(nameof(AboutPage));

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task OpenPrivacyPolicyAsync() => Shell.Current.GoToAsync(nameof(PrivacyPolicyPage));

    // --- Segmentation execution provider (CPU vs NNAPI) ---

    // null = "Tự động" (follow SegmentationProviderResolver's own benchmarked recommendation); the three
    // IsSegmentationXxx bools below are what SettingsPage.xaml actually binds its checkmark rows to, since XAML
    // has no clean way to compare a bound enum? to a specific value inline.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSegmentationAuto))]
    [NotifyPropertyChangedFor(nameof(IsSegmentationCpu))]
    [NotifyPropertyChangedFor(nameof(IsSegmentationNnapi))]
    private SegmentationProvider? selectedOverride;

    public bool IsSegmentationAuto => SelectedOverride == null;
    public bool IsSegmentationCpu => SelectedOverride == SegmentationProvider.Cpu;
    public bool IsSegmentationNnapi => SelectedOverride == SegmentationProvider.Nnapi;

    [ObservableProperty]
    private string benchmarkText = "";

    [ObservableProperty]
    private bool isBenchmarking;

    [RelayCommand]
    private void SetSegmentationAuto() => ApplyOverride(null);

    [RelayCommand]
    private void SetSegmentationCpu() => ApplyOverride(SegmentationProvider.Cpu);

    [RelayCommand]
    private void SetSegmentationNnapi() => ApplyOverride(SegmentationProvider.Nnapi);

    private void ApplyOverride(SegmentationProvider? value)
    {
        SegmentationProviderResolver.UserOverride = value;
        SelectedOverride = value;
    }

    /// <summary>"Đo lại tốc độ": forces a fresh Cpu-vs-Nnapi timing on THIS device right now, regardless of any
    /// cached result -- useful after an OS update, or if the owner just wants to confirm the original
    /// recommendation still holds. Same two-session benchmark App's own constructor already triggers once,
    /// automatically, in the background on first-ever launch (SegmentationProviderResolver.WarmUpInBackground);
    /// this just re-triggers it on demand from Settings. AllowConcurrentExecutions = false replaces this
    /// method's own former hand-rolled `if (IsBenchmarking) return;` guard -- same effect, one less place to
    /// keep in sync with IsBenchmarking by hand.</summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RemeasureSpeedAsync()
    {
        IsBenchmarking = true;
        try
        {
            string modelPath = SegmentationProviderResolver.EnsureModelCopied();
            await Task.Run(() => SegmentationProviderResolver.RunBenchmarkAndCache(modelPath));
        }
        finally
        {
            IsBenchmarking = false;
            RefreshBenchmarkText();
        }
    }

    private void RefreshBenchmarkText()
    {
        (SegmentationProvider Recommendation, long CpuMs, long NnapiMs)? result = SegmentationProviderResolver.AutoResult;
        BenchmarkText = result == null
            ? "Chưa đo tốc độ trên máy này -- đang đo ở nền, hoặc bấm \"Đo lại tốc độ\" bên dưới để đo ngay."
            : $"Đã đo trên máy này: CPU {result.Value.CpuMs}ms · NNAPI {result.Value.NnapiMs}ms. Khuyến nghị: {(result.Value.Recommendation == SegmentationProvider.Cpu ? "CPU" : "NNAPI")}.";
    }
}
