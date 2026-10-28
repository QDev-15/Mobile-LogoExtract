using LogoExtract.Services;

namespace LogoExtract.Views;

/// <summary>About the app: author and contact, version, terms of use, a link to PrivacyPolicyPage, and
/// open-source credits. Same contact details as the sibling PdfReader/DocScanner apps (same author, same
/// public contact across the whole portfolio).</summary>
public partial class AboutPage : ContentPage
{
    private const string SupportEmail = "nguyenquynhvp.ictu@gmail.com";
    private const string SupportPhone = "0988632841";

    private readonly AppDialog _dialog;

    public AboutPage(AppDialog dialog)
    {
        InitializeComponent();
        _dialog = dialog;
        VersionLabel.Text = $"Phiên bản {AppInfo.Current.VersionString} (bản dựng {AppInfo.Current.BuildString})";
    }

    // Same gotcha as ImportPage/EditPage (mobile-app-design.md §5.5/§8): an open dialog must close first on
    // Back, not get swept away by the page itself popping out from under it.
    protected override bool OnBackButtonPressed() => _dialog.Dismiss() || base.OnBackButtonPressed();

    private async void OnEmail(object? sender, TappedEventArgs e)
    {
        string subject = Uri.EscapeDataString($"Logo Extract {AppInfo.Current.VersionString} - hỗ trợ");
        try { await Launcher.Default.OpenAsync($"mailto:{SupportEmail}?subject={subject}"); }
        catch (Exception)
        {
            await Clipboard.Default.SetTextAsync(SupportEmail);
            await _dialog.AlertAsync("Email", "Đã chép địa chỉ email.", "Đóng");
        }
    }

    private async void OnPhone(object? sender, TappedEventArgs e)
    {
        try { await Launcher.Default.OpenAsync($"tel:{SupportPhone}"); }
        catch (Exception)
        {
            await Clipboard.Default.SetTextAsync(SupportPhone);
            await _dialog.AlertAsync("Điện thoại", "Đã chép số điện thoại.", "Đóng");
        }
    }

    private async void OnPrivacyPolicyTapped(object? sender, TappedEventArgs e) => await Shell.Current.GoToAsync(nameof(PrivacyPolicyPage));
}
