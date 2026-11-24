using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CueATEM;

namespace CueVideo.App;

/// <summary>
/// The booth screen. Top half: the Next and Live monitors and the source row. Bottom half: saved
/// shots, camera framing (joystick, zoom, focus, speed), and taking it live (Fade, Cut, Black,
/// key buttons, routing buttons). The camera controls always drive the camera on Next.
/// </summary>
public sealed class OperatorView : LiveView
{
    private static Booth B => Booth.Shared;
    private static ATEMClient Atem => B.Atem;
    private static PTZController Ptz => B.Ptz;
    private static ConfigStore Store => B.Store;

    private readonly Pictures.Keeper _pics = new();

    public OperatorView() { Background = T.Bg; }

    private static ATEMState S => Atem.State;
    private static List<PTZCamera> Cams => Ptz.Cameras.Where(c => c.Host.Length > 0).ToList();

    private static PTZCamera? Target
    {
        get
        {
            var i = S.PreviewOf(B.Me);
            return i == null ? null : Ptz.Cameras.FirstOrDefault(c => c.AtemInput == i && c.Host.Length > 0);
        }
    }

    protected override string StructureKey()
    {
        var s = S;
        var sb = new System.Text.StringBuilder();
        sb.Append(B.Me).Append('|').Append(Store.Config.Signature().GetHashCode()).Append('|');
        foreach (var i in s.Inputs.Values.OrderBy(x => x.Id)) sb.Append(i.Id).Append(i.Long).Append(i.Short).Append(',');
        sb.Append('|').Append(Target?.Id);
        foreach (var c in Cams)
        {
            sb.Append('|').Append(c.Id).Append(PTZController.Signature(new[] { c }).GetHashCode());
            foreach (var (n, nm) in Ptz.PresetNames(c)) sb.Append(n).Append(nm);
            if (Ptz.ExposurePresets.TryGetValue(c.Id, out var ep)) sb.Append(string.Join(",", ep));
        }
        sb.Append('|').Append(s.HasRecording).Append(s.HasStreaming);
        return sb.ToString();
    }

    public override void Teardown() => _pics.Clear();

    /// <summary>Keeps the pictures running for Next, Live, the source tiles and the keys' NDI overlays.</summary>
    private void RetainPictures() =>
        _pics.Want(new[] { S.PreviewOf(B.Me), S.ProgramOf(B.Me) }.Concat(Store.VolunteerSources(S).Select(v => (int?)v.Input)),
                   Store.Config.Keys.Select(k => k.Ndi ?? ""));

    /// <summary>The camera on this input, when its picture is showing (a Canon's own, or NDI).</summary>
    private static PTZCamera? PicturedOn(int? input) =>
        input is int i && Pictures.Has(i) ? Ptz.Cameras.FirstOrDefault(c => c.AtemInput == i && c.Host.Length > 0) : null;

    /// <summary>Keys with an NDI picture (a lower third with alpha), laid over a monitor when the key
    /// is on it: on Live when it's on air, on Next when it will be after the take.</summary>
    private static IEnumerable<KeyButton> Overlays(bool live, int? input) =>
        Store.Config.Keys.Where(k => !string.IsNullOrEmpty(k.Ndi) && k.Ndi != Pictures.Ndi(input)
                                     && (k.Dsk != null || (k.Me ?? 1) - 1 == B.Me) && (live ? k.IsOn(S) : k.OnNext(S)));

    protected override UIElement Build()
    {
        Live(RetainPictures);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Monitors
        var mons = new Grid { Margin = new Thickness(36, 16, 36, 8) };
        mons.ColumnDefinitions.Add(new ColumnDefinition());
        mons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        mons.ColumnDefinitions.Add(new ColumnDefinition());
        var next = Tour.Mark(Monitor("NEXT", T.Preview, () => S.PreviewOf(B.Me)), "next");
        var live = Tour.Mark(Monitor("LIVE", T.Program, () => S.ProgramOf(B.Me)), "live");
        Grid.SetColumn(live, 2);
        mons.Children.Add(next); mons.Children.Add(live);
        root.Children.Add(mons);

        // Source row
        var sourceRow = Tour.Mark(T.Column(8, T.Step(1, "Pick what's next").With(s => s.HorizontalAlignment = HorizontalAlignment.Center), SourceRow()), "sources");
        sourceRow.Margin = new Thickness(0, 4, 0, 14);
        Grid.SetRow(sourceRow, 1);
        root.Children.Add(sourceRow);

        // Bottom panels
        var bottom = new Grid { Background = T.Toolbar };
        bottom.Children.Add(new Border { BorderBrush = T.Line, BorderThickness = new Thickness(0, 1, 0, 0) });
        var cols = new Grid { Margin = new Thickness(16, 14, 16, 18) };
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 400 });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var shots = ShotsPanel();
        var frame = FramePanel(); Grid.SetColumn(frame, 1); frame.Margin = new Thickness(24, 0, 24, 0);
        var take = TakePanel(); Grid.SetColumn(take, 2);
        cols.Children.Add(shots); cols.Children.Add(frame); cols.Children.Add(take);
        bottom.Children.Add(cols);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);
        return root;
    }

    // MARK: Monitors

    private Border Monitor(string title, Brush color, Func<int?> input)
    {
        var img = new Image { Stretch = Stretch.Uniform };
        var name = new TextBlock { FontSize = 34, FontWeight = FontWeights.Bold, Foreground = T.InkMuted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = T.Sans };
        var badge = new Border
        {
            Background = color, CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = T.Text(T.Spaced(title), 13, FontWeights.Bold, title == "LIVE" ? Brushes.White : T.OnAction),
        };
        var keys = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = T.Ink, Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var progress = new Border { Height = 5, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        var inner = new Grid { Background = Brushes.Black, ClipToBounds = true };
        // Auto tracking: the person being followed is outlined; a badge says what it's doing and stops it.
        var outline = new System.Windows.Shapes.Rectangle { StrokeThickness = 4, RadiusX = 8, RadiusY = 8, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var overlay = new Canvas { IsHitTestVisible = false };
        overlay.Children.Add(outline);
        var trackWords = T.Text("", 22, FontWeights.Bold, T.OnAction);
        var trackBadge = T.Button(T.Row(10, T.Text("🚶", 20, null, T.OnAction), trackWords, T.Text("✕", 18, FontWeights.Bold, T.OnAction)), null, T.Accent, T.Transparent, double.NaN, 48, 24, new Thickness(16, 0, 16, 0), "Stop auto tracking");
        trackBadge.Visibility = Visibility.Collapsed;
        var topRight = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 22, 22, 0) };
        topRight.Children.Add(trackBadge);
        // The Frame panel only reaches the camera on Next, so once a tracking camera is taken
        // Live its follow speed is nudged from here.
        var liveSpeed = title == "LIVE" ? SpeedStepper(onVideo: true) : null;
        if (liveSpeed != null) { liveSpeed.Margin = new Thickness(0, 12, 0, 0); liveSpeed.Visibility = Visibility.Collapsed; topRight.Children.Add(liveSpeed); }
        keys.Margin = new Thickness(0, 8, 0, 0); keys.HorizontalAlignment = HorizontalAlignment.Right;
        // Lower thirds and other keys with an NDI picture, over the shot when the key is on it.
        var keyLayer = new Grid { IsHitTestVisible = false };
        var keyImages = Store.Config.Keys.Where(k => !string.IsNullOrEmpty(k.Ndi)).Select(k => k.Ndi!).Distinct()
            .ToDictionary(n => n, n => new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed });
        foreach (var ki in keyImages.Values) keyLayer.Children.Add(ki);
        inner.Children.Add(name); inner.Children.Add(img); inner.Children.Add(keyLayer); inner.Children.Add(overlay); inner.Children.Add(badge); inner.Children.Add(topRight); inner.Children.Add(progress);
        // Clicking a person on Next follows them. On Live, a click only switches who an
        // already-tracking camera follows, so a stray click can't start moving the live shot.
        PTZCamera? Pickable()
        {
            var cam = PicturedOn(input());
            if (cam == null || img.Source == null || !Tracker.CanTrack(cam)) return null;
            return title == "NEXT" || Tracker.IsTracking(cam.Id) ? cam : null;
        }
        inner.MouseLeftButtonDown += (_, e) =>
        {
            if (Pickable() is not PTZCamera cam) return;
            var p = e.GetPosition(img);
            if (img.ActualWidth <= 0 || p.X < 0 || p.Y < 0 || p.X > img.ActualWidth || p.Y > img.ActualHeight) return;
            Start(cam, new PointD(p.X / img.ActualWidth, p.Y / img.ActualHeight));
            e.Handled = true;
        };
        var box = new Border
        {
            BorderBrush = color, BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(10), Child = inner, Background = Brushes.Black,
        };
        var outer = new Border { Child = new Viewbox { Stretch = Stretch.Uniform, Child = new Border { Width = 1280, Height = 720, Child = box.With(b => { b.BorderThickness = new Thickness(6); b.CornerRadius = new CornerRadius(16); }) } } };
        name.FontSize = 72;
        badge.Child = T.Text(T.Spaced(title), 26, FontWeights.Bold, title == "LIVE" ? Brushes.White : T.OnAction);
        badge.Padding = new Thickness(18, 6, 18, 6); badge.Margin = new Thickness(22);
        keys.FontSize = 24; topRight.Children.Add(keys);
        progress.Height = 10;
        Live(() =>
        {
            var i = input();
            var cam = PicturedOn(i);
            var pic = Pictures.Image(i);
            img.Source = pic;
            name.Text = Store.Label(i, S);
            name.Visibility = pic == null ? Visibility.Visible : Visibility.Collapsed;
            var showing = Overlays(title == "LIVE", i).Select(k => k.Ndi!).ToHashSet();
            foreach (var (n, ki) in keyImages)
            {
                var src = showing.Contains(n) ? Pictures.NdiImage(n) : null;
                ki.Source = src;
                ki.Visibility = src != null ? Visibility.Visible : Visibility.Collapsed;
            }
            var on = Store.Config.Keys.Where(k => title == "LIVE" ? k.IsOn(S) : k.OnNext(S)).Select(k => k.Label).ToList();
            keys.Text = on.Count > 0 ? "+ " + string.Join(", ", on) : "";
            var fading = title == "LIVE" && S.Get(S.InTransition, B.Me);
            progress.Width = fading ? 1268 * Math.Clamp(S.Position.TryGetValue(B.Me, out var p) ? p : 0, 0, 1) : 0;

            var ts = cam != null && Tracker.Sessions.TryGetValue(cam.Id, out var x) ? x : null;
            trackBadge.Visibility = ts != null ? Visibility.Visible : Visibility.Collapsed;
            if (liveSpeed != null) liveSpeed.Visibility = trackBadge.Visibility;
            if (ts != null)
            {
                var following = ts.Phase == AutoTracker.Phase.Following;
                trackWords.Text = T.Spaced((AutoTracker.Words(ts.Phase) ?? "").ToUpperInvariant());
                trackBadge.Background = following ? T.Accent : T.Warning;
                trackBadge.Tag = cam!.Id;
            }
            if (ts?.Target is TrackedPerson tp && pic != null && img.ActualWidth > 0)
            {
                var o = img.TranslatePoint(new Point(0, 0), inner);
                outline.Visibility = Visibility.Visible;
                outline.Stroke = ts.Phase == AutoTracker.Phase.Following ? T.Accent : T.Warning;
                outline.StrokeDashArray = ts.Phase == AutoTracker.Phase.Following ? null : new DoubleCollection { 3, 2 };
                Canvas.SetLeft(outline, o.X + tp.Box.X * img.ActualWidth);
                Canvas.SetTop(outline, o.Y + tp.Box.Y * img.ActualHeight);
                outline.Width = Math.Max(4, tp.Box.W * img.ActualWidth);
                outline.Height = Math.Max(4, tp.Box.H * img.ActualHeight);
            }
            else outline.Visibility = Visibility.Collapsed;
            inner.Cursor = Pickable() != null ? System.Windows.Input.Cursors.Cross : null;
        });
        trackBadge.Click += (_, _) => { if (trackBadge.Tag is Guid id) Tracker.Stop(id); };
        return outer;
    }

    // MARK: Source row

    private UIElement SourceRow()
    {
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var sources = Store.VolunteerSources(S);
        if (sources.Count == 0)
        {
            return T.Text(Atem.Link.Kind == LinkKind.Online ? "No sources set up yet. Add them in Engineer → Setup → Sources." : "No switcher connected. Add its address in Engineer → Setup → Switcher.", 13, null, T.InkSubtle)
                .With(t => t.HorizontalAlignment = HorizontalAlignment.Center);
        }
        int n = 0;
        foreach (var src in sources)
        {
            var index = n++;
            var input = src.Input;
            var img = new Image { Stretch = Stretch.UniformToFill };
            var label = T.Text(Store.Label(input, S), 14, FontWeights.Bold);
            label.VerticalAlignment = VerticalAlignment.Bottom; label.Margin = new Thickness(12, 0, 8, 10);
            label.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.9 };
            var keyHint = T.Text(index < 10 ? ((index + 1) % 10).ToString(CultureInfo.InvariantCulture) : "", 11, FontWeights.Bold, T.InkSubtle);
            keyHint.HorizontalAlignment = HorizontalAlignment.Right; keyHint.VerticalAlignment = VerticalAlignment.Top; keyHint.Margin = new Thickness(0, 6, 8, 0);
            var g = new Grid { ClipToBounds = true };
            g.Children.Add(img); g.Children.Add(label); g.Children.Add(keyHint);
            var tile = T.Button(g, () => Atem.SetPreview(input, B.Me), T.SurfaceRaised, T.Line, 170, 96, 8, new Thickness(0),
                tip: index < 10 ? $"Put on Next (key {(index + 1) % 10}). Shift+{(index + 1) % 10} puts it straight on Live." : null);
            tile.HorizontalContentAlignment = HorizontalAlignment.Stretch; tile.VerticalContentAlignment = VerticalAlignment.Stretch;
            tile.Margin = new Thickness(4);
            Live(() =>
            {
                var live = S.ProgramOf(B.Me) == input; var next = S.PreviewOf(B.Me) == input;
                tile.BorderBrush = live ? T.Program : next ? T.Preview : T.Line;
                tile.BorderThickness = new Thickness(live || next ? 3 : 1);
                img.Source = Pictures.Image(input);
            });
            row.Children.Add(tile);
        }
        return row;
    }

    // MARK: Shots

    private FrameworkElement ShotsPanel()
    {
        var p = new DockPanel { LastChildFill = true };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var t = Target;
        var head = new Border
        {
            Padding = new Thickness(12, 0, 12, 0), Height = 34, CornerRadius = new CornerRadius(8), Background = T.SurfaceSunken,
            BorderBrush = t == null ? T.Line : T.Preview, BorderThickness = new Thickness(t == null ? 1 : 2), HorizontalAlignment = HorizontalAlignment.Left,
            Child = t == null
                ? T.Row(8, T.Text("No camera on Next", 16, FontWeights.Bold, T.InkMuted), T.Text(Store.Label(S.PreviewOf(B.Me), S), 13, null, T.InkSubtle))
                : T.Row(8, T.Text("🎥", 13), T.Text(t.Name, 16, FontWeights.Bold), T.Badge("On next", T.OnAction, T.Preview)),
            ToolTip = "Exposure and the shots below control the camera that's on Next, never the one on Live.",
        };
        // A Panasonic camcorder's iris, gain and white balance aren't wired up yet.
        if (t != null && t.Proto != VISCAProtocol.PanasonicHTTP)
        {
            var expo = T.Small("Exposure", () => ExposureWindow.Show(t.Id), "Gain, shutter, iris and white balance for the camera on Next");
            DockPanel.SetDock(expo, Dock.Right);
            header.Children.Add(expo);
        }
        header.Children.Add(head);
        Tour.Mark(header, "camera");
        DockPanel.SetDock(header, Dock.Top);
        p.Children.Add(header);

        // The shot deck keeps its own state (the camera shown, a drag under way) across rebuilds
        // of this screen, so it's made once and moved into each new layout.
        if (_deck.Parent is Panel old) old.Children.Remove(_deck);
        p.Children.Add(_deck);
        Tour.Mark(_deck, "shots");
        Live(() => _deck.Refresh());
        return p;
    }

    private readonly ShotDeckView _deck = new();

    private static bool OnLive(PTZCamera c) => c.AtemInput is int i && S.OnLive(i);

    // MARK: Frame

    private FrameworkElement FramePanel()
    {
        var t = Target;
        var title = T.Step(2, t == null ? "Frame the shot" : $"Frame {t.Name}");
        // A camcorder with no head zooms and focuses only: the joystick is greyed out for it.
        var joy = new Joystick { Width = 170, Height = 170, IsEnabled = t != null && t.Proto.HasPanTilt(), Opacity = t != null && !t.Proto.HasPanTilt() ? 0.35 : 1, ToolTip = t != null && !t.Proto.HasPanTilt() ? $"{t.Name} can't pan or tilt. Zoom and Focus work." : null, OnMove = (x, y) => Ptz.Drive(x, y), OnEnd = () => Ptz.StopDrive() };
        var zoom = new ZoomRocker { Width = 44, Height = 170, IsEnabled = t != null, OnZoom = v => Ptz.Zoom(v) };
        var status = T.Text("", 11, FontWeights.SemiBold, T.Warning, wrap: true); status.Width = 150;
        var side = T.Column(8,
            T.Small("Focus", () => Ptz.FocusOnce(), "One-shot autofocus on the camera on Next (Ctrl+Shift+F)").With(b => { b.Width = 150; b.IsEnabled = t != null; }),
            TrackControls(t),
            status);
        side.VerticalAlignment = VerticalAlignment.Center;
        Live(() =>
        {
            if (t == null) { status.Text = ""; return; }
            status.Text = Ptz.InStandby(t) ? $"{t.Name} is in standby. Wake it with the button at the top."
                : Ptz.Waking(t) ? $"{t.Name} is waking up."
                : !Ptz.Online.Contains(t.Id) ? "Not answering"
                : !Ptz.HasControl(t) ? $"Can't move {t.Name} yet: something else has control of it, such as its web page or another controller. Close that, and this app takes over by itself." : "";
        });
        var controls = Tour.Mark(T.Row(16, joy, zoom, side), "frame");
        controls.Opacity = t == null ? 0.35 : 1;

        // Speed: shared by every camera and saved in the setup.
        var slider = new Slider { Minimum = 0.25, Maximum = 2, Value = Store.Config.PtzSpeed, Width = 150, VerticalAlignment = VerticalAlignment.Center, Focusable = false, IsMoveToPointEnabled = true };
        var val = T.Text(Store.Config.PtzSpeed.ToString("0.##", CultureInfo.InvariantCulture) + "×", 11, null, T.InkMuted); val.FontFamily = T.Mono; val.Width = 40;
        slider.ValueChanged += (_, _) => val.Text = slider.Value.ToString("0.##", CultureInfo.InvariantCulture) + "×";
        slider.PreviewMouseUp += (_, _) => Store.Update(c => c.PtzSpeed = Math.Round(slider.Value * 20) / 20);
        var reset = T.Button(T.Text("Reset", 11, null, T.InkSubtle), () => { slider.Value = 1; Store.Update(c => c.PtzSpeed = 1); }, T.Transparent, T.Transparent, pad: new Thickness(4));
        var speed = T.Row(8, T.Text("🐢", 11, null, T.InkSubtle), slider, T.Text("🐇", 11, null, T.InkSubtle), val, reset);
        speed.ToolTip = "How fast the joystick, zoom and arrow keys move the camera. Slow it down for small adjustments on a close-up.";
        Tour.Mark(speed, "speed");

        var col = T.Column(12, title, controls, speed);
        if (t == null)
        {
            var g = new Grid();
            g.Children.Add(col);
            g.Children.Add(new Border
            {
                Background = T.SurfaceRaised, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = T.Text("Put a camera on Next to frame it", 13, FontWeights.SemiBold),
            });
            return g;
        }
        Ptz.Selected = t.Id;
        return col;
    }

    // MARK: Tracking

    private static AutoTracker Tracker => AutoTracker.Shared;
    /// <summary>Why the last start found no one, shown under the button until the next try.</summary>
    private string? _trackProblem;

    /// <summary>Turns auto tracking on and off for the camera on Next. On, it follows the person
    /// nearest the middle; clicking someone on the Next picture follows them instead.</summary>
    private FrameworkElement TrackControls(PTZCamera? t)
    {
        var col = Tour.Mark(new StackPanel { Width = 150 }, "track");
        var walk = T.Text("🚶", 12);
        var label = T.Text("Track", 12, FontWeights.SemiBold);
        var btn = T.Button(T.Row(6, walk, label), () =>
        {
            if (t == null) return;
            _trackProblem = null;
            if (Tracker.IsTracking(t.Id)) { Tracker.Stop(t.Id); return; }
            Start(t, null);
        }, T.SurfaceRaised, T.LineStrong, 150, 30, 6);
        btn.IsEnabled = t != null;
        col.Children.Add(btn);

        // People labelled in the stage scan can be followed by name, wherever they are in the shot.
        var byName = T.Button(T.Text("Follow by name ▾", 11, FontWeights.SemiBold, T.AccentStrong), null, T.Transparent, T.Transparent, double.NaN, 22, 4, new Thickness(2, 0, 2, 0), "Follow someone named in Engineer → Stage scan");
        byName.HorizontalAlignment = HorizontalAlignment.Left;
        byName.Click += (_, _) =>
        {
            if (t == null) return;
            var m = T.Menu();
            foreach (var n in StageCast.Shared.Names) { var name = n; m.Items.Add(T.Item(name, () => { _trackProblem = null; Tracker.Start(t, person: name, done: why => { if (why != null && why != "Tracking was stopped.") _trackProblem = why; }); })); }
            m.PlacementTarget = byName; m.IsOpen = true;
        };
        col.Children.Add(byName);

        // The follow shot: tap to reframe. The lit one is what it's framing to.
        var sizes = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
        var sizeButtons = new List<(ShotSize Z, Button B, TextBlock L)>();
        foreach (var z in ShotSizes.All)
        {
            var size = z;
            var l = T.Text(z.Short(), 11, FontWeights.SemiBold);
            var b = T.Button(l, () => { if (t != null) Tracker.SetSize(t.Id, size); }, T.SurfaceSunken, T.Transparent, double.NaN, 24, 5, new Thickness(2), $"Frame {z.Name().ToLowerInvariant()}");
            b.Margin = new Thickness(1, 0, 1, 0);
            sizes.Children.Add(b);
            sizeButtons.Add((z, b, l));
        }
        col.Children.Add(sizes);

        // Follow speed and headroom, right here so they can be nudged during a service.
        var speedRow = SpeedStepper(onVideo: false); speedRow.Margin = new Thickness(0, 4, 0, 0);
        col.Children.Add(speedRow);
        var headRow = HeadroomStepperRow(() => t != null && Tracker.Sessions.TryGetValue(t.Id, out var hs) ? hs.Size : null);
        headRow.Margin = new Thickness(0, 4, 0, 0);
        col.Children.Add(headRow);

        var info = T.Text("", 11, FontWeights.SemiBold, T.InkSubtle, wrap: true);
        info.Margin = new Thickness(0, 4, 0, 0);
        col.Children.Add(info);

        Live(() =>
        {
            var s = t != null && Tracker.Sessions.TryGetValue(t.Id, out var x) ? x : null;
            var can = t != null && Tracker.CanTrack(t);
            btn.IsEnabled = can || s != null;
            btn.Opacity = btn.IsEnabled ? 1 : 0.5;
            label.Text = s == null ? "Track" : "Tracking";
            label.Foreground = walk.Foreground = s == null ? T.Ink : T.OnAction;
            btn.Background = s == null ? T.SurfaceRaised : T.Accent;
            btn.BorderBrush = s == null ? T.LineStrong : T.Accent;
            btn.ToolTip = t == null ? null
                : s != null ? "Stop following (Ctrl+T). Esc or Ctrl+. stops every camera that's tracking. Moving the joystick or picking a shot also stops it."
                : can ? "The camera follows the person nearest the middle of the picture (Ctrl+T), or click a person on the Next picture to follow them. Moving the joystick stops it."
                : Tracker.Detector == null ? Tracker.Unavailable ?? "Auto tracking isn't set up on this computer."
                : !t.Proto.HasPanTilt() ? $"{t.Name} can't pan or tilt, so it can't follow anyone."
                : "Auto tracking needs to see this camera's picture. Canon cameras send their own; for other cameras, choose an NDI picture for its input in Engineer → Setup → Sources.";
            sizes.Visibility = speedRow.Visibility = headRow.Visibility = s != null ? Visibility.Visible : Visibility.Collapsed;
            byName.Visibility = s == null && can && StageCast.Shared.Names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (z, b, l) in sizeButtons)
            {
                var on = s?.Size == z;
                b.Background = on ? T.Accent : T.SurfaceSunken;
                l.Foreground = on ? T.OnAction : T.InkMuted;
            }
            if (s != null)
            {
                info.Text = string.Join(" · ", new[] { AutoTracker.Words(s.Phase), s.Person, s.Phase == AutoTracker.Phase.Following ? s.Size.Name() : null }.Where(w => w != null));
                info.Foreground = s.Phase == AutoTracker.Phase.Following ? T.AccentStrong : T.Warning;
            }
            else if (_trackProblem != null) { info.Text = _trackProblem; info.Foreground = T.Warning; }
            else if (t != null && !can && Tracker.Detector == null) { info.Text = Tracker.Unavailable ?? ""; info.Foreground = T.InkSubtle; }
            else { info.Text = can ? "Or click a person on Next" : ""; info.Foreground = T.InkSubtle; }
        });
        return col;
    }

    /// <summary>Follow speed for tracking cameras, a step at a time. In the Frame panel for the
    /// camera on Next, and over the Live picture while the camera on Live is tracking (the Frame
    /// panel only reaches the camera on Next). Over a picture: white on a dark pill.</summary>
    private FrameworkElement SpeedStepper(bool onVideo)
    {
        var scale = onVideo ? 2.0 : 1.0;   // the monitors are drawn at 1280 wide and scaled down
        Brush fg = onVideo ? Brushes.White : T.Ink;
        var val = T.Text("", 11 * scale, null, fg); val.FontFamily = T.Mono; val.Width = 36 * scale; val.TextAlignment = TextAlignment.Center; val.VerticalAlignment = VerticalAlignment.Center;
        Button Step(string s, double d) => T.Button(T.Text(s, 11 * scale, FontWeights.Bold, fg), () =>
            Store.Update(c => c.TrackSpeed = Math.Min(4, Math.Max(0.5, Math.Round((c.TrackSpeed + d) * 4, MidpointRounding.AwayFromZero) / 4))),
            onVideo ? new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)) : T.SurfaceSunken, T.Transparent, (onVideo ? 24 : 22) * scale, 20 * scale, 4 * scale, new Thickness(0));
        var minus = Step("−", -0.25); var plus = Step("+", 0.25);
        var label = T.Text("Speed", 11 * scale, onVideo ? FontWeights.SemiBold : null, onVideo ? Brushes.White : T.InkMuted); label.VerticalAlignment = VerticalAlignment.Center;
        FrameworkElement el;
        if (onVideo)
        {
            label.Margin = new Thickness(0, 0, 4 * scale, 0);
            el = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0)), CornerRadius = new CornerRadius(14 * scale), Height = 28 * scale,
                Padding = new Thickness(8 * scale, 0, 8 * scale, 0), HorizontalAlignment = HorizontalAlignment.Right,
                Child = T.Row(4 * scale, label, minus, val, plus).With(r => r.VerticalAlignment = VerticalAlignment.Center),
            };
        }
        else
        {
            var row = new DockPanel();
            DockPanel.SetDock(plus, Dock.Right); DockPanel.SetDock(val, Dock.Right); DockPanel.SetDock(minus, Dock.Right);
            val.Width = 30;
            row.Children.Add(plus); row.Children.Add(val); row.Children.Add(minus); row.Children.Add(label);
            el = row;
        }
        el.ToolTip = "How quickly a tracking camera follows the person. Raise it if the camera falls behind; lower it if it swings past them.";
        Live(() =>
        {
            var sp = Store.Config.TrackSpeed;
            val.Text = sp.ToString("0.##", CultureInfo.InvariantCulture) + "×";
            minus.IsEnabled = sp > 0.5; plus.IsEnabled = sp < 4;
        });
        return el;
    }

    /// <summary>Headroom for the follow-shot size being framed, a step at a time: moves where the
    /// eyes sit in the frame. Shown as steps from the standard ("Std", "+1", "−2"). Changes every
    /// camera framing this size. Right-click to go back to the standard.</summary>
    private FrameworkElement HeadroomStepperRow(Func<ShotSize?> size)
    {
        var val = T.Text("", 11, null, T.Ink); val.FontFamily = T.Mono; val.Width = 30; val.TextAlignment = TextAlignment.Center; val.VerticalAlignment = VerticalAlignment.Center;
        void Nudge(int dir)
        {
            if (size() is not ShotSize z) return;
            Store.Update(c => HeadroomStepper.Set(c, z, HeadroomStepper.Stepped(z.EyeLine(c), dir)));
        }
        var minus = T.Button(T.Text("−", 11, FontWeights.Bold), () => Nudge(-1), T.SurfaceSunken, T.Transparent, 22, 20, 4, new Thickness(0));
        var plus = T.Button(T.Text("+", 11, FontWeights.Bold), () => Nudge(1), T.SurfaceSunken, T.Transparent, 22, 20, 4, new Thickness(0));
        var row = new DockPanel();
        DockPanel.SetDock(plus, Dock.Right); DockPanel.SetDock(val, Dock.Right); DockPanel.SetDock(minus, Dock.Right);
        row.Children.Add(plus); row.Children.Add(val); row.Children.Add(minus);
        row.Children.Add(T.Text("Headroom", 11, null, T.InkMuted).With(x => x.VerticalAlignment = VerticalAlignment.Center));
        var m = T.Menu();
        m.Items.Add(T.Item("Back to the default", () => { if (size() is ShotSize z) Store.Update(c => HeadroomStepper.Set(c, z, z.Standard().EyeLine ?? z.EyeLine(c))); }));
        row.ContextMenu = m;
        Live(() =>
        {
            if (size() is not ShotSize z) return;
            var eye = z.EyeLine(Store.Config); var std = z.Standard().EyeLine ?? eye;
            val.Text = HeadroomStepper.Label(HeadroomStepper.Steps(eye, std));
            minus.IsEnabled = eye > HeadroomStepper.Low + 0.001; plus.IsEnabled = eye < HeadroomStepper.High - 0.001;
            row.ToolTip = $"How much space is left above the head on the {z.Name().ToLowerInvariant()} shot. Minus for less, plus for more.";
        });
        return row;
    }

    /// <summary>Starts following on a camera, at a point on its picture or the middle.</summary>
    private void Start(PTZCamera c, PointD? at)
    {
        _trackProblem = null;
        Tracker.Start(c, at, done: why => { if (why != null && why != "Tracking was stopped.") _trackProblem = why; });
    }

    // MARK: Take

    private FrameworkElement TakePanel()
    {
        var me = B.Me;
        var fadeTitle = T.Text("FADE", 26, FontWeights.Bold, Brushes.White); fadeTitle.HorizontalAlignment = HorizontalAlignment.Center;
        var fadeSub = T.Text("", 11, FontWeights.Medium, Brushes.White); fadeSub.HorizontalAlignment = HorizontalAlignment.Center; fadeSub.Opacity = 0.85;
        var fade = T.Button(T.Column(2, fadeTitle, fadeSub), () => Atem.Auto(me), T.Program, new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)), 124, 92, 8, tip: "Fade from Live to Next (Return)");
        var cutT = T.Text("CUT", 26, FontWeights.Bold); cutT.HorizontalAlignment = HorizontalAlignment.Center;
        var cutS = T.Text("Space", 11, FontWeights.Medium, T.InkMuted); cutS.HorizontalAlignment = HorizontalAlignment.Center;
        var cut = T.Button(T.Column(2, cutT, cutS), () => Atem.Cut(me), T.SurfaceRaised, T.LineStrong, 124, 92, 8, tip: "Switch Next to Live instantly (Space)");
        Live(() =>
        {
            var fading = S.Get(S.InTransition, me);
            fadeTitle.Text = fading ? "FADING" : "FADE";
            fadeSub.Text = fading ? $"{Store.Label(S.ProgramOf(me), S)} to {Store.Label(S.PreviewOf(me), S)}"
                                  : $"{((S.MixRate.TryGetValue(me, out var r) ? r : 30) / 30.0).ToString("0.0", CultureInfo.InvariantCulture)} s · Return";
            fade.Opacity = fading ? 0.6 : 1;
        });

        var grid = new WrapPanel { Width = 264, HorizontalAlignment = HorizontalAlignment.Left };
        var black = new T.LitButton(() => Atem.FadeToBlack(me), 124, 48, 14);
        black.Button.Margin = new Thickness(0, 0, 8, 8);
        Live(() =>
        {
            bool inBlack = S.Get(S.FtbBlack, me), ftb = S.Get(S.FtbInTransition, me);
            var blink = inBlack && (Environment.TickCount64 / 400) % 2 == 0;
            black.Set("BLACK", inBlack ? "IN BLACK" : ftb ? "FADING" : "OFF", ftb || (inBlack && blink) ? T.Program : null,
                inBlack ? "Fade back up from black" : "Fade everything to black");
        });
        grid.Children.Add(black.Button);
        foreach (var k in Store.Config.Keys)
        {
            var key = k;
            var lb = new T.LitButton(() => key.Toggle(Atem), 124, 48, 13);
            lb.Button.Margin = new Thickness(0, 0, 8, 8);
            Live(() =>
            {
                var on = key.IsOn(S);
                lb.Set(key.Label.ToUpperInvariant(), on ? "ON AIR" : "OFF", on ? T.Program : null,
                    on ? $"Take {key.Label} off ({key.Target})" : $"Put {key.Label} on ({key.Target})");
            });
            grid.Children.Add(lb.Button);
        }

        var col = T.Column(12, T.Step(3, "Take it live"), Tour.Mark(T.Row(8, fade, cut), "take"), Tour.Mark(grid, "keys"));
        if (Store.Config.Routes.Count > 0)
        {
            var routes = new WrapPanel();
            foreach (var r in Store.Config.Routes)
            {
                var route = r;
                var title = T.Text(r.Label, 12, FontWeights.SemiBold); title.HorizontalAlignment = HorizontalAlignment.Center; title.TextWrapping = TextWrapping.Wrap; title.TextAlignment = TextAlignment.Center;
                var sub = T.Text("", 9, FontWeights.Bold); sub.HorizontalAlignment = HorizontalAlignment.Center; sub.Opacity = 0.75;
                var b = T.Button(T.Column(1, title, sub), () => route.Run(Atem), T.SurfaceRaised, T.Line, 124, 46, 8, new Thickness(6, 2, 6, 2), Store.RouteDetail(r, S));
                b.Margin = new Thickness(0, 0, 8, 8);
                Live(() =>
                {
                    var on = route.IsActive(S);
                    b.Background = on ? T.Accent : T.SurfaceRaised; b.BorderBrush = on ? T.Accent : T.Line;
                    title.Foreground = sub.Foreground = on ? T.OnAction : T.Ink;
                    sub.Text = on ? "ON" : Store.RouteSummary(route);
                });
                routes.Children.Add(b);
            }
            col.Children.Add(T.Caps("Screens").Margin(0, 4, 0, 0));
            col.Children.Add(Tour.Mark(routes, "screens").Margin(0, 6, 0, 0));
        }
        return col;
    }
}
