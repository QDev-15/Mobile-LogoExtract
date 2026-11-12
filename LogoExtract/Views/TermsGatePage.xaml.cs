using LogoExtract.Core.Licensing;
using LogoExtract.Services;
using LogoExtract.ViewModels;
using Microsoft.Maui.ApplicationModel;

namespace LogoExtract.Views;

/// <summary>Owner's request, 2026-11-12: a blocking terms/ads-data-collection gate shown once before the user
/// ever reaches AppShell. Constructed directly (<c>new Views.TermsGatePage(...)</c>) from App.xaml.cs's own
/// CreateMainPage, the same way App.CreateWindow already constructs <c>new Views.SplashPage(...)</c> and
/// AppShell itself -- NOT a DI-resolved/Shell-routed page (there is no Shell instance yet at this point in the
/// startup sequence), so it is not registered in MauiProgram.cs either.</summary>
public partial class TermsGatePage : ContentPage
{
    /// <summary>Preferences key for "has this install already agreed to this gate's ad-supported terms at
    /// least once". Versioned (_v1) by deliberate owner design -- see TermsGateViewModel's own class doc and
    /// App.xaml.cs's CreateMainPage comment: if this page's actual legal/product copy ever changes meaningfully,
    /// bumping to _v2 naturally re-prompts every existing install, same idiom LicenseService.CachedIsProKey
    /// already uses (Preferences.Default, a plain string key) for a different, unrelated flag in this same
    /// codebase.</summary>
    public const string TermsAcceptedKey = "terms_accepted_v1";

    private readonly AppDialog _dialog;
    private readonly Func<Page> _createShell;
    private readonly TermsGateViewModel _vm;

    /// <summary>True once any of the 3 outcomes has already fired -- guards against a double hand-off, e.g. the
    /// background RefreshInBackgroundAsync's own late "turns out Pro" result arriving a moment after the user
    /// already tapped "Từ chối &amp; Thoát" (or vice versa); whichever happens first wins, the other is ignored.</summary>
    private bool _handled;

    public TermsGatePage(ILicenseService license, AppDialog dialog, Func<Page> createShell)
    {
        InitializeComponent();
        _dialog = dialog;
        _createShell = createShell;
        _vm = new TermsGateViewModel(license, dialog);
        _vm.ProceedRequested += OnProceed;
        _vm.ExitRequested += OnExit;
        BindingContext = _vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Fire-and-forget on purpose -- see TermsGateViewModel.RefreshInBackgroundAsync's own doc comment for
        // why this must NOT block showing the gate's content (same "already correct instantly from cache,
        // re-verify live after" philosophy as App.xaml.cs's own license.RefreshAsync() call).
        _ = _vm.RefreshInBackgroundAsync();
    }

    /// <summary>Hardware/gesture Back while the gate is showing must behave EXACTLY like the explicit "Từ chối
    /// &amp; Thoát" button -- owner's exact spec, "let them exit the app" -- never silently do nothing and
    /// never just background the app (the Android default for an unhandled Back on a root-ish page). Same
    /// "dismiss the topmost open overlay first" shape as EditPage.OnBackButtonPressed's own override: if a
    /// purchase-error/timeout dialog happens to be open over this page right now, Back should close THAT first
    /// (so the person can see the exit buttons again and re-decide), not exit the whole app out from under it.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_dialog.Dismiss()) return true;
        OnExit();
        return true;
    }

    private void OnProceed()
    {
        if (_handled) return;
        _handled = true;
        Unsubscribe();
        // Same hand-off technique as SplashPage.OnAppearing's own "Window.Page = main" -- a hard cut, not a
        // cross-fade (a fading page would expose the window's plain background behind it mid-transition).
        if (Window != null) Window.Page = _createShell();
    }

    private void OnExit()
    {
        if (_handled) return;
        _handled = true;
        Unsubscribe();
        // Owner's exact spec: "let them exit the app" -- the whole process, not just this page. FinishAffinity
        // closes this Activity AND every other Activity in the same task stack, which for this single-Activity
        // MAUI app is the entire app; confirmed to compile/link against this project's net10.0-android target
        // (Platform.CurrentActivity, Microsoft.Maui.ApplicationModel, already used elsewhere in this codebase
        // for AppInfo.Current.ShowSettingsUI()'s related-but-different permission-settings hand-off). Not the
        // harder Process.killProcess -- no concrete reason this app needs that stronger hammer over the
        // standard MAUI/Android way to close an app from code.
        Platform.CurrentActivity?.FinishAffinity();
    }

    private void Unsubscribe()
    {
        _vm.ProceedRequested -= OnProceed;
        _vm.ExitRequested -= OnExit;
    }
}
