using LogoExtract.Services;
using LogoExtract.ViewModels;
using SkiaSharp;

namespace LogoExtract.Views;

/// <summary>Merged "tách logo khỏi nền" + "chỉnh sửa" screen (owner's request, 2026-10-29) -- the former
/// SegmentPage's code-behind (Editor wiring, brush-mode/region-select button handlers) is relocated into here
/// near-verbatim; SegmentPage/SegmentViewModel are deleted.</summary>
public partial class EditPage : ContentPage
{
    private readonly EditViewModel _vm;
    private readonly AppDialog _dialog;
    // Owner's request, 2026-10-29, "tránh nhiều action cùng chạy rồi loạn app gây crash": OnDone/OnConfirmRegion
    // are plain code-behind `async void` handlers, not [RelayCommand]s, so they can't use
    // CommunityToolkit.Mvvm's own AllowConcurrentExecutions guard (see EditViewModel's RelayCommand methods for
    // that pattern) -- this is the same protection by hand. Specifically needed for OnConfirmRegion: "Xác nhận
    // vùng" is deliberately NOT IsEnabled-gated on IsBusy (EditViewModel's own class doc comment: it must stay
    // reachable while busy, same as "Chọn vùng"), so nothing else stops a fast double-tap from calling
    // ConfirmRegionSelectAsync twice concurrently, both racing to crop+segment+write the same files.
    private bool _actionInFlight;

    // Cycle order/labels for the preview-background chip (unchanged from before this merge).
    private static readonly (PreviewBackgroundKind Kind, string Label)[] PreviewBackgrounds =
    [
        (PreviewBackgroundKind.Checkerboard, "Ô vuông"),
        (PreviewBackgroundKind.White, "Trắng"),
        (PreviewBackgroundKind.Black, "Đen"),
        (PreviewBackgroundKind.Gray, "Xám"),
    ];
    private int _previewBgIndex;

    public EditPage(EditViewModel vm, AppDialog dialog)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        _dialog = dialog;
        // Same wiring SegmentPage used to have: pushes _image/_mask into MaskEditorView every time the VM
        // publishes a new pair (first load, region-select start/cancel/confirm), regardless of whether the
        // brush sub-toolbar is visible right now -- so Editor is already showing the right thing the moment the
        // user taps "Chỉnh sửa".
        _vm.ImagesReady += (image, mask) => MainThread.BeginInvokeOnMainThread(() => Editor.Load(image, mask));
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Unload();
    }

    // Same rationale as before this merge: an open AppDialog (segmentation failure, vectorize result/error,
    // storage permission, save-to-gallery result) must close first on Back, not get swept away by the page
    // itself popping out from under it.
    protected override bool OnBackButtonPressed() => _dialog.Dismiss() || base.OnBackButtonPressed();

    // Relocated from SegmentPage.xaml.cs, unchanged -- plain code-behind talking directly to Editor (a
    // ViewModel RelayCommand can't reach MaskEditorView's own SetMode/CurrentMask without the ViewModel holding
    // a reference to the View).
    private void OnAddMode(object? sender, EventArgs e) => Editor.SetMode(MaskEditorView.BrushMode.Add);
    private void OnRemoveMode(object? sender, EventArgs e) => Editor.SetMode(MaskEditorView.BrushMode.Remove);

    private async void OnDone(object? sender, EventArgs e)
    {
        if (_actionInFlight) return;
        SKBitmap? mask = Editor.CurrentMask;
        if (mask == null) return;
        _actionInFlight = true;
        try { await _vm.SaveMaskAsync(mask); }
        finally { _actionInFlight = false; }
    }

    private void OnCancelRegion(object? sender, EventArgs e) => _vm.CancelRegionSelect();

    private async void OnConfirmRegion(object? sender, EventArgs e)
    {
        if (_actionInFlight) return;
        SKBitmap? selection = Editor.CurrentMask;
        if (selection == null) return;
        _actionInFlight = true;
        try { await _vm.ConfirmRegionSelectAsync(selection); }
        finally { _actionInFlight = false; }
    }

    // Local UI-only state (not persisted, not ViewModel-bound) -- same shape as OnAddMode/OnRemoveMode above.
    private void OnCyclePreviewBackground(object? sender, TappedEventArgs e)
    {
        _previewBgIndex = (_previewBgIndex + 1) % PreviewBackgrounds.Length;
        (PreviewBackgroundKind kind, string label) = PreviewBackgrounds[_previewBgIndex];
        PreviewCanvas.PreviewBackground = kind;
        PreviewBgLabel.Text = label;
    }
}
