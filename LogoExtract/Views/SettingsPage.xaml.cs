using LogoExtract.ViewModels;

namespace LogoExtract.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _vm;

    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    // Subscribe only while this specific page instance is actually on screen -- see SettingsViewModel's own
    // Subscribe/Unsubscribe doc comment for why a bare constructor subscription would leak.
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Subscribe();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Unsubscribe();
    }
}
