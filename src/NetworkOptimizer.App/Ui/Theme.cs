using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NetworkOptimizer.App.Ui;

public static class Theme
{
    // Discord-like surfaces + Postman run accent
    public static readonly Color Chrome = Color.FromRgb(0x1E, 0x1F, 0x22);
    public static readonly Color Sidebar = Color.FromRgb(0x2B, 0x2D, 0x31);
    public static readonly Color Workspace = Color.FromRgb(0x31, 0x33, 0x38);
    public static readonly Color Panel = Color.FromRgb(0x2B, 0x2D, 0x31);
    public static readonly Color Input = Color.FromRgb(0x38, 0x3A, 0x40);
    public static readonly Color ConsoleBg = Color.FromRgb(0x1E, 0x1F, 0x22);
    public static readonly Color Line = Color.FromRgb(0x3F, 0x41, 0x47);
    public static readonly Color Blurple = Color.FromRgb(0x58, 0x65, 0xF2);
    public static readonly Color BlurpleHover = Color.FromRgb(0x47, 0x54, 0xC4);
    public static readonly Color Run = Color.FromRgb(0xFF, 0x6C, 0x37);
    public static readonly Color RunHover = Color.FromRgb(0xF0, 0x5A, 0x24);
    public static readonly Color Success = Color.FromRgb(0x23, 0xA5, 0x59);
    public static readonly Color Danger = Color.FromRgb(0xF2, 0x3F, 0x43);
    public static readonly Color Warn = Color.FromRgb(0xF0, 0xB2, 0x32);
    public static readonly Color Text = Color.FromRgb(0xF2, 0xF3, 0xF5);
    public static readonly Color Muted = Color.FromRgb(0xB5, 0xBA, 0xC1);
    public static readonly Color Dim = Color.FromRgb(0x80, 0x84, 0x8E);

    public static readonly FontFamily UiFont = new("Segoe UI, Segoe UI Variable, Arial");
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");

    public static SolidColorBrush Brush(Color c)
    {
        var brush = new SolidColorBrush(c);
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    public static Color TagColor(string tag) => tag.ToUpperInvariant() switch
    {
        "OK" or "SUCCESS" => Success,
        "FAIL" or "STOP" or "ERR" => Danger,
        "APPLY" or "SNAP" => Blurple,
        "TEST" or "DISC" or "GEN" => Warn,
        "ROLL" or "WAIT" or "SKIP" => Dim,
        _ => Muted
    };

    public static TextBlock TextBlock(string text, double size, FontWeight weight, Color color, Thickness? margin = null) =>
        new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = Brush(color),
            FontFamily = UiFont,
            Margin = margin ?? new Thickness(0),
            TextWrapping = TextWrapping.Wrap
        };

    public static Border RoundButton(string text, Color bg, Color fg, RoutedEventHandler click, double minWidth = 160, double radius = 4)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(fg),
            FontFamily = UiFont,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var border = new Border
        {
            Background = Brush(bg),
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(16, 9, 16, 9),
            MinWidth = minWidth,
            Cursor = Cursors.Hand,
            Child = label,
            Margin = new Thickness(0, 0, 8, 0)
        };
        border.MouseLeftButtonUp += (_, _) => click(border, new RoutedEventArgs());
        border.MouseEnter += (_, _) => border.Opacity = 0.88;
        border.MouseLeave += (_, _) => border.Opacity = 1;
        return border;
    }

    public static Border GhostButton(string text, RoutedEventHandler click, double minWidth = 140)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Text),
            FontFamily = UiFont,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var border = new Border
        {
            Background = Brush(Input),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 8, 14, 8),
            MinWidth = minWidth,
            Cursor = Cursors.Hand,
            Child = label,
            Margin = new Thickness(0, 0, 8, 0)
        };
        border.MouseLeftButtonUp += (_, _) => click(border, new RoutedEventArgs());
        border.MouseEnter += (_, _) => border.Background = Brush(Line);
        border.MouseLeave += (_, _) => border.Background = Brush(Input);
        return border;
    }

    public static Border ChromeButton(string glyph, RoutedEventHandler click, bool danger = false)
    {
        var label = new TextBlock
        {
            Text = glyph,
            FontSize = 12,
            Foreground = Brush(Muted),
            FontFamily = UiFont,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var border = new Border
        {
            Width = 46,
            Height = 48,
            Background = Brushes.Transparent,
            Child = label,
            Cursor = Cursors.Hand
        };
        border.MouseEnter += (_, _) =>
        {
            border.Background = Brush(danger ? Danger : Input);
            label.Foreground = Brush(Text);
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = Brushes.Transparent;
            label.Foreground = Brush(Muted);
        };
        border.MouseLeftButtonUp += (_, _) => click(border, new RoutedEventArgs());
        return border;
    }

    public static UIElement StatusPill(string name, bool? ok, string? extra = null)
    {
        var color = ok is null ? Dim : ok.Value ? Success : Danger;
        var state = ok is null ? "checking" : ok.Value ? "live" : "down";
        var grid = new Grid { Margin = new Thickness(0, 0, 16, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brush(color),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = name.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Dim),
            FontFamily = UiFont
        });
        label.Children.Add(new TextBlock
        {
            Text = extra is null ? state : $"{state}  ·  {extra}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Text),
            FontFamily = UiFont
        });
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(label, 1);
        grid.Children.Add(dot);
        grid.Children.Add(label);
        return grid;
    }

    public static Border Card(UIElement child) => new()
    {
        Background = Brush(Panel),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        Margin = new Thickness(0, 0, 0, 12),
        Child = child
    };
}
