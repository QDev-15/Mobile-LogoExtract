using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core.Licensing;
using LogoExtract.Services;
using LogoExtract.Views;

namespace LogoExtract.ViewModels;

/// <summary>TermsGatePage's view model -- owner's request, 2026-11-12 ("thêm popup trước khi vào app, yêu cầu
/// đồng ý điều khoản quảng cáo thu thập thông tin cá nhân, hoặc mua Pro"). Three, and only three, ways out of
/// this screen, all ending in exactly one of the two events below:
/// <list type="bullet">
/// <item>Buy Pro successfully (<see cref="PurchaseProCommand"/>) -- removes the entire ads/data-collection
/// concern this gate is about, so it proceeds regardless of the checkbox. Deliberately does NOT also persist
/// <see cref="TermsGatePage.TermsAcceptedKey"/> here: that flag means "agreed to the ad-supported terms", which
/// isn't actually true for someone who bought Pro instead -- if this purchase were ever refunded/revoked, the
/// NEXT launch should fall through to <see cref="ILicenseService.State"/>.IsPro (now false) and show this gate
/// again rather than silently skip it forever on a stale "accepted" flag. The cached-Pro check in
/// App.CreateMainPage covers returning Pro owners instead, independently of this flag.</item>
/// <item>Tick the checkbox + <see cref="AgreeAndContinueCommand"/> -- the free/ad-supported path; DOES persist
/// the acceptance flag, that is the whole point of this path.</item>
/// <item><see cref="DeclineAndExitCommand"/>, or the hardware Back button (TermsGatePage's own
/// OnBackButtonPressed override) -- exits the app entirely, see TermsGatePage.xaml.cs.</item>
/// </list>
/// A fourth, silent path also exists: <see cref="RefreshInBackgroundAsync"/>, kicked off from OnAppearing, can
/// detect a live Pro status before the user taps anything (e.g. bought on another device) -- treated the same
/// as the explicit checkbox path (acceptance persisted too, "for consistency", owner's own words) so a real
/// returning Pro owner never has to read any of this.</summary>
public partial class TermsGateViewModel : ObservableObject
{
    private readonly ILicenseService _license;
    private readonly AppDialog _dialog;

    /// <summary>Raised exactly once (TermsGatePage subscribes once and ignores anything after the first) when
    /// any path has decided the gate is satisfied; the page does the actual Window.Page swap to AppShell.</summary>
    public event Action? ProceedRequested;

    /// <summary>Raised exactly once for the "neither bought Pro nor agreed" outcome; the page closes the app.</summary>
    public event Action? ExitRequested;

    [ObservableProperty]
    private bool isAgreed;

    [ObservableProperty]
    private string buyButtonText = "Mua Pro ngay";

    [ObservableProperty]
    private bool isPurchaseBusy;

    public TermsGateViewModel(ILicenseService license, AppDialog dialog)
    {
        _license = license;
        _dialog = dialog;
        RefreshBuyButtonText();
    }

    private void RefreshBuyButtonText() =>
        // Same "append price once known" pattern as SettingsViewModel.RefreshLicenseState's own BuyButtonText --
        // ProPriceText is null until the first successful RefreshAsync (or if Play can't be reached), so the
        // button reads a plain "Mua Pro ngay" until then, same as Settings' own "Mua Pro" does.
        BuyButtonText = _license.ProPriceText is { } price ? $"Mua Pro ngay · {price}" : "Mua Pro ngay";

    /// <summary>Kicked off from TermsGatePage.OnAppearing, NOT awaited there -- same "already correct instantly
    /// from cache, re-verify live after" idea App.xaml.cs's own constructor comment already states, just with a
    /// short (2.5s) timeout instead of letting a slow/unreachable Play connection hang this screen indefinitely.
    /// Covers the one case the synchronous cache check in App.CreateMainPage cannot: a Pro purchase that
    /// completed on a DIFFERENT device with the same Google account since this device's own cache was last
    /// refreshed.</summary>
    public async Task RefreshInBackgroundAsync()
    {
        try
        {
            Task refresh = _license.RefreshAsync();
            await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(2.5)));
        }
        catch
        {
            // Best-effort only -- the cache already decided it was worth showing this page at all; a failed or
            // timed-out live refresh just means the user answers the question themselves instead.
        }
        RefreshBuyButtonText();
        if (_license.State.IsPro) ProceedWithAcceptance(); // outcome 1-equivalent, no tap needed -- see class doc
    }

    /// <summary>Same 1-minute safety-timeout race via Task.WhenAny, and the same dialog copy, as the sibling
    /// SettingsViewModel.PurchaseProAsync -- PurchaseProAsync itself has no cancellation hook (Play Billing's
    /// own API, not this app's choice), so on timeout the spinner here is just dropped, same as there; the
    /// underlying call, if it ever does answer, still lands in LicenseService's own cached State via its own
    /// RefreshAsync, just too late for THIS tap's own UI feedback. Only difference from Settings' version: the
    /// success path here proceeds straight into the app instead of staying put, and Cancelled still shows no
    /// dialog (closing Play's own sheet needs no extra confirmation here either).</summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task PurchaseProAsync()
    {
        if (_license.State.IsPro) { ProceedWithoutAcceptance(); return; }
        IsPurchaseBusy = true;
        try
        {
            Task<PurchaseOutcome> task = _license.PurchaseProAsync();
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(1))) != task)
            {
                await _dialog.AlertAsync("Quá thời gian chờ", "Google Play không phản hồi sau 1 phút. Hãy thử lại.");
                return;
            }
            switch (await task)
            {
                case PurchaseOutcome.Purchased or PurchaseOutcome.AlreadyOwned:
                    await _dialog.AlertAsync("Cảm ơn bạn!", "Đã nâng cấp Logo Extract Pro. Quảng cáo đã được tắt.");
                    ProceedWithoutAcceptance();
                    break;
                case PurchaseOutcome.Error:
                    await _dialog.AlertAsync("Chưa mua được", _license.LastError ?? "Không kết nối được Google Play. Hãy thử lại sau.");
                    break;
                    // Cancelled: no dialog -- closing Play's own purchase sheet needs no extra confirmation.
            }
        }
        finally { IsPurchaseBusy = false; }
    }

    /// <summary>Tapping the agreement LABEL also toggles the checkbox (bigger, easier target than the bare
    /// CheckBox alone -- mobile-app-design.md §7's 44x44dp floor is about the CheckBox control itself, this is
    /// just a convenience on top, same idea as EditPage's double-tap-reset labels being tappable in their own
    /// right).</summary>
    [RelayCommand]
    private void ToggleAgreed() => IsAgreed = !IsAgreed;

    [RelayCommand]
    private void AgreeAndContinue()
    {
        if (!IsAgreed) return; // belt-and-suspenders -- XAML already disables this command's own button until ticked
        ProceedWithAcceptance();
    }

    [RelayCommand]
    private void DeclineAndExit() => ExitRequested?.Invoke();

    private void ProceedWithAcceptance()
    {
        Preferences.Default.Set(TermsGatePage.TermsAcceptedKey, true);
        ProceedRequested?.Invoke();
    }

    /// <summary>Buying Pro proceeds WITHOUT setting the acceptance flag -- see this class's own doc comment for
    /// why (a later refund should re-show this gate, not skip it forever on a stale flag).</summary>
    private void ProceedWithoutAcceptance() => ProceedRequested?.Invoke();
}
