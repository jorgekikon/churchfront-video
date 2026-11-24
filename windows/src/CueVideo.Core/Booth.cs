using System.Text.Json;
using CueATEM;

namespace CueVideo;

public enum AppView { Operate, Engineer }

/// <summary>The pages after Operate, as tabs in the top bar.</summary>
public enum EngineerSection { Scan, Routing, Switcher, Setup, Status }

public static class EngineerSections
{
    public static string Title(this EngineerSection s) => s switch
    {
        EngineerSection.Scan => "Stage scan",
        EngineerSection.Routing => "Routing",
        EngineerSection.Switcher => "Full switcher",
        EngineerSection.Setup => "Setup",
        _ => "Status",
    };
}

/// <summary>What the whole app shares: the switcher, the cameras and the operator's settings.
/// Used from the main (UI) thread.</summary>
public sealed class Booth
{
    public static Booth Shared { get; } = new();
    public ATEMClient Atem { get; } = new();
    public PTZController Ptz { get; } = new();
    public ConfigStore Store { get; } = new();
    public Prefs Prefs { get; } = Prefs.Load();

    /// <summary>Raised on the main thread when Me, View or Section change.</summary>
    public event Action? Changed;

    private int _me;
    public int Me { get => _me; set { if (_me != value) { _me = value; Changed?.Invoke(); } } }

    private AppView _view = AppView.Operate;
    /// <summary>Which view the main window shows. It always opens on Operator. Leaving Operator
    /// lets go of the cameras: the joystick or an arrow key may still be held.</summary>
    public AppView View
    {
        get => _view;
        set
        {
            if (_view == value) return;
            var old = _view;
            _view = value;
            if (old == AppView.Operate) { HeldArrows.Clear(); Ptz.StopAllManual(); }
            Changed?.Invoke();
        }
    }

    private EngineerSection _section = EngineerSection.Setup;
    public EngineerSection Section { get => _section; set { if (_section != value) { _section = value; Changed?.Invoke(); } } }

    public void Show(EngineerSection s) { Section = s; View = AppView.Engineer; }

    /// <summary>Set by the app: brings the window to the front (show_window).</summary>
    public Action? BringToFront;

    /// <summary>The camera on Next, which the camera controls act on.</summary>
    public PTZCamera? CameraOnNext
    {
        get
        {
            var next = Atem.State.PreviewOf(Me);
            return Ptz.Cameras.FirstOrDefault(c => c.AtemInput == next && c.Host.Length > 0);
        }
    }

    /// <summary>Arrow keys being held, by name ("Left", "Right", "Up", "Down").</summary>
    public HashSet<string> HeldArrows { get; } = new();

    /// <summary>Lets go of any arrow keys being held and stops that camera.</summary>
    public void ReleaseArrows()
    {
        if (HeldArrows.Count == 0) return;
        HeldArrows.Clear();
        Ptz.StopDrive();
    }

    public bool ConnectedOnce => Atem.State.Inputs.Count > 0;

    /// <summary>An arrow key went down or up. Only the camera on Next moves, as with the joystick.</summary>
    public void Arrow(string key, bool down)
    {
        var next = Atem.State.PreviewOf(Me);
        if (!Ptz.Cameras.Any(c => c.AtemInput == next && c.Id == Ptz.Selected)) { HeldArrows.Clear(); Ptz.StopDrive(); return; }
        if (down) HeldArrows.Add(key); else HeldArrows.Remove(key);
        double x = 0, y = 0;
        foreach (var k in HeldArrows)
        {
            switch (k) { case "Left": x -= 0.6; break; case "Right": x += 0.6; break; case "Down": y -= 0.6; break; case "Up": y += 0.6; break; }
        }
        if (HeldArrows.Count == 0) Ptz.StopDrive(); else Ptz.Drive(x, y);
    }

    /// <summary>A number key (0 to 9, 0 being the tenth tile) puts that tile on Next, or on Live with Shift.</summary>
    public void Digit(int index, bool live)
    {
        var src = Store.VolunteerSources(Atem.State);
        if (index < 0 || index >= src.Count) return;
        if (live) Atem.SetProgram(src[index].Input, Me); else Atem.SetPreview(src[index].Input, Me);
    }

    /// <summary>Wires the parts together. Call once on the main thread.</summary>
    public void Start()
    {
        Atem.Changed += () =>
        {
            // The camera controls follow the camera on Next.
            if (Prefs.FollowPreview) Ptz.Follow(Atem.State.PreviewOf(Me));
            // Cameras with their own tally light (a Panasonic camcorder) light it while on Live.
            Ptz.SetTally(Atem.State.OnLive);
        };
        Ptz.CamerasChanged += () => Ptz.SetTally(Atem.State.OnLive);
        Store.Attach(this);
    }
}

/// <summary>The operator's own settings, kept beside the config file (prefs.json).</summary>
public sealed class Prefs
{
    public bool Simple { get; set; }
    public bool FollowPreview { get; set; } = true;
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool Maximized { get; set; }

    private static string FilePath => Path.Combine(ConfigStore.Folder, "prefs.json");

    public static Prefs Load()
    {
        try { return JsonSerializer.Deserialize<Prefs>(File.ReadAllText(FilePath), CueJson.Options) ?? new(); } catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.Folder);
            ConfigStore.WriteAtomic(FilePath, JsonSerializer.Serialize(this, CueJson.Pretty));
        }
        catch { }
    }
}
