using LogoExtract.ViewModels;

namespace LogoExtract.Views;

public partial class ImportPage : ContentPage
{
    public ImportPage(ImportViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
