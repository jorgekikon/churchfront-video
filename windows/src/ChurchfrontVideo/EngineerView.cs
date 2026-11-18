using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CueATEM;

namespace CueVideo.App;

/// <summary>The Engineer view: what's on air now along the top, the pages down the side.
/// Space, Return and the number keys do nothing here.</summary>
public sealed class EngineerView : Grid
{
    private static Booth B => Booth.Shared;
    private readonly ContentControl _page = new();
    private LiveView? _current;
    private EngineerSection? _shown;
    private readonly List<(EngineerSection S, Button B, TextBlock T1, TextBlock T2)> _tabs = new();
    private readonly TextBlock _liveName = T.Text("", 13, FontWeights.SemiBold), _nextName = T.Text("", 13, FontWeights.SemiBold);

    public EngineerView()
    {
        Background = T.Bg;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        ColumnDefinitions.Add(new ColumnDefinition());

        // On air now
        var bar = new DockPanel { Margin = new Thickness(16, 8, 16, 8) };
        var back = T.Small("Back to Operator", () => B.View = AppView.Operate);
        DockPanel.SetDock(back, Dock.Right);
        bar.Children.Add(back);
        var note = T.Text("Space, Return and the number keys do nothing in this view.", 12, null, T.InkMuted).Margin(0, 0, 12, 0);
        DockPanel.SetDock(note, Dock.Right);
        bar.Children.Add(note);
        bar.Children.Add(T.Row(10, T.Caps("On air now"),
            Pill(T.Badge("Live", Brushes.White, T.Program), _liveName, T.Program),
            Pill(T.Badge("Next", T.OnAction, T.Preview), _nextName, T.Preview)));
        var barBorder = new Border { Child = bar, BorderBrush = T.Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = T.Toolbar };
        SetColumnSpan(barBorder, 2);
        Children.Add(barBorder);

        // Sidebar
        var side = new StackPanel { Margin = new Thickness(8, 14, 8, 8) };
        foreach (var (s, sub) in new[] { (EngineerSection.Scan, "People, labels, shots"), (EngineerSection.Routing, "Screens and buttons"),
                                         (EngineerSection.Switcher, "Keys, T-bar, macros, aux"), (EngineerSection.Setup, "Sources, keys, cameras"),
                                         (EngineerSection.Status, "Connections and log") })
        {
            var t1 = T.Text(s.Title(), 13, FontWeights.SemiBold); var t2 = T.Text(sub, 11, null, T.InkMuted);
            var section = s;
            var b = T.Button(T.Column(1, t1, t2), () => B.Show(section), T.Transparent, T.Transparent, h: 46, radius: 8, pad: new Thickness(12, 4, 8, 4));
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(0, 0, 0, 2);
            _tabs.Add((s, b, t1, t2));
            side.Children.Add(b);
        }
        var sideBorder = new Border { Child = side, BorderBrush = T.Line, BorderThickness = new Thickness(0, 0, 1, 0), Background = T.Toolbar };
        SetRow(sideBorder, 1);
        Children.Add(sideBorder);

        var scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(20, 18, 20, 24) };
        SetRow(scroll, 1); SetColumn(scroll, 1);
        Children.Add(scroll);
    }

    private static Border Pill(UIElement badge, TextBlock name, Brush color) => new()
    {
        BorderBrush = color, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(6), Padding = new Thickness(4, 3, 10, 3),
        Child = T.Row(8, badge, name),
    };

    public void Refresh()
    {
        var s = B.Atem.State;
        _liveName.Text = B.Store.Label(s.ProgramOf(B.Me), s);
        _nextName.Text = B.Store.Label(s.PreviewOf(B.Me), s);
        foreach (var (sec, b, t1, t2) in _tabs)
        {
            var on = sec == B.Section;
            b.Background = on ? T.Accent : T.Transparent;
            t1.Foreground = on ? T.OnAction : T.Ink;
            t2.Foreground = on ? T.OnAction : T.InkMuted;
        }
        if (_shown != B.Section)
        {
            _current?.Teardown();
            _shown = B.Section;
            _current = B.Section switch
            {
                EngineerSection.Routing => new RoutingPage(),
                EngineerSection.Switcher => new SwitcherPage(),
                EngineerSection.Setup => new SetupPage(),
                EngineerSection.Status => new StatusPage(),
                _ => new ScanPage(),
            };
            _page.Content = _current;
        }
        _current?.Refresh();
    }

    public void Leave() { _current?.Teardown(); }

    internal static UIElement Heading(string title, string subtitle, UIElement? right = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        if (right != null) { DockPanel.SetDock(right, Dock.Right); d.Children.Add(right); }
        d.Children.Add(T.Column(4, T.Text(title, 24, FontWeights.Bold), T.Text(subtitle, 13, null, T.InkMuted, wrap: true)));
        return d;
    }
}

// MARK: - Stage scan

/// <summary>Engineer → Stage scan: find everyone on stage, label them, and save a shot for each
/// person on each camera. Laid out as its three steps. See StageScan.</summary>
internal sealed class ScanPage : LiveView
{
    private static Booth B => Booth.Shared;
    private static StageScan Scan => StageScan.Shared;
    private List<string> _saved = new();

    private static List<PTZCamera> Cameras => B.Ptz.Cameras.Where(c => c.Host.Length > 0).ToList();

    protected override string StructureKey() =>
        $"{PTZController.Signature(B.Ptz.Cameras).GetHashCode()}|{Scan.Version}|{string.Join(",", StageCast.Shared.Names)}|{_saved.Count}";

    private static StackPanel Step(int n, string title, params UIElement[] content)
    {
        var col = T.Column(10, T.Step(n, title));
        foreach (var c in content) col.Children.Add(c);
        return col;
    }

    protected override UIElement Build()
    {
        var page = new StackPanel { MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(EngineerView.Heading("Stage scan", "With everyone in place, find each person, name them, and save a shot of them on every camera."));
        page.Children.Add(T.Text("Each camera that isn't on Live goes to a wide shot, finds the people in it, and comes back. The wide shot is a camera's shot named Stage or Wide if it has one; otherwise it's where the camera points now, zoomed all the way out. A tighter Stage shot makes people bigger in the picture and easier to find. The shots are for where people stood during the scan, so run it at rehearsal or soundcheck.",
            12, null, T.InkMuted, wrap: true).Margin(0, -6, 0, 16));

        // 1 Cameras
        var camList = new StackPanel();
        if (Cameras.Count == 0) camList.Children.Add(T.Text("No cameras set up yet. Add them in Setup.", 13, null, T.InkMuted));
        foreach (var c in Cameras)
        {
            var cam = c;
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            var name = T.Text(c.Name, 13, FontWeights.SemiBold); name.Width = 110; name.TextTrimming = TextTrimming.CharacterEllipsis;
            var what = T.Text("", 12, null, T.InkMuted, wrap: true);
            Live(() =>
            {
                var why = Scan.Blocked(cam);
                dot.Fill = why == null ? T.Success : T.LineStrong;
                what.Text = why == null ? "Will go wide and come back" : $"Left out: {why}";
                what.Foreground = why == null ? T.InkMuted : T.Warning;
            });
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(dot, Dock.Left); DockPanel.SetDock(name, Dock.Left);
            dot.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(dot); row.Children.Add(name); row.Children.Add(what);
            camList.Children.Add(row);
        }

        // 2 Scan
        var sizes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var z in ShotSizes.All)
        {
            var size = z;
            var l = T.Text(z.Name(), 12, FontWeights.SemiBold);
            var b = T.Button(l, () => Scan.Size = size, T.SurfaceSunken, T.Transparent, double.NaN, 28, 6, new Thickness(10, 0, 10, 0));
            b.Margin = new Thickness(0, 0, 4, 0);
            Live(() => { var on = Scan.Size == size; b.Background = on ? T.Accent : T.SurfaceSunken; l.Foreground = on ? T.OnAction : T.InkMuted; });
            sizes.Children.Add(b);
        }
        var go = T.Primary("Scan the stage", () => { _saved = new(); Scan.Scan(); }, "Moves each camera marked \"Will go wide and come back\". Cameras on Live don't move.");
        var goText = (TextBlock)go.Content;
        var state = T.Text("", 12, null, T.InkSubtle, wrap: true);
        var notes = T.Text("", 12, FontWeights.SemiBold, T.Warning, wrap: true);
        Live(() =>
        {
            var ready = Cameras.Count(c => Scan.Blocked(c) == null);
            goText.Text = Scan.Running ? "Scanning…" : Scan.People.Count == 0 ? "Scan the stage" : "Scan again";
            go.IsEnabled = !Scan.Running && ready > 0 && Scan.Unavailable == null;
            go.Opacity = go.IsEnabled ? 1 : 0.5;
            (state.Text, state.Foreground) = Scan.Running ? (Scan.Progress, T.InkMuted)
                : Scan.Unavailable is string u ? (u, T.Warning)
                : ready == 0 ? ("No camera is free to scan right now.", T.Warning)
                : ($"{ready} camera{(ready == 1 ? "" : "s")} will move.", T.InkSubtle);
            notes.Text = string.Join("\n", Scan.Notes);
            notes.Visibility = Scan.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        var top = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        var c1 = T.Card(Step(1, "Cameras", camList));
        var c2 = T.Card(Step(2, "Scan", T.Row(10, T.Text("Frame each person", 12, null, T.InkMuted).With(t => t.VerticalAlignment = VerticalAlignment.Center), sizes), T.Row(12, go, state.With(s => s.VerticalAlignment = VerticalAlignment.Center)), notes));
        Grid.SetColumn(c2, 2);
        top.Children.Add(c1); top.Children.Add(c2);
        page.Children.Add(top);

        // 3 Name people and save
        var step3 = new StackPanel();
        if (Scan.People.Count == 0)
        {
            step3.Children.Add(T.Text(Scan.ScannedAt == null ? "After a scan, each person each camera saw appears here with a box for their name."
                                                              : "The last scan found no one. Check the notes above, then scan again.", 13, null, T.InkMuted, wrap: true));
        }
        else
        {
            step3.Children.Add(T.Text("Type a name or a role under each person. The letter is Churchfront Video's guess at the same person on another camera: naming one fills in the others with that letter. Fix any it got wrong, and leave a person blank to skip them.",
                12, null, T.InkMuted, wrap: true).Margin(0, 0, 0, 10));
            foreach (var c in Cameras.Where(c => Scan.People.Any(p => p.Camera == c.Id)))
            {
                step3.Children.Add(T.Text(c.Name, 13, FontWeights.Bold).Margin(0, 4, 0, 8));
                var wrap = new WrapPanel();
                foreach (var p in Scan.People.Where(p => p.Camera == c.Id)) wrap.Children.Add(PersonCard(p, c));
                step3.Children.Add(wrap);
            }
            // Out of the name box first, so the page can show what was saved.
            var save = T.Primary("Save shots and names", () => { System.Windows.Input.Keyboard.ClearFocus(); _saved = Scan.Save(); });
            Live(() => { save.IsEnabled = Scan.People.Any(p => p.Label.Trim().Length > 0) && !Scan.Running; save.Opacity = save.IsEnabled ? 1 : 0.5; });
            step3.Children.Add(T.Row(12, save, T.Text("Replaces the shots from the last scan. Shots you made yourself are kept.", 12, null, T.InkSubtle).With(t => t.VerticalAlignment = VerticalAlignment.Center)).Margin(0, 6, 0, 0));
            foreach (var line in _saved) step3.Children.Add(T.Text(line, 12, FontWeights.SemiBold, T.Success, wrap: true).Margin(0, 6, 0, 0));
        }
        page.Children.Add(T.Card(Step(3, "Name people and save", step3)).Margin(0, 0, 0, 14));

        if (StageCast.Shared.Names.Count > 0)
            page.Children.Add(T.Card(T.Column(4, T.Caps("Can follow by name"), T.Text(string.Join(", ", StageCast.Shared.Names), 13, FontWeights.SemiBold),
                T.Text("On Operator, under Track, choose Follow by name. This lasts until Churchfront Video quits or the next scan is saved.", 12, null, T.InkSubtle, wrap: true))));
        return page;
    }

    /// <summary>One person from the scan: their picture, the same-person letter, a label, and a way to try the shot.</summary>
    private UIElement PersonCard(StageScan.Person p, PTZCamera cam)
    {
        var pic = new Grid { Height = 170, Background = Brushes.Black, ClipToBounds = true };
        if (p.Thumb != null)
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(p.Thumb); bmp.EndInit(); bmp.Freeze();
                pic.Children.Add(new Image { Source = bmp, Stretch = Stretch.Uniform });
            }
            catch { }
        }
        pic.Children.Add(new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = T.Accent, Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = T.Text(p.Letter, 12, FontWeights.Bold, T.OnAction).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; }),
        });
        var label = new TextBox
        {
            Text = p.Label, Height = 30, Background = T.SurfaceSunken, Foreground = T.Ink, BorderBrush = T.Line, CaretBrush = T.Ink,
            Padding = new Thickness(6, 4, 6, 4), FontSize = 13, FontFamily = T.Sans, VerticalContentAlignment = VerticalAlignment.Center,
        };
        T.Placeholder(label, "Name or role");
        label.TextChanged += (_, _) => { if (label.Text != p.Label) Scan.Label(p.Id, label.Text); };
        label.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) System.Windows.Input.Keyboard.ClearFocus(); };
        // Naming one fills in the others with the same letter: show it, unless that box is being typed in.
        Live(() => { if (!label.IsKeyboardFocused && label.Text != p.Label) label.Text = p.Label; });
        var tryIt = T.Small("Try the shot", () => B.Ptz.Go(new PTZShot("", p.Shot), cam.Id));
        Live(() =>
        {
            var why = Scan.Blocked(cam);
            tryIt.IsEnabled = !Scan.Running && why == null;
            tryIt.ToolTip = why != null ? $"{cam.Name} can't move now: {why}." : $"Send {cam.Name} to this shot to check it";
        });
        var bottom = T.Row(6, tryIt);
        if (p.Rough)
            bottom.Children.Add(T.Text("size is a guess", 10, null, T.Warning).With(t =>
            {
                t.VerticalAlignment = VerticalAlignment.Center;
                t.ToolTip = "Only this person's outline was found, so how tight the shot is was estimated. Try the shot and adjust.";
            }));
        var card = T.Column(6, new Border { Child = pic, BorderBrush = T.Line, BorderThickness = new Thickness(1) }, label, bottom);
        card.Width = 160;
        card.Margin = new Thickness(0, 0, 12, 12);
        return card;
    }
}

// MARK: - Routing

internal sealed class RoutingPage : LiveView
{
    private static Booth B => Booth.Shared;
    private static ATEMState S => B.Atem.State;

    protected override string StructureKey() =>
        B.Store.Config.Signature().GetHashCode() + "|" + string.Join(",", S.Aux.Keys) + "|" + string.Join(",", S.Inputs.Keys) + "|" + string.Join(",", S.Program.Keys);

    private static IEnumerable<(string, Action)> SourceChoices(Action<int> pick)
    {
        foreach (var me in S.Program.Keys.OrderBy(x => x))
        {
            var m = me;
            yield return (B.Store.MixName(m), () => pick(10000 + (m + 1) * 10));
            yield return (B.Store.MixName(m) + " preview", () => pick(10000 + (m + 1) * 10 + 1));
        }
        foreach (var i in S.AuxSources.Where(i => i.Id < 10000))
        {
            var id = i.Id;
            yield return (B.Store.Label(id, S), () => pick(id));
        }
    }

    protected override UIElement Build()
    {
        var col = new StackPanel();
        col.Children.Add(EngineerView.Heading("Routing", "What each mix is for, what every screen shows and where it's wired, and the operator's routing buttons."));

        // Mixes
        var mixes = new StackPanel();
        mixes.Children.Add(T.Caps("Mixes").Margin(0, 0, 0, 8));
        foreach (var me in S.Program.Keys.OrderBy(x => x))
        {
            var m = me;
            var forT = T.Field(m < B.Store.Config.MixNames.Count ? B.Store.Config.MixNames[m] : "", v => B.Store.Update(c =>
            {
                while (c.MixNames.Count <= m) c.MixNames.Add("");
                c.MixNames[m] = v.Trim();
                while (c.MixNames.Count > 0 && c.MixNames[^1].Length == 0) c.MixNames.RemoveAt(c.MixNames.Count - 1);
            }), 200, "What it's for");
            var now = T.Text("", 13, null, T.InkMuted);
            Live(() => now.Text = $"Live: {B.Store.Label(S.ProgramOf(m), S)}   Next: {B.Store.Label(S.PreviewOf(m), S)}{(m == B.Me ? "   (the operator's mix)" : "")}");
            mixes.Children.Add(T.Row(12, T.Text($"M/E {m + 1}", 13, FontWeights.Bold).With(t => t.Width = 50), forT, now).Margin(0, 0, 0, 8));
        }
        if (S.Program.Count == 0) mixes.Children.Add(T.Text("Connect the switcher to see its mixes.", 13, null, T.InkSubtle));
        col.Children.Add(T.Card(mixes).Margin(0, 0, 0, 14));

        // Screens
        var screens = new StackPanel();
        screens.Children.Add(T.Caps("Screens").Margin(0, 0, 0, 8));
        foreach (var sc in B.Store.Config.Screens)
        {
            var screen = sc;
            var showing = T.Text("", 13, FontWeights.SemiBold);
            Live(() => showing.Text = B.Store.Showing(screen, S));
            var picker = T.Button(T.Row(6, showing, T.Text("▾", 11, null, T.InkMuted)), null, T.SurfaceRaised, T.Line, 240, 30, 6, new Thickness(10, 0, 8, 0));
            picker.HorizontalContentAlignment = HorizontalAlignment.Left;
            picker.Click += (_, _) =>
            {
                if (!screen.ForVolunteers && !Dialogs.Confirm($"Change {screen.Name}?", "This screen is hidden from volunteers. It may feed an encoder, a recorder or the stream.", "Change it")) return;
                var menu = T.Menu();
                foreach (var (label, pick) in SourceChoices(src => { foreach (var a in screen.Aux) B.Atem.SetAux(a - 1, src); })) menu.Items.Add(T.Item(label, pick));
                menu.PlacementTarget = picker; menu.IsOpen = true;
            };
            var info = T.Column(2, T.Row(8, T.Text(screen.Name, 14, FontWeights.Bold), screen.ForVolunteers ? new TextBlock() : T.Badge("Hidden from volunteers", T.InkMuted, T.SurfaceRaised)),
                T.Text($"Aux {string.Join(", ", screen.Aux)}{(string.IsNullOrEmpty(screen.Note) ? "" : " · " + screen.Note)}", 12, null, T.InkMuted));
            info.Width = 360;
            var pin = T.Check("Preview in top bar", B.Store.PinnedScreens.Any(p => p.Id == screen.Id), on => B.Store.SetPinned(screen.Id, on));
            screens.Children.Add(T.Row(16, info, picker, pin).Margin(0, 0, 0, 10));
        }
        if (B.Store.Config.Screens.Count == 0)
            screens.Children.Add(T.Text("No screens named yet. Name them in the setup file (screens), or ask Claude to set up routing.", 13, null, T.InkSubtle, wrap: true));

        // Unnamed auxes
        var named = B.Store.Config.Screens.SelectMany(s => s.Aux).ToHashSet();
        foreach (var aux in S.Aux.Keys.OrderBy(x => x).Where(a => !named.Contains(a + 1)))
        {
            var a = aux;
            var showing = T.Text("", 13, FontWeights.SemiBold);
            Live(() => showing.Text = B.Store.RouteLabel(S.Aux.TryGetValue(a, out var v) ? v : null, S));
            var picker = T.Button(T.Row(6, showing, T.Text("▾", 11, null, T.InkMuted)), null, T.SurfaceRaised, T.Line, 240, 30, 6, new Thickness(10, 0, 8, 0));
            picker.HorizontalContentAlignment = HorizontalAlignment.Left;
            picker.Click += (_, _) =>
            {
                var menu = T.Menu();
                foreach (var (label, pick) in SourceChoices(src => B.Atem.SetAux(a, src))) menu.Items.Add(T.Item(label, pick));
                menu.PlacementTarget = picker; menu.IsOpen = true;
            };
            screens.Children.Add(T.Row(16, T.Text($"Aux {a + 1}", 14, FontWeights.Bold, T.InkMuted).With(t => t.Width = 360), picker).Margin(0, 0, 0, 10));
        }
        col.Children.Add(T.Card(screens).Margin(0, 0, 0, 14));

        // Buttons
        var buttons = new StackPanel();
        buttons.Children.Add(T.Caps("Operator routing buttons").Margin(0, 0, 0, 8));
        foreach (var r in B.Store.Config.Routes)
        {
            var route = r;
            var state = T.Text("", 11, FontWeights.Bold);
            Live(() => { var on = route.IsActive(S); state.Text = on ? "ON" : ""; state.Foreground = T.Success; });
            buttons.Children.Add(T.Row(12, T.Small(r.Label, () => route.Run(B.Atem)), state,
                T.Text(B.Store.RouteDetail(r, S).Replace("\n", "   "), 12, null, T.InkMuted)).Margin(0, 0, 0, 8));
        }
        if (B.Store.Config.Routes.Count == 0)
            buttons.Children.Add(T.Text("No routing buttons yet. Add them in the setup file (routes), or ask Claude.", 13, null, T.InkSubtle));
        col.Children.Add(T.Card(buttons));
        return col;
    }
}

// MARK: - Full switcher

internal sealed class SwitcherPage : LiveView
{
    private static Booth B => Booth.Shared;
    private static ATEMClient Atem => B.Atem;
    private static ATEMState S => Atem.State;
    private int _me;

    protected override string StructureKey() =>
        $"{_me}|{S.MeCount}|{S.KeyerCount}|{S.DskCount}|{string.Join(",", S.BusSources.Select(i => i.Id + i.Short))}|{string.Join(",", S.Macros.Select(kv => kv.Key + kv.Value))}|{string.Join(",", S.Aux.Keys)}|{B.Store.Config.Signature().GetHashCode()}";

    protected override UIElement Build()
    {
        if (_me >= S.MeCount) _me = 0;
        var me = _me;
        var col = new StackPanel();
        UIElement? meSel = null;
        if (S.MeCount > 1)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < S.MeCount; i++)
            {
                var m = i;
                var b = T.Button(T.Text($"M/E {m + 1}", 12, FontWeights.SemiBold, m == me ? T.OnAction : T.Ink), () => { _me = m; Refresh(force: true); },
                    m == me ? T.Accent : T.SurfaceRaised, T.Line, h: 30, radius: 6);
                b.Margin = new Thickness(0, 0, 6, 0);
                row.Children.Add(b);
            }
            meSel = row;
        }
        var model = T.Text("", 12, null, T.InkMuted);
        Live(() => model.Text = S.Product);
        col.Children.Add(EngineerView.Heading("Full switcher", "Every control on the switcher. What you press here goes to air like the hardware panel.", meSel ?? model));
        if (S.Inputs.Count == 0)
        {
            col.Children.Add(T.Card(T.Text("Connect the switcher in Setup to see its controls.", 13, null, T.InkMuted)));
            return col;
        }

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Buses
        var buses = new StackPanel();
        buses.Children.Add(T.Caps("Program", 11, T.Program).Margin(0, 0, 0, 8));
        buses.Children.Add(Bus(me, program: true));
        buses.Children.Add(T.Caps("Preview", 11, T.Preview).Margin(0, 12, 0, 8));
        buses.Children.Add(Bus(me, program: false));
        top.Children.Add(T.Card(buses).Margin(0, 0, 14, 14));

        // Transition
        var styles = new StackPanel();
        string[] names = { "MIX", "DIP", "WIPE", "DVE" };
        for (int i = 0; i < names.Length; i++)
        {
            var st = i;
            var lb = new T.LitButton(() => Atem.SetStyle(st, me), 64, 32, 11);
            lb.Button.Margin = new Thickness(0, 0, 0, 8);
            Live(() => lb.Set(names[st], null, (S.Style.TryGetValue(me, out var v) ? v : 0) == st ? T.Warning : null));
            styles.Children.Add(lb.Button);
        }
        var rate = T.Text("", 13, FontWeights.SemiBold); rate.Width = 50; rate.TextAlignment = TextAlignment.Center;
        Live(() =>
        {
            var f = S.MixRate.TryGetValue(me, out var r) ? r : 30;
            rate.Text = $"{f / 30}:{f % 30:00}";
        });
        void Rate(int d) { var f = S.MixRate.TryGetValue(me, out var r) ? r : 30; Atem.SetMixRate(Math.Clamp(f + d, 1, 250), me); }
        styles.Children.Add(T.Row(2, T.Small("−", () => Rate(-5)), rate, T.Small("+", () => Rate(5))).With(r => r.ToolTip = "Mix rate in seconds:frames"));
        var actions = new StackPanel { Margin = new Thickness(14, 0, 14, 0) };
        actions.Children.Add(T.Panel("CUT", null, null, () => Atem.Cut(me), 86, 52, 15).Margin(0, 0, 0, 8));
        var auto = new T.LitButton(() => Atem.Auto(me), 86, 52, 15);
        auto.Button.Margin = new Thickness(0, 0, 0, 8);
        Live(() => auto.Set("AUTO", null, S.Get(S.InTransition, me) ? T.Program : null));
        actions.Children.Add(auto.Button);
        var ftb = new T.LitButton(() => Atem.FadeToBlack(me), 86, 40, 12);
        Live(() => ftb.Set("FTB", null, S.Get(S.FtbBlack, me) || S.Get(S.FtbInTransition, me) ? T.Program : null));
        actions.Children.Add(ftb.Button);
        var tbar = new Slider { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 1, Height = 170, Focusable = false, IsMoveToPointEnabled = false };
        var dragging = false;
        tbar.PreviewMouseDown += (_, _) => dragging = true;
        tbar.PreviewMouseUp += (_, _) =>
        {
            dragging = false;
            // Like a hardware T-bar: at the far end the transition completes and the bar rests there.
            if (tbar.Value is > 0.02 and < 0.98) { }
        };
        tbar.ValueChanged += (_, _) =>
        {
            if (!dragging) return;
            var atTop = B.Atem.TbarAtTop.TryGetValue(me, out var t) && t;
            var pos = atTop ? 1 - tbar.Value : tbar.Value;
            Atem.SetPosition(pos, me);
        };
        Live(() =>
        {
            if (dragging) return;
            var atTop = B.Atem.TbarAtTop.TryGetValue(me, out var t) && t;
            var p = S.Get(S.InTransition, me) ? (S.Position.TryGetValue(me, out var v) ? v : 0) : 0;
            tbar.Value = atTop ? 1 - p : p;
        });
        var trans = T.Column(10, T.Caps("Transition"), T.Row(0, styles, actions, tbar));
        var transCard = T.Card(trans).Margin(0, 0, 0, 14);
        Grid.SetColumn(transCard, 1);
        top.Children.Add(transCard);
        col.Children.Add(top);

        // Keys and DSKs
        var lower = new WrapPanel();
        var keys = new StackPanel();
        keys.Children.Add(T.Caps("Keys").Margin(0, 0, 0, 8));
        keys.Children.Add(T.Text("Next transition takes", 12, null, T.InkMuted).Margin(0, 0, 0, 6));
        var next = new StackPanel { Orientation = Orientation.Horizontal };
        for (int bit = 0; bit <= S.KeyerCount; bit++)
        {
            var b = bit;
            var lb = new T.LitButton(() =>
            {
                var mask = S.NextSelection.TryGetValue(me, out var m) ? m : 1;
                Atem.SetNextSelection(mask ^ (1 << b), me);
            }, 62, 40, 12);
            lb.Button.Margin = new Thickness(0, 0, 8, 0);
            Live(() => lb.Set(b == 0 ? "BKGD" : $"KEY {b}", null, (((S.NextSelection.TryGetValue(me, out var m) ? m : 1) >> b) & 1) == 1 ? T.Warning : null));
            next.Children.Add(lb.Button);
        }
        keys.Children.Add(next);
        if (S.KeyerCount > 0)
        {
            keys.Children.Add(T.Text("On air", 12, null, T.InkMuted).Margin(0, 12, 0, 6));
            var onAir = new StackPanel { Orientation = Orientation.Horizontal };
            for (int k = 0; k < S.KeyerCount; k++)
            {
                var key = k;
                bool IsOn() => S.KeyOnAir.TryGetValue(me, out var ko) && ko.TryGetValue(key, out var on) && on;
                var lb = new T.LitButton(() => Atem.SetKeyOnAir(key, !IsOn(), me), 62, 40, 12);
                lb.Button.Margin = new Thickness(0, 0, 8, 0);
                Live(() => lb.Set($"KEY {key + 1}", null, IsOn() ? T.Program : null));
                onAir.Children.Add(lb.Button);
            }
            keys.Children.Add(onAir);
        }
        var dsks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 0) };
        for (int d = 0; d < S.DskCount; d++)
        {
            var dsk = d;
            var tie = new T.LitButton(() => Atem.SetDSKTie(dsk, !S.Get(S.DskTie, dsk)), 62, 36, 12);
            var cut = new T.LitButton(() => Atem.SetDSKOnAir(dsk, !S.Get(S.DskOnAir, dsk)), 62, 36, 12);
            var au = new T.LitButton(() => Atem.DskAuto(dsk), 62, 36, 12);
            Live(() =>
            {
                tie.Set("TIE", null, S.Get(S.DskTie, dsk) ? T.Warning : null);
                cut.Set("CUT", null, S.Get(S.DskOnAir, dsk) ? T.Program : null);
                au.Set("AUTO", null, S.Get(S.DskInTransition, dsk) ? T.Program : null);
            });
            dsks.Children.Add(T.Column(8, T.Text($"DSK {dsk + 1}", 12, null, T.InkMuted).With(t => t.HorizontalAlignment = HorizontalAlignment.Center), tie.Button, cut.Button, au.Button).Margin(0, 0, 12, 0));
        }
        lower.Children.Add(T.Card(T.Row(0, keys, dsks)).Margin(0, 0, 14, 14));

        // Macros
        var macros = new StackPanel();
        macros.Children.Add(T.Caps("Macros").Margin(0, 0, 0, 8));
        var mw = new WrapPanel { MaxWidth = 640 };
        foreach (var (i, name) in S.Macros.OrderBy(kv => kv.Key))
        {
            var idx = i;
            var lb = new T.LitButton(() => Atem.RunMacro(idx), 110, 42, 12);
            lb.Button.Margin = new Thickness(0, 0, 8, 8);
            Live(() => lb.Set(name, (idx + 1).ToString(CultureInfo.InvariantCulture), S.MacroRunning == idx ? T.Program : null));
            mw.Children.Add(lb.Button);
        }
        if (S.Macros.Count == 0) mw.Children.Add(T.Text("No macros recorded on the switcher.", 13, null, T.InkSubtle));
        macros.Children.Add(mw);
        if (S.Macros.Count > 0) macros.Children.Add(T.Small("Stop macro", () => Atem.StopMacro()).With(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        lower.Children.Add(T.Card(macros).Margin(0, 0, 14, 14));
        col.Children.Add(lower);

        // Aux
        if (S.AuxCount > 0)
        {
            var aux = new StackPanel();
            aux.Children.Add(T.Caps("Aux outputs").Margin(0, 0, 0, 8));
            var wrap = new WrapPanel();
            foreach (var a in S.Aux.Keys.OrderBy(x => x))
            {
                var ax = a;
                var showing = T.Text("", 13, FontWeights.SemiBold);
                Live(() => showing.Text = S.Aux.TryGetValue(ax, out var v) ? (S.Inputs.TryGetValue(v, out var inp) ? inp.Long : v.ToString()) : "?");
                var picker = T.Button(T.Row(6, showing, T.Text("▾", 11, null, T.InkMuted)), null, T.SurfaceRaised, T.Line, 220, 30, 6, new Thickness(10, 0, 8, 0));
                picker.HorizontalContentAlignment = HorizontalAlignment.Left;
                picker.Click += (_, _) =>
                {
                    var menu = T.Menu();
                    foreach (var i in S.AuxSources) { var id = i.Id; menu.Items.Add(T.Item(i.Long, () => Atem.SetAux(ax, id))); }
                    menu.PlacementTarget = picker; menu.IsOpen = true;
                };
                var screen = B.Store.Screen(ax + 1)?.Name;
                wrap.Children.Add(T.Column(4, T.Row(6, T.Text($"Aux {ax + 1}", 12, FontWeights.Bold), T.Text(screen ?? "", 12, null, T.InkMuted)), picker).Margin(0, 0, 16, 10));
            }
            aux.Children.Add(wrap);
            col.Children.Add(T.Card(aux));
        }
        return col;
    }

    private UIElement Bus(int me, bool program)
    {
        var w = new WrapPanel();
        foreach (var i in S.BusSources)
        {
            var id = i.Id;
            var lb = new T.LitButton(() => { if (program) Atem.SetProgram(id, me); else Atem.SetPreview(id, me); }, 76, 48, 13, i.Long);
            lb.Button.Margin = new Thickness(0, 0, 8, 8);
            Live(() => lb.Set(i.Short.ToUpperInvariant(), null, (program ? S.ProgramOf(me) : S.PreviewOf(me)) == id ? (program ? T.Program : T.Preview) : null));
            w.Children.Add(lb.Button);
        }
        return w;
    }
}

// MARK: - Status

internal sealed class StatusPage : LiveView
{
    private static Booth B => Booth.Shared;
    protected override string StructureKey() => PTZController.Signature(B.Ptz.Cameras).GetHashCode().ToString();

    protected override UIElement Build()
    {
        var col = new StackPanel();
        col.Children.Add(EngineerView.Heading("Status", "Connections, and the last commands sent to the switcher."));
        var sw = T.Text("", 13, null, T.Ink, wrap: true);
        Live(() =>
        {
            var s = B.Atem.State;
            sw.Text = $"Switcher: {ControlTools.LinkText(B.Atem.Link)}{(B.Atem.Host.Length > 0 ? $" ({B.Atem.Host})" : "")}\n" +
                      (s.Product.Length > 0 ? $"Model: {s.Product}, protocol {s.Version}\n" : "") +
                      $"Inputs: {s.Inputs.Count}   M/Es: {s.MeCount}   Keys: {s.KeyerCount}   DSKs: {s.DskCount}   Aux: {s.AuxCount}\n" +
                      $"Claude's control server: {(ControlServer.Shared.Running ? $"listening on 127.0.0.1:{ControlServer.Port}" : $"not running{(ControlServer.Shared.Error is string e ? $" ({e})" : "")}")}\n" +
                      $"Setup file: {B.Store.FilePath}{(B.Store.FileError is string fe ? $"\n  Problem: {fe}" : "")}";
        });
        col.Children.Add(T.Card(sw).Margin(0, 0, 0, 14));

        var cams = new StackPanel();
        cams.Children.Add(T.Caps("Cameras").Margin(0, 0, 0, 8));
        foreach (var c in B.Ptz.Cameras)
        {
            var cam = c;
            var t = T.Text("", 13, null, T.Ink);
            Live(() => t.Text = $"{cam.Name}  ·  {cam.Host}:{cam.Port}  ·  {cam.Proto.Label()}  ·  " +
                                (B.Ptz.Online.Contains(cam.Id) ? "answering" : "not answering") +
                                (cam.Proto == VISCAProtocol.CanonXC ? (B.Ptz.Controlled.Contains(cam.Id) ? ", in control" : ", waiting for control") : "") +
                                (B.Ptz.Models.TryGetValue(cam.Id, out var m) ? $"  ·  {m}" : ""));
            cams.Children.Add(t.Margin(0, 0, 0, 6));
        }
        if (B.Ptz.Cameras.Count == 0) cams.Children.Add(T.Text("No cameras set up.", 13, null, T.InkSubtle));
        col.Children.Add(T.Card(cams).Margin(0, 0, 0, 14));

        var log = new TextBlock { FontFamily = T.Mono, FontSize = 12, Foreground = T.InkMuted, TextWrapping = TextWrapping.Wrap };
        Live(() =>
        {
            var s = B.Atem.State;
            log.Text = B.Atem.SentLog.Count == 0 ? "Nothing sent yet." :
                string.Join("\n", B.Atem.SentLog.AsEnumerable().Reverse().Take(30).Select(e => $"{e.Time:HH:mm:ss}  {(e.Sent ? "" : "(offline) ")}{e.Describe(s)}"));
        });
        col.Children.Add(T.Card(T.Column(8, T.Caps("Sent to the switcher"), log)));
        return col;
    }
}
