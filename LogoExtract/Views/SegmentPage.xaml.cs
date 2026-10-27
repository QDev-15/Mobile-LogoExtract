using LogoExtract.ViewModels;
using SkiaSharp;

namespace LogoExtract.Views;

public partial class SegmentPage : ContentPage
{
    private readonly SegmentViewModel _vm;

    public SegmentPage(SegmentViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        _vm.ImagesReady += (image, mask) => MainThread.BeginInvokeOnMainThread(() => Editor.Load(image, mask));
    }

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
