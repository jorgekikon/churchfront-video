using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CueATEM;

namespace CueVideo.App;

/// <summary>
/// Engineer → Routing, a port of the Mac's routing schematic (RoutingPatch.swift) and routing
/// page (Routing.swift). Everything that can be sent is on the left, everywhere it can go is on
/// the right, and a wire for each output's routing. The mixes (M/Es) lead the left column: to
/// an output a mix is a source like any other. Drag a source or a mix onto a destination (or
/// click one, then the other) to change it right away; drop a source on the other mix to put
/// it live there. The pencil on a destination names it, sets which outputs feed it and notes
/// how it's wired. Outputs that feed encoders and recorders are locked so nobody changes the
/// stream by accident, and ports that only go to the patch bay fold away as spare.
/// Below: the operator's routing buttons.
/// </summary>
internal sealed class RoutingPage : LiveView
{
    private static Booth B => Booth.Shared;
    private static ATEMState S => B.Atem.State;
    private static ConfigStore Store => B.Store;

    private int? _selected, _hover, _dropOn;
    private bool _showSpareIn, _showInside, _showSpareOut, _feedsUnlocked;
    private string _message = "";
    private readonly List<(string What, Action Back)> _undo = new();
    private readonly Pictures.Keeper _pics = new();

    // Filled by Build, read by the wire drawing.
    private readonly Dictionary<int, FrameworkElement> _srcEls = new();
    private readonly List<(ScreenOutput Screen, FrameworkElement El)> _dstEls = new();

    /// <summary>None is tally red or green, which only ever mean Live and Next.</summary>
    private static readonly Brush[] Palette = new[] { "#7CC2EE", "#F2B84B", "#B39DF5", "#D9DE6A", "#F58FB8", "#FF9E6B", "#9BB7C9", "#C9C9C9" }.Select(Frozen).ToArray();
    private static Brush Frozen(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    private const double Side = 400, RowH = 40, PicRowH = 76;

    protected override string StructureKey() =>
        string.Join("|", Store.Config.Signature().GetHashCode(), string.Join(",", S.Aux.Select(kv => $"{kv.Key}={kv.Value}")),
            string.Join(",", S.Inputs.Values.Select(i => i.Id + i.Long + i.Short)), string.Join(",", S.Program.Select(kv => $"{kv.Key}={kv.Value}")),
            string.Join(",", S.Preview.Select(kv => $"{kv.Key}={kv.Value}")), B.Me, _showSpareIn, _showInside, _showSpareOut, _feedsUnlocked,
            PTZController.Signature(B.Ptz.Cameras).GetHashCode());

    public override void Teardown() => _pics.Clear();

    // MARK: Pictures

    /// <summary>Whether this source has a live picture to show (a Canon's own, or NDI; a mix shows what's live on it).</summary>
    private static bool HasPic(int? input) => Pictures.Has(input);

    private FrameworkElement Thumb(Func<int?> input, double w, double h)
    {
        var img = new Image { Stretch = Stretch.UniformToFill };
        var g = new Grid { Width = w, Height = h, Background = Brushes.Black, ClipToBounds = true };
        g.Children.Add(img);
        Live(() => img.Source = Pictures.Image(input()));
        return g;
    }

    // MARK: Build

    protected override UIElement Build()
    {
        _srcEls.Clear(); _dstEls.Clear();
        // Keep every source's picture running while this page shows.
        _pics.Want(S.AuxSources.Select(i => (int?)i.Id));

        var col = new StackPanel();
        col.Children.Add(EngineerView.Heading("Routing", "What each mix is for, what every screen shows and where it's wired, and the operator's routing buttons."));
        if (S.Aux.Count == 0) col.Children.Add(T.Text("The switcher hasn't reported its outputs yet. Connect it in Setup.", 13, null, T.InkMuted).Margin(0, 0, 0, 20));
        else col.Children.Add(Schematic());
        col.Children.Add(Buttons());
        return col;
    }

    private UIElement Schematic()
    {
        var cfg = Store.Config;
        var dGroups = RoutingEdit.Destinations(cfg, S);
        var dOpen = new Dictionary<RoutingEdit.Group, bool> { [RoutingEdit.Group.Screens] = true, [RoutingEdit.Group.Feeds] = true, [RoutingEdit.Group.SpareOut] = _showSpareOut };

        // What's in use, so a folded group still shows the sources that matter.
        var inUse = new HashSet<int>();
        foreach (var (g, list) in dGroups) if (dOpen[g]) foreach (var sc in list) if (RoutingEdit.Now(sc, S).Source is int v) inUse.Add(v);
        foreach (var v in S.Program.Values.Concat(S.Preview.Values)) inUse.Add(v);

        // Left: the mixes, then the inputs by kind.
        var left = new StackPanel { Width = Side };
        left.Children.Add(Head(RoutingEdit.Group.Mixes, S.Program.Count, false, true));
        foreach (var me in S.Program.Keys.OrderBy(k => k)) left.Children.Add(MixCard(me));
        var inputs = S.AuxSources.Where(i => !RoutingEdit.IsMix(i.Id) && !(i.Id >= 7000 && i.Id < 7100)).ToList();
        var sOpen = new Dictionary<RoutingEdit.Group, bool> { [RoutingEdit.Group.Cameras] = true, [RoutingEdit.Group.Graphics] = true, [RoutingEdit.Group.Ndi] = true, [RoutingEdit.Group.SpareIn] = _showSpareIn, [RoutingEdit.Group.Inside] = _showInside };
        foreach (var g in new[] { RoutingEdit.Group.Cameras, RoutingEdit.Group.Graphics, RoutingEdit.Group.Ndi, RoutingEdit.Group.SpareIn, RoutingEdit.Group.Inside })
        {
            var all = inputs.Where(i => RoutingEdit.SourceGroup(i, Store.Label(i.Id, S), cfg) == g).ToList();
            if (all.Count == 0) continue;
            var foldable = g is RoutingEdit.Group.SpareIn or RoutingEdit.Group.Inside;
            left.Children.Add(Head(g, all.Count, foldable, sOpen[g]));
            foreach (var i in all.Where(i => sOpen[g] || inUse.Contains(i.Id))) left.Children.Add(SourceRow(i.Id));
        }

        // Right: the destinations.
        var right = new StackPanel { Width = Side };
        foreach (var (g, list) in dGroups)
        {
            if (list.Count == 0) continue;
            right.Children.Add(Head(g, list.Count, g == RoutingEdit.Group.SpareOut, dOpen[g]));
            if (dOpen[g]) foreach (var sc in list) right.Children.Add(DestRow(sc, g == RoutingEdit.Group.Feeds && !_feedsUnlocked));
        }

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Side) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Side) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var h1 = T.Row(8, T.Text("Sources", 17, FontWeights.Bold), T.Text("Mixes and switcher inputs", 12, null, T.InkSubtle).With(t => t.VerticalAlignment = VerticalAlignment.Bottom));
        var h2 = T.Row(8, T.Text("Destinations", 17, FontWeights.Bold), T.Text("Switcher outputs", 12, null, T.InkSubtle).With(t => t.VerticalAlignment = VerticalAlignment.Bottom));
        h1.Margin = h2.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetColumn(h2, 2);
        grid.Children.Add(h1); grid.Children.Add(h2);
        Grid.SetRow(left, 1); Grid.SetRow(right, 1); Grid.SetColumn(right, 2);
        grid.Children.Add(left); grid.Children.Add(right);
        var wires = new Canvas { IsHitTestVisible = false };
        Grid.SetRow(wires, 1); Grid.SetColumnSpan(wires, 3);
        grid.Children.Add(wires);
        Live(() => DrawWires(wires, grid));

        // What's happening, and undo.
        var say = T.Text("", 12, null, T.InkSubtle, wrap: true);
        var undo = T.Small("Undo", () =>
        {
            if (_undo.Count == 0) return;
            var u = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
            u.Back(); _message = $"{u.What} is back to what it was.";
        }, "Put back the last thing changed here");
        Live(() =>
        {
            (say.Text, say.Foreground, say.FontWeight) = _selected is int sel
                ? ($"Now click where {Store.RouteLabel(sel, S)} should go. Click it again to let go.", T.AccentStrong, FontWeights.SemiBold)
                : _message.Length > 0 ? (_message, T.Success, FontWeights.SemiBold)
                : ("Drag a source or a mix onto a destination, or click one and then the other. It changes right away.", T.InkSubtle, FontWeights.Normal);
            undo.IsEnabled = _undo.Count > 0; undo.Opacity = undo.IsEnabled ? 1 : 0.5;
        });
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 24) };
        DockPanel.SetDock(undo, Dock.Right);
        bar.Children.Add(undo); bar.Children.Add(say.With(t => t.VerticalAlignment = VerticalAlignment.Center));
        return T.Column(0, grid, bar);
    }

    private UIElement Head(RoutingEdit.Group g, int count, bool foldable, bool open)
    {
        var row = new DockPanel { Height = 24, Margin = new Thickness(0, 6, 0, 2), Background = Brushes.Transparent };
        if (g == RoutingEdit.Group.Feeds)
        {
            var lockT = T.Text(_feedsUnlocked ? "🔓 Unlocked" : "🔒 Locked", 11, FontWeights.SemiBold, _feedsUnlocked ? T.InkMuted : T.Warning);
            var lockB = T.Button(lockT, () => _feedsUnlocked = !_feedsUnlocked, T.Transparent, T.Transparent, double.NaN, 22, 4, new Thickness(4, 0, 4, 0),
                _feedsUnlocked ? "Lock these again" : "One of these may carry the live stream. Unlock to change them.");
            DockPanel.SetDock(lockB, Dock.Right);
            row.Children.Add(lockB);
        }
        var parts = new List<UIElement>();
        if (foldable) parts.Add(T.Text(open ? "▾" : "▸", 11, FontWeights.Bold, T.InkSubtle));
        parts.Add(T.Caps(g.Title()));
        if (foldable) parts.Add(T.Text(count.ToString(), 11, FontWeights.SemiBold, T.InkSubtle));
        var label = T.Row(6, parts.ToArray());
        label.VerticalAlignment = VerticalAlignment.Center;
        if (!foldable) { row.Children.Add(label); return row; }
        // A folding head is a button: click (or any assistive tool) shows or hides the group.
        var fold = T.Button(label, () =>
        {
            if (g == RoutingEdit.Group.SpareIn) _showSpareIn = !_showSpareIn;
            else if (g == RoutingEdit.Group.Inside) _showInside = !_showInside;
            else _showSpareOut = !_showSpareOut;
        }, T.Transparent, T.Transparent, double.NaN, 22, 4, new Thickness(0, 0, 6, 0),
            g is RoutingEdit.Group.SpareIn or RoutingEdit.Group.SpareOut ? "Ports that only go to patch bay tie lines (and outputs not named yet). Click to show or hide." : "Click to show or hide");
        fold.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(fold);
        return row;
    }

    /// <summary>"IN 9", "OUT 4+5": which connector on the switcher this is.</summary>
    private static Border Port(string text) => new()
    {
        Background = T.SurfaceRaised, BorderBrush = T.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontFamily = T.Mono, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = T.InkMuted },
    };

    private static string ShortMix(int me) => me < Store.Config.MixNames.Count && Store.Config.MixNames[me].Length > 0 ? Store.Config.MixNames[me] : $"M/E {me + 1}";

    /// <summary>Where a source is in use on the mixes: "Live · M/E 1", "Next · Side screens".</summary>
    private static List<(string Text, Brush Bg, Brush Fg)> Tallies(int id, bool shortNames)
    {
        var o = new List<(string, Brush, Brush)>();
        foreach (var me in S.Program.Keys.OrderBy(k => k))
        {
            var n = shortNames ? $"M/E {me + 1}" : ShortMix(me);
            if (S.ProgramOf(me) == id) o.Add(($"Live · {n}", T.Program, Brushes.White));
            else if (S.PreviewOf(me) == id) o.Add(($"Next · {n}", T.Preview, T.OnAction));
        }
        return o;
    }

    /// <summary>A row is a button: a click (or assistive tools, or UI Automation) chooses or routes,
    /// and the pencil inside it is a button of its own.</summary>
    private static Button RowButton(UIElement content, double h, Thickness pad, string tip)
    {
        var b = T.Button(content, null, T.SurfaceSunken, T.Line, double.NaN, h, 8, pad, tip);
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        b.Margin = new Thickness(0, 0, 0, 6);
        return b;
    }

    private Button SourceRow(int id)
    {
        var pic = HasPic(id);
        var dock = new DockPanel { LastChildFill = true };
        // The pencil on a source: the NDI picture that shows what this switcher input carries.
        // Only for looking at here; it changes nothing on the switcher. A Canon shows its own.
        if (id >= 1 && id <= 999 && Pictures.Canon(id) == null)
        {
            var input = id;
            Button? pencil = null;
            pencil = T.Button(T.Text("✎", 14, null, T.InkMuted), () => NdiPicker.Open(pencil!, Store.Ndi(input), "No picture, just the name", n => Store.SetPicture(input, n)),
                T.Transparent, T.Transparent, 28, 28, 4, new Thickness(0), "Choose a live NDI picture for this source");
            DockPanel.SetDock(pencil, Dock.Right);
            dock.Children.Add(pencil);
        }
        if (id >= 1 && id <= 999) { var p = Port($"IN {id}"); DockPanel.SetDock(p, Dock.Right); dock.Children.Add(p); }
        if (pic) { var th = Thumb(() => id, 120, 68); th.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(th, Dock.Left); dock.Children.Add(th); }
        var words = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = T.Text(Store.Label(id, S), pic ? 18 : 15, FontWeights.SemiBold);
        name.Margin = new Thickness(0, 0, 8, 0); name.TextTrimming = TextTrimming.CharacterEllipsis;
        words.Children.Add(name);
        foreach (var (t, bg, fg) in Tallies(id, !pic)) words.Children.Add(T.Badge(t, fg, bg).With(b => b.Margin = new Thickness(0, 2, 6, 2)));
        dock.Children.Add(words);
        var row = RowButton(dock, pic ? PicRowH : RowH, new Thickness(pic ? 4 : 10, 0, 6, 0),
            (id >= 1 && id <= 999 ? $"Switcher input {id}: {(S.Inputs.TryGetValue(id, out var x) ? x.Long : "")}\n" : "")
            + "Drag onto a destination or a mix, or click this and then click where it should go");
        _srcEls[id] = row;
        PickUp(row, id);
        return row;
    }

    private Button MixCard(int me)
    {
        var mine = me == B.Me;
        var output = 10010 + me * 10;
        var title = new DockPanel { Height = 28 };
        var pencil = T.Button(T.Text("✎", 14, null, T.InkMuted), () =>
        {
            if (Dialogs.Prompt($"What M/E {me + 1} is for", "Such as Live stream or Side screens. It's how this mix is named on every page and to Claude.",
                    me < Store.Config.MixNames.Count ? Store.Config.MixNames[me] : "", "Live stream") is string n)
                Store.Update(c =>
                {
                    while (c.MixNames.Count <= me) c.MixNames.Add("");
                    c.MixNames[me] = n;
                    while (c.MixNames.Count > 0 && c.MixNames[^1].Length == 0) c.MixNames.RemoveAt(c.MixNames.Count - 1);
                });
        }, T.Transparent, T.Transparent, 26, 26, 4, new Thickness(0), "Name what this mix is for");
        DockPanel.SetDock(pencil, Dock.Right);
        title.Children.Add(pencil);
        var hint = T.Text(mine ? "Run from Operator" : "Drop a source to put it live", 12, null, T.InkSubtle);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(hint, Dock.Right);
        title.Children.Add(hint);
        title.Children.Add(T.Row(6, T.Text(ShortMix(me), 17, FontWeights.Bold), T.Text($"M/E {me + 1}", 12, null, T.InkSubtle).With(t => t.VerticalAlignment = VerticalAlignment.Bottom)));

        var slotW = (Side - 26) / 2;
        UIElement Slot(string tag, Brush color, Brush ink, Func<int?> input)
        {
            var g = new Grid { Width = slotW, Height = Math.Round(slotW * 9 / 16), Background = T.SurfaceSunken, ClipToBounds = true };
            g.Children.Add(Thumb(input, slotW, Math.Round(slotW * 9 / 16)));
            var label = T.Text("", 14, FontWeights.SemiBold, Brushes.White);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            g.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), CornerRadius = new CornerRadius(4), Margin = new Thickness(4), Padding = new Thickness(4, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = T.Row(5, T.Badge(tag, ink, color), label),
            });
            Live(() => label.Text = Store.Label(input(), S));
            return new Border { Child = g, BorderBrush = color, BorderThickness = new Thickness(tag == "Live" ? 2.5 : 1.5) };
        }
        var slots = T.Row(6, Slot("Live", T.Program, Brushes.White, () => S.ProgramOf(me)), Slot("Next", T.Preview, T.OnAction, () => S.PreviewOf(me)));
        var card = T.Button(T.Column(6, title, slots), null, T.SurfaceRaised, T.LineStrong, double.NaN, double.NaN, 12, new Thickness(10, 6, 10, 10),
            mine ? $"{Store.MixName(me)}: what Operator cuts and fades. Drag it onto a destination to send it there."
                 : $"{Store.MixName(me)}: drop a source here to put it live, or drag this onto a destination");
        card.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        card.Margin = new Thickness(0, 0, 0, 8);
        card.BorderThickness = new Thickness(2.5);
        _srcEls[output] = card;
        PickUp(card, output, onClick: () =>
        {
            if (_selected is int sel && !RoutingEdit.IsMix(sel) && !mine) { PutLive(sel, me); return true; }
            return false;
        });
        if (!mine)
        {
            card.AllowDrop = true;
            card.DragOver += (_, e) => { e.Effects = Dragged(e) is int s && !RoutingEdit.IsMix(s) ? DragDropEffects.Copy : DragDropEffects.None; _dropOn = e.Effects == DragDropEffects.Copy ? output : null; e.Handled = true; };
            card.DragLeave += (_, _) => _dropOn = null;
            card.Drop += (_, e) => { _dropOn = null; if (Dragged(e) is int s && !RoutingEdit.IsMix(s)) PutLive(s, me); };
        }
        return card;
    }

    private Button DestRow(ScreenOutput sc, bool locked)
    {
        var dock = new DockPanel { LastChildFill = true };
        var named = RoutingEdit.IsNamed(Store.Config, sc);
        var pencil = T.Button(T.Text("✎", 14, null, T.InkMuted), () => EditScreen(sc), T.Transparent, T.Transparent, 28, 28, 4, new Thickness(0),
            named ? "Change the name, outputs or wiring note" : "Name this output");
        DockPanel.SetDock(pencil, Dock.Right);
        dock.Children.Add(pencil);
        if (locked) { var l = T.Text("🔒", 12, null, T.Warning).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(4, 0, 4, 0); }); DockPanel.SetDock(l, Dock.Right); dock.Children.Add(l); }
        var (src, _) = RoutingEdit.Now(sc, S);
        var pic = HasPic(src);
        var port = Port("OUT " + string.Join("+", sc.Aux));
        port.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(port, Dock.Left);
        dock.Children.Add(port);
        if (pic) { var th = Thumb(() => RoutingEdit.Now(sc, S).Source, 120, 68); th.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(th, Dock.Left); dock.Children.Add(th); }
        var showing = T.Text("", pic ? 14 : 13, null, T.InkMuted);
        showing.TextTrimming = TextTrimming.CharacterEllipsis;
        Live(() =>
        {
            var (s, mixed) = RoutingEdit.Now(sc, S);
            showing.Text = mixed ? "← Mixed" : s is int v ? "← " + (RoutingEdit.IsMix(v) && v % 10 == 0 ? ShortMix((v - 10000) / 10 - 1) : Store.RouteLabel(v, S)) : "";
        });
        var name = T.Text(sc.Name, pic ? 18 : 15, FontWeights.SemiBold, named ? T.Ink : T.InkMuted);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        dock.Children.Add(pic ? T.Column(2, name, showing).With(c => c.VerticalAlignment = VerticalAlignment.Center)
                              : new DockPanel { VerticalAlignment = VerticalAlignment.Center }.With(d => { DockPanel.SetDock(showing, Dock.Right); showing.Margin = new Thickness(8, 0, 4, 0); d.Children.Add(showing); d.Children.Add(name); }));
        var row = RowButton(dock, pic ? PicRowH : RowH, new Thickness(6, 0, 2, 0),
            $"{sc.Name}, switcher output {string.Join(", ", sc.Aux)}" + (string.IsNullOrEmpty(sc.Note) ? "" : "\n" + sc.Note)
            + (locked ? "\nLocked. Unlock Encoders and feeds to change it." : "\nClick a source or a mix first, then this, to send it here."));
        row.AllowDrop = true;
        _dstEls.Add((sc, row));
        row.MouseEnter += (_, _) => _hover = RoutingEdit.Now(sc, S).Source;
        row.MouseLeave += (_, _) => _hover = null;
        row.Click += (_, _) => { if (_selected is int s) Route(s, sc, locked); };
        row.DragOver += (_, e) => { e.Effects = Dragged(e) != null && !locked ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        row.Drop += (_, e) => { if (Dragged(e) is int s) Route(s, sc, locked); };
        row.Tag = locked;
        return row;
    }

    // MARK: Picking up, dragging, dropping

    private const string DragFormat = "cuevideo-route-source";
    private static int? Dragged(DragEventArgs e) => e.Data.GetDataPresent(DragFormat) ? (int)e.Data.GetData(DragFormat) : null;

    /// <summary>Something that can be picked up as a source: hover to see its wires, click to
    /// choose it, drag it onto a destination or a mix. `onClick` gets first say on a click.</summary>
    private void PickUp(Button el, int id, Func<bool>? onClick = null)
    {
        Point? down = null;
        var dragged = false;
        el.MouseEnter += (_, _) => _hover = id;
        el.MouseLeave += (_, _) => { if (_hover == id) _hover = null; };
        el.PreviewMouseLeftButtonDown += (_, e) => { dragged = false; down = InButton(e.OriginalSource as DependencyObject, el) ? null : e.GetPosition(el); };
        el.PreviewMouseMove += (_, e) =>
        {
            if (down is not Point p || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(el);
            if (Math.Abs(now.X - p.X) < 6 && Math.Abs(now.Y - p.Y) < 6) return;
            down = null; dragged = true;
            _selected = null; _message = "";
            el.ReleaseMouseCapture();
            DragDrop.DoDragDrop(el, new DataObject(DragFormat, id), DragDropEffects.Copy);
            _dropOn = null;
        };
        el.Click += (_, e) =>
        {
            if (dragged) { dragged = false; return; }
            if (e.OriginalSource != el) return;   // the pencil inside
            if (onClick?.Invoke() == true) return;
            _selected = _selected == id ? null : id;
            _message = "";
        };
    }

    /// <summary>Whether a click landed on a button inside the row (the pencil), which handles it itself.</summary>
    private static bool InButton(DependencyObject? d, DependencyObject row)
    {
        for (var x = d; x != null && x != row; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is Button) return true;
        return false;
    }

    private void Route(int source, ScreenOutput sc, bool locked)
    {
        _selected = null;
        if (locked) { _message = $"{sc.Name} is locked. Unlock Encoders and feeds to change it."; return; }
        var was = sc.Aux.Where(a => S.Aux.ContainsKey(a - 1)).Select(a => (Aux: a, Source: S.Aux[a - 1])).ToList();
        if (was.Count == sc.Aux.Count && was.All(w => w.Source == source)) return;
        var atem = B.Atem;
        Push(sc.Name, () => { foreach (var (a, v) in was) atem.SetAux(a - 1, v); });
        foreach (var a in sc.Aux) atem.SetAux(a - 1, source);
        _message = $"{sc.Name} now shows {Store.RouteLabel(source, S)}.";
    }

    private void PutLive(int source, int me)
    {
        _selected = null;
        if (S.ProgramOf(me) is not int was || was == source) return;
        var atem = B.Atem;
        Push(Store.MixName(me), () => atem.SetProgram(was, me));
        atem.SetProgram(source, me);
        _message = $"{Store.Label(source, S)} is now live on {Store.MixName(me)}.";
    }

    private void Push(string what, Action back)
    {
        _undo.Add((what, back));
        if (_undo.Count > 20) _undo.RemoveAt(0);
    }

    // MARK: Wires

    /// <summary>A curve from each destination back to what feeds it. All of a source's curves
    /// leave from one point, so a mix feeding five outputs reads as one fan.</summary>
    private void DrawWires(Canvas canvas, Grid grid)
    {
        canvas.Children.Clear();
        if (grid.ActualWidth < 1) return;
        var focus = _selected ?? _hover;
        // Colors: the mixes first, then each source that feeds something, in order down the page.
        var colors = new Dictionary<int, Brush>();
        var n = 0;
        foreach (var me in S.Program.Keys.OrderBy(k => k)) colors[10010 + me * 10] = Palette[n++ % Palette.Length];
        var feeds = _dstEls.Select(d => (d.El, Src: RoutingEdit.Now(d.Screen, S).Source)).ToList();
        foreach (var id in _srcEls.Keys) if (!colors.ContainsKey(id) && feeds.Any(f => f.Src == id)) colors[id] = Palette[n++ % Palette.Length];

        foreach (var (id, el) in _srcEls)
        {
            var on = focus == id || (_dropOn == id);
            var c = colors.GetValueOrDefault(id);
            el.Opacity = 1;
            if (el is Control b)
            {
                b.BorderBrush = on ? T.AccentStrong : c ?? (RoutingEdit.IsMix(id) ? T.LineStrong : T.Line);
                b.BorderThickness = new Thickness(on ? 3 : c != null || RoutingEdit.IsMix(id) ? 2 : 1);
            }
            var ends = feeds.Where(f => f.Src == id && f.El.ActualHeight > 0).Select(f => f.El.TranslatePoint(new Point(0, f.El.ActualHeight / 2), grid)).ToList();
            if (ends.Count == 0 || el.ActualHeight == 0) continue;
            var start = el.TranslatePoint(new Point(el.ActualWidth, el.ActualHeight / 2), grid);
            start.Y -= GridRowTop(grid);
            var dim = focus != null && focus != id;
            var geo = new PathGeometry();
            foreach (var e0 in ends)
            {
                var e = new Point(e0.X, e0.Y - GridRowTop(grid));
                var reach = (e.X - start.X) * 0.5;
                var f = new PathFigure { StartPoint = start };
                f.Segments.Add(new BezierSegment(new Point(start.X + reach, start.Y), new Point(e.X - reach, e.Y), e, true));
                geo.Figures.Add(f);
                canvas.Children.Add(Dot(e, 4, c!, dim));
            }
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = geo, Stroke = c, StrokeThickness = focus == id ? 4 : 3, Opacity = dim ? 0.15 : 1, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            canvas.Children.Add(Dot(start, 5, c!, dim));
        }
        foreach (var (sc, el) in _dstEls)
        {
            var src = RoutingEdit.Now(sc, S).Source;
            var target = _selected != null && el.Tag is false;
            if (el is Control b)
            {
                b.BorderBrush = target ? T.AccentStrong : src is int s && colors.TryGetValue(s, out var c) ? c : T.Line;
                b.BorderThickness = new Thickness(target ? 3 : src != null && colors.ContainsKey(src.Value) ? 2 : 1);
            }
        }
    }

    private static double GridRowTop(Grid g) => g.RowDefinitions[1].Offset;

    private static Ellipse Dot(Point p, double r, Brush c, bool dim)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = c, Opacity = dim ? 0.15 : 1 };
        Canvas.SetLeft(e, p.X - r); Canvas.SetTop(e, p.Y - r);
        return e;
    }

    // MARK: Destination editor

    /// <summary>The pencil on a destination: its name, which outputs feed it, its wiring note,
    /// and (once named) whether operators see it and whether it's previewed in the top bar.</summary>
    private static void EditScreen(ScreenOutput sc)
    {
        var named = RoutingEdit.IsNamed(Store.Config, sc);
        var auxLabel = (sc.Aux.Count == 1 ? "Aux " : "Aux ") + string.Join(", ", sc.Aux);
        var win = Dialogs.Make(named ? $"Edit {sc.Name}" : $"Name {auxLabel}", 400);
        TextBox Box(string text, string hint)
        {
            var tb = new TextBox
            {
                Text = text, Height = 32, Background = T.SurfaceSunken, Foreground = T.Ink, BorderBrush = T.LineStrong, CaretBrush = T.Ink,
                Padding = new Thickness(6, 4, 6, 4), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center,
            };
            T.Placeholder(tb, hint);
            return tb;
        }
        var name = Box(named ? sc.Name : "", "Such as Lobby TVs");
        var aux = Box(string.Join(", ", sc.Aux), "4, 5");
        var note = Box(sc.Note ?? "", "Such as Converter 1 → Lobby TV");
        var problem = T.Text("", 12, FontWeights.SemiBold, T.Warning, wrap: true);
        UIElement Field(string label, UIElement box, string? sub = null)
        {
            var c = T.Column(4, T.Text(label, 11, FontWeights.SemiBold, T.InkMuted), box);
            if (sub != null) c.Children.Add(T.Text(sub, 11, null, T.InkSubtle, wrap: true));
            return c;
        }
        var col = T.Column(12, T.Text(named ? $"Edit {sc.Name}" : $"Name {auxLabel}", 15, FontWeights.Bold),
            Field("Name", name), Field("Aux outputs", aux, "Counted from 1. Several, such as 4, 5, switch together."), Field("Wiring note", note));
        bool? volunteer = sc.ForVolunteers, pinned = Store.PinnedScreens.Any(p => p.Id == sc.Id);
        if (named)
        {
            col.Children.Add(T.Check("Preview in the top bar", pinned == true, v => pinned = v).With(c => c.ToolTip = "A small live picture of what this is showing, in the bar across the top. Up to four."));
            col.Children.Add(T.Check("Show to operators", volunteer == true, v => volunteer = v).With(c => c.ToolTip = "Off for encoders, recorders and patch bay feeds: they move to Encoders and feeds, locked, and Claude needs to be told to change them."));
        }
        col.Children.Add(problem);
        void Save()
        {
            string? why = null;
            Store.Update(c =>
            {
                why = RoutingEdit.SaveScreen(c, named ? sc : null, name.Text, aux.Text, note.Text);
                if (why == null && named && c.Screens.FirstOrDefault(x => x.Name == name.Text.Trim()) is ScreenOutput s) s.Volunteer = volunteer == true ? null : false;
            });
            if (why != null) { problem.Text = why; return; }
            var saved = Store.Config.Screens.FirstOrDefault(x => x.Name == name.Text.Trim());
            if (named && saved != null && Store.PinnedScreens.Any(p => p.Id == saved.Id) != (pinned == true)) Store.SetPinned(saved.Id, pinned == true);
            win.Close();
        }
        var buttons = new DockPanel();
        if (named)
        {
            var forget = T.Small("Forget this name", () =>
            {
                Store.Update(c => c.Screens.RemoveAll(x => x.Id == sc.Id));
                win.Close();
            }, "The output stays; it goes back to Spare outputs, not named yet.", danger: true);
            DockPanel.SetDock(forget, Dock.Left);
            buttons.Children.Add(forget);
        }
        var right = T.Row(8, T.Small("Cancel", () => win.Close()), T.Primary("Save", Save));
        right.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Children.Add(right);
        col.Children.Add(buttons);
        foreach (var tb in new[] { name, aux, note }) tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); if (e.Key == Key.Escape) win.Close(); };
        win.Content = col.Margin(20, 18, 20, 18);
        win.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        win.ShowDialog();
    }

    // MARK: Operator buttons

    private UIElement Buttons()
    {
        var col = new StackPanel();
        var add = T.Small("Save the current routing as a button…", NewButton, "Pick screens; the button sets them to what they show now");
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        head.Children.Add(T.Caps("Operator routing buttons").With(t => t.VerticalAlignment = VerticalAlignment.Center));
        col.Children.Add(head);
        col.Children.Add(T.Text("Buttons on Operator, under Screens. Each one sends one or more screens a picture, like a macro, and lights up while the screens match it. The easy way to make one: route the screens the way you want above, then save that as a button.",
            12, null, T.InkMuted, wrap: true).Margin(0, 0, 0, 10));
        var routes = Store.Config.Routes;
        if (routes.Count == 0) col.Children.Add(T.Text("No buttons yet.", 13, null, T.InkSubtle));
        var wrap = new WrapPanel();
        for (int i = 0; i < routes.Count; i++)
        {
            var r = routes[i]; var idx = i;
            var label = T.Field(r.Label, v => { if (v.Trim().Length > 0) Store.Update(c => { if (idx < c.Routes.Count) c.Routes[idx].Label = v.Trim(); }); }, 200);
            var on = T.Badge("On", T.OnAction, T.Accent);
            var detail = T.Text("", 12, null, T.InkMuted, wrap: true);
            Button Icon(string glyph, Action a, string tip, bool enabled = true) =>
                T.Button(T.Text(glyph, 13, FontWeights.Bold, T.InkMuted), a, T.Transparent, T.Transparent, 28, 28, 4, new Thickness(0), tip).With(b => { b.IsEnabled = enabled; b.Opacity = enabled ? 1 : 0.35; });
            var tools = T.Row(2, T.Small("Run now", () => r.Run(B.Atem)),
                Icon("◀", () => Store.Update(c => RoutingEdit.MoveButton(c, idx, -1)), "Move earlier", idx > 0),
                Icon("▶", () => Store.Update(c => RoutingEdit.MoveButton(c, idx, 1)), "Move later", idx < routes.Count - 1),
                Icon("🗑", () => { if (Dialogs.Confirm($"Remove {r.Label}?", "The button goes from Operator. The screens aren't changed.", "Remove", danger: true)) Store.Update(c => { if (idx < c.Routes.Count) c.Routes.RemoveAt(idx); }); }, "Remove this button"));
            var card = T.Card(T.Column(8, T.Row(8, label, on), detail, tools), 12);
            card.Width = 300; card.Margin = new Thickness(0, 0, 12, 12);
            Live(() =>
            {
                var lit = r.IsActive(S);
                on.Visibility = lit ? Visibility.Visible : Visibility.Collapsed;
                card.BorderBrush = lit ? T.Accent : T.Line;
                detail.Text = Store.RouteDetail(r, S);
            });
            wrap.Children.Add(card);
        }
        col.Children.Add(wrap);
        return col;
    }

    /// <summary>Saves what chosen screens show now as an operator button.</summary>
    private static void NewButton()
    {
        var win = Dialogs.Make("New routing button", 420);
        var label = new TextBox
        {
            Height = 32, Background = T.SurfaceSunken, Foreground = T.Ink, BorderBrush = T.LineStrong, CaretBrush = T.Ink,
            Padding = new Thickness(6, 4, 6, 4), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center,
        };
        T.Placeholder(label, "Label, such as Lobby: stream");
        var picked = new HashSet<string>();
        var list = new StackPanel();
        foreach (var sc in Store.Config.Screens)
        {
            var id = sc.Id;
            list.Children.Add(T.Check($"{sc.Name}  ←  {Store.Showing(sc, S)}", false, v => { if (v) picked.Add(id); else picked.Remove(id); }).Margin(0, 2, 0, 2));
        }
        if (Store.Config.Screens.Count == 0) list.Children.Add(T.Text("Name the screens first, with the pencil on each output.", 12, null, T.Warning, wrap: true));
        var problem = T.Text("", 12, FontWeights.SemiBold, T.Warning, wrap: true);
        void Save()
        {
            string? why = null;
            var cfg = Store.Config.Clone();
            why = RoutingEdit.SaveButton(cfg, label.Text, picked, S);
            if (why != null) { problem.Text = why; return; }
            Store.Update(c => RoutingEdit.SaveButton(c, label.Text, picked, S));
            win.Close();
        }
        var buttons = T.Row(8, T.Small("Cancel", () => win.Close()), T.Primary("Save button", Save));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        label.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); if (e.Key == Key.Escape) win.Close(); };
        win.Content = T.Column(12, T.Text("New routing button", 15, FontWeights.Bold), label,
            T.Text("Screens it sets, to what they show now", 12, null, T.InkMuted), list, problem, buttons).Margin(20, 18, 20, 18);
        win.Loaded += (_, _) => label.Focus();
        win.ShowDialog();
    }
}
