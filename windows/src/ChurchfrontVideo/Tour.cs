using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CueVideo.App;

/// <summary>
/// A guided tour of the Operator screen for a first-time volunteer: the screen is dimmed except
/// one part at a time, with a card explaining it, in the order the screen is used (pick what's
/// next, frame it, take it live). It opens by itself the first time the app runs with no setup
/// file, and from the Tour button on Operator. While it's open, every click and key goes to the
/// tour, so nothing reaches the switcher or the cameras by accident (Space and Return included).
/// Parts of the screen are found by the names they were marked with (<see cref="Mark"/>).
/// </summary>
public sealed class Tour
{
    private static Booth B => Booth.Shared;

    // MARK: Marks: parts of the screen the tour can point at. Views mark what they build.

    private static readonly Dictionary<string, WeakReference<FrameworkElement>> Marks = new();

    public static TE Mark<TE>(TE e, string name) where TE : FrameworkElement
    {
        Marks[name] = new WeakReference<FrameworkElement>(e);
        return e;
    }

    private static FrameworkElement? Find(string name) => Marks.TryGetValue(name, out var w) && w.TryGetTarget(out var e) ? e : null;

    // MARK: Steps

    private sealed record Step(string Title, string Body, string[] Parts, bool SkipIfMissing = false);

    private static List<Step> Steps() => new()
    {
        new("Welcome to Churchfront Video",
            "This short tour shows the Operator screen, where you choose what the congregation and the livestream see, and aim the cameras. It takes about two minutes.\n\n" +
            "Use Next and Back (or the arrow keys). Press Esc to leave at any time. While the tour is open, nothing you press reaches the switcher or the cameras.",
            Array.Empty<string>()),
        new("LIVE: what everyone sees",
            "The red picture is on the screens and the livestream right now. Everything you do is about getting the next picture ready without disturbing this one.\n\n" +
            "The camera controls never move the camera that's on Live.",
            new[] { "live" }),
        new("NEXT: what goes out next",
            "The green picture is ready to go, but nobody sees it yet. Get it looking right here first, then take it live.",
            new[] { "next" }),
        new("Step 1: pick what's next",
            "Each button is a camera or another source, such as the slides. Click one, or press its number key (1 to 9, then 0), to put it on Next. Its border turns green; the one on Live has a red border.\n\n" +
            "Shift plus a number puts a source straight on Live, skipping Next. Avoid it unless you really mean it.",
            new[] { "sources" }),
        new("The camera on Next",
            "The camera that's on Next is named here. The shots and the camera controls below always act on it.\n\n" +
            "Exposure opens its brightness and colour settings, for when its picture looks too dark, too bright, or the wrong colour.",
            new[] { "camera" }),
        new("Saved shots",
            "Each tile is a position saved for the camera on Next, such as Pulpit or Wide. Click a tile, or press the letter shown on it (Q to G), to send the camera there.\n\n" +
            "The tabs show another camera's shots, the arrows (or the [ and ] keys) turn the page, and the + tile saves where the camera is pointing now. If that camera is on Live, the app asks before moving it.",
            new[] { "shots" }),
        new("Step 2: frame the shot",
            "Drag the joystick to aim the camera on Next, or hold the arrow keys. The further you drag, the faster it moves. Slide the zoom up to zoom in and down to zoom out. Focus sharpens the picture once.\n\n" +
            "Small, slow moves look best. Make them on Next, before the shot is live.",
            new[] { "frame" }),
        new("Auto tracking",
            "Track makes the camera follow the person nearest the middle of the picture. You can also click a person on the Next picture to follow them.\n\n" +
            "While it's tracking you can choose how much of the person to show, and adjust its speed and the space above their head. Moving the joystick, picking a shot, or pressing Esc stops it.",
            new[] { "track" }),
        new("Camera speed",
            "How fast the joystick, zoom and arrow keys move the camera. Slow it down for small, smooth adjustments on a close-up; speed it up to get across the stage quickly.",
            new[] { "speed" }),
        new("Step 3: take it live",
            "When Next looks right, take it live:\n" +
            "•  Fade (or Return) blends smoothly from Live to Next. Use it most of the time.\n" +
            "•  Cut (or Space) switches instantly, for example to follow fast action.\n\n" +
            "What was on Live moves to Next, so you're ready to set up the following shot.",
            new[] { "take" }),
        new("Black and graphics",
            "Black fades everything to black; press it again to fade back up. It's for the very start and end of a service.\n\n" +
            "Any buttons beside it put graphics, such as lower thirds with a speaker's name, on or off air. They show ON AIR in red while the graphic is showing.",
            new[] { "keys" }),
        new("Screens",
            "These buttons change what other screens in the building show, such as the lobby TVs or a side screen. A button lights up while the screens show what it sets.",
            new[] { "screens" }, SkipIfMissing: true),
        new("Is it connected?",
            "Online in green means the switcher is connected and listening. If it says Offline or Connecting, nothing you press reaches it; let the person in charge know.",
            new[] { "connection" }),
        new("Recording, streaming and power",
            "Record and Stream start and stop the switcher's recording and the livestream. Each one asks before it changes anything.\n\n" +
            "Before a service, a Wake button may appear here when cameras are in standby: click it to power them on. The small pictures show Live and Next while you're on another page.",
            new[] { "topright" }),
        new("Engineer",
            "Engineer has Status (connections and the app's version) and the setup pages for the person in charge. Most of those pages need a PIN; Status is always open. Operator brings you back here.",
            new[] { "views" }),
        new("The rhythm of a service",
            "1.  Pick what's next.\n2.  Frame it on Next.\n3.  Fade (or Cut) to take it live.\nThen do it again.\n\n" +
            "Look at Next before you take it, and leave the Live camera alone. If this computer is brand new, the person in charge sets up the switcher and cameras first, in Engineer → Setup.\n\n" +
            "You can watch this tour again at any time with the Tour button at the top.",
            new[] { "tour" }),
    };

    // MARK: The overlay

    private readonly Grid _host;
    private readonly Canvas _layer = new() { Visibility = Visibility.Collapsed, Background = Brushes.Transparent, Focusable = true };
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _ring = new() { Stroke = T.Accent, StrokeThickness = 3, RadiusX = 10, RadiusY = 10, IsHitTestVisible = false };
    private readonly Border _card;
    private readonly TextBlock _count, _title, _body;
    private readonly Button _back, _next;
    private readonly TextBlock _nextText;
    private List<Step> _steps = new();
    private int _at;

    public bool IsOpen => _layer.Visibility == Visibility.Visible;

    public Tour(Grid host)
    {
        _host = host;
        _count = T.Text("", 11, FontWeights.SemiBold, T.AccentStrong);
        _title = T.Text("", 18, FontWeights.Bold, T.Ink, wrap: true);
        _body = T.Text("", 13, null, T.InkMuted, wrap: true);
        _body.LineHeight = 19;
        _nextText = T.Text("Next", 13, FontWeights.SemiBold, T.OnAction);
        _next = T.Button(_nextText, () => Go(_at + 1), T.Accent, T.Accent, h: 32, radius: 6, pad: new Thickness(16, 0, 16, 0));
        _back = T.Small("Back", () => Go(_at - 1));
        var end = T.Button(T.Text("End tour", 12, null, T.InkMuted), Close, T.Transparent, T.Transparent, h: 32, radius: 6, pad: new Thickness(8, 0, 8, 0));
        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_next, Dock.Right); DockPanel.SetDock(_back, Dock.Right);
        _back.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(end); buttons.Children.Add(_next); buttons.Children.Add(_back);
        _card = new Border
        {
            Width = 400, Background = T.Surface, BorderBrush = T.Accent, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 16, 20, 14),
            Child = T.Column(6, _count, _title, _body, buttons),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
        };
        System.Windows.Automation.AutomationProperties.SetName(_card, "Guided tour");
        _layer.Children.Add(_dim); _layer.Children.Add(_ring); _layer.Children.Add(_card);
        // Clicks on the dimmed screen are swallowed, so nothing behind the tour is pressed.
        _layer.MouseDown += (_, e) => e.Handled = true;
        Panel.SetZIndex(_layer, 1000);
        host.Children.Add(_layer);
        host.SizeChanged += (_, _) => { if (IsOpen) Place(); };
    }

    /// <summary>Opens the tour on the Operator screen, from the first step.</summary>
    public void Open()
    {
        B.View = AppView.Operate;
        _steps = Steps();
        _layer.Visibility = Visibility.Visible;
        _layer.Focus();
        // Let the Operator screen lay out before pointing at it.
        _layer.Dispatcher.BeginInvoke(() => Go(0), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void Close() => _layer.Visibility = Visibility.Collapsed;

    /// <summary>Keys while the tour is open: arrows and Return move, Esc ends. Every key is kept
    /// from the rest of the app, so Space and Return can't cut or fade behind the tour.</summary>
    public bool Key(KeyEventArgs e)
    {
        if (!IsOpen) return false;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape: Close(); break;
            case System.Windows.Input.Key.Right: case System.Windows.Input.Key.Enter: Go(_at + 1); break;
            case System.Windows.Input.Key.Left: Go(_at - 1); break;
        }
        return true;
    }

    private bool Present(Step s) => s.Parts.Any(p => Find(p) is FrameworkElement f && f.IsVisible && f.ActualWidth > 0);

    private void Go(int i)
    {
        if (i >= _steps.Count) { Close(); return; }
        if (i < 0) i = 0;
        // Steps about something this booth doesn't have (no screen buttons) are skipped.
        var dir = i >= _at ? 1 : -1;
        while (i > 0 && i < _steps.Count - 1 && _steps[i].SkipIfMissing && !Present(_steps[i])) i += dir;
        _at = Math.Clamp(i, 0, _steps.Count - 1);
        var s = _steps[_at];
        var shown = _steps.Where(x => !x.SkipIfMissing || Present(x)).ToList();
        _count.Text = $"STEP {shown.IndexOf(s) + 1} OF {shown.Count}";
        _title.Text = s.Title;
        _body.Text = s.Body;
        _back.Visibility = _at == 0 ? Visibility.Collapsed : Visibility.Visible;
        _nextText.Text = _at == 0 ? "Start the tour" : _at == _steps.Count - 1 ? "Finish" : "Next";
        Place();
    }

    /// <summary>The part of the screen the step is about, in the overlay's coordinates.</summary>
    private Rect? Target()
    {
        if (_at >= _steps.Count) return null;
        Rect? all = null;
        foreach (var p in _steps[_at].Parts)
        {
            if (Find(p) is not FrameworkElement f || !f.IsVisible || f.ActualWidth < 1 || !_host.IsAncestorOf(f)) continue;
            try
            {
                var r = f.TransformToAncestor(_host).TransformBounds(new Rect(0, 0, f.ActualWidth, f.ActualHeight));
                all = all is Rect a ? Rect.Union(a, r) : r;
            }
            catch (InvalidOperationException) { }
        }
        return all;
    }

    /// <summary>Called a few times a second while open: the screen may have been redrawn.</summary>
    public void Refresh() { if (IsOpen) Place(); }

    private void Place()
    {
        double w = _host.ActualWidth, h = _host.ActualHeight;
        if (w < 1 || h < 1) return;
        _layer.Width = w; _layer.Height = h;
        var full = new RectangleGeometry(new Rect(0, 0, w, h));
        _card.Measure(new Size(_card.Width, double.PositiveInfinity));
        double cw = _card.Width, ch = _card.DesiredSize.Height, gap = 16;
        if (Target() is not Rect raw)
        {
            _dim.Data = full;
            _ring.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(_card, (w - cw) / 2); Canvas.SetTop(_card, Math.Max(16, (h - ch) / 2));
            return;
        }
        var r = new Rect(raw.X - 8, raw.Y - 8, raw.Width + 16, raw.Height + 16);
        r.Intersect(new Rect(2, 2, w - 4, h - 4));
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(r, 10, 10));
        _ring.Visibility = Visibility.Visible;
        _ring.Width = r.Width; _ring.Height = r.Height;
        Canvas.SetLeft(_ring, r.X); Canvas.SetTop(_ring, r.Y);
        // Beside the part if there's room (below, above, right, left), else in a corner away from it.
        double x, y;
        if (r.Bottom + gap + ch <= h - 8) { x = r.X + r.Width / 2 - cw / 2; y = r.Bottom + gap; }
        else if (r.Top - gap - ch >= 8) { x = r.X + r.Width / 2 - cw / 2; y = r.Top - gap - ch; }
        else if (r.Right + gap + cw <= w - 8) { x = r.Right + gap; y = r.Y + r.Height / 2 - ch / 2; }
        else if (r.Left - gap - cw >= 8) { x = r.Left - gap - cw; y = r.Y + r.Height / 2 - ch / 2; }
        else { x = r.X + r.Width / 2 < w / 2 ? w - cw - 24 : 24; y = r.Y + r.Height / 2 < h / 2 ? h - ch - 24 : 24; }
        Canvas.SetLeft(_card, Math.Clamp(x, 8, Math.Max(8, w - cw - 8)));
        Canvas.SetTop(_card, Math.Clamp(y, 8, Math.Max(8, h - ch - 8)));
    }
}
