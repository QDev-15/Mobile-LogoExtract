using System.Windows.Input;

namespace LogoExtract.Views;

/// <summary>A toolbar button: an icon (Material Icons glyph, see <see cref="Icons"/>) over a small caption, no
/// background, the whole cell tappable. Same control as DocScanner's own Views/ToolButton.cs, ported verbatim
/// (just retargeted at LogoExtract's own Primary/TextSecondary palette) per the owner's request, 2026-10-27:
/// "các icon ở phần toolbar bottom cần có cả icon và name giống như docscanner" -- every bottom toolbar in this
/// app (SegmentPage, EditPage) used icon-only buttons before this; <see cref="IsActive"/> tints it with the
/// brand color for a toggled-on tool, a disabled button fades out and ignores taps.</summary>
public class ToolButton : ContentView
{
    public static readonly BindableProperty IconProperty =
        BindableProperty.Create(nameof(Icon), typeof(string), typeof(ToolButton), "", propertyChanged: (b, _, v) => ((ToolButton)b)._icon.Text = (string)v);

    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(nameof(Text), typeof(string), typeof(ToolButton), "", propertyChanged: (b, _, v) => ((ToolButton)b)._caption.Text = (string)v);

    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(ToolButton));

    public static readonly BindableProperty CommandParameterProperty =
        BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(ToolButton));

    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.Create(nameof(IsActive), typeof(bool), typeof(ToolButton), false, propertyChanged: (b, _, _) => ((ToolButton)b).UpdateColors());

    public static readonly BindableProperty IconSizeProperty =
        BindableProperty.Create(nameof(IconSize), typeof(double), typeof(ToolButton), 24.0, propertyChanged: (b, _, v) => ((ToolButton)b)._icon.FontSize = (double)v);

    /// <summary>Fires alongside Command (same dual Command+Clicked shape as a plain MAUI Button) for callers that
    /// need code-behind, not a bindable ICommand -- e.g. SegmentPage's "Thêm"/"Xoá" set brush mode directly on
    /// its MaskEditorView, and "Xong"/"Huỷ"/"Xác nhận vùng" need that same view's CurrentMask, neither of which a
    /// plain ViewModel RelayCommand can reach without the ViewModel holding a reference to the View.</summary>
    public event EventHandler? Clicked;

    private readonly Label _icon;
    private readonly Label _caption;

    public ToolButton()
    {
        _icon = new Label
        {
            FontFamily = Icons.FontFamily,
            FontSize = 24,
            HorizontalOptions = LayoutOptions.Center,
        };
        _caption = new Label
        {
            FontSize = 11,
            HorizontalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        Content = new VerticalStackLayout
        {
            Spacing = 2,
            Padding = new Thickness(4, 6),
            VerticalOptions = LayoutOptions.Center,
            Children = { _icon, _caption },
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (!IsEnabled) return;
            if (Command?.CanExecute(CommandParameter) ?? false) Command.Execute(CommandParameter);
            Clicked?.Invoke(this, EventArgs.Empty);
        };
        GestureRecognizers.Add(tap);
        MinimumWidthRequest = 56;
        BackgroundColor = Colors.Transparent;
        UpdateColors();
        // Loaded/Unloaded, not a bare constructor subscription with no matching unsubscribe: every page using
        // ToolButton is AddTransient, so a plain += here would root every past instance (and everything it closes
        // over) in Application.Current for the app's whole lifetime. -= before +=: MAUI does not guarantee
        // Loaded/Unloaded fire strictly 1:1 (reparenting, Shell page caching can raise Loaded again with no
        // Unloaded in between); without this, a second Loaded would stack a duplicate subscription the one
        // matching Unloaded can't fully remove.
        Loaded += (_, _) =>
        {
            if (Application.Current is not { } app) return;
            app.RequestedThemeChanged -= OnThemeChanged;
            app.RequestedThemeChanged += OnThemeChanged;
        };
        Unloaded += (_, _) => { if (Application.Current != null) Application.Current.RequestedThemeChanged -= OnThemeChanged; };
    }

    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e) => UpdateColors();

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == nameof(IsEnabled)) Opacity = IsEnabled ? 1 : 0.35;
    }

    private void UpdateColors()
    {
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        // Light theme uses Tertiary (#1A6B50), NOT Primary (#1FAE82), for this active-icon foreground --
        // consistency pass 2026-10-27: Primary-as-foreground-on-the-light-card-background (this toolbar's own
        // bg) measures ~2.8:1, under even the 3:1 UI-component floor, because the brand mint's luminance is
        // high regardless of how saturated it looks. Dark theme is unaffected: PrimaryDark-as-foreground on
        // this app's near-black dark surfaces already clears >9:1, so it's untouched. See Colors.xaml's own
        // remarks on Tertiary for the full reasoning.
        Color color = IsActive
            ? Color.FromArgb(dark ? "#27E3A3" : "#1A6B50") // matches Resources/Styles/Colors.xaml's PrimaryDark/Tertiary
            : Color.FromArgb(dark ? "#9AA6A0" : "#5B6560"); // matches TextSecondaryDark/TextSecondaryLight
        _icon.TextColor = color;
        _caption.TextColor = color;
        _caption.FontAttributes = IsActive ? FontAttributes.Bold : FontAttributes.None;
    }
}
