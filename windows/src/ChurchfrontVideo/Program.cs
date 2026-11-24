using System.IO;
using System.Windows;
using CueATEM;

namespace CueVideo.App;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        var env = Environment.GetEnvironmentVariables();
        var selfTest = env.Keys.Cast<string>().Any(k => k.StartsWith("CUEVIDEO_", StringComparison.Ordinal));
        // One copy at a time: two would fight over the cameras and the control port.
        using var single = new Mutex(true, selfTest ? "ChurchfrontVideo-selftest" : "ChurchfrontVideo-single", out var first);
        if (!first)
        {
            MessageBox.Show("Churchfront Video is already open.", "Churchfront Video", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        MainThread.Poster = a => app.Dispatcher.BeginInvoke(a);
        MainThread.IsMainCheck = () => app.Dispatcher.CheckAccess();
        app.DispatcherUnhandledException += (_, e) => { Log(e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception x) Log(x); };

        var booth = Booth.Shared;
        // Self-tests name their own switcher; otherwise the config file says where it is.
        // The probe: a read-only look at a (real) switcher. Commands are refused before they can go out.
        var probe = Probe.On;
        if (probe) booth.Atem.ReadOnly = true;
        if (Environment.GetEnvironmentVariable("CUEVIDEO_HOST") is string host && host.Length > 0)
            booth.Atem.Connect(host, int.TryParse(Environment.GetEnvironmentVariable("CUEVIDEO_PORT"), out var p) ? p : 9910);
        if (Environment.GetEnvironmentVariable("CUEVIDEO_FAKECAMS") is string fake)
        {
            var cams = new List<PTZCamera>();
            var names = new[] { "Center", "Left", "Right" };
            for (int i = 0; i < names.Length; i++)
                cams.Add(new PTZCamera
                {
                    Name = names[i], Host = fake, Port = (ushort)(52381 + i), Proto = VISCAProtocol.SonyUDP, AtemInput = i + 1,
                    PresetNames = new() { [1] = "Pulpit", [2] = "Wide", [3] = "Band" },
                });
            booth.Ptz.Cameras = cams;
        }
        booth.Start();
        // The probe without CUEVIDEO_HOST looks at the switcher named in the setup file (read,
        // not applied: a self-test never connects to the file's switcher by itself).
        if (probe && booth.Atem.Host.Length == 0 && booth.Store.Config.AtemHost.Trim() is { Length: > 0 } fileHost)
            booth.Atem.Connect(fileHost, int.TryParse(Environment.GetEnvironmentVariable("CUEVIDEO_PORT"), out var fp) ? fp : 9910);
        Snapshots.Imaging = CanonFeedHub.Imaging;
        Snapshots.ToJpeg = Pictures.Jpeg;

        // Auto tracking: Canon pictures in, the pose model finds people. The model takes a
        // second or two to load, so it does that in the background.
        var tracker = AutoTracker.Shared;
        tracker.Pictures = new TrackPictures();
        StageScan.Shared.Images = new StillImages();
        tracker.Attach();
        tracker.Unavailable = probe ? "Auto tracking is off during the switcher probe." : "Auto tracking is still starting up. Try again in a few seconds.";
        if (!probe)
            Task.Run(() =>
            {
                var d = PoseDetector.Load(out var why);
                MainThread.Post(() => { tracker.Detector = d; tracker.Unavailable = why; Detector = d; });
            });

        var win = new MainWindow();
        ControlTools.ShowWindow = w => win.ShowView(w);
        booth.BringToFront = () => win.ShowView("main");
        // Claude's way in (through cuevideo-mcp). Not during self-tests, which may run beside the real app.
        if (!booth.Store.ReadOnly) ControlServer.Shared.Start();
        if (Environment.GetEnvironmentVariable("CUEVIDEO_VIEW") is string v)
        {
            var parts = v.Split(':');
            if (parts[0] == "engineer") win.ShowView(parts.Length > 1 ? parts[1] : "setup");
        }

        // Quitting stops every camera this app is moving and waits a moment for the stops to
        // reach the cameras: a Canon keeps turning at the last speed it was given.
        app.Exit += (_, _) =>
        {
            ControlServer.Shared.Stop();
            tracker.StopAll();
            NdiHub.StopAll();
            booth.Ptz.Shutdown();
            // The NDI pictures close their receivers within a quarter second (Shutdown above
            // already took longer), so senders see this app leave.
            Ndi.WaitReceiversClosed(1000);
        };
        app.MainWindow = win;
        win.Show();
        // CUEVIDEO_SCREENSHOT: a self-test saves what the window shows every two seconds, as a
        // PNG, even when the screen is locked and nothing is drawn on it.
        if (Environment.GetEnvironmentVariable("CUEVIDEO_SCREENSHOT") is string shot && shot.Length > 0)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (_, _) => SaveWindow(win, shot);
            t.Start();
        }
        if (probe) Probe.Run(win);
        // CUEVIDEO_KEYS: a self-test presses shortcuts, one every two seconds, straight into the
        // app ("ctrl+shift+r;ctrl+1;ctrl+t"), so nothing else on the desktop receives them.
        if (Environment.GetEnvironmentVariable("CUEVIDEO_KEYS") is string keys && keys.Length > 0)
        {
            var queue = new Queue<string>(keys.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var kt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            kt.Tick += (_, _) =>
            {
                if (queue.Count == 0) { kt.Stop(); return; }
                var parts = queue.Dequeue().ToLowerInvariant().Split('+');
                var mods = System.Windows.Input.ModifierKeys.None;
                foreach (var m in parts[..^1]) mods |= m switch { "ctrl" => System.Windows.Input.ModifierKeys.Control, "shift" => System.Windows.Input.ModifierKeys.Shift, "alt" => System.Windows.Input.ModifierKeys.Alt, _ => 0 };
                var k = parts[^1] switch { "," => System.Windows.Input.Key.OemComma, "." => System.Windows.Input.Key.OemPeriod, "1" => System.Windows.Input.Key.D1,
                    "[" => System.Windows.Input.Key.OemOpenBrackets, "]" => System.Windows.Input.Key.OemCloseBrackets,
                    var s => Enum.TryParse<System.Windows.Input.Key>(s, true, out var parsed) ? parsed : System.Windows.Input.Key.None };
                // Plain keys go to the shot deck (Q to G, [ and ]), the way the booth keys do.
                var used = mods == System.Windows.Input.ModifierKeys.None ? win.DeckKey(k) : win.Shortcut(k, mods);
                Console.Out.WriteLine($"key {string.Join("+", parts)}: {(used ? "used" : "not used")}; view {Booth.Shared.View}{(Booth.Shared.View == AppView.Engineer ? " " + Booth.Shared.Section : "")}; tracking {AutoTracker.Shared.Sessions.Count}");
            };
            kt.Start();
        }
        var code = app.Run();
        // Everything is stopped and saved by now: the setup and prefs are written as they change
        // and when the window closes, and the cameras were told to stop above. Ending the process
        // here, rather than returning, skips Windows unloading the native libraries (NDI, ONNX
        // Runtime, DirectML) one by one. After a day and a half open, one of those hung the app
        // at that step: window gone, the process left behind with one thread until it was killed.
        single.ReleaseMutex();
        single.Dispose();
        Console.Out.Flush();
        TerminateProcess(GetCurrentProcess(), (uint)code);
        return code;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>Saves what the window shows as a PNG (works with the screen locked). False if it couldn't.</summary>
    public static bool SaveWindow(Window win, string path)
    {
        try
        {
            if (win.Content is not System.Windows.FrameworkElement root || root.ActualWidth < 1) return false;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
            return true;
        }
        catch (Exception e) { Log(e); return false; }
    }

    /// <summary>The loaded pose model, for the Status page. Null until it loads, or if it can't.</summary>
    public static PoseDetector? Detector { get; private set; }

    private static void Log(Exception e)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.Folder);
            File.AppendAllText(Path.Combine(ConfigStore.Folder, "errors.log"), $"{DateTime.Now:u} {e}\n\n");
        }
        catch { }
        try { Booth.Shared.Ptz.StopAllManual(); } catch { }
    }
}
