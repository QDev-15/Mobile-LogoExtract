using LogoExtract.Services;
using LogoExtract.ViewModels;

namespace LogoExtract.Views;

public partial class ImportPage : ContentPage
{
    private readonly AppDialog _dialog;

    public ImportPage(ImportViewModel vm, AppDialog dialog)
    {
        InitializeComponent();
        BindingContext = vm;
        _dialog = dialog;
    }

    // An open dialog (e.g. "camera not supported") must close first on Back, not get swept away by the page
    // itself popping out from under it (mobile-app-design.md §5.5/§8 gotcha: close the topmost open overlay
    // before it falls through to page navigation). Added 2026-10-27 alongside AppDialogOverlay.
    protected override bool OnBackButtonPressed() => _dialog.Dismiss() || base.OnBackButtonPressed();
}
