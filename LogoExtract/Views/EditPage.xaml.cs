using LogoExtract.Services;
using LogoExtract.ViewModels;

namespace LogoExtract.Views;

public partial class EditPage : ContentPage
{
    private readonly EditViewModel _vm;
    private readonly AppDialog _dialog;

    // Cycle order/labels for the preview-background chip (added 2026-10-27). Checkerboard first: the universal
    // "this is transparency" convention, and the most useful default for judging a cutout's own edges.
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
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Unload();
    }

    // Same rationale as ImportPage's own override: an open AppDialog (save-color confirmation, vectorize
    // result, storage permission, save-to-gallery result) must close first on Back, not get swept away by the
    // page itself popping out from under it. Added 2026-10-27 alongside AppDialogOverlay.
    protected override bool OnBackButtonPressed() => _dialog.Dismiss() || base.OnBackButtonPressed();

    // Local UI-only state (not persisted, not ViewModel-bound) -- same shape as SegmentPage's own
    // OnAddMode/OnRemoveMode brush-mode toggling, which is also plain code-behind talking directly to its own
    // Skia view rather than round-tripping through a ViewModel property for something this transient.
    private void OnCyclePreviewBackground(object? sender, TappedEventArgs e)
    {
        _previewBgIndex = (_previewBgIndex + 1) % PreviewBackgrounds.Length;
        (PreviewBackgroundKind kind, string label) = PreviewBackgrounds[_previewBgIndex];
        PreviewCanvas.PreviewBackground = kind;
        PreviewBgLabel.Text = label;
    }
}
