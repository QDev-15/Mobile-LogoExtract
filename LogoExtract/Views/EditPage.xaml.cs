using LogoExtract.ViewModels;

namespace LogoExtract.Views;

public partial class EditPage : ContentPage
{
    private readonly EditViewModel _vm;

    public EditPage(EditViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Unload();
    }
}
