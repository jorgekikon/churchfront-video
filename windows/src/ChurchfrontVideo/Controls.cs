using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CueVideo.App;

/// <summary>
/// A screen that rebuilds itself only when what it shows changes shape (cameras added, a source
/// renamed), and otherwise just updates colors and words in place, many times a second.
/// Build() registers those in-place updates with <see cref="Live"/>.
/// </summary>
public abstract class LiveView : Border
{
    private readonly List<Action> _updaters = new();
    private string? _key;

    /// <summary>A string that changes when the screen needs building again.</summary>
    protected abstract string StructureKey();
    /// <summary>Makes the screen. Return the root element.</summary>
    protected abstract UIElement Build();

    /// <summary>Registers an update to run now and on every refresh.</summary>
    protected void Live(Action a) { _updaters.Add(a); a(); }

    /// <summary>Don't rebuild while someone is typing in this screen: the field would vanish under them.</summary>
    protected virtual bool HoldRebuild => IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox;

    public void Refresh(bool force = false)
    {
        var key = StructureKey();
        if (force || key != _key)
        {
            if (!force && _key != null && HoldRebuild) { RunUpdaters(); return; }
            _key = key;
            _updaters.Clear();
            Teardown();
            Child = Build();
        }
        else RunUpdaters();
    }

    private void RunUpdaters() { foreach (var u in _updaters) { try { u(); } catch (Exception e) { Console.Error.WriteLine(e); } } }

    /// <summary>Called before a rebuild and when the screen goes away: let go of camera pictures.</summary>
    public virtual void Teardown() { }
}

/// <summary>The pan and tilt joystick: drag the knob, let go to stop. Up is positive y.</summary>
public sealed class Joystick : FrameworkElement
{
    private Point _knob;       // -1 to 1 each way
    private bool _dragging;
    public Action<double, double>? OnMove;
    public Action? OnEnd;

    public Joystick() { Cursor = Cursors.Hand; Focusable = false; }

    protected override void OnRender(DrawingContext dc)
    {
        var r = Math.Min(ActualWidth, ActualHeight) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(T.SurfaceSunken, new Pen(T.Line, 1), c, r - 1, r - 1);
        var dash = new Pen(T.LineStrong, 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
        dc.DrawEllipse(null, dash, c, r * 0.55, r * 0.55);
        // Direction marks.
        var mark = new Pen(T.InkSubtle, 1.5);
        double m = r - 12, s = 4;
        dc.DrawLine(mark, new Point(c.X - s, c.Y - m + s), new Point(c.X, c.Y - m)); dc.DrawLine(mark, new Point(c.X, c.Y - m), new Point(c.X + s, c.Y - m + s));
        dc.DrawLine(mark, new Point(c.X - s, c.Y + m - s), new Point(c.X, c.Y + m)); dc.DrawLine(mark, new Point(c.X, c.Y + m), new Point(c.X + s, c.Y + m - s));
        dc.DrawLine(mark, new Point(c.X - m + s, c.Y - s), new Point(c.X - m, c.Y)); dc.DrawLine(mark, new Point(c.X - m, c.Y), new Point(c.X - m + s, c.Y + s));
        dc.DrawLine(mark, new Point(c.X + m - s, c.Y - s), new Point(c.X + m, c.Y)); dc.DrawLine(mark, new Point(c.X + m, c.Y), new Point(c.X + m - s, c.Y + s));
        var kr = r * 0.32;
        var travel = r - kr - 4;
        var kp = new Point(c.X + _knob.X * travel, c.Y - _knob.Y * travel);
        dc.DrawEllipse(_dragging ? T.LineStrong : T.SurfaceRaised, new Pen(T.LineStrong, 1), kp, kr, kr);
    }

    private void Track(Point p)
    {
        var r = Math.Min(ActualWidth, ActualHeight) / 2;
        var kr = r * 0.32;
        var travel = Math.Max(1, r - kr - 4);
        double x = (p.X - ActualWidth / 2) / travel, y = -(p.Y - ActualHeight / 2) / travel;
        var len = Math.Sqrt(x * x + y * y);
        if (len > 1) { x /= len; y /= len; }
        _knob = new Point(x, y);
        InvalidateVisual();
        OnMove?.Invoke(x, y);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled) return;
        _dragging = true;
        CaptureMouse();
        Track(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) Track(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (_dragging) ReleaseMouseCapture(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) => End();

    private void End()
    {
        if (!_dragging) return;
        _dragging = false;
        _knob = default;
        InvalidateVisual();
        OnEnd?.Invoke();
    }
}

/// <summary>The zoom rocker: drag the thumb up to zoom in, down to zoom out; it springs back.</summary>
public sealed class ZoomRocker : FrameworkElement
{
    private double _v;
    private bool _dragging;
    public Action<double>? OnZoom;

    public ZoomRocker() { Cursor = Cursors.SizeNS; Focusable = false; }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRoundedRectangle(T.SurfaceSunken, new Pen(T.Line, 1), new Rect(0.5, 0.5, w - 1, h - 1), w / 2, w / 2);
        var ft = (string s, double y) =>
        {
            var t = new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(T.Sans, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 14, T.InkSubtle, 1.0);
            dc.DrawText(t, new Point((w - t.Width) / 2, y));
        };
        ft("+", 6);
        ft("−", h - 24);
        var th = 34.0;
        var travel = (h - th) / 2 - 22;
        var y = h / 2 - _v * travel;
        dc.DrawRoundedRectangle(_dragging ? T.LineStrong : T.SurfaceRaised, new Pen(T.LineStrong, 1), new Rect(4, y - th / 2, w - 8, th), 10, 10);
    }

    private void Track(Point p)
    {
        var h = ActualHeight;
        var travel = Math.Max(1, (h - 34) / 2 - 22);
        _v = Math.Clamp(-(p.Y - h / 2) / travel, -1, 1);
        InvalidateVisual();
        OnZoom?.Invoke(_v);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { if (!IsEnabled) return; _dragging = true; CaptureMouse(); Track(e.GetPosition(this)); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) Track(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (_dragging) ReleaseMouseCapture(); }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false; _v = 0; InvalidateVisual();
        OnZoom?.Invoke(0);
    }
}

/// <summary>Small dark dialogs: a question, a name to type, a message.</summary>
public static class Dialogs
{
    internal static Window Make(string title, double w)
    {
        var win = new Window
        {
            Title = L.Tr(title), Width = w, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Background = T.Surface, Foreground = T.Ink, ShowInTaskbar = false, FontFamily = T.Sans,
            Owner = Application.Current?.MainWindow,
        };
        return win;
    }

    public static bool Confirm(string title, string message, string yes = "OK", bool danger = false)
    {
        var win = Make(title, 420);
        var ok = false;
        var buttons = T.Row(8, T.Small("Cancel", () => win.Close()), danger
            ? T.Button(T.Text(yes, 12, FontWeights.SemiBold, T.Ink), () => { ok = true; win.Close(); }, T.Danger, T.Danger, h: 30, radius: 6)
            : T.Primary(yes, () => { ok = true; win.Close(); }));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        win.Content = T.Column(12, T.Text(title, 15, FontWeights.Bold, wrap: true), T.Text(message, 13, null, T.InkMuted, wrap: true), buttons).Margin(20, 18, 20, 18);
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); if (e.Key == Key.Enter) { ok = true; win.Close(); } };
        win.ShowDialog();
        return ok;
    }

    public static void Info(string title, string message)
    {
        var win = Make(title, 420);
        var buttons = T.Row(8, T.Primary("OK", () => win.Close()));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        win.Content = T.Column(12, T.Text(title, 15, FontWeights.Bold, wrap: true), T.Text(message, 13, null, T.InkMuted, wrap: true), buttons).Margin(20, 18, 20, 18);
        win.KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) win.Close(); };
        win.ShowDialog();
    }

    /// <summary>Asks for a name. Null when cancelled.</summary>
    public static string? Prompt(string title, string message, string initial = "", string placeholder = "")
    {
        var win = Make(title, 380);
        string? result = null;
        var tb = new TextBox
        {
            Text = initial, Height = 32, Background = T.SurfaceSunken, Foreground = T.Ink, BorderBrush = T.LineStrong, CaretBrush = T.Ink,
            Padding = new Thickness(6, 4, 6, 4), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (placeholder.Length > 0) T.Placeholder(tb, placeholder);
        void Done() { result = tb.Text.Trim(); win.Close(); }
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Done(); if (e.Key == Key.Escape) win.Close(); };
        var buttons = T.Row(8, T.Small("Cancel", () => win.Close()), T.Primary("Save", Done));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        win.Content = T.Column(12, T.Text(title, 15, FontWeights.Bold, wrap: true), T.Text(message, 13, null, T.InkMuted, wrap: true), tb, buttons).Margin(20, 18, 20, 18);
        win.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        win.ShowDialog();
        return result;
    }
}
