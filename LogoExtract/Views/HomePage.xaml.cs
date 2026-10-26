using LogoExtract.ViewModels;

namespace LogoExtract.Views;

public partial class HomePage : ContentPage
{
    private readonly ItemsViewModel _vm;

    public HomePage(ItemsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Refresh();
    }
}
