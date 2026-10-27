using Microsoft.Maui.Controls.Shapes;

namespace LogoExtract.Views;

/// <summary>A short bottom-anchored message, replacing the native Android Toast for "press Back again to
/// exit" (added 2026-10-27, same shape as PdfReader's own <c>Dialogs.Toast</c> -- bottom bubble, rounded,
/// auto-dismiss ~2.2s). LogoExtract had no toast helper at all before this; unlike PdfReader's version (which
/// hardcodes one grey/ink pair with no dark-theme variant), this one resolves THIS app's own themed tokens
/// against the theme active at the moment it's shown -- a toast that only lives ~2.2s has no practical need to
/// react to a theme flip mid-display (unlike a persistent self-painting control such as ToolButton, which does
/// -- see mobile-app-design.md §8 gotcha #5), so a one-time resource lookup here is enough, not a
/// RequestedThemeChanged subscription.</summary>
public static class Toast
{
    /// <summary><paramref name="bottomMargin"/> must clear whatever fixed bottom bar/toolbar the host page
    /// docks (ad banner + CTA pill, bottom toolbar, ...) -- there is no shared height to assume across pages,
    /// so callers pass their own page's own value.</summary>
    public static void ShowToast(this Page host, string text, double bottomMargin = 96)
    {
        if (host is not ContentPage { Content: Grid root }) return;

        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        Color card = ResourceColor(dark ? "CardBackgroundDark" : "CardBackgroundLight", dark ? Color.FromArgb("#1C2023") : Colors.White);
        Color text_ = ResourceColor(dark ? "TextPrimaryDark" : "TextPrimaryLight", dark ? Color.FromArgb("#F2F4F3") : Color.FromArgb("#15181A"));
        Color border = ResourceColor(dark ? "BorderDark" : "BorderLight", dark ? Color.FromArgb("#262A2D") : Color.FromArgb("#E3E8E6"));

        var bubble = new Border
        {
            Content = new Label { Text = text, FontSize = 13.5, TextColor = text_, HorizontalTextAlignment = TextAlignment.Center },
            BackgroundColor = card,
            Stroke = border,
            StrokeThickness = 1,
            Padding = new Thickness(18, 11),
            Margin = new Thickness(30, 0, 30, bottomMargin),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.End,
            ZIndex = 900,
            InputTransparent = true, // never blocks the real Back-press-to-exit tap/gesture underneath it
            StrokeShape = new RoundRectangle { CornerRadius = 22 },
            Shadow = new Shadow { Brush = Colors.Black, Opacity = 0.25f, Radius = 12, Offset = new Point(0, 4) },
        };
        Grid.SetRowSpan(bubble, Math.Max(1, root.RowDefinitions.Count));
        Grid.SetColumnSpan(bubble, Math.Max(1, root.ColumnDefinitions.Count));
        root.Add(bubble);
        host.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(2200), () => root.Remove(bubble));
    }

    private static Color ResourceColor(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is Color c ? c : fallback;
}
