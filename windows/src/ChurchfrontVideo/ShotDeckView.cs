using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CueATEM;

namespace CueVideo.App;

/// <summary>
/// The saved shots, one camera at a time (a port of the Mac's ShotDeck.swift). Tabs across the
/// top, one per camera; the shown camera follows Next, and clicking a tab puts that camera on
/// Next. Its shots fill a 5 by 2 grid of big tiles, paged with the arrows (or [ and ]) instead of
/// scrolling. Each tile has a key, Q to G. The last tile saves where the camera is now.
/// Right-click a shot to update, rename or delete it; hold one, then drag it, to put it somewhere
/// else on its page. The Live camera's tab shows its shots, locked: a click asks before it moves
/// the camera on air. The logic (pages, keys, the drag maths) is in CueVideo.Core's ShotDeck.cs.
/// </summary>
public sealed class ShotDeckView : LiveView
{
    private static Booth B => Booth.Shared;
    private static ATEMState S => B.Atem.State;
    private static PTZController Ptz => B.Ptz;
    private static ShotDeckState Deck => ShotDeckState.Shared;

    private const double Gap = 6, TileH = 64;
    public const string NoReading = "The camera didn't report its settings, so the shot wasn't saved. Check that it's answering and try again.";

    /// <summary>The Live camera, when its tab was clicked. Otherwise the deck shows the camera on Next.</summary>
    private Guid? _viewingLive;
    private int? _previewSeen;
    private ShotDrag? _drag;

    public ShotDeckView() { Deck.Changed += () => Refresh(); }

    private static List<PTZCamera> Cams => Ptz.Cameras.Where(c => c.Host.Length > 0).ToList();
    private static bool OnLive(PTZCamera c) => c.AtemInput is int i && S.OnLive(i);
    private static bool OnNext(PTZCamera c) => c.AtemInput is int i && S.PreviewOf(B.Me) == i;

    private PTZCamera? Shown
    {
        get
        {
            var cams = Cams;
            if (_viewingLive is Guid id && cams.FirstOrDefault(c => c.Id == id) is PTZCamera lc && OnLive(lc)) return lc;
            return cams.FirstOrDefault(OnNext);
        }
    }

    // A drag in progress must never have its tiles rebuilt under it.
    protected override bool HoldRebuild => _drag != null || base.HoldRebuild;

    protected override string StructureKey()
    {
        // Picking another camera for Next goes back to showing Next.
        var pv = S.PreviewOf(B.Me);
        if (pv != _previewSeen) { _previewSeen = pv; _viewingLive = null; }
        var sb = new System.Text.StringBuilder();
        var shown = Shown;
        sb.Append(shown?.Id).Append('|').Append(shown != null && OnLive(shown)).Append('|');
        if (shown != null) sb.Append(Deck.PageOf(shown, Ptz)).Append('|');
        foreach (var c in Cams)
        {
            sb.Append(PTZController.Signature(new[] { c }).GetHashCode()).Append(',');
            foreach (var (n, nm) in Ptz.PresetNames(c)) sb.Append(n).Append(nm);
            if (Ptz.ExposurePresets.TryGetValue(c.Id, out var ep)) sb.Append(string.Join(",", ep));
            sb.Append(';');
        }
        return sb.ToString();
    }

    protected override UIElement Build()
    {
        var root = new DockPanel { LastChildFill = true };
        var tabs = Tabs(); DockPanel.SetDock(tabs, Dock.Top); root.Children.Add(tabs);
        var cams = Cams;
        if (cams.Count == 0) { root.Children.Add(Note("No cameras set up yet. Add them in Engineer → Setup → Cameras.")); return root; }
        if (Shown is not PTZCamera c) { root.Children.Add(Note("Put a camera on Next to see its shots, or click a camera name above.")); return root; }
        if (!c.Proto.HasPanTilt()) { root.Children.Add(Note($"{c.Name} zooms and focuses only, so it has no shots.")); return root; }
        if (OnLive(c))
        {
            var banner = T.Text($"⚠  {c.Name} is on Live. Picking a shot moves it while everyone is watching, so each one asks first.", 11, FontWeights.SemiBold, T.Program, wrap: true);
            banner.Margin = new Thickness(0, 0, 0, Gap);
            DockPanel.SetDock(banner, Dock.Top); root.Children.Add(banner);
        }
        var pager = Pager(c); DockPanel.SetDock(pager, Dock.Bottom); root.Children.Add(pager);
        root.Children.Add(Grid(c));
        return root;
    }

    private static UIElement Note(string s)
    {
        var g = new Grid { Height = TileH * 2 + Gap, Margin = new Thickness(0, Gap, 0, 0) };
        g.Children.Add(new Rectangle { Stroke = T.Line, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, RadiusX = 8, RadiusY = 8 });
        g.Children.Add(T.Text(s, 13, null, T.InkSubtle).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; }));
        return g;
    }

    // MARK: Tabs

    private UIElement Tabs()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, Gap) };
        foreach (var c in Cams)
        {
            var cam = c;
            var name = T.Text(c.Name, 13, FontWeights.Bold);
            var badgeText = T.Text("", 9, FontWeights.Bold, Brushes.White);
            var badge = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1), Child = badgeText, VerticalAlignment = VerticalAlignment.Center };
            var count = T.Text(ShotDeckState.Items(c, Ptz).Count.ToString(System.Globalization.CultureInfo.InvariantCulture), 11, FontWeights.SemiBold, T.InkSubtle);
            var b = T.Button(T.Row(6, name, badge, count), () =>
            {
                if (OnLive(cam)) { _viewingLive = cam.Id; Refresh(); }
                else if (cam.AtemInput is int i) { _viewingLive = null; B.Atem.SetPreview(i, B.Me); }
            }, T.SurfaceSunken, T.Line, double.NaN, 30, 8, new Thickness(10, 0, 10, 0));
            b.Margin = new Thickness(0, 0, Gap, 0);
            Live(() =>
            {
                bool live = OnLive(cam), next = OnNext(cam), open = Shown?.Id == cam.Id;
                badge.Visibility = live || next ? Visibility.Visible : Visibility.Collapsed;
                badgeText.Text = live ? "LIVE" : "NEXT";
                badge.Background = live ? T.Program : T.Preview;
                badgeText.Foreground = live ? Brushes.White : T.OnAction;
                name.Foreground = open ? T.Ink : T.InkMuted;
                b.Background = open ? T.SurfaceRaised : T.SurfaceSunken;
                b.BorderBrush = live ? T.Program : next ? T.Preview : T.Line;
                b.BorderThickness = new Thickness(open && (live || next) ? 2 : 1);
                b.ToolTip = live ? $"{cam.Name} is on Live. Show its shots (they ask before moving it)." : $"Put {cam.Name} on Next";
            });
            row.Children.Add(b);
        }
        return row;
    }

    // MARK: Grid

    private UIElement Grid(PTZCamera c)
    {
        var items = ShotDeckState.Items(c, Ptz);
        int page = Deck.PageOf(c, Ptz), per = ShotDeckState.PerPage, cols = ShotDeckState.Columns, start = page * per;
        var canSave = Ptz.CanSaveShots(c);
        var nShots = items.Count(x => !x.IsPreset);
        var grid = new Grid { Height = TileH * 2 + Gap * 2, Margin = new Thickness(-Gap / 2, 0, -Gap / 2, 0) };
        for (int i = 0; i < cols; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
        var tiles = new FrameworkElement?[per];
        for (int slot = 0; slot < per; slot++)
        {
            var i = start + slot;
            FrameworkElement cell;
            if (i < items.Count)
            {
                var item = items[i];
                var (lo, hi) = ShotDeckState.Run(item.IsPreset, nShots, items.Count, page);
                cell = Tile(c, item, slot, page, lo, hi, tiles);
                tiles[slot] = cell;
            }
            else if (i == items.Count && canSave) cell = SaveTile(c);
            else cell = new Border { Background = T.SurfaceSunken, Opacity = 0.5, CornerRadius = new CornerRadius(8) };
            cell.Margin = new Thickness(Gap / 2);
            System.Windows.Controls.Grid.SetColumn(cell, slot % cols);
            System.Windows.Controls.Grid.SetRow(cell, slot / cols);
            grid.Children.Add(cell);
        }
        return grid;
    }

    /// <summary>One shot: its key, its name, and whether it's where the camera was last sent.
    /// A click sends the camera there. Held for a moment it lifts, and can be dragged to a new place.</summary>
    private FrameworkElement Tile(PTZCamera c, DeckItem item, int slot, int page, int lo, int hi, FrameworkElement?[] tiles)
    {
        var letter = ShotDeckState.Letters[slot];
        var canDrag = hi - lo + 1 > 1;
        // Two lines at most, never broken inside a word; shrunk to fit a narrow tile (the Mac
        // scales down to 70%), and kept clear of the key letter and the ON CAMERA line.
        var name = T.Text(item.Name, 15, FontWeights.Bold, T.Ink);
        name.TextWrapping = TextWrapping.WrapWithOverflow; name.TextAlignment = TextAlignment.Center;
        name.MaxWidth = 104; name.MaxHeight = 42; name.TextTrimming = TextTrimming.CharacterEllipsis;
        var nameBox = new Viewbox
        {
            Child = name, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 17, 6, item.IsPreset ? 13 : 4),
        };
        var keyT = T.Text(letter, 10, FontWeights.Bold, T.InkMuted); keyT.FontFamily = T.Mono; keyT.HorizontalAlignment = HorizontalAlignment.Center;
        var keyBox = new Border { Width = 18, Height = 16, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = T.LineStrong, Child = keyT,
                                  HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6) };
        var here = T.Text(T.Spaced("HERE"), 9, FontWeights.Bold, T.OnAction);
        here.HorizontalAlignment = HorizontalAlignment.Right; here.VerticalAlignment = VerticalAlignment.Top; here.Margin = new Thickness(7);
        var g = new Grid();
        g.Children.Add(nameBox); g.Children.Add(keyBox); g.Children.Add(here);
        TextBlock? onCam = null;
        if (item.IsPreset)
        {
            onCam = T.Text(T.Spaced("ON CAMERA"), 8, FontWeights.Bold, T.Ink);
            onCam.Opacity = 0.6; onCam.HorizontalAlignment = HorizontalAlignment.Center; onCam.VerticalAlignment = VerticalAlignment.Bottom; onCam.Margin = new Thickness(0, 0, 0, 5);
            g.Children.Add(onCam);
        }
        var tile = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = g, Cursor = Cursors.Hand,
            RenderTransform = new TranslateTransform(), ToolTip = canDrag ? $"Click (or press {letter}) to send the camera to this shot. Hold it, then drag, to change its place." : $"Click (or press {letter}) to send the camera to this shot",
        };
        var lifted = false;
        Live(() =>
        {
            var used = Deck.LastUsed.TryGetValue(c.Id, out var u) && u == item.Key;
            var live = OnLive(c);
            tile.Background = used ? T.Accent : T.SurfaceRaised;
            tile.BorderBrush = lifted || used ? T.Accent : live ? T.Program : T.Line;
            tile.BorderThickness = new Thickness(lifted ? 2 : 1);
            if (!lifted) tile.Opacity = live && !used ? 0.75 : 1;
            name.Foreground = used ? T.OnAction : T.Ink;
            if (onCam != null) onCam.Foreground = used ? T.OnAction : T.Ink;
            keyT.Foreground = used ? T.OnAction : T.InkMuted;
            keyBox.BorderBrush = used ? T.OnAction : T.LineStrong;
            here.Visibility = used ? Visibility.Visible : Visibility.Collapsed;
        });

        // Hold, then drag. Measured against the grid, since the tile itself moves under the mouse.
        Point down = default;
        bool pressed = false, strayed = false;
        DispatcherTimer? hold = null;
        FrameworkElement Space() => (FrameworkElement)tile.Parent;
        void Reset()
        {
            hold?.Stop(); hold = null;
            pressed = false; strayed = false; lifted = false;
            foreach (var t in tiles) if (t?.RenderTransform is TranslateTransform tt) { tt.X = 0; tt.Y = 0; Panel.SetZIndex(t, 0); }
            tile.Opacity = 1;
            _drag = null;
        }
        tile.MouseLeftButtonDown += (_, e) =>
        {
            pressed = true; strayed = false; lifted = false;
            down = e.GetPosition(Space());
            tile.CaptureMouse();
            tile.Opacity = 0.7;
            if (canDrag)
            {
                hold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.35) };
                hold.Tick += (_, _) =>
                {
                    hold?.Stop();
                    if (!pressed || strayed) return;
                    lifted = true;
                    tile.Opacity = 1;
                    Panel.SetZIndex(tile, 1);
                    _drag = new ShotDrag { Camera = c.Id, Page = page, From = slot, Lo = lo, Hi = hi, CellW = tile.ActualWidth + Gap, CellH = tile.ActualHeight + Gap };
                    RunUpdaters();
                };
                hold.Start();
            }
            e.Handled = true;
        };
        tile.MouseMove += (_, e) =>
        {
            if (!pressed) return;
            var p = e.GetPosition(Space());
            double dx = p.X - down.X, dy = p.Y - down.Y;
            if (lifted && _drag != null)
            {
                _drag.MovedX = dx; _drag.MovedY = dy;
                for (int s = 0; s < tiles.Length; s++)
                    if (tiles[s]?.RenderTransform is TranslateTransform tt && _drag.Holds(s)) { var (x, y) = _drag.Shift(s); tt.X = x; tt.Y = y; }
            }
            else if (Math.Sqrt(dx * dx + dy * dy) > 8) { strayed = true; tile.Opacity = 1; }
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            tile.ReleaseMouseCapture();
            var d = _drag; var wasLifted = lifted; var wasStrayed = strayed;
            Reset();
            if (wasLifted && d != null)
            {
                // Puts the dragged shot down where it was dropped, and saves the new order.
                if (d.To != d.From) ShotDeckState.Move(c.Id, d.Page * ShotDeckState.PerPage + d.From, d.Page * ShotDeckState.PerPage + d.To, Ptz);
            }
            else if (!wasStrayed) Go(c, () => Deck.Go(item, c, Ptz));
            RunUpdaters();
            e.Handled = true;
        };
        tile.LostMouseCapture += (_, _) => { if (pressed) { Reset(); RunUpdaters(); } };
        tile.ContextMenu = Menu(item, c);
        return tile;
    }

    private void RunUpdaters() => Refresh();

    private static ContextMenu? Menu(DeckItem item, PTZCamera c)
    {
        var m = T.Menu();
        if (item.Shot is PTZShot shot)
        {
            m.Items.Add(T.Item("Update to where the camera is now", () => Ptz.SaveShot("", c.Id, shot.Kind, shot.Id, ok => { if (!ok) Dialogs.Info("Not saved", NoReading); })));
            m.Items.Add(T.Item("Rename…", () =>
            {
                if (Dialogs.Prompt("Rename shot", $"A new name for {shot.Name} on {c.Name}.", shot.Name) is string n && n.Trim().Length > 0)
                    Ptz.Edit(list => { var x = list.First(y => y.Id == c.Id).Shots.FirstOrDefault(y => y.Id == shot.Id); if (x != null) x.Name = n.Trim(); });
            }));
            m.Items.Add(new Separator());
            m.Items.Add(T.Item("Delete…", () =>
            {
                if (Dialogs.Confirm($"Delete {shot.Name}?", "The shot is removed from Churchfront Video. Nothing on the camera itself changes.", "Delete", danger: true))
                    Ptz.Edit(list => list.First(y => y.Id == c.Id).Shots.RemoveAll(y => y.Id == shot.Id));
            }, danger: true));
            return m;
        }
        if (!Ptz.CanSaveShots(c)) return null;
        var preset = item.Preset; var nm = item.Name;
        m.Items.Add(T.Item("Delete from camera…", () =>
        {
            if (Dialogs.Confirm($"Delete \"{nm}\" from {c.Name}?", "This deletes the preset on the camera itself, so it's gone for everything that uses the camera, including Companion. It can't be undone.", "Delete from camera", danger: true))
                Ptz.DeleteCameraPreset(preset, c.Id, why => { if (why != null) Dialogs.Info("Couldn't delete that preset", why); });
        }, danger: true));
        return m;
    }

    private static FrameworkElement SaveTile(PTZCamera c)
    {
        var g = new Grid();
        g.Children.Add(new Rectangle { Stroke = T.LineStrong, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, RadiusX = 8, RadiusY = 8 });
        var plus = T.Text("+", 18, FontWeights.Bold, T.AccentStrong); plus.HorizontalAlignment = HorizontalAlignment.Center;
        var words = T.Text($"Save where {c.Name} is", 12, FontWeights.SemiBold, T.AccentStrong, wrap: true); words.TextAlignment = TextAlignment.Center; words.HorizontalAlignment = HorizontalAlignment.Center;
        var col = T.Column(2, plus, words); col.VerticalAlignment = VerticalAlignment.Center; col.Margin = new Thickness(6, 0, 6, 0);
        g.Children.Add(col);
        var b = T.Button(g, () =>
        {
            if (Dialogs.Prompt($"Save a shot on {c.Name}", "Saves where the camera is pointing and how far it's zoomed, right now.", "", "Pulpit") is string name)
                Ptz.SaveShot(name.Trim(), c.Id, ShotKind.Position, null, ok => { if (!ok) Dialogs.Info("Not saved", NoReading); });
        }, T.Transparent, T.Transparent, double.NaN, double.NaN, 8, new Thickness(0), $"Save where {c.Name} is pointing now as a new shot");
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch; b.VerticalContentAlignment = VerticalAlignment.Stretch;
        return b;
    }

    // MARK: Pager

    private UIElement Pager(PTZCamera c)
    {
        int pages = ShotDeckState.Pages(c, Ptz), page = Deck.PageOf(c, Ptz);
        var row = new DockPanel { Height = 22, Margin = new Thickness(0, Gap, 0, 0), Opacity = pages > 1 ? 1 : 0.6, LastChildFill = false };
        Button Arrow(string s, bool on, int by, string tip)
        {
            var b = T.Button(T.Text(s, 12, FontWeights.Bold, on ? T.Ink : T.InkSubtle), () => Deck.Turn(c, by, Ptz), T.SurfaceRaised, T.Transparent, 30, 22, 4, new Thickness(0), tip);
            b.IsEnabled = on;
            return b;
        }
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Arrow("‹", page > 0, -1, "Previous page ([)"));
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        for (int p = 0; p < pages; p++)
        {
            var to = p;
            var dot = new Ellipse { Width = 6, Height = 6, Fill = p == page ? T.Accent : T.LineStrong, Margin = new Thickness(2.5, 0, 2.5, 0), Cursor = Cursors.Hand };
            dot.MouseLeftButtonUp += (_, _) => Deck.SetPage(c, to, Ptz);
            dots.Children.Add(dot);
        }
        left.Children.Add(dots);
        left.Children.Add(Arrow("›", page < pages - 1, 1, "Next page (])"));
        left.Children.Add(T.Text($"Page {page + 1} of {pages}", 11, FontWeights.SemiBold, T.InkSubtle).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(8, 0, 0, 0); }));
        DockPanel.SetDock(left, Dock.Left);
        row.Children.Add(left);
        var hint = T.Text($"Keys Q to G (the letters on each shot) send {c.Name} to that shot", 11, null, T.InkSubtle); hint.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(hint, Dock.Right);
        row.Children.Add(hint);
        return row;
    }

    // MARK: Actions

    /// <summary>Moves a camera, asking first if it's on Live.</summary>
    private static void Go(PTZCamera c, Action run)
    {
        if (OnLive(c) && !Dialogs.Confirm($"{c.Name} is on Live. Move it anyway?", "Everyone watching will see the camera move.", "Move it on Live", danger: true)) return;
        run();
    }
}
