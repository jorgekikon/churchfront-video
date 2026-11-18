using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CueATEM;

namespace CueVideo.App;

/// <summary>The one window: a top bar, then the Operator or Engineer view. Keyboard: 1 to 9 and 0
/// put that tile of the source row on Next, Shift puts it on Live, Space cuts, Enter fades,
/// arrows move the camera on Next. Only in the Operator view, and never while typing.
/// The Mac's menu shortcuts (Ctrl for ⌘) work on every page: see Shortcut.</summary>
public sealed class MainWindow : Window
{
    public const string WindowTitle = "Churchfront Video";
    private static Booth B => Booth.Shared;
    private readonly ContentControl _content = new();
    private readonly OperatorView _operator = new();
    private readonly EngineerView _engineer = new();
    private readonly TopMonitors _monitors = new();
    private readonly Button _opTab, _enTab;
    private readonly TextBlock _opText, _enText, _status, _cams;
    private readonly System.Windows.Shapes.Ellipse _dot = new() { Width = 8, Height = 8 };
    private readonly T.LitButton _record, _stream;
    private bool _dirty = true;
    private long _lastRefresh;
    private AppView? _shownView;

    public MainWindow()
    {
        Title = WindowTitle;
        Width = B.Prefs.WindowWidth ?? 1440; Height = B.Prefs.WindowHeight ?? 900;
        MinWidth = 1100; MinHeight = 720;
        if (B.Prefs.Maximized) WindowState = WindowState.Maximized;
        Background = T.Bg; Foreground = T.Ink; FontFamily = T.Sans;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/AppIcon.ico")); } catch { }

        // Top bar
        _opText = T.Text("Operator", 13, FontWeights.SemiBold); _enText = T.Text("Engineer", 13, FontWeights.SemiBold);
        _opTab = T.Button(_opText, () => B.View = AppView.Operate, T.Transparent, T.Transparent, h: 28, radius: 6, tip: "The booth screen (Ctrl+1)");
        _enTab = T.Button(_enText, () => B.View = AppView.Engineer, T.Transparent, T.Transparent, h: 28, radius: 6, tip: "Routing, full switcher, setup and status");
        var seg = new Border { Background = T.SurfaceSunken, BorderBrush = T.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = T.Row(2, _opTab, _enTab) };
        _status = T.Text("", 13, FontWeights.SemiBold);
        _cams = T.Text("", 13, null, T.InkMuted);
        _record = new T.LitButton(() => Toggle(recording: true), 110, 30, 12);
        _stream = new T.LitButton(() => Toggle(recording: false), 110, 30, 12);
        _record.Button.Margin = new Thickness(0, 0, 8, 0); _stream.Button.Margin = new Thickness(0, 0, 8, 0);
        var gear = T.Button(T.Text("⚙", 16, null, T.InkMuted), () => B.Show(EngineerSection.Setup), T.Transparent, T.Transparent, 34, 30, 6, new Thickness(0), "Setup (Ctrl+,)");
        var right = T.Row(0, _record.Button, _stream.Button, gear);
        right.VerticalAlignment = VerticalAlignment.Center;
        var bar = new DockPanel { Height = 64, Margin = new Thickness(20, 0, 16, 0) };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        // Small live pictures: Live and Next (off Operator), then the pinned screens.
        _monitors.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(_monitors, Dock.Right);
        bar.Children.Add(_monitors);
        var left = T.Row(16, T.Text("Churchfront Video", 16, FontWeights.Bold), seg, T.Row(6, _dot, _status), _cams);
        left.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(left);

        var root = new DockPanel();
        var barBorder = new Border { Child = bar, Background = T.Toolbar, BorderBrush = T.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(barBorder, Dock.Top);
        root.Children.Add(barBorder);
        root.Children.Add(_content);
        Content = root;

        // Anything that changes marks the screen for a redraw; a timer redraws at most 30 times a second.
        B.Atem.Changed += () => _dirty = true;
        B.Ptz.Changed += () => _dirty = true;
        B.Store.Changed += () => _dirty = true;
        B.Changed += () => _dirty = true;
        CanonFeedHub.FrameArrived += () => _dirty = true;
        AutoTracker.Shared.Changed += () => _dirty = true;
        StageScan.Shared.Changed += () => _dirty = true;
        NdiHub.FrameArrived += () => _dirty = true;     // from the NDI receiving threads: only a flag
        NdiDirectory.Changed += () => _dirty = true;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            var now = Environment.TickCount64;
            if (!_dirty && now - _lastRefresh < 200) return;   // still refresh a few times a second, for blinking
            _dirty = false; _lastRefresh = now;
            RefreshAll();
        };
        timer.Start();

        PreviewKeyDown += (_, e) => e.Handled = Key(e, down: true);
        PreviewKeyUp += (_, e) => e.Handled = Key(e, down: false);
        // A key or mouse release may never arrive once another window or app is in front.
        Deactivated += (_, _) => { B.ReleaseArrows(); B.Ptz.StopAllManual(); };
        Closing += (_, _) =>
        {
            // A self-test (the probe, CUEVIDEO_ settings) leaves the operator's window settings alone.
            if (B.Store.ReadOnly) return;
            B.Prefs.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { B.Prefs.WindowWidth = Width; B.Prefs.WindowHeight = Height; }
            B.Prefs.Save();
        };
        RefreshAll();
    }

    private void Toggle(bool recording)
    {
        var s = B.Atem.State;
        var on = recording ? s.Recording : s.Streaming;
        var what = recording ? "recording" : "stream";
        if (on && !Dialogs.Confirm($"Stop the {what}?", recording ? "The switcher stops recording." : "The live stream ends for everyone watching.", "Stop", danger: true)) return;
        if (!on && !Dialogs.Confirm(recording ? "Start recording?" : "Start the stream?", recording ? "The switcher starts recording." : "The switcher starts streaming to its configured service.", "Start")) return;
        if (recording) B.Atem.SetRecording(!on); else B.Atem.SetStreaming(!on);
    }

    private void RefreshAll()
    {
        var view = B.View;
        _opTab.Background = view == AppView.Operate ? T.Accent : T.Transparent;
        _opText.Foreground = view == AppView.Operate ? T.OnAction : T.Ink;
        _enTab.Background = view == AppView.Engineer ? T.Accent : T.Transparent;
        _enText.Foreground = view == AppView.Engineer ? T.OnAction : T.Ink;
        var link = B.Atem.Link;
        (_status.Text, _status.Foreground, _dot.Fill) = link.Kind switch
        {
            LinkKind.Online => ("Online", T.Success, T.Success),
            LinkKind.Connecting => ("Connecting", T.Warning, T.Warning),
            LinkKind.Offline => ("Offline", T.Danger, T.Danger),
            _ => ("No switcher set up", T.InkMuted, T.InkSubtle),
        };
        _status.ToolTip = ControlTools.LinkText(link) + (B.Atem.Host.Length > 0 ? $" ({B.Atem.Host})" : "");
        var n = B.Ptz.Cameras.Count;
        _cams.Text = n == 0 ? "" : $"{n} camera{(n == 1 ? "" : "s")}";
        var s = B.Atem.State;
        _record.Button.Visibility = s.HasRecording ? Visibility.Visible : Visibility.Collapsed;
        _stream.Button.Visibility = s.HasStreaming ? Visibility.Visible : Visibility.Collapsed;
        _record.Set(s.Recording ? "● Recording" : "● Record", null, s.Recording ? T.Program : null);
        _stream.Set(s.Streaming ? "● Streaming" : s.StreamConnecting ? "Connecting" : "● Stream", null, s.Streaming ? T.Program : s.StreamConnecting ? T.Warning : null);

        if (_shownView != view)
        {
            if (_shownView == AppView.Operate) _operator.Teardown();
            if (_shownView == AppView.Engineer) _engineer.Leave();
            _shownView = view;
            _content.Content = view == AppView.Operate ? _operator : _engineer;
            if (view == AppView.Operate) _operator.Refresh(force: true);
        }
        if (view == AppView.Operate) _operator.Refresh(); else _engineer.Refresh();
        _monitors.Refresh();
        ExposureWindow.RefreshOpen();
    }

    /// <summary>The Mac's menu shortcuts, with Ctrl for ⌘, on every page (they don't clash with
    /// typing: Ctrl+C, V, X, Z and A are left to text fields). Returns true when one was used.
    ///   Ctrl+1 Operator · Ctrl+, Setup · Ctrl+Shift+S Stage scan · Ctrl+Shift+R Routing ·
    ///   Ctrl+Shift+K Full switcher · Ctrl+T track the camera on Next (again: stop) ·
    ///   Ctrl+. stop all tracking · Ctrl+Shift+F focus the camera on Next</summary>
    private bool Shortcut(KeyEventArgs e) =>
        !e.IsRepeat && Shortcut(e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);

    public bool Shortcut(Key key, ModifierKeys mods)
    {
        if ((mods & ModifierKeys.Control) == 0 || (mods & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        var shift = (mods & ModifierKeys.Shift) != 0;
        switch (key, shift)
        {
            case (System.Windows.Input.Key.D1 or System.Windows.Input.Key.NumPad1, false): B.View = AppView.Operate; return true;
            case (System.Windows.Input.Key.OemComma, false): B.Show(EngineerSection.Setup); return true;
            case (System.Windows.Input.Key.S, true): B.Show(EngineerSection.Scan); return true;
            case (System.Windows.Input.Key.R, true): B.Show(EngineerSection.Routing); return true;
            case (System.Windows.Input.Key.K, true): B.Show(EngineerSection.Switcher); return true;
            case (System.Windows.Input.Key.OemPeriod, false): AutoTracker.Shared.StopAll(); return true;
            case (System.Windows.Input.Key.T, false):
                if (B.CameraOnNext is PTZCamera t)
                {
                    if (AutoTracker.Shared.IsTracking(t.Id)) AutoTracker.Shared.Stop(t.Id);
                    else if (AutoTracker.Shared.CanTrack(t)) AutoTracker.Shared.Start(t);
                }
                return true;
            case (System.Windows.Input.Key.F, true):
                if (B.CameraOnNext is PTZCamera f) B.Ptz.FocusOnce(f.Id);
                return true;
        }
        return false;
    }

    /// <summary>The booth keys. Returns true when the key was used.</summary>
    private bool Key(KeyEventArgs e, bool down)
    {
        if (down && IsActive && Shortcut(e)) return true;
        if (B.View != AppView.Operate || !IsActive) { B.ReleaseArrows(); return false; }
        if (Keyboard.FocusedElement is TextBox or PasswordBox) return false;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case System.Windows.Input.Key.Left: B.Arrow("Left", down); return true;
            case System.Windows.Input.Key.Right: B.Arrow("Right", down); return true;
            case System.Windows.Input.Key.Up: B.Arrow("Up", down); return true;
            case System.Windows.Input.Key.Down: B.Arrow("Down", down); return true;
        }
        if (!down) return false;
        if (e.IsRepeat) return key is System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter;
        switch (key)
        {
            // Escape stops every tracking camera (the Mac's Command-period).
            case System.Windows.Input.Key.Escape when AutoTracker.Shared.Sessions.Count > 0: AutoTracker.Shared.StopAll(); return true;
            case System.Windows.Input.Key.Space: B.Atem.Cut(B.Me); return true;
            case System.Windows.Input.Key.Enter: B.Atem.Auto(B.Me); return true;
        }
        int? digit = key switch
        {
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => key - System.Windows.Input.Key.D0,
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => key - System.Windows.Input.Key.NumPad0,
            _ => null,
        };
        if (digit is int d)
        {
            B.Digit(d == 0 ? 9 : d - 1, live: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            return true;
        }
        return false;
    }

    /// <summary>Claude's show_window.</summary>
    public void ShowView(string w)
    {
        switch (w)
        {
            case "operator": case "main": B.View = AppView.Operate; break;
            case "scan": B.Show(EngineerSection.Scan); break;
            case "routing": B.Show(EngineerSection.Routing); break;
            case "switcher": B.Show(EngineerSection.Switcher); break;
            case "setup": B.Show(EngineerSection.Setup); break;
            case "status": B.Show(EngineerSection.Status); break;
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
