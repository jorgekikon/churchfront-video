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

    /// <summary>The shot each camera was last sent to, lit in the grid.</summary>
    private readonly Dictionary<Guid, string> _lastUsed = new();
    private readonly HashSet<Guid> _retained = new();

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

    public override void Teardown()
    {
        foreach (var id in _retained) if (Ptz.Camera(id) is PTZCamera c) CanonFeedHub.Release(c);
        _retained.Clear();
    }

    /// <summary>Keeps the pictures running for the cameras on Next and Live.</summary>
    private void RetainPictures()
    {
        var want = Ptz.Cameras.Where(c => c.Proto == VISCAProtocol.CanonXC && c.Host.Length > 0 && c.AtemInput is int i
                                          && (S.PreviewOf(B.Me) == i || S.ProgramOf(B.Me) == i || Store.VolunteerSources(S).Any(v => v.Input == i)))
                              .Select(c => c.Id).ToHashSet();
        foreach (var id in _retained.Except(want).ToList()) { if (Ptz.Camera(id) is PTZCamera c) CanonFeedHub.Release(c); _retained.Remove(id); }
        foreach (var id in want.Except(_retained).ToList()) { if (Ptz.Camera(id) is PTZCamera c) CanonFeedHub.Retain(c); _retained.Add(id); }
    }

    private static PTZCamera? PicturedOn(int? input) =>
        input is int i ? Ptz.Cameras.FirstOrDefault(c => c.AtemInput == i && c.Proto == VISCAProtocol.CanonXC && c.Host.Length > 0) : null;

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
        var next = Monitor("NEXT", T.Preview, () => S.PreviewOf(B.Me));
        var live = Monitor("LIVE", T.Program, () => S.ProgramOf(B.Me));
        Grid.SetColumn(live, 2);
        mons.Children.Add(next); mons.Children.Add(live);
        root.Children.Add(mons);

        // Source row
        var sourceRow = T.Column(8, T.Step(1, "Pick what's next").With(s => s.HorizontalAlignment = HorizontalAlignment.Center), SourceRow());
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
        keys.Margin = new Thickness(0, 8, 0, 0); keys.HorizontalAlignment = HorizontalAlignment.Right;
        inner.Children.Add(name); inner.Children.Add(img); inner.Children.Add(overlay); inner.Children.Add(badge); inner.Children.Add(topRight); inner.Children.Add(progress);
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
            var feed = cam != null ? CanonFeedHub.Feed(cam) : null;
            var pic = feed?.IsLive == true ? feed.Image : null;
            img.Source = pic;
            name.Text = Store.Label(i, S);
            name.Visibility = pic == null ? Visibility.Visible : Visibility.Collapsed;
            var on = Store.Config.Keys.Where(k => title == "LIVE" ? k.IsOn(S) : k.OnNext(S)).Select(k => k.Label).ToList();
            keys.Text = on.Count > 0 ? "+ " + string.Join(", ", on) : "";
            var fading = title == "LIVE" && S.Get(S.InTransition, B.Me);
            progress.Width = fading ? 1268 * Math.Clamp(S.Position.TryGetValue(B.Me, out var p) ? p : 0, 0, 1) : 0;

            var ts = cam != null && Tracker.Sessions.TryGetValue(cam.Id, out var x) ? x : null;
            trackBadge.Visibility = ts != null ? Visibility.Visible : Visibility.Collapsed;
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
            return T.Text(Atem.Link.Kind == LinkKind.Online ? "No sources set up yet. Add them in Engineer, Setup." : "Connect the switcher in Engineer, Setup.", 13, null, T.InkSubtle)
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
                var cam = PicturedOn(input);
                var feed = cam != null ? CanonFeedHub.Feed(cam) : null;
                img.Source = feed?.IsLive == true ? feed.Image : null;
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
            ToolTip = "Exposure and saved shots below act on the camera that's on Next.",
        };
        if (t != null)
        {
            var expo = T.Small("Exposure", () => ExposureWindow.Show(t.Id), "Gain, shutter, iris and white balance for the camera on Next");
            DockPanel.SetDock(expo, Dock.Right);
            header.Children.Add(expo);
        }
        header.Children.Add(head);
        DockPanel.SetDock(header, Dock.Top);
        p.Children.Add(header);
        var label = T.Caps("Shots").Margin(0, 0, 0, 6);
        DockPanel.SetDock(label, Dock.Top);
        p.Children.Add(label);

        var rows = new StackPanel();
        if (Cams.Count == 0) rows.Children.Add(T.Text("No cameras set up yet. Add them in Engineer, Setup.", 12, null, T.InkSubtle));
        foreach (var c in Cams) rows.Children.Add(ShotRow(c));
        p.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 200 });
        return p;
    }

    private static bool OnLive(PTZCamera c) => c.AtemInput is int i && S.OnLive(i);
    private static bool OnNext(PTZCamera c) => c.AtemInput is int i && S.PreviewOf(B.Me) == i;

    private UIElement ShotRow(PTZCamera c)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        var state = T.Text(" ", 9, FontWeights.Bold);
        var camBtn = T.Button(T.Column(1, T.Text(c.Name, 13, FontWeights.Bold), state).With(s => s.HorizontalAlignment = HorizontalAlignment.Left),
            () => { if (c.AtemInput is int i) Atem.SetPreview(i, B.Me); }, T.SurfaceSunken, T.Line, 86, 42, 8, new Thickness(8, 0, 8, 0), $"Put {c.Name} on Next");
        camBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
        Live(() =>
        {
            bool live = OnLive(c), next = OnNext(c);
            state.Text = live ? "LIVE" : next ? "NEXT" : " ";
            state.Foreground = live ? T.Program : T.Preview;
            camBtn.BorderBrush = live ? T.Program : next ? T.Preview : T.Line;
            camBtn.BorderThickness = new Thickness(live || next ? 2 : 1);
        });
        DockPanel.SetDock(camBtn, Dock.Left);
        row.Children.Add(camBtn);

        var cells = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var s in c.Shots.Where(s => s.Kind == ShotKind.Position))
        {
            var shot = s;
            var key = $"shot:{s.Id}";
            var cell = Cell(c, s.Name, null, key, () => { Ptz.Go(shot, c.Id); });
            var m = T.Menu();
            m.Items.Add(T.Item("Update to where the camera is now", () => Ptz.SaveShot("", c.Id, shot.Kind, shot.Id, ok => { if (!ok) Dialogs.Info("Not saved", NoReading); })));
            m.Items.Add(T.Item("Rename…", () =>
            {
                if (Dialogs.Prompt("Rename shot", $"A new name for {shot.Name} on {c.Name}.", shot.Name) is string n && n.Length > 0)
                    Ptz.Edit(list => { var x = list.First(y => y.Id == c.Id).Shots.FirstOrDefault(y => y.Id == shot.Id); if (x != null) x.Name = n; });
            }));
            m.Items.Add(new Separator());
            m.Items.Add(T.Item("Delete…", () =>
            {
                if (Dialogs.Confirm($"Delete {shot.Name}?", "The shot is removed from Churchfront Video's setup. The camera isn't changed.", "Delete", danger: true))
                    Ptz.Edit(list => list.First(y => y.Id == c.Id).Shots.RemoveAll(y => y.Id == shot.Id));
            }, danger: true));
            cell.ContextMenu = m;
            cells.Children.Add(cell);
        }
        var exp = Ptz.ExposurePresets.TryGetValue(c.Id, out var e) ? e : new HashSet<int>();
        foreach (var (n, nm) in Ptz.PresetNames(c).OrderBy(kv => kv.Key))
        {
            if (exp.Contains(n)) continue;
            var preset = n;
            var cell = Cell(c, nm, "ON CAMERA", $"preset:{n}", () => Ptz.Recall(preset, c.Id));
            if (Ptz.CanSaveShots(c))
            {
                var m = T.Menu();
                m.Items.Add(T.Item("Delete from camera…", () =>
                {
                    if (Dialogs.Confirm($"Delete \"{nm}\" from {c.Name}?", "This removes it from the camera itself, for every controller that uses it. It can't be undone.", "Delete from camera", danger: true))
                        Ptz.DeleteCameraPreset(preset, c.Id, why => { if (why != null) Dialogs.Info("Couldn't delete that preset", why); });
                }, danger: true));
                cell.ContextMenu = m;
            }
            cells.Children.Add(cell);
        }
        if (Ptz.CanSaveShots(c))
        {
            var add = T.Button(T.Text("+", 16, FontWeights.Bold, T.AccentStrong), () =>
            {
                if (Dialogs.Prompt($"Save a shot on {c.Name}", "Pan, tilt and zoom, where the camera is pointing now.", "", "Pulpit") is string name)
                    Ptz.SaveShot(name, c.Id, ShotKind.Position, null, ok => { if (!ok) Dialogs.Info("Not saved", NoReading); });
            }, T.Transparent, T.LineStrong, 42, 42, 8, new Thickness(0), $"Save where {c.Name} is now as a new shot");
            add.Margin = new Thickness(6, 0, 0, 0);
            cells.Children.Add(add);
        }
        row.Children.Add(new ScrollViewer { Content = cells, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(6, 0, 0, 0) });
        return row;
    }

    private const string NoReading = "The camera didn't report its settings, so the shot wasn't saved. Check that it's answering and try again.";

    private Button Cell(PTZCamera c, string title, string? subtitle, string key, Action run)
    {
        var titleT = T.Text(title, 13, FontWeights.SemiBold); titleT.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel(); stack.Children.Add(titleT);
        TextBlock? sub = null;
        if (subtitle != null) { sub = T.Text(subtitle, 8, FontWeights.Bold, T.InkMuted); sub.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(sub); }
        var b = T.Button(stack, () =>
        {
            // A camera on Live asks before it moves.
            if (OnLive(c) && !Dialogs.Confirm($"{c.Name} is on Live. Move it anyway?", "Everyone watching will see the camera move.", "Move it on Live", danger: true)) return;
            run();
            _lastUsed[c.Id] = key;
        }, T.SurfaceRaised, T.Line, 106, 42, 8, new Thickness(6, 0, 6, 0));
        b.Margin = new Thickness(6, 0, 0, 0);
        Live(() =>
        {
            var used = _lastUsed.TryGetValue(c.Id, out var u) && u == key;
            b.Background = used ? T.Accent : T.SurfaceRaised;
            b.BorderBrush = used ? T.Accent : T.Line;
            titleT.Foreground = used ? T.OnAction : T.Ink;
            if (sub != null) sub.Foreground = used ? T.OnAction : T.InkMuted;
            b.Opacity = OnLive(c) && !used ? 0.75 : 1;
        });
        return b;
    }

    // MARK: Frame

    private FrameworkElement FramePanel()
    {
        var t = Target;
        var title = T.Step(2, t == null ? "Frame the shot" : $"Frame {t.Name}");
        var joy = new Joystick { Width = 170, Height = 170, IsEnabled = t != null, OnMove = (x, y) => Ptz.Drive(x, y), OnEnd = () => Ptz.StopDrive() };
        var zoom = new ZoomRocker { Width = 44, Height = 170, IsEnabled = t != null, OnZoom = v => Ptz.Zoom(v) };
        var status = T.Text("", 11, FontWeights.SemiBold, T.Warning, wrap: true); status.Width = 150;
        var side = T.Column(8,
            T.Small("Focus", () => Ptz.FocusOnce(), "One-shot autofocus on the camera on Next").With(b => { b.Width = 150; b.IsEnabled = t != null; }),
            TrackControls(t),
            status);
        side.VerticalAlignment = VerticalAlignment.Center;
        Live(() =>
        {
            if (t == null) { status.Text = ""; return; }
            status.Text = !Ptz.Online.Contains(t.Id) ? "Not answering"
                : !Ptz.HasControl(t) ? $"Waiting for control. Another controller has {t.Name}, maybe its web page. Close it and this takes over." : "";
        });
        var controls = T.Row(16, joy, zoom, side);
        controls.Opacity = t == null ? 0.35 : 1;

        // Speed: shared by every camera and saved in the setup.
        var slider = new Slider { Minimum = 0.25, Maximum = 2, Value = Store.Config.PtzSpeed, Width = 150, VerticalAlignment = VerticalAlignment.Center, Focusable = false, IsMoveToPointEnabled = true };
        var val = T.Text(Store.Config.PtzSpeed.ToString("0.##", CultureInfo.InvariantCulture) + "×", 11, null, T.InkMuted); val.FontFamily = T.Mono; val.Width = 40;
        slider.ValueChanged += (_, _) => val.Text = slider.Value.ToString("0.##", CultureInfo.InvariantCulture) + "×";
        slider.PreviewMouseUp += (_, _) => Store.Update(c => c.PtzSpeed = Math.Round(slider.Value * 20) / 20);
        var reset = T.Button(T.Text("Reset", 11, null, T.InkSubtle), () => { slider.Value = 1; Store.Update(c => c.PtzSpeed = 1); }, T.Transparent, T.Transparent, pad: new Thickness(4));
        var speed = T.Row(8, T.Text("🐢", 11, null, T.InkSubtle), slider, T.Text("🐇", 11, null, T.InkSubtle), val, reset);
        speed.ToolTip = "Camera speed for the joystick, zoom and arrow keys. Slow it down for fine framing on a tight shot.";

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
        var col = new StackPanel { Width = 150 };
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
        var byName = T.Button(T.Text("Follow by name ▾", 11, FontWeights.SemiBold, T.AccentStrong), null, T.Transparent, T.Transparent, double.NaN, 22, 4, new Thickness(2, 0, 2, 0), "Follow someone labelled in the stage scan");
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

        // Follow speed, right here so it can be nudged during a service.
        var speedVal = T.Text("", 11, null, T.Ink); speedVal.FontFamily = T.Mono; speedVal.Width = 36; speedVal.TextAlignment = TextAlignment.Center;
        Button Step(string s, double d) => T.Button(T.Text(s, 11, FontWeights.Bold), () =>
            Store.Update(c => c.TrackSpeed = Math.Min(4, Math.Max(0.5, Math.Round((c.TrackSpeed + d) * 4) / 4))), T.SurfaceSunken, T.Transparent, 24, 20, 4, new Thickness(0));
        var speedRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0), ToolTip = "How quickly tracking cameras follow. Raise it if the camera falls behind, lower it if it overshoots." };
        var plus = Step("+", 0.25); DockPanel.SetDock(plus, Dock.Right);
        var val = speedVal; DockPanel.SetDock(val, Dock.Right);
        var minus = Step("−", -0.25); DockPanel.SetDock(minus, Dock.Right);
        speedRow.Children.Add(plus); speedRow.Children.Add(val); speedRow.Children.Add(minus);
        speedRow.Children.Add(T.Text("Speed", 11, null, T.InkMuted).With(x => x.VerticalAlignment = VerticalAlignment.Center));
        col.Children.Add(speedRow);

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
                : s != null ? "Stop following (Esc stops every tracking camera). Moving the joystick or recalling a shot also stops it."
                : can ? "Follow the person in the middle of the shot. Or click someone on the Next picture. Moving the joystick stops tracking."
                : Tracker.Detector == null ? Tracker.Unavailable ?? "Auto tracking isn't set up on this computer."
                : "Auto tracking needs the camera's own picture: on Windows, that's a Canon camera.";
            sizes.Visibility = speedRow.Visibility = s != null ? Visibility.Visible : Visibility.Collapsed;
            byName.Visibility = s == null && can && StageCast.Shared.Names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (z, b, l) in sizeButtons)
            {
                var on = s?.Size == z;
                b.Background = on ? T.Accent : T.SurfaceSunken;
                l.Foreground = on ? T.OnAction : T.InkMuted;
            }
            speedVal.Text = Store.Config.TrackSpeed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
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
        var cut = T.Button(T.Column(2, cutT, cutS), () => Atem.Cut(me), T.SurfaceRaised, T.LineStrong, 124, 92, 8, tip: "Switch to Next instantly (Space)");
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

        var col = T.Column(12, T.Step(3, "Take it live"), T.Row(8, fade, cut), grid);
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
            col.Children.Add(routes.Margin(0, 6, 0, 0));
        }
        return col;
    }
}
