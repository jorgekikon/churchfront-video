using System.Windows;
using System.Windows.Controls;
using CueATEM;

namespace CueVideo.App;

/// <summary>Exposure for one camera: gain, shutter, iris and white balance, stepped through the
/// values the camera accepts, plus exposure shots. A small window beside the main one.</summary>
public sealed class ExposureWindow : Window
{
    private static ExposureWindow? _open;
    private readonly Guid _camera;
    private readonly ExposureBody _body;

    public static void Show(Guid camera)
    {
        _open?.Close();
        var w = new ExposureWindow(camera);
        _open = w;
        w.Closed += (_, _) => { if (_open == w) _open = null; };
        w.Show();
        Booth.Shared.Ptz.RefreshExposure(camera);
    }

    /// <summary>Called on every refresh of the main window.</summary>
    public static void RefreshOpen() => _open?._body.Refresh();

    private ExposureWindow(Guid camera)
    {
        _camera = camera;
        var cam = Booth.Shared.Ptz.Camera(camera);
        Title = $"Exposure: {cam?.Name}";
        Width = 460; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Background = T.Surface; FontFamily = T.Sans;
        Owner = Application.Current.MainWindow; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _body = new ExposureBody(camera) { Padding = new Thickness(18) };
        Content = _body;
        _body.Refresh();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private sealed class ExposureBody : LiveView
    {
        private readonly Guid _id;
        public ExposureBody(Guid id) { _id = id; }
        private static PTZController Ptz => Booth.Shared.Ptz;

        protected override string StructureKey()
        {
            var c = Ptz.Camera(_id);
            var x = Ptz.Exposure.TryGetValue(_id, out var e) ? e : null;
            return $"{c?.Name}|{x != null}|{x?.HasAutoMode}|{x?.KelvinList.Count}|{(c == null ? "" : PTZController.Signature(new[] { c }))}|" +
                   string.Join(",", Ptz.ExposurePresets.TryGetValue(_id, out var s) ? s : new HashSet<int>());
        }

        protected override UIElement Build()
        {
            var c = Ptz.Camera(_id);
            if (c == null) return T.Text("That camera is gone.", 13, null, T.InkMuted);
            if (!Ptz.Exposure.ContainsKey(_id))
                return T.Column(10, T.Text(c.Name, 16, FontWeights.Bold), T.Text("Waiting for the camera to report its exposure…", 13, null, T.InkMuted));
            var col = T.Column(10, T.Text(c.Name, 16, FontWeights.Bold));
            col.Children.Add(Row("Gain", x => x.GainLabel(x.Value.Gain), x => x.GainAuto, x => x.GainList, x => x.Value.Gain, brighterUp: true, (v) => new PTZExposure { Gain = v }));
            col.Children.Add(Row("Shutter", x => CameraExposure.ShutterText(x.Value.Shutter), x => x.ShutterAuto, x => x.ShutterList, x => x.Value.Shutter, brighterUp: false, v => new PTZExposure { Shutter = v }));
            col.Children.Add(Row("Iris", x => CameraExposure.IrisText(x.Value.Iris), x => x.IrisAuto, x => x.IrisList, x => x.Value.Iris, brighterUp: false, v => new PTZExposure { Iris = v }));
            col.Children.Add(WhiteBalance());
            var x0 = Ptz.Exposure[_id];
            var actions = new WrapPanel();
            if (x0.HasAutoMode) actions.Children.Add(T.Small("Auto exposure", () => Ptz.AutoExposure(_id), "Let the camera set its own brightness (gain, shutter and iris)").Margin(0, 0, 8, 8));
            actions.Children.Add(T.Small("Read again", () => Ptz.RefreshExposure(_id), "Read the camera's exposure now, if someone changed it elsewhere").Margin(0, 0, 8, 8));
            actions.Children.Add(T.Small("Save as exposure shot…", () =>
            {
                if (Dialogs.Prompt($"Save exposure on {c.Name}", "Saves the camera's current brightness and colour settings (gain, shutter, iris and white balance), to put back later with one click.", "", "Bright stage") is string n)
                    Ptz.SaveShot(n, _id, ShotKind.Exposure, null, ok => { if (!ok) Dialogs.Info("Not saved", "The camera didn't report its exposure."); });
            }).Margin(0, 0, 8, 8));
            col.Children.Add(actions.Margin(0, 6, 0, 0));

            var shots = c.Shots.Where(s => s.Kind == ShotKind.Exposure).ToList();
            var presets = Ptz.ExposurePresets.TryGetValue(_id, out var ep) ? ep.OrderBy(x => x).ToList() : new List<int>();
            if (shots.Count + presets.Count > 0)
            {
                col.Children.Add(T.Caps("Exposure shots").Margin(0, 6, 0, 0));
                var wrap = new WrapPanel();
                foreach (var s in shots)
                {
                    var shot = s;
                    var b = T.Small(s.Name, () => Ptz.Go(shot, _id), ExposureText(s.Exposure));
                    var m = T.Menu();
                    m.Items.Add(T.Item("Delete…", () =>
                    {
                        if (Dialogs.Confirm($"Delete {shot.Name}?", "The saved exposure is removed from Churchfront Video. The camera isn't changed.", "Delete", danger: true))
                            Ptz.Edit(list => list.First(y => y.Id == _id).Shots.RemoveAll(y => y.Id == shot.Id));
                    }, danger: true));
                    b.ContextMenu = m;
                    wrap.Children.Add(b.Margin(0, 0, 8, 8));
                }
                var names = Ptz.PresetNames(c);
                foreach (var n in presets)
                {
                    var p = n;
                    wrap.Children.Add(T.Small($"{(names.TryGetValue(n, out var nm) ? nm : $"Preset {n}")} (camera)", () => Ptz.Recall(p, _id)).Margin(0, 0, 8, 8));
                }
                col.Children.Add(wrap);
            }
            return col;
        }

        private static string ExposureText(PTZExposure? e)
        {
            if (e == null) return "";
            var parts = new List<string>();
            if (e.Gain is int g) parts.Add(CameraExposure.GainText(g));
            if (e.Shutter is int s) parts.Add(CameraExposure.ShutterText(s));
            if (e.Iris is int i) parts.Add(CameraExposure.IrisText(i));
            if (e.HasWhiteBalance) parts.Add(CameraExposure.WbText(e.Wb, e.Kelvin));
            return string.Join(", ", parts);
        }

        private UIElement Row(string label, Func<CameraExposure, string> text, Func<CameraExposure, bool> auto, Func<CameraExposure, List<int>> list,
                              Func<CameraExposure, int?> value, bool brighterUp, Func<int, PTZExposure> make)
        {
            var name = T.Text(label, 13, FontWeights.SemiBold); name.Width = 90;
            var val = T.Text("", 14, FontWeights.Bold); val.Width = 110; val.TextAlignment = TextAlignment.Center;
            var tag = T.Text("", 10, FontWeights.Bold, T.Success); tag.Width = 50;
            void Step(bool brighter)
            {
                if (!Ptz.Exposure.TryGetValue(_id, out var x)) return;
                var l = list(x).OrderBy(v => v).ToList();
                if (l.Count == 0) return;
                var cur = value(x) ?? l[0];
                var i = l.FindIndex(v => v >= cur);
                if (i < 0) i = l.Count - 1;
                var up = brighter == brighterUp;
                var j = Math.Clamp(i + (up ? 1 : -1), 0, l.Count - 1);
                if (l[j] != cur) Ptz.SetExposure(make(l[j]), _id);
            }
            var darker = T.Small("− Darker", () => Step(false));
            var brighter = T.Small("Brighter +", () => Step(true));
            Live(() =>
            {
                if (!Ptz.Exposure.TryGetValue(_id, out var x)) return;
                val.Text = text(x);
                tag.Text = auto(x) ? "AUTO" : "";
            });
            return T.Row(8, name, darker, val, brighter, tag);
        }

        private UIElement WhiteBalance()
        {
            var name = T.Text("White bal.", 13, FontWeights.SemiBold); name.Width = 90;
            var val = T.Text("", 14, FontWeights.Bold); val.Width = 110; val.TextAlignment = TextAlignment.Center;
            void Step(bool warmer)
            {
                if (!Ptz.Exposure.TryGetValue(_id, out var x)) return;
                var l = x.KelvinList.OrderBy(v => v).ToList();
                if (l.Count == 0) return;
                var cur = x.Value.Kelvin ?? 5600;
                var i = l.FindIndex(v => v >= cur); if (i < 0) i = l.Count - 1;
                var j = Math.Clamp(i + (warmer ? 1 : -1), 0, l.Count - 1);
                Ptz.SetExposure(new PTZExposure { Wb = "kelvin", Kelvin = l[j] }, _id);
            }
            Live(() => { if (Ptz.Exposure.TryGetValue(_id, out var x)) val.Text = CameraExposure.WbText(x.Value.Wb, x.Value.Kelvin); });
            var modes = T.Row(6,
                T.Small("Auto", () => Ptz.SetExposure(new PTZExposure { Wb = "auto" }, _id)),
                T.Small("Daylight", () => Ptz.SetExposure(new PTZExposure { Wb = "daylight" }, _id)),
                T.Small("Tungsten", () => Ptz.SetExposure(new PTZExposure { Wb = "tungsten" }, _id)));
            modes.Margin = new Thickness(98, 6, 0, 0);
            return T.Column(0, T.Row(8, name, T.Small("− Cooler", () => Step(false)), val, T.Small("Warmer +", () => Step(true))), modes);
        }
    }
}
