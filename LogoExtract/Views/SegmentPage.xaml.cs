using LogoExtract.Services;
using LogoExtract.ViewModels;
using SkiaSharp;

namespace LogoExtract.Views;

public partial class SegmentPage : ContentPage
{
    private readonly SegmentViewModel _vm;
    private readonly AppDialog _dialog;

    public SegmentPage(SegmentViewModel vm, AppDialog dialog)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        _dialog = dialog;
        _vm.ImagesReady += (image, mask) => MainThread.BeginInvokeOnMainThread(() => Editor.Load(image, mask));
    }

    // Same rationale as EditPage/ImportPage's own override: the segmentation-failure AppDialog this page can now
    // show (SegmentViewModel's catch blocks, added 2026-10-27) must close first on Back, not get swept away by
    // the page itself popping out from under it.
    protected override bool OnBackButtonPressed() => _dialog.Dismiss() || base.OnBackButtonPressed();

    private void OnAddMode(object? sender, EventArgs e) => Editor.SetMode(MaskEditorView.BrushMode.Add);
    private void OnRemoveMode(object? sender, EventArgs e) => Editor.SetMode(MaskEditorView.BrushMode.Remove);

    private async void OnDone(object? sender, EventArgs e)
    {
        SKBitmap? mask = Editor.CurrentMask;
        if (mask == null) return;
        await _vm.SaveAsync(mask);
    }

    private void OnCancelRegion(object? sender, EventArgs e) => _vm.CancelRegionSelect();

    private async void OnConfirmRegion(object? sender, EventArgs e)
    {
        SKBitmap? selection = Editor.CurrentMask;
        if (selection == null) return;
        await _vm.ConfirmRegionSelectAsync(selection);
    }
}
