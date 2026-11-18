using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CueATEM;

namespace CueVideo.App;

/// <summary>Setup: the switcher, the operator's source row and key buttons, the cameras, Claude,
/// and the setup file. Every change is written to config.json straight away.</summary>
internal sealed class SetupPage : LiveView
{
    private static Booth B => Booth.Shared;
    private static ConfigStore Store => B.Store;
    private static ATEMState S => B.Atem.State;
    private string _tab = "Switcher";
    private static readonly string[] Tabs = { "Switcher", "Sources", "Key buttons", "Cameras", "Claude", "Setup file" };

    protected override string StructureKey() =>
        $"{_tab}|{Store.Config.Signature()}|{string.Join(",", S.Inputs.Values.Select(i => i.Id + i.Long))}|{S.MeCount}|{S.KeyerCount}|{S.DskCount}|{Store.FileError}";

    protected override UIElement Build()
    {
        var col = new StackPanel();
        col.Children.Add(EngineerView.Heading("Setup", _tab switch
        {
            "Switcher" => "The ATEM switcher Churchfront Video connects to, and the M/E the operator runs.",
            "Sources" => "The operator's source row, left to right. Number keys 1 to 0 pick them.",
            "Key buttons" => "On and off buttons for keys, such as lower thirds, under Fade and Cut.",
            "Cameras" => "The PTZ cameras, the switcher input each one feeds, and how to reach them.",
            "Claude" => "Let Claude set up and run Churchfront Video for you.",
            _ => "Everything here is kept in one plain file that a person or Claude can read and edit.",
        }));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var tabBox = new Border { Child = tabs, Background = T.SurfaceSunken, BorderBrush = T.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var t in Tabs)
        {
            var tab = t;
            var on = tab == _tab;
            tabs.Children.Add(T.Button(T.Text(tab, 13, FontWeights.SemiBold, on ? T.OnAction : T.Ink), () => { _tab = tab; Refresh(force: true); },
                on ? T.Accent : T.Transparent, on ? T.Accent : T.Transparent, h: 30, radius: 6));
        }
        col.Children.Add(tabBox.Margin(0, 0, 0, 14));
        col.Children.Add(_tab switch
        {
            "Switcher" => Switcher(),
            "Sources" => Sources(),
            "Key buttons" => Keys(),
            "Cameras" => Cameras(),
            "Claude" => Claude(),
            _ => SetupFile(),
        });
        return col;
    }

    private static UIElement Label(string s, double w) => T.Caps(s, 10).With(t => t.Width = w);

    // MARK: Switcher

    private UIElement Switcher()
    {
        var host = T.Field(Store.Config.AtemHost, v => Store.Update(c => c.AtemHost = v.Trim()), 220, "192.168.1.240");
        var state = T.Text("", 13, FontWeights.SemiBold);
        Live(() =>
        {
            state.Text = ControlTools.LinkText(B.Atem.Link) + (S.Product.Length > 0 ? $" · {S.Product}" : "");
            state.Foreground = B.Atem.Link.Kind == LinkKind.Online ? T.Success : B.Atem.Link.Kind == LinkKind.Offline ? T.Warning : T.InkMuted;
        });
        var mes = Enumerable.Range(1, Math.Max(1, S.MeCount)).Select(m => ($"M/E {m}", (Action)(() => Store.Update(c => c.Me = m))));
        var find = T.Small("Find on the network…", () => FindDevices(), "Looks for ATEM switchers and Canon cameras on this computer's networks (about 4 seconds)");
        var card = T.Column(14,
            T.Row(12, Label("Address", 120), host, find, state),
            T.Row(12, Label("Operator's M/E", 120), T.Picker($"M/E {Store.Config.Me}", mes, 140)),
            T.Text("The switcher must be on the same network as this computer. Churchfront Video talks to it directly over UDP 9910; no Blackmagic software is needed.", 12, null, T.InkMuted, wrap: true).With(t => t.MaxWidth = 640));
        return T.Card(card).With(c => c.HorizontalAlignment = HorizontalAlignment.Left);
    }

    private static void FindDevices()
    {
        var win = new Window
        {
            Title = "Find devices", Width = 560, Height = 420, Background = T.Surface, Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = T.Sans,
        };
        var list = new StackPanel();
        list.Children.Add(T.Text("Looking on this computer's networks…", 13, null, T.InkMuted));
        win.Content = new ScrollViewer { Content = list, Padding = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        win.Show();
        DeviceFinder.Find(3, (found, nets) => MainThread.Post(() =>
        {
            list.Children.Clear();
            list.Children.Add(T.Text($"Searched {string.Join(", ", nets)}", 12, null, T.InkMuted).Margin(0, 0, 0, 10));
            if (found.Count == 0) list.Children.Add(T.Text("Nothing answered. Check this computer is on the same network as the switcher and cameras, and that Windows Firewall allows Churchfront Video on private networks.", 13, null, T.Ink, wrap: true));
            foreach (var f in found)
            {
                var dev = f;
                UIElement action = new TextBlock();
                if (f.Kind.StartsWith("ATEM"))
                    action = T.Small("Use this switcher", () => { Store.Update(c => c.AtemHost = dev.Host); win.Close(); });
                else if (f.Kind.StartsWith("Canon"))
                    action = T.Small("Add camera", () =>
                    {
                        B.Ptz.Edit(l => l.Add(new PTZCamera { Name = dev.Name, Host = dev.Host, Proto = VISCAProtocol.CanonXC, Port = 80 }));
                    });
                var d = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                DockPanel.SetDock(action, Dock.Right);
                d.Children.Add(action);
                d.Children.Add(T.Column(2, T.Row(8, T.Text(f.Name, 13, FontWeights.Bold), T.Text(f.Host, 13, null, T.AccentStrong)), T.Text(f.Kind, 11, null, T.InkMuted, wrap: true)));
                list.Children.Add(d);
            }
        }));
    }

    // MARK: Sources

    private IEnumerable<(string, Action)> InputChoices(Action<int> pick) =>
        S.BusSources.Select(i => { var id = i.Id; return ($"{i.Long} ({id})", (Action)(() => pick(id))); });

    private UIElement Sources()
    {
        var col = new StackPanel();
        var list = Store.Config.Sources;
        if (list.Count == 0)
        {
            col.Children.Add(T.Text(Store.Config.Cameras.Any(c => c.AtemInput != null)
                ? "Not set up yet, so the operator sees every camera's input. Add sources to choose exactly which, in what order."
                : "No sources yet.", 13, null, T.InkMuted, wrap: true).Margin(0, 0, 0, 10));
        }
        else col.Children.Add(T.Row(8, Label("", 30), Label("Switcher input", 240), Label("Label (optional)", 200), Label("Picture", 280)).Margin(0, 0, 0, 6));
        for (int i = 0; i < list.Count; i++)
        {
            var idx = i;
            var src = list[i];
            var inputName = S.Inputs.TryGetValue(src.Input, out var inp) ? $"{inp.Long} ({src.Input})" : $"Input {src.Input}";
            var picker = S.Inputs.Count > 0
                ? (UIElement)T.Picker(inputName, InputChoices(v => Store.Update(c => c.Sources[idx].Input = v)), 240)
                : T.Field(src.Input.ToString(CultureInfo.InvariantCulture), v => { if (int.TryParse(v, out var n)) Store.Update(c => c.Sources[idx].Input = n); }, 240);
            var label = T.Field(src.Label ?? "", v => Store.Update(c => c.Sources[idx].Label = v.Trim().Length == 0 ? null : v.Trim()), 200, "Same as the switcher");
            var up = T.Small("↑", () => Store.Update(c => { if (idx > 0) (c.Sources[idx - 1], c.Sources[idx]) = (c.Sources[idx], c.Sources[idx - 1]); }), "Move left");
            var down = T.Small("↓", () => Store.Update(c => { if (idx < c.Sources.Count - 1) (c.Sources[idx + 1], c.Sources[idx]) = (c.Sources[idx], c.Sources[idx + 1]); }), "Move right");
            var del = T.Small("Remove", () => Store.Update(c => c.Sources.RemoveAt(idx)), danger: true);
            var key = T.Text(idx < 10 ? ((idx + 1) % 10).ToString(CultureInfo.InvariantCulture) : "", 13, FontWeights.Bold, T.InkSubtle).With(t => t.Width = 30);
            // Its tile and the Next and Live pictures: a Canon camera's own, or an NDI source.
            var input = src.Input;
            UIElement pictureChoice = Pictures.Canon(input) != null
                ? T.Text("🎥 The camera's own picture", 12, null, T.InkSubtle).With(t => { t.Width = 280; t.VerticalAlignment = VerticalAlignment.Center; })
                : NdiPicker.Make(src.Ndi, "No picture", n => Store.Update(c => { if (idx < c.Sources.Count) c.Sources[idx].Ndi = n; }), 280,
                    "An NDI source showing what this input carries, such as a PTZOptics camera or a Churchfront Screens screen sent to NDI, for its tile and the Next and Live pictures.");
            col.Children.Add(T.Row(8, key, picker, label, pictureChoice, up, down, del).Margin(0, 0, 0, 8));
        }
        var add = T.Small("+ Add a source", () =>
        {
            if (S.Inputs.Count == 0) { Store.Update(c => c.Sources.Add(new VolunteerSource { Input = 1 })); return; }
            var used = Store.Config.Sources.Select(s => s.Input).ToHashSet();
            var first = S.BusSources.FirstOrDefault(i => i.IsExternal && !used.Contains(i.Id)) ?? S.BusSources.First();
            Store.Update(c =>
            {
                // The first time, start from what the operator sees now (every camera input).
                if (c.Sources.Count == 0) c.Sources.AddRange(Store.VolunteerSources(S).Select(v => new VolunteerSource { Input = v.Input }));
                if (!c.Sources.Any(s => s.Input == first.Id)) c.Sources.Add(new VolunteerSource { Input = first.Id });
            });
        });
        col.Children.Add(add.With(b => b.HorizontalAlignment = HorizontalAlignment.Left).Margin(30, 4, 0, 0));
        return T.Card(col);
    }

    // MARK: Keys

    private UIElement Keys()
    {
        var col = new StackPanel();
        var list = Store.Config.Keys;
        if (list.Count == 0) col.Children.Add(T.Text("No key buttons yet.", 13, null, T.InkMuted).Margin(0, 0, 0, 10));
        else col.Children.Add(T.Row(8, Label("Label", 200), Label("Key", 180), Label("Preview picture", 280)).Margin(0, 0, 0, 6));
        for (int i = 0; i < list.Count; i++)
        {
            var idx = i;
            var k = list[i];
            var label = T.Field(k.Label, v => { if (v.Trim().Length > 0) Store.Update(c => c.Keys[idx].Label = v.Trim()); }, 200);
            var choices = new List<(string, Action)>();
            for (int m = 1; m <= Math.Max(1, S.MeCount); m++)
                for (int kk = 1; kk <= Math.Max(1, S.KeyerCount); kk++)
                {
                    var me = m; var keyer = kk;
                    choices.Add(($"M/E {me} Key {keyer}", () => Store.Update(c => { c.Keys[idx].Me = me; c.Keys[idx].Keyer = keyer; c.Keys[idx].Dsk = null; })));
                }
            for (int d = 1; d <= Math.Max(1, S.DskCount); d++)
            {
                var dsk = d;
                choices.Add(($"DSK {dsk}", () => Store.Update(c => { c.Keys[idx].Dsk = dsk; c.Keys[idx].Me = null; c.Keys[idx].Keyer = null; })));
            }
            var picker = T.Picker(k.Target, choices, 180);
            var preview = NdiPicker.Make(k.Ndi, "No preview", n => Store.Update(c => { if (idx < c.Keys.Count) c.Keys[idx].Ndi = n; }), 280,
                "An NDI source with alpha showing this key, such as Churchfront Screens' lower thirds sent to NDI with alpha. It's laid over the Next and Live pictures when the key is on them.");
            var del = T.Small("Remove", () => Store.Update(c => c.Keys.RemoveAt(idx)), danger: true);
            col.Children.Add(T.Row(8, label, picker, preview, del).Margin(0, 0, 0, 8));
        }
        col.Children.Add(T.Small("+ Add a key button", () => Store.Update(c => c.Keys.Add(new KeyButton { Label = "Lower thirds", Me = 1, Keyer = 1 })))
            .With(b => b.HorizontalAlignment = HorizontalAlignment.Left).Margin(0, 4, 0, 0));
        col.Children.Add(T.Text("Upstream keys cut on and off; DSKs fade with their own rate.", 12, null, T.InkMuted).Margin(0, 10, 0, 0));
        return T.Card(col);
    }

    // MARK: Cameras

    private UIElement Cameras()
    {
        var outer = new StackPanel();
        var col = new StackPanel();
        var cams = B.Ptz.Cameras;
        if (cams.Count == 0) col.Children.Add(T.Text("No cameras yet. Add one, or use Find on the network in the Switcher tab.", 13, null, T.InkMuted).Margin(0, 0, 0, 10));
        else col.Children.Add(T.Row(8, Label("", 10), Label("Name", 150), Label("Address", 150), Label("Control", 180), Label("Port", 70), Label("Switcher input", 200), Label("Presets", 130)).Margin(0, 0, 0, 6));
        foreach (var c in cams)
        {
            var id = c.Id;
            void Edit(Action<PTZCamera> f) => B.Ptz.Edit(l => { var x = l.FirstOrDefault(y => y.Id == id); if (x != null) f(x); });
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8 };
            Live(() => dot.Fill = B.Ptz.HasControl(c) ? T.Success : B.Ptz.Online.Contains(id) ? T.Warning : T.InkSubtle);
            dot.ToolTip = "Green: answering and in control. Amber: answering, another controller has it. Grey: not answering.";
            var name = T.Field(c.Name, v => Edit(x => x.Name = v.Trim().Length > 0 ? v.Trim() : "Camera"), 150);
            var host = T.Field(c.Host, v => Edit(x => x.Host = v.Trim()), 150, "192.168.1.91");
            var proto = T.Picker(c.Proto.Label(), Enum.GetValues<VISCAProtocol>().Select(p => (p.Label(), (Action)(() => Edit(x => { if (x.Proto != p) { x.Proto = p; x.Port = p.DefaultPort(); } })))), 180);
            var port = T.Field(c.Port.ToString(CultureInfo.InvariantCulture), v => { if (ushort.TryParse(v, out var p) && p > 0) Edit(x => x.Port = p); }, 70);
            var inputs = new List<(string, Action)> { ("None", () => Edit(x => x.AtemInput = null)) };
            inputs.AddRange(S.BusSources.Where(i => i.IsExternal).Select(i => { var iid = i.Id; return ($"{i.Long} ({iid})", (Action)(() => Edit(x => x.AtemInput = iid))); }));
            var inputName = c.AtemInput is int ai ? (S.Inputs.TryGetValue(ai, out var inp) ? inp.Long : $"Input {ai}") : "None";
            UIElement input = S.Inputs.Count > 0 ? T.Picker(inputName, inputs, 200)
                : T.Field(c.AtemInput?.ToString(CultureInfo.InvariantCulture) ?? "", v => Edit(x => x.AtemInput = int.TryParse(v, out var n) ? n : null), 200, "Input number");
            var presets = c.Proto == VISCAProtocol.CanonXC ? (UIElement)T.Text("From the camera", 12, null, T.InkMuted).With(t => t.Width = 130)
                : T.Picker($"Count from {c.FirstPreset}", new[] { ("Count from 0", (Action)(() => Edit(x => x.FirstPreset = 0))), ("Count from 1", (Action)(() => Edit(x => x.FirstPreset = 1))) }, 130);
            var del = T.Small("Delete", () =>
            {
                if (Dialogs.Confirm($"Delete {c.Name}?", "The camera and its saved shots are removed from Churchfront Video. The camera itself isn't changed.", "Delete", danger: true))
                    B.Ptz.Edit(l => l.RemoveAll(y => y.Id == id));
            }, danger: true);
            col.Children.Add(T.Row(8, dot, name, host, proto, port, input, presets, del).Margin(0, 0, 0, 8));
        }
        col.Children.Add(T.Small("+ Add a camera", () => B.Ptz.Edit(l => l.Add(new PTZCamera { Name = $"Camera {l.Count + 1}", Proto = VISCAProtocol.CanonXC, Port = 80 })))
            .With(b => b.HorizontalAlignment = HorizontalAlignment.Left).Margin(18, 4, 0, 0));
        outer.Children.Add(T.Card(col).Margin(0, 0, 0, 14));

        // Camera admin login
        var saved = CameraLogin.Load();
        var user = T.Field(saved?.User ?? "", _ => { }, 180, "User name");
        var pass = new PasswordBox { Width = 200, Height = 30, Background = T.SurfaceSunken, Foreground = T.Ink, BorderBrush = T.Line, CaretBrush = T.Ink, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
        pass.ToolTip = saved != null ? "Saved. Type to replace it." : "Password";
        var login = T.Column(10,
            T.Caps("Camera admin login"),
            T.Text("Only needed to delete presets stored on the Canon cameras themselves. Kept in Windows Credential Manager, never in the setup file.", 12, null, T.InkMuted, wrap: true).With(t => t.MaxWidth = 560),
            T.Row(8, user, pass,
                T.Primary("Save", () =>
                {
                    if (user.Text.Trim().Length == 0 || pass.Password.Length == 0) { Dialogs.Info("Not saved", "Type the user name and the password."); return; }
                    if (!CameraLogin.Save(user.Text.Trim(), pass.Password)) Dialogs.Info("Not saved", "Windows didn't save the login.");
                    pass.Clear();
                    Refresh(force: true);
                }),
                T.Small("Remove", () => { CameraLogin.Clear(); Refresh(force: true); }, danger: true).With(b => b.IsEnabled = saved != null)),
            T.Text(saved != null ? $"Saved for {saved.User}." : "No login saved.", 12, null, saved != null ? T.Success : T.InkSubtle));
        outer.Children.Add(T.Card(login).With(c => c.HorizontalAlignment = HorizontalAlignment.Left));
        return outer;
    }

    // MARK: Claude

    private UIElement Claude()
    {
        var state = ClaudeConnect.Current();
        var words = state switch
        {
            ClaudeConnect.State.Connected => "Connected. Open Claude (or restart it if it was open) and ask it to help with Churchfront Video.",
            ClaudeConnect.State.StalePath => "Claude is connected to a copy of Churchfront Video somewhere else. Connect it to this copy.",
            ClaudeConnect.State.NoClaude => "The Claude desktop app isn't installed on this computer. Install it from claude.ai/download, then connect.",
            ClaudeConnect.State.RunFromTemporaryPlace => "Churchfront Video is running from inside a zip file. Unzip it to a folder of its own and open it from there first.",
            _ => "Not connected yet.",
        };
        var col = T.Column(12,
            T.Text("Connecting adds Churchfront Video to the Claude desktop app's connectors, so Claude can find your switcher and cameras, set them up, and run the booth when you ask. It changes only Churchfront Video's entry in Claude's settings.", 13, null, T.InkMuted, wrap: true).With(t => t.MaxWidth = 640),
            T.Text(words, 13, FontWeights.SemiBold, state == ClaudeConnect.State.Connected ? T.Success : T.Ink, wrap: true).With(t => t.MaxWidth = 640),
            T.Row(8,
                T.Primary(state == ClaudeConnect.State.Connected ? "Connect again" : "Connect Claude", () =>
                {
                    if (ClaudeConnect.Connect() is string err) Dialogs.Info("Not connected", err);
                    else Dialogs.Info("Connected", "Quit Claude completely (from the tray icon) and open it again, then try:\n\n" + ClaudeConnect.FirstPrompt);
                    Refresh(force: true);
                }).With(b => b.IsEnabled = state != ClaudeConnect.State.RunFromTemporaryPlace),
                T.Small("Disconnect", () => { if (ClaudeConnect.Disconnect() is string err) Dialogs.Info("Couldn't disconnect", err); Refresh(force: true); }).With(b => b.IsEnabled = state is ClaudeConnect.State.Connected or ClaudeConnect.State.StalePath),
                T.Small("Copy a first message", () => { try { Clipboard.SetText(ClaudeConnect.FirstPrompt); } catch { } })),
            T.Text($"Claude's settings: {ClaudeConnect.ConfigPath}", 11, null, T.InkSubtle, wrap: true));
        return T.Card(col).With(c => c.HorizontalAlignment = HorizontalAlignment.Left);
    }

    // MARK: Setup file

    private UIElement SetupFile()
    {
        var col = T.Column(12,
            T.Text(Store.FilePath, 13, FontWeights.SemiBold, T.Ink, wrap: true),
            T.Text("Edits saved to the file are picked up within a second. If the file has a mistake in it, Churchfront Video keeps running on the last setup that worked, and says so here.", 13, null, T.InkMuted, wrap: true).With(t => t.MaxWidth = 640),
            T.Row(8,
                T.Small("Open the folder", () => Open(Path.GetDirectoryName(Store.FilePath)!)),
                T.Small("Open in Notepad", () => { try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Store.FilePath}\"") { UseShellExecute = true }); } catch { } })));
        if (Store.FileError is string e) col.Children.Add(T.Text($"Problem: {e}", 13, FontWeights.SemiBold, T.Warning, wrap: true).With(t => t.MaxWidth = 640));
        else if (Store.LoadedAt is DateTime d) col.Children.Add(T.Text($"Read cleanly at {d:t}.", 12, null, T.Success));
        if (Store.ReadOnly) col.Children.Add(T.Text("Running a self-test (a CUEVIDEO_ setting is set), so nothing is written to the file.", 12, null, T.Warning));
        return T.Card(col).With(c => c.HorizontalAlignment = HorizontalAlignment.Left);
    }

    private static void Open(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }
}
