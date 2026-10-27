using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LogoExtract.Services;

/// <summary>This app's own confirm / message dialog, replacing the native <c>DisplayAlert</c> calls that
/// EditViewModel/ImportViewModel used before this consistency pass (2026-10-27) -- the shared portfolio rule
/// (mobile-app-design.md §5.5, matching DocScanner's own AppDialog/AppDialogOverlay) is custom dialogs only,
/// never a native Android AlertDialog, so every screen's chrome actually matches. Deliberately smaller than
/// DocScanner's full version: just Confirm + Alert (no text-prompt), since nothing in this app asks for typed
/// input in a dialog yet.
///
/// <para>One singleton, drawn by <see cref="Views.AppDialogOverlay"/> wherever a page hosts one; at most one
/// dialog open at a time (a new one cancels/answers a stale one as "cancelled" rather than leaving it
/// hanging).</para></summary>
public sealed partial class AppDialog : ObservableObject
{
    private TaskCompletionSource<bool>? _pending;

    [ObservableProperty]
    private bool isOpen;

    [ObservableProperty]
    private string title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string message = "";

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    [ObservableProperty]
    private string acceptText = "OK";

    [ObservableProperty]
    private string cancelText = "Huỷ";

    /// <summary>False for a plain one-button message (AlertAsync).</summary>
    [ObservableProperty]
    private bool showCancel = true;

    /// <summary>Red accept button -- deleting or another irreversible action.</summary>
    [ObservableProperty]
    private bool isDestructive;

    /// <summary>True when accepted, false when cancelled/dismissed.</summary>
    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Huỷ", bool destructive = false) =>
        OpenAsync(title, message, accept, cancel, showCancel: true, destructive);

    /// <summary>One-button message dialog -- the custom-styled replacement for <c>DisplayAlert(title, message, ok)</c>.</summary>
    public Task AlertAsync(string title, string message, string ok = "Đóng") =>
        OpenAsync(title, message, ok, "", showCancel: false, destructive: false);

    private Task<bool> OpenAsync(string title, string message, string accept, string cancel, bool showCancel, bool destructive)
    {
        Close(false); // a dialog somehow still open is answered as cancelled, never left hanging

        Title = title;
        Message = message;
        AcceptText = accept;
        CancelText = cancel;
        ShowCancel = showCancel;
        IsDestructive = destructive;

        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsOpen = true;
        return _pending.Task;
    }

    [RelayCommand]
    private void Accept() => Close(true);

    [RelayCommand]
    private void Cancel() => Close(false);

    /// <summary>Closes the dialog as cancelled (hardware/gesture Back); false when none was open. Callers
    /// should check this before falling through to their own Back handling (§5.5/§8: close the topmost open
    /// overlay first, before it reaches page navigation or exit).</summary>
    public bool Dismiss()
    {
        if (!IsOpen) return false;
        Close(false);
        return true;
    }

    private void Close(bool accepted)
    {
        IsOpen = false;
        TaskCompletionSource<bool>? pending = _pending;
        _pending = null;
        pending?.TrySetResult(accepted);
    }
}
