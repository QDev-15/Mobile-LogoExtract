using LogoExtract.Services;
using LogoExtract.ViewModels;

namespace LogoExtract.Views;

public partial class HomePage : ContentPage
{
    private readonly ItemsViewModel _vm;
    private readonly AppDialog _dialog;
    private DateTime _lastBackPress = DateTime.MinValue;

    public HomePage(ItemsViewModel vm, AppDialog dialog)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        _dialog = dialog;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Refresh();
    }

    /// <summary>HomePage is the Shell root (sole ShellContent, FlyoutBehavior disabled) -- Back here would
    /// otherwise exit the app on the very first press, which is too easy to trigger by accident (an edge swipe
    /// while scrolling the list). Added 2026-10-27, same shape as PdfReader's DashboardPage/DocScanner's
    /// HomePage: an open dialog closes first, then a single Back shows a toast and is consumed; only a SECOND
    /// Back within 2s actually exits (by returning false and letting Android's own default back-on-root-page
    /// behavior proceed). ImportPage/SegmentPage/EditPage are NOT Shell roots and keep plain single-press Back
    /// (pop to the previous screen) -- this override deliberately lives only here, not in AppShell or a shared
    /// base page, since nothing else in this app should ever need a second press to go back.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_dialog.Dismiss()) return true;

        DateTime now = DateTime.UtcNow;
        if (now - _lastBackPress < TimeSpan.FromSeconds(2)) return false; // 2nd press in time: let it exit for real
        _lastBackPress = now;
        // 150 clears the bottom bar's CTA pill (20+52+12=84) plus the ad banner row above it with margin to spare.
        this.ShowToast("Nhấn Back lần nữa để thoát", bottomMargin: 150);
        return true; // consume this press
    }
}
