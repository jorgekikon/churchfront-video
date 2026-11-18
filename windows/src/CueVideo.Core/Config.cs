using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CueATEM;

namespace CueVideo;

/// <summary>One tile in the volunteer's source row: an ATEM input, with an optional friendlier name.</summary>
public sealed class VolunteerSource
{
    [JsonRequired] public int Input { get; set; }
    public string? Label { get; set; }
    /// <summary>An NDI source showing what this input carries. Not shown on Windows yet.</summary>
    public string? Ndi { get; set; }
}

/// <summary>A volunteer button that puts a key on or off air: an upstream key (me and keyer,
/// both from 1) or a downstream key (dsk, from 1).</summary>
public sealed class KeyButton
{
    [JsonRequired] public string Label { get; set; } = "";
    public int? Me { get; set; }
    public int? Keyer { get; set; }
    public int? Dsk { get; set; }
    public string? Ndi { get; set; }
    [JsonIgnore] public string Id => $"{Label}|{Me ?? 0}|{Keyer ?? 0}|{Dsk ?? 0}";

    public bool IsOn(ATEMState s)
    {
        if (Dsk is int d) return s.Get(s.DskOnAir, d - 1);
        if (Keyer is not int k) return false;
        return s.KeyOnAir.TryGetValue((Me ?? 1) - 1, out var m) && m.TryGetValue(k - 1, out var on) && on;
    }

    public void Toggle(ATEMClient atem)
    {
        if (Dsk is int d) { atem.DskAuto(d - 1); return; }
        if (Keyer is not int k) return;
        atem.SetKeyOnAir(k - 1, !IsOn(atem.State), (Me ?? 1) - 1);
    }

    /// <summary>Whether the key will be on air after the next Cut or Fade.</summary>
    public bool OnNext(ATEMState s)
    {
        if (Dsk is int d) return s.Get(s.DskOnAir, d - 1) != s.Get(s.DskTie, d - 1);
        if (Keyer is not int k) return false;
        var m = (Me ?? 1) - 1;
        var sel = ((s.NextSelection.TryGetValue(m, out var n) ? n : 1) >> k) & 1;
        return IsOn(s) != (sel == 1);
    }

    /// <summary>"M/E 1 Key 1" or "DSK 2".</summary>
    [JsonIgnore] public string Target => Dsk is int d ? $"DSK {d}" : $"M/E {Me ?? 1} Key {Keyer ?? 0}";
}

/// <summary>A screen (or other destination) fed by one or more aux outputs, counted from 1.</summary>
public sealed class ScreenOutput
{
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public List<int> Aux { get; set; } = new();
    public string? Note { get; set; }
    public bool? Volunteer { get; set; }
    public bool? Pinned { get; set; }
    [JsonIgnore] public string Id => Name + string.Join(",", Aux);
    [JsonIgnore] public bool ForVolunteers => Volunteer ?? true;
}

/// <summary>How a follow shot is framed by auto tracking: changes to the standard for one size.</summary>
public sealed class FramingRule
{
    public double? EyeLine { get; set; }
    public double? Scale { get; set; }
    public double? LeadRoom { get; set; }
    public double? DeadX { get; set; }
    public double? DeadY { get; set; }
    [JsonIgnore] public bool IsEmpty => EyeLine == null && Scale == null && LeadRoom == null && DeadX == null && DeadY == null;
    public FramingRule Merged(FramingRule? o) => o == null ? this : new FramingRule
    {
        EyeLine = o.EyeLine ?? EyeLine, Scale = o.Scale ?? Scale, LeadRoom = o.LeadRoom ?? LeadRoom, DeadX = o.DeadX ?? DeadX, DeadY = o.DeadY ?? DeadY,
    };
}

public sealed class TrackTuning
{
    public double? Correction { get; set; }
    public double? Latency { get; set; }
    public double? WalkThreshold { get; set; }
    public double? ZoomThreshold { get; set; }
    public double? ZoomWait { get; set; }
}

public sealed class RouteChange
{
    [JsonRequired] public int Aux { get; set; }
    [JsonRequired] public int Source { get; set; }
}

/// <summary>A volunteer button that routes one or more screens at once. Lit when the
/// switcher already matches every change in it.</summary>
public sealed class RouteButton
{
    [JsonRequired] public string Label { get; set; } = "";
    [JsonRequired] public List<RouteChange> Set { get; set; } = new();
    public bool IsActive(ATEMState s) => Set.Count > 0 && Set.All(c => s.Aux.TryGetValue(c.Aux - 1, out var v) && v == c.Source);
    public void Run(ATEMClient atem) { foreach (var c in Set) atem.SetAux(c.Aux - 1, c.Source); }
}

/// <summary>The three follow shots auto tracking frames (see Tracker.cs).</summary>
public enum ShotSize { Shoulders, Waist, Full }

public static class ShotSizes
{
    public static readonly ShotSize[] All = { ShotSize.Shoulders, ShotSize.Waist, ShotSize.Full };
    public static string Key(this ShotSize z) => z.ToString().ToLowerInvariant();
    public static string Name(this ShotSize z) => z switch { ShotSize.Shoulders => "Shoulders up", ShotSize.Waist => "Waist up", _ => "Full length" };
    public static ShotSize? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "shoulders" => ShotSize.Shoulders, "waist" => ShotSize.Waist, "full" => ShotSize.Full, _ => null,
    };
    public static FramingRule Standard(this ShotSize z) => z switch
    {
        ShotSize.Shoulders => new FramingRule { EyeLine = 0.29, Scale = 0.36, LeadRoom = 0.09, DeadX = 0.08, DeadY = 0.05 },
        ShotSize.Waist => new FramingRule { EyeLine = 0.29, Scale = 0.21, LeadRoom = 0.11, DeadX = 0.07, DeadY = 0.05 },
        _ => new FramingRule { EyeLine = 0.12, Scale = 0.097, LeadRoom = 0.13, DeadX = 0.06, DeadY = 0.04 },
    };
    public static FramingRule Rule(this ShotSize z, CueConfig c) => z.Standard().Merged(c.Framing.TryGetValue(z.Key(), out var r) ? r : null);
}

/// <summary>How Churchfront Video is set up, kept as plain JSON so a person or Claude can read
/// and edit it: %APPDATA%\CueVideo\config.json. The same file format as the Mac app.</summary>
public sealed class CueConfig
{
    public string AtemHost { get; set; } = "";
    /// <summary>The M/E the volunteer runs, counted from 1 as on the switcher.</summary>
    public int Me { get; set; } = 1;
    public List<VolunteerSource> Sources { get; set; } = new();
    public List<PTZCamera> Cameras { get; set; } = new();
    public List<KeyButton> Keys { get; set; } = new();
    public double TrackSpeed { get; set; } = 1;
    public double PtzSpeed { get; set; } = 1;
    public bool TrackTilt { get; set; } = true;
    public bool TrackZoom { get; set; } = true;
    public List<string> MixNames { get; set; } = new();
    public Dictionary<string, string> Pictures { get; set; } = new();
    public List<ScreenOutput> Screens { get; set; } = new();
    public List<RouteButton> Routes { get; set; } = new();
    public Dictionary<string, FramingRule> Framing { get; set; } = new();
    public TrackTuning TrackTuning { get; set; } = new();

    /// <summary>Fills in what a hand-written file left out, and keeps speeds in range.</summary>
    public CueConfig Normalized()
    {
        AtemHost ??= "";
        Sources ??= new(); Cameras ??= new(); Keys ??= new(); MixNames ??= new(); Pictures ??= new();
        Screens ??= new(); Routes ??= new(); Framing ??= new(); TrackTuning ??= new();
        TrackSpeed = Math.Clamp(TrackSpeed, 0.5, 4);
        PtzSpeed = Math.Clamp(PtzSpeed, 0.25, 2);
        foreach (var c in Cameras) c.Normalized();
        return this;
    }

    public static CueConfig Parse(string json)
    {
        var c = JsonSerializer.Deserialize<CueConfig>(json, CueJson.Options) ?? throw new JsonException("The file is empty.");
        return c.Normalized();
    }

    public static CueConfig Parse(JsonNode node) => Parse(node.ToJsonString());

    /// <summary>The file's text: pretty printed with keys in order, as the Mac writes it.</summary>
    public string ToFileText() => CueJson.Sorted(ToNode())!.ToJsonString(CueJson.Pretty).Replace("\r\n", "\n") + "\n";
    public JsonObject ToNode() => JsonSerializer.SerializeToNode(this, CueJson.Options)!.AsObject();
    public string Signature() => CueJson.Sorted(ToNode())!.ToJsonString();
    public CueConfig Clone() => Parse(ToNode());
}

/// <summary>Keeps the config file and the running app in step: edits to the file are picked up
/// within a second, and changes made in the app are written back to the file.</summary>
public sealed class ConfigStore
{
    public CueConfig Config { get; private set; } = new();
    /// <summary>Why the file couldn't be read, if it couldn't. The app keeps running on the last good setup.</summary>
    public string? FileError { get; private set; }
    public DateTime? LoadedAt { get; private set; }
    /// <summary>Raised on the main thread when the setup or the file's state changes.</summary>
    public event Action? Changed;

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CueVideo");
    public string FilePath { get; }
    /// <summary>Self-tests (any CUEVIDEO_ environment setting) bring their own switcher and
    /// cameras; they must never be written over the real setup.</summary>
    public bool ReadOnly { get; }
    private bool OwnFile { get; }
    private DateTime? _lastModified;
    private bool _unreadable;
    private string LastGoodPath => Path.Combine(Path.GetDirectoryName(FilePath)!, "config.last-good.json");
    private Timer? _timer;
    private Booth? _booth;
    private bool _applying;

    public ConfigStore(string? path = null, bool? readOnly = null)
    {
        // CUEVIDEO_CONFIG: a self-test's own setup file (its cameras are used; it's never written).
        OwnFile = path == null && Environment.GetEnvironmentVariable("CUEVIDEO_CONFIG") is string own && own.Length > 0;
        FilePath = path ?? (OwnFile ? Environment.GetEnvironmentVariable("CUEVIDEO_CONFIG")! : Path.Combine(Folder, "config.json"));
        ReadOnly = readOnly ?? Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(k => k.StartsWith("CUEVIDEO_", StringComparison.Ordinal));
    }

    public void Attach(Booth booth)
    {
        _booth = booth;
        if (Read() is CueConfig c) Config = c;
        else if (FileError == null) { Config = new CueConfig(); Write(); }
        else
        {
            // config.json has a mistake in it: open on the last setup that worked, and say so.
            try { Config = CueConfig.Parse(File.ReadAllText(LastGoodPath)); } catch { }
        }
        Apply();
        // Cameras edited in the app go back to the file.
        booth.Ptz.CamerasChanged += () =>
        {
            if (_applying) return;
            var cams = booth.Ptz.Cameras;
            Update(c => c.Cameras = cams.Select(x => x.Clone()).ToList());
        };
        _timer = new Timer(_ => MainThread.Post(Poll), null, 1000, 1000);
        Changed?.Invoke();
    }

    /// <summary>Change the setup from the app. Writes the file and applies what changed.</summary>
    public void Update(Action<CueConfig> change)
    {
        var c = Config.Clone();
        change(c);
        c.Normalized();
        if (c.Signature() == Config.Signature()) return;
        Config = c;
        Write();
        Apply();
        Changed?.Invoke();
    }

    /// <summary>Replaces the whole setup (Claude's update_config).</summary>
    public void Replace(CueConfig c) => Update(x =>
    {
        foreach (var p in typeof(CueConfig).GetProperties()) p.SetValue(x, p.GetValue(c));
    });

    /// <summary>Sources for the volunteer's row: the configured ones, or every camera input until it's set up.</summary>
    public List<VolunteerSource> VolunteerSources(ATEMState state) =>
        Config.Sources.Count > 0 ? Config.Sources : Config.Cameras.Where(c => c.AtemInput != null).Select(c => new VolunteerSource { Input = c.AtemInput!.Value }).ToList();

    public string? Ndi(int? input)
    {
        if (input is not int i) return null;
        var n = Config.Sources.FirstOrDefault(s => s.Input == i)?.Ndi;
        if (!string.IsNullOrEmpty(n)) return n;
        return Config.Pictures.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out var p) && p.Length > 0 ? p : null;
    }

    /// <summary>What an M/E is for: "Live stream (M/E 1)", or just "M/E 2".</summary>
    public string MixName(int me)
    {
        var n = me >= 0 && me < Config.MixNames.Count ? Config.MixNames[me] : "";
        return n.Length == 0 ? $"M/E {me + 1}" : $"{n} (M/E {me + 1})";
    }

    /// <summary>A routing source in plain words: M/E outputs by what they're for, others by label.</summary>
    public string RouteLabel(int? source, ATEMState state)
    {
        if (source is not int s) return "Nothing";
        if (s >= 10000 && s < 11000)
        {
            var me = (s - 10000) / 10 - 1;
            return s % 10 == 1 ? $"{MixName(me)} preview" : MixName(me);
        }
        return Label(s, state);
    }

    /// <summary>The screens previewed in the top bar, four at most.</summary>
    public List<ScreenOutput> PinnedScreens
    {
        get
        {
            var all = Config.Screens;
            if (all.Any(s => s.Pinned != null)) return all.Where(s => s.Pinned == true).Take(4).ToList();
            return all.Where(s => s.ForVolunteers).Take(3).ToList();
        }
    }

    public void SetPinned(string id, bool on)
    {
        var now = PinnedScreens.Select(s => s.Id).ToHashSet();
        Update(c => { foreach (var s in c.Screens) s.Pinned = s.Id == id ? on : now.Contains(s.Id); });
    }

    public ScreenOutput? Screen(int aux) => Config.Screens.FirstOrDefault(s => s.Aux.Contains(aux));

    public string Label(int? input, ATEMState state)
    {
        if (input is not int i) return "Nothing";
        var l = Config.Sources.FirstOrDefault(s => s.Input == i)?.Label;
        if (!string.IsNullOrEmpty(l)) return l;
        var cam = Config.Cameras.FirstOrDefault(c => c.AtemInput == i);
        if (cam != null && cam.Name.Length > 0) return cam.Name;
        return state.Inputs.TryGetValue(i, out var x) ? x.Long : $"Input {i}";
    }

    /// <summary>What a screen shows: one source, or "Mixed" when its linked auxes disagree.</summary>
    public string Showing(ScreenOutput sc, ATEMState s)
    {
        var now = sc.Aux.Select(a => s.Aux.TryGetValue(a - 1, out var v) ? (int?)v : null).Distinct().ToList();
        return now.Count == 1 ? RouteLabel(now[0], s) : "Mixed";
    }

    /// <summary>Screen names a button changes: "LED WALL, LOBBY TVS".</summary>
    public string RouteSummary(RouteButton r)
    {
        var names = new List<string>();
        foreach (var c in r.Set) { var n = Screen(c.Aux)?.Name ?? $"Aux {c.Aux}"; if (!names.Contains(n)) names.Add(n); }
        return string.Join(", ", names).ToUpperInvariant();
    }

    public string RouteDetail(RouteButton r, ATEMState s)
    {
        var lines = new List<string>();
        foreach (var c in r.Set)
        {
            var line = $"{Screen(c.Aux)?.Name ?? $"Aux {c.Aux}"} ← {RouteLabel(c.Source, s)}";
            if (!lines.Contains(line)) lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    // MARK: Private

    private void Apply()
    {
        if (_booth is not Booth b) return;
        b.Ptz.ManualSpeed = Config.PtzSpeed;
        var me = Math.Max(0, Config.Me - 1);
        if (b.Me != me) b.Me = me;
        // A self-test leaves the switcher and cameras it brought alone, unless it named its own setup file.
        if (ReadOnly && !OwnFile) return;
        if (b.Atem.Host != Config.AtemHost.Trim() && !(ReadOnly && Config.AtemHost.Trim().Length == 0)) b.Atem.Connect(Config.AtemHost);
        if (PTZController.Signature(b.Ptz.Cameras) != PTZController.Signature(Config.Cameras))
        {
            _applying = true;
            try { b.Ptz.Cameras = Config.Cameras; } finally { _applying = false; }
        }
    }

    private DateTime? Modified() { try { return File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : null; } catch { return null; } }

    private void Poll()
    {
        if (Modified() == _lastModified) return;
        var before = FileError;
        if (Read() is CueConfig c && c.Signature() != Config.Signature())
        {
            Config = c;
            Apply();
            Changed?.Invoke();
        }
        else if (before != FileError) Changed?.Invoke();
    }

    private CueConfig? Read()
    {
        _lastModified = Modified();
        string text;
        try { text = File.ReadAllText(FilePath); } catch { return null; }
        try
        {
            var c = CueConfig.Parse(text);
            FileError = null;
            _unreadable = false;
            LoadedAt = DateTime.Now;
            KeepLastGood(c);
            return c;
        }
        catch (Exception e)
        {
            FileError = Explain(e);
            _unreadable = true;
            return null;
        }
    }

    private void Write()
    {
        if (ReadOnly) return;
        var text = Config.ToFileText();
        try { if (File.Exists(FilePath) && File.ReadAllText(FilePath) == text) return; } catch { }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (_unreadable && File.Exists(FilePath))
            {
                // The file on disk has a mistake in it and may hold work that isn't in the app.
                // Keep it under another name before saving over it; if that fails, leave it alone.
                var aside = Path.Combine(Path.GetDirectoryName(FilePath)!, $"config.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(FilePath, aside);
                _unreadable = false;
            }
            WriteAtomic(FilePath, text);
            _lastModified = Modified();
            FileError = null;
            KeepLastGood(Config);
        }
        catch (Exception e)
        {
            FileError = $"Couldn't save the setup: {e.Message}";
        }
    }

    internal static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    /// <summary>Remembers a setup that loaded or saved cleanly (never an empty one, and never during self-tests).</summary>
    private void KeepLastGood(CueConfig c)
    {
        if (ReadOnly || c.Signature() == new CueConfig().Signature()) return;
        var text = c.ToFileText();
        try
        {
            if (File.Exists(LastGoodPath) && File.ReadAllText(LastGoodPath) == text) return;
            WriteAtomic(LastGoodPath, text);
        }
        catch { }
    }

    private static string Explain(Exception e)
    {
        if (e is not JsonException j) return e.Message;
        var line = j.LineNumber is long l ? $" (line {l + 1})" : "";
        var path = (j.Path ?? "").TrimStart('$').TrimStart('.');
        // A value of the wrong kind has a path to it; broken JSON (a missing comma or quote) is at the top.
        return path.Length > 0 && j.InnerException is not null || path.Length > 0 && !j.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            ? $"Wrong value at {path}{line}: {FirstLine(j.Message)}"
            : $"Not valid JSON{line}: {FirstLine(j.Message)}";
    }

    private static string FirstLine(string s)
    {
        foreach (var cut in new[] { " Path:", " LineNumber:" })
        {
            var i = s.IndexOf(cut, StringComparison.Ordinal);
            if (i > 0) s = s[..i];
        }
        return s.Trim().TrimEnd('.') + ".";
    }
}
