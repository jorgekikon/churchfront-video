using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CueVideo.App;

/// <summary>Colors and the small set of building blocks every screen uses, in the dark look of
/// the Mac app (docs/*.png). Everything is made in code, so this is the whole design system.</summary>
public static class T
{
    public static SolidColorBrush Hex(string h)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush Bg = Hex("#1C1C1E");
    public static readonly SolidColorBrush Surface = Hex("#232325");
    public static readonly SolidColorBrush Toolbar = Hex("#1F1F21");
    public static readonly SolidColorBrush SurfaceRaised = Hex("#2C2C2F");
    public static readonly SolidColorBrush SurfaceSunken = Hex("#121213");
    public static readonly SolidColorBrush Line = Hex("#343437");
    public static readonly SolidColorBrush LineStrong = Hex("#4A4A4E");
    public static readonly SolidColorBrush Ink = Hex("#F2F2F4");
    public static readonly SolidColorBrush InkMuted = Hex("#A3A3A9");
    public static readonly SolidColorBrush InkSubtle = Hex("#6E6E74");
    public static readonly SolidColorBrush Accent = Hex("#CFE3FA");
    public static readonly SolidColorBrush AccentStrong = Hex("#5AA2F7");
    public static readonly SolidColorBrush AccentSoft = Hex("#24364D");
    public static readonly SolidColorBrush OnAction = Hex("#111113");
    public static readonly SolidColorBrush Success = Hex("#3CCBA8");
    public static readonly SolidColorBrush SuccessSoft = Hex("#173A33");
    public static readonly SolidColorBrush Warning = Hex("#F2B33D");
    public static readonly SolidColorBrush WarningSoft = Hex("#3D3016");
    public static readonly SolidColorBrush Danger = Hex("#E5483B");
    public static readonly SolidColorBrush DangerSoft = Hex("#43201D");
    /// <summary>Tally: program red, preview green, as on every switcher panel.</summary>
    public static readonly SolidColorBrush Program = Hex("#E5483B");
    public static readonly SolidColorBrush Preview = Hex("#5CB85C");
    public static readonly SolidColorBrush Transparent = Brushes.Transparent;

    public static readonly FontFamily Sans = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    /// <summary>A text, in the language in use (<see cref="L"/>). Names people typed have no translation, so they show as typed.</summary>
    public static TextBlock Text(string s, double size = 13, FontWeight? weight = null, Brush? fg = null, bool wrap = false) => Raw(L.Tr(s), size, weight, fg, wrap);

    /// <summary>A text shown exactly as given, never translated.</summary>
    public static TextBlock Raw(string s, double size = 13, FontWeight? weight = null, Brush? fg = null, bool wrap = false) => new()
    {
        Text = s, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Foreground = fg ?? Ink, FontFamily = Sans,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, HorizontalAlignment = wrap ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>ALL CAPS panel label.</summary>
    public static TextBlock Caps(string s, double size = 11, Brush? fg = null) => Raw(Spaced(L.Tr(s).ToUpperInvariant()), size, FontWeights.Bold, fg ?? InkSubtle);

    /// <summary>A little letter spacing for caps labels (WPF has no tracking, so thin spaces).</summary>
    public static string Spaced(string s) => string.Join(" ", s.ToCharArray());

    public static Border Card(UIElement child, double pad = 16) => new()
    {
        Child = child, Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(pad),
    };

    public static Border Badge(string text, Brush fg, Brush bg) => new()
    {
        Child = Raw(L.Tr(text).ToUpperInvariant(), 10, FontWeights.Bold, fg),
        Background = bg, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2), VerticalAlignment = VerticalAlignment.Center,
    };

    public static System.Windows.Shapes.Rectangle Hairline(bool vertical = false) => new()
    {
        Fill = Line, Width = vertical ? 1 : double.NaN, Height = vertical ? double.NaN : 1,
    };

    // MARK: Buttons

    private static ControlTemplate? _flat;
    /// <summary>A button that is just its Background and BorderBrush: no Windows chrome.
    /// Hover lightens it a little, pressing dims it.</summary>
    public static ControlTemplate FlatTemplate
    {
        get
        {
            if (_flat != null) return _flat;
            var t = new ControlTemplate(typeof(ButtonBase));
            var border = new FrameworkElementFactory(typeof(Border), "b");
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            border.AppendChild(cp);
            t.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88));
            var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            press.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7));
            var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35));
            t.Triggers.Add(hover); t.Triggers.Add(press); t.Triggers.Add(off);
            _flat = t;
            return t;
        }
    }

    public static Button Button(object content, Action? click, Brush? bg = null, Brush? border = null, double w = double.NaN, double h = double.NaN,
                                double radius = 8, Thickness? pad = null, string? tip = null)
    {
        var b = new Button
        {
            Content = content, Template = FlatTemplate, Background = bg ?? SurfaceRaised, BorderBrush = border ?? Line, BorderThickness = new Thickness(1),
            Width = w, Height = h, Padding = pad ?? new Thickness(12, 4, 12, 4), Foreground = Ink, Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
            FontFamily = Sans,
        };
        if (tip != null) b.ToolTip = L.Tr(tip);
        SetRadius(b, radius);
        if (click != null) b.Click += (_, _) => click();
        return b;
    }

    /// <summary>The template's corner radius, set after it applies.</summary>
    public static void SetRadius(Control c, double r)
    {
        c.Loaded += (_, _) => { if (c.Template?.FindName("b", c) is Border bd) bd.CornerRadius = new CornerRadius(r); };
    }

    /// <summary>A small secondary button with a text label.</summary>
    public static Button Small(string title, Action click, string? tip = null, bool danger = false) =>
        Button(Text(title, 12, FontWeights.SemiBold, danger ? Danger : Ink), click, SurfaceRaised, LineStrong, h: 30, radius: 6, tip: tip);

    public static Button Primary(string title, Action click, string? tip = null) =>
        Button(Text(title, 12, FontWeights.SemiBold, OnAction), click, Accent, Accent, h: 30, radius: 6, tip: tip);

    /// <summary>A backlit panel button, like the keys on an ATEM control surface.</summary>
    public static Button Panel(string title, string? subtitle, Brush? lit, Action click, double w = 76, double h = 52, double size = 14, string? tip = null)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var fg = lit != null ? OnAction : InkMuted;
        stack.Children.Add(new TextBlock { Text = L.Tr(title), FontSize = size, FontWeight = FontWeights.Bold, Foreground = fg, FontFamily = Sans, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        if (subtitle != null) stack.Children.Add(new TextBlock { Text = L.Tr(subtitle), FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = fg, Opacity = 0.8, FontFamily = Sans, HorizontalAlignment = HorizontalAlignment.Center });
        var b = Button(stack, click, lit ?? SurfaceRaised, lit != null ? new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)) : Line, w, h, 8, new Thickness(4), tip);
        if (lit != null && lit is SolidColorBrush sb) b.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = sb.Color, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 };
        return b;
    }

    /// <summary>A panel button whose words and light change in place (never rebuilt, so a click
    /// can't land on a button that was just replaced).</summary>
    public sealed class LitButton
    {
        public readonly Button Button;
        private readonly TextBlock _title, _sub;
        private (string, string?, Brush?)? _last;
        public LitButton(Action click, double w, double h, double size, string? tip = null)
        {
            _title = new TextBlock { FontSize = size, FontWeight = FontWeights.Bold, FontFamily = Sans, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _sub = new TextBlock { FontSize = 9, FontWeight = FontWeights.SemiBold, Opacity = 0.8, FontFamily = Sans, HorizontalAlignment = HorizontalAlignment.Center };
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            // The title shrinks to fit rather than being cut off (long labels, other languages).
            _title.TextTrimming = TextTrimming.None;
            stack.Children.Add(new Viewbox { Child = _title, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(_sub);
            Button = T.Button(stack, click, SurfaceRaised, Line, w, h, 8, new Thickness(4), tip);
            Button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        }
        public void Set(string title, string? subtitle, Brush? lit, string? tip = null)
        {
            if (tip != null) Button.ToolTip = L.Tr(tip);
            title = L.Tr(title); subtitle = subtitle == null ? null : L.Tr(subtitle);
            if (_last is var (a, b, c) && a == title && b == subtitle && c == lit) return;
            _last = (title, subtitle, lit);
            _title.Text = title; _sub.Text = subtitle ?? ""; _sub.Visibility = subtitle == null ? Visibility.Collapsed : Visibility.Visible;
            _title.Foreground = _sub.Foreground = lit != null ? OnAction : InkMuted;
            Button.Background = lit ?? SurfaceRaised;
            Button.BorderBrush = lit != null ? new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)) : Line;
            Button.Effect = lit is SolidColorBrush sb ? new System.Windows.Media.Effects.DropShadowEffect { Color = sb.Color, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 } : null;
        }
    }

    /// <summary>A button that shows a value and opens a menu of choices.</summary>
    public static Button Picker(string current, IEnumerable<(string Label, Action Pick)> choices, double w = 200, string? tip = null)
    {
        var g = new DockPanel { LastChildFill = true };
        var arrow = Text("▾", 11, null, InkMuted);
        DockPanel.SetDock(arrow, Dock.Right);
        arrow.Margin = new Thickness(6, 0, 0, 0);
        g.Children.Add(arrow);
        g.Children.Add(Text(current, 13, FontWeights.SemiBold));
        var b = Button(g, null, SurfaceRaised, Line, w, 30, 6, new Thickness(10, 0, 8, 0), tip);
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var list = choices.ToList();
        b.Click += (_, _) =>
        {
            var m = Menu();
            foreach (var (label, pick) in list) m.Items.Add(Item(label, pick));
            m.PlacementTarget = b; m.Placement = PlacementMode.Bottom; m.IsOpen = true;
        };
        return b;
    }

    public static ContextMenu Menu() => new() { Background = SurfaceRaised, Foreground = Ink, BorderBrush = LineStrong, FontFamily = Sans, FontSize = 13 };
    public static MenuItem Item(string label, Action pick, bool danger = false)
    {
        var i = new MenuItem { Header = L.Tr(label), Foreground = danger ? Danger : Ink, Background = SurfaceRaised };
        i.Click += (_, _) => pick();
        return i;
    }

    // MARK: Fields

    public static TextBox Field(string text, Action<string> commit, double w = 200, string? placeholder = null)
    {
        var tb = new TextBox
        {
            Text = text, Width = w, Height = 30, Background = SurfaceSunken, Foreground = Ink, BorderBrush = Line, CaretBrush = Ink,
            Padding = new Thickness(6, 4, 6, 4), FontSize = 13, FontFamily = Sans, VerticalContentAlignment = VerticalAlignment.Center,
            SelectionBrush = AccentStrong,
        };
        var last = text;
        void Commit() { if (tb.Text != last) { last = tb.Text; commit(tb.Text); } }
        tb.LostKeyboardFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); e.Handled = true; }
            if (e.Key == Key.Escape) { tb.Text = last; Keyboard.ClearFocus(); e.Handled = true; }
        };
        if (placeholder != null) Placeholder(tb, placeholder);
        return tb;
    }

    /// <summary>Grey hint text in an empty field.</summary>
    public static void Placeholder(TextBox tb, string hint)
    {
        var brush = new VisualBrush(new TextBlock { Text = L.Tr(hint), Foreground = InkSubtle, FontSize = 13, FontFamily = Sans, Margin = new Thickness(2, 0, 0, 0) })
        { AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Center, Stretch = Stretch.None };
        ((TextBlock)brush.Visual).Padding = new Thickness(6, 0, 0, 0);
        void Update() => tb.Background = tb.Text.Length == 0 && !tb.IsKeyboardFocused ? brush : SurfaceSunken;
        tb.TextChanged += (_, _) => Update();
        tb.GotKeyboardFocus += (_, _) => Update();
        tb.LostKeyboardFocus += (_, _) => Update();
        tb.Loaded += (_, _) => Update();
    }

    public static CheckBox Check(string label, bool on, Action<bool> changed)
    {
        var c = new CheckBox { Content = Text(label, 13), IsChecked = on, Foreground = Ink, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false };
        // Checked/Unchecked, not Click: a screen reader or UI Automation ticks the box without clicking it.
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    // MARK: Layout

    public static StackPanel Row(double gap, params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] is FrameworkElement fe && i > 0) fe.Margin = new Thickness(gap, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            p.Children.Add(items[i]);
        }
        return p;
    }

    public static StackPanel Column(double gap, params UIElement[] items)
    {
        var p = new StackPanel();
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] is FrameworkElement fe && i > 0) fe.Margin = new Thickness(fe.Margin.Left, gap, fe.Margin.Right, fe.Margin.Bottom);
            p.Children.Add(items[i]);
        }
        return p;
    }

    public static T2 With<T2>(this T2 e, Action<T2> f) where T2 : UIElement { f(e); return e; }
    public static T2 Margin<T2>(this T2 e, double l, double t, double r, double b) where T2 : FrameworkElement { e.Margin = new Thickness(l, t, r, b); return e; }

    /// <summary>The numbered step label: "① PICK WHAT'S NEXT".</summary>
    public static StackPanel Step(int n, string text)
    {
        var circle = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Accent,
            Child = new TextBlock { Text = n.ToString(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = OnAction, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        return Row(8, circle, Caps(text));
    }
}
