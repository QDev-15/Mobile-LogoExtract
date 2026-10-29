using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core.Imaging;
using LogoExtract.Core.Licensing;
using LogoExtract.Core.Segmentation;
using LogoExtract.Services;
using LogoExtract.Views;
using Microsoft.Maui.ApplicationModel;

namespace LogoExtract.ViewModels;

/// <summary>SettingsPage's view model. Now owns three independent sections: Pro/Billing (owner's request,
/// 2026-10-29, "thêm chức năng mua bản quyền mua pro giống như các app khác"), the segmentation
/// execution-provider picker (CPU vs NNAPI -- see SegmentationProviderResolver's own doc comment), and the
/// edge-treatment picker (white-fringe fix -- see EdgeTreatmentResolver's own doc comment). No theme or
/// language picker (unlike the sibling PdfReader/Minesweeper apps).</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ILicenseService _license;
    private readonly AppDialog _dialog;

    public string VersionText => $"Logo Extract · Phiên bản {AppInfo.Current.VersionString} (bản dựng {AppInfo.Current.BuildString})";

    public SettingsViewModel(ILicenseService license, AppDialog dialog)
    {
        _license = license;
        _dialog = dialog;
        selectedOverride = SegmentationProviderResolver.UserOverride;
        selectedEdgeTreatment = EdgeTreatmentResolver.Current;
        RefreshBenchmarkText();
        RefreshLicenseState();
    }

    // Subscribe/Unsubscribe, not a constructor-only `+=`: this ViewModel is `AddTransient` (a NEW instance every
    // time the owner opens Settings), so a bare constructor subscription to the ILicenseService SINGLETON's own
    // Changed event would leak every past instance forever (mobile-app-design.md §8 item 5's own exact warning,
    // just for an event instead of RequestedThemeChanged). SettingsPage.xaml.cs calls these from
    // OnAppearing/OnDisappearing.
    public void Subscribe() => _license.Changed += OnLicenseChanged;
    public void Unsubscribe() => _license.Changed -= OnLicenseChanged;

    private void OnLicenseChanged() => MainThread.BeginInvokeOnMainThread(RefreshLicenseState);

    // --- Pro / Billing ---

    [ObservableProperty]
    private bool isPro;

    [ObservableProperty]
    private string licenseSummaryText = "";

    [ObservableProperty]
    private string buyButtonText = "Mua Pro";

    [ObservableProperty]
    private bool isPurchaseBusy;

    private void RefreshLicenseState()
    {
        IsPro = _license.State.IsPro;
        LicenseSummaryText = _license.State.SummaryText;
        BuyButtonText = _license.ProPriceText is { } price ? $"Mua Pro · {price}" : "Mua Pro";
    }

    /// <summary>Same 1-minute safety timeout as the sibling PdfReader app's own OnBuyClicked: PurchaseProAsync
    /// has no cancellation hook (Play Billing's own API, not this app's choice), so on timeout the spinner is
    /// just dropped here -- the underlying call, if it ever does answer, still lands in LicenseService's own
    /// cached State via its own RefreshAsync, just too late for THIS tap's own UI feedback.</summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task PurchaseProAsync()
    {
        if (_license.State.IsPro) return;
        IsPurchaseBusy = true;
        try
        {
            Task<PurchaseOutcome> task = _license.PurchaseProAsync();
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(1))) != task)
            {
                await _dialog.AlertAsync("Quá thời gian chờ", "Google Play không phản hồi sau 1 phút. Hãy thử lại.");
                return;
            }
            switch (await task)
            {
                case PurchaseOutcome.Purchased or PurchaseOutcome.AlreadyOwned:
                    await _dialog.AlertAsync("Cảm ơn bạn!", "Đã nâng cấp Logo Extract Pro. Quảng cáo đã được tắt.");
                    break;
                case PurchaseOutcome.Error:
                    await _dialog.AlertAsync("Chưa mua được", _license.LastError ?? "Không kết nối được Google Play. Hãy thử lại sau.");
                    break;
                    // Cancelled: no dialog -- the owner closing Play's own purchase sheet needs no extra confirmation.
            }
        }
        finally { IsPurchaseBusy = false; }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RestorePurchaseAsync()
    {
        IsPurchaseBusy = true;
        try
        {
            Task<PurchaseOutcome> task = _license.RestoreAsync();
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(1))) != task)
            {
                await _dialog.AlertAsync("Quá thời gian chờ", "Google Play không phản hồi sau 1 phút. Hãy thử lại.");
                return;
            }
            PurchaseOutcome outcome = await task;
            string message = outcome switch
            {
                PurchaseOutcome.Purchased or PurchaseOutcome.AlreadyOwned => "Đã khôi phục: tài khoản Google Play này đã mua Pro.",
                PurchaseOutcome.Error => _license.LastError ?? "Không kết nối được Google Play.",
                _ => "Không tìm thấy giao dịch mua Pro nào với tài khoản Google Play đang đăng nhập.",
            };
            await _dialog.AlertAsync("Khôi phục giao dịch", message);
        }
        finally { IsPurchaseBusy = false; }
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

    // --- Edge treatment for the "viền trắng" (white fringe) artifact -- owner's request, 2026-10-29: "có bao
    // nhiêu phương án giải quyết... nếu có 2 cách thì làm thêm setting". Same IsXxx-bool-per-option shape as the
    // segmentation provider picker above, for the same XAML-binding reason (no clean inline enum comparison).
    // Only affects items segmented/re-segmented AFTER the setting changes -- already-saved cutout.png files are
    // not retroactively reprocessed, same as the segmentation provider choice.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdgeDecontaminate))]
    [NotifyPropertyChangedFor(nameof(IsEdgeChoke))]
    private EdgeTreatment selectedEdgeTreatment;

    public bool IsEdgeDecontaminate => SelectedEdgeTreatment == EdgeTreatment.Decontaminate;
    public bool IsEdgeChoke => SelectedEdgeTreatment == EdgeTreatment.Choke;

    [RelayCommand]
    private void SetEdgeDecontaminate() => ApplyEdgeTreatment(EdgeTreatment.Decontaminate);

    [RelayCommand]
    private void SetEdgeChoke() => ApplyEdgeTreatment(EdgeTreatment.Choke);

    private void ApplyEdgeTreatment(EdgeTreatment value)
    {
        EdgeTreatmentResolver.Current = value;
        SelectedEdgeTreatment = value;
    }
}
