using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Views;
using Microsoft.Maui.ApplicationModel;

namespace LogoExtract.ViewModels;

/// <summary>SettingsPage's view model. Thin by design -- LogoExtract has no Pro tier / IAP and no theme or
/// language picker (unlike the sibling PdfReader/Minesweeper apps), so there is nothing to persist here yet;
/// this only exists so SettingsPage follows the same real-ViewModel-with-[RelayCommand] convention as every
/// other page in this app (ItemsViewModel, ImportViewModel, SegmentViewModel, EditViewModel), rather than
/// PdfReader's/Minesweeper's own plain-code-behind Settings pages.</summary>
public partial class SettingsViewModel : ObservableObject
{
    public string VersionText => $"Logo Extract · Phiên bản {AppInfo.Current.VersionString} (bản dựng {AppInfo.Current.BuildString})";

    [RelayCommand]
    private Task OpenAboutAsync() => Shell.Current.GoToAsync(nameof(AboutPage));

    [RelayCommand]
    private Task OpenPrivacyPolicyAsync() => Shell.Current.GoToAsync(nameof(PrivacyPolicyPage));
}
