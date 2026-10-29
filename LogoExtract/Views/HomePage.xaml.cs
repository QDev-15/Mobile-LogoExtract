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
        // Drives the FAB's slide-up/fade/rotate reveal off ItemsViewModel.IsAddExpanded (owner's request,
        // 2026-10-29: "animation trượt lên rồi 2 button hiện lên"). This lives here, not in the ViewModel,
        // because the actual animation calls (ViewExtensions.TranslateTo/FadeTo/RotateTo) need a real View
        // instance -- IsAddExpanded itself stays a plain MVVM bool so XAML bindings (the tap-catcher's
        // IsVisible) can still use it directly. Defensive -=/+= (mobile-app-design.md §8 gotcha about
        // RequestedThemeChanged leaks on transient pages) even though this constructor only runs once per
        // HomePage instance today -- cheap insurance if that ever stops being true.
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Refresh();
    }

    private async void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ItemsViewModel.IsAddExpanded)) return;
        if (_vm.IsAddExpanded) await ExpandAddAsync();
        else await CollapseAddAsync();
    }

    // FadeTo/TranslateTo/RotateTo (Microsoft.Maui.Controls.ViewExtensions) already return Task<bool> -- there
    // is NO separate "...Async"-suffixed overload (see SplashPage.xaml.cs's own comment for the real gotcha
    // this avoids: calling a non-existent name silently compiles but leaves the element never actually moving).
    // AddActionsPanel.IsVisible is flipped true BEFORE animating in and false AFTER animating out, not bound
    // straight to IsAddExpanded, so the slide/fade is actually visible instead of the panel just popping in/out.
    private async Task ExpandAddAsync()
    {
        AddActionsPanel.IsVisible = true;
        await Task.WhenAll(
            AddActionsPanel.FadeTo(1, 220, Easing.CubicOut),
            AddActionsPanel.TranslateTo(0, 0, 220, Easing.CubicOut),
            FabIcon.RotateTo(45, 200, Easing.CubicOut));
    }

    private async Task CollapseAddAsync()
    {
        await Task.WhenAll(
            AddActionsPanel.FadeTo(0, 180, Easing.CubicIn),
            AddActionsPanel.TranslateTo(0, 24, 180, Easing.CubicIn),
            FabIcon.RotateTo(0, 180, Easing.CubicIn));
        AddActionsPanel.IsVisible = false;
    }

    /// <summary>Settings gear icon in the header -- plain Tapped handler, not a Command binding; predates the
    /// 2026-10-29 FAB redesign and wasn't touched by it (see HomePage.xaml's own header comment).</summary>
    private async void OnSettingsTapped(object? sender, TappedEventArgs e) => await Shell.Current.GoToAsync(nameof(SettingsPage));

    /// <summary>HomePage is the Shell root (sole ShellContent, FlyoutBehavior disabled) -- Back here would
    /// otherwise exit the app on the very first press, which is too easy to trigger by accident (an edge swipe
    /// while scrolling the list). Added 2026-10-27, same shape as PdfReader's DashboardPage/DocScanner's
    /// HomePage: an open dialog closes first, then an open FAB tray closes second (2026-10-29, same "undo the
    /// topmost thing first" ordering as the dialog check), then a single Back shows a toast and is consumed;
    /// only a SECOND Back within 2s actually exits (by returning false and letting Android's own default
    /// back-on-root-page behavior proceed). EditPage is NOT a Shell root and keeps plain single-press Back
    /// (pop to the previous screen) -- this override deliberately lives only here, not in AppShell or a shared
    /// base page, since nothing else in this app should ever need a second press to go back.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_dialog.Dismiss()) return true;
        if (_vm.IsAddExpanded) { _vm.IsAddExpanded = false; return true; }

        DateTime now = DateTime.UtcNow;
        if (now - _lastBackPress < TimeSpan.FromSeconds(2)) return false; // 2nd press in time: let it exit for real
        _lastBackPress = now;
        // 150 clears the floating FAB (16 margin + 64 height = 80) plus the ad banner row above/under it with
        // margin to spare -- same target height the old CTA pill's margin math cleared, not re-measured here.
        this.ShowToast("Nhấn Back lần nữa để thoát", bottomMargin: 150);
        return true; // consume this press
    }
}
