using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using CueATEM;

namespace CueVideo;

// A port of Sources/CueVideo/StageScan.swift.
//
// Stage scan: before a service, with everyone in place (rehearsal or soundcheck), each Canon
// camera that isn't on Live goes to a wide shot, Churchfront Video finds everyone in it, and
// works out the shot that frames each person from that camera. A person labels them ("Pastor",
// "Keys"), and the app saves a shot per person per camera and remembers what each one looks
// like, so auto tracking can follow someone by name for the rest of the day.
//
// The wide shot is the camera's shot named "Stage" or "Wide" if it has one; otherwise where it's
// pointing now, zoomed all the way out. Cameras go back to where they were afterwards. A camera
// that is on Live or auto tracking is left alone.

/// <summary>Turning a place in a camera's picture into where to point the camera. Canon cameras
/// report pan and tilt in degrees and their zoom as the horizontal angle of view, so this is
/// exact geometry, not a guess. Free of cameras and pictures so it can be tested on its own.</summary>
public static class ShotMath
{
    /// <summary>Where a camera points (degrees; right and up are positive) and how wide it sees.</summary>
    public readonly record struct View(double Pan, double Tilt, double Fov);

    public readonly record struct Range(double Lo, double Hi) { public double Clamp(double v) => Math.Min(Hi, Math.Max(Lo, v)); }

    public sealed record Limits(Range Fov, Range Pan, Range Tilt)
    {
        public static Limits Default { get; } = new(new(3, 75), new(-170, 170), new(-30, 90));
    }

    private const double Rad = Math.PI / 180;

    /// <summary>How far right of and above the middle of the picture a point is, in degrees
    /// (x and y from 0 to 1, origin top left; the picture is 16:9).</summary>
    public static (double Right, double Up) Angles(PointD p, double fov)
    {
        var t = Math.Tan(fov / 2 * Rad);
        return (Math.Atan((p.X - 0.5) * 2 * t) / Rad, Math.Atan((0.5 - p.Y) * 2 * t * 9 / 16) / Rad);
    }

    /// <summary>The view that frames a person seen in `from`: their eyes `eyeLine` down from
    /// the top, and their eyes-to-shoulders drop `wantScale` of the frame height. `scale` is
    /// that drop as measured in the picture now.</summary>
    public static View Shot(PointD eye, double scale, View from, double eyeLine, double wantScale, Limits? limits = null)
    {
        var l = limits ?? Limits.Default;
        // Size in the picture goes with 1 / tan(half the angle of view).
        var wanted = 2 * Math.Atan(Math.Tan(from.Fov / 2 * Rad) * scale / wantScale) / Rad;
        var fov = l.Fov.Clamp(wanted);
        var (right, up) = Angles(eye, from.Fov);
        // In the new shot the eyes sit above the middle by this much.
        var lift = Math.Atan((0.5 - eyeLine) * 2 * Math.Tan(fov / 2 * Rad) * 9 / 16) / Rad;
        // A camera tilted up or down turns a little farther to cover the same sideways distance.
        var pan = from.Pan + right / Math.Max(0.5, Math.Cos(from.Tilt * Rad));
        var tilt = from.Tilt + up - lift;
        return new View(l.Pan.Clamp(pan), l.Tilt.Clamp(tilt), fov);
    }
}

/// <summary>What a Canon camera says about itself in one read: where it is and how far it can go.</summary>
public sealed record CanonState(PTZPosition Position, ShotMath.Limits Limits)
{
    public ShotMath.View View => new(Position.Pan / 100.0, Position.Tilt / 100.0, Position.Zoom / 100.0);

    /// <summary>From the camera's info page ("key:=value" lines; "key==value" while a value is changing).</summary>
    public static CanonState? Parse(string text)
    {
        var kv = new Dictionary<string, double>();
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            var i = l.IndexOf(":=", StringComparison.Ordinal); if (i < 0) i = l.IndexOf("==", StringComparison.Ordinal);
            if (i < 0) continue;
            if (double.TryParse(l[(i + 2)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) kv[l[..i]] = v;
        }
        if (!kv.TryGetValue("c.1.pan", out var p) || !kv.TryGetValue("c.1.tilt", out var t) || !kv.TryGetValue("c.1.zoom", out var z)) return null;
        ShotMath.Range R(string k, ShotMath.Range fallback) =>
            kv.TryGetValue($"c.1.{k}.limit.min", out var lo) && kv.TryGetValue($"c.1.{k}.limit.max", out var hi) && lo < hi ? new(lo / 100, hi / 100) : fallback;
        var d = ShotMath.Limits.Default;
        return new CanonState(new PTZPosition((int)p, (int)t, (int)z), new ShotMath.Limits(R("zoom", d.Fov), R("pan", d.Pan), R("tilt", d.Tilt)));
    }

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private static string Base(PTZCamera c) => $"http://{c.Host}{(c.Port == 80 ? "" : ":" + c.Port)}/-wvhttp-01-/";

    public static async Task<CanonState?> Read(PTZCamera c)
    {
        try { return Parse(await Http.GetStringAsync(Base(c) + "info.cgi?item=c.1.pan,c.1.tilt,c.1.zoom,c.1.pan.limit,c.1.tilt.limit,c.1.zoom.limit")); }
        catch { return null; }
    }

    /// <summary>A still picture from the camera (the same one camera_snapshots uses; changes nothing on it).</summary>
    public static async Task<byte[]?> Still(PTZCamera c)
    {
        try { return await Http.GetByteArrayAsync(Base(c) + "image.cgi"); } catch { return null; }
    }
}

/// <summary>Decoding and cropping still pictures: the app's job (it has the imaging code).</summary>
public interface IStillImages
{
    /// <summary>A JPEG as pixels, at most `maxWidth` wide; null if it isn't a picture.</summary>
    TrackFrame? Decode(byte[] jpeg, int maxWidth);
    /// <summary>Part of a picture (0 to 1, origin top left) as a JPEG at most `maxWidth` wide.</summary>
    byte[]? Crop(TrackFrame frame, RectD box, int maxWidth);
}

public sealed class StageScan
{
    public static StageScan Shared { get; } = new();

    /// <summary>One person as one camera saw them.</summary>
    public sealed class Person
    {
        /// <summary>1, 2, 3 and so on across the scan.</summary>
        public int Id { get; init; }
        public Guid Camera { get; init; }
        public string CameraName { get; init; } = "";
        /// <summary>A small picture of them (JPEG), for labelling.</summary>
        public byte[]? Thumb { get; init; }
        /// <summary>The shot that frames them from this camera.</summary>
        public PTZPosition Shot { get; init; }
        /// <summary>Only their outline was found (too small or turned away for body points), so the shot's size is an estimate.</summary>
        public bool Rough { get; init; }
        public Appearance? Look { get; init; }
        /// <summary>People with the same group are the app's guess at the same person seen by
        /// different cameras (by their look). A suggestion: labels decide.</summary>
        public int Group { get; set; }
        public string Label { get; set; } = "";
        public string Letter => ((char)('A' + Group % 26)).ToString();
    }

    public List<Person> People { get; private set; } = new();
    public bool Running { get; private set; }
    public string Progress { get; private set; } = "";
    /// <summary>Cameras left out and why, and anything else worth knowing about the last scan.</summary>
    public List<string> Notes { get; private set; } = new();
    public DateTime? ScannedAt { get; private set; }
    /// <summary>Changes each scan, so pages know to rebuild.</summary>
    public int Version { get; private set; }
    /// <summary>The shot size the scan frames people as.</summary>
    public ShotSize Size { get; set; } = ShotSize.Waist;

    /// <summary>Raised on the main thread whenever anything above changes.</summary>
    public event Action? Changed;
    /// <summary>Set by the app.</summary>
    public IStillImages? Images;

    private static Booth B => Booth.Shared;

    /// <summary>Why a camera can't be scanned right now, or null if it can. Main thread.</summary>
    public string? Blocked(PTZCamera c)
    {
        if (c.Proto != VISCAProtocol.CanonXC || c.Host.Length == 0) return "only Canon cameras report where they point";
        if (c.AtemInput is int i && B.Atem.State.OnLive(i)) return "it's on Live";
        if (AutoTracker.Shared.IsTracking(c.Id)) return "it's auto tracking";
        if (!B.Ptz.HasControl(c)) return "Churchfront Video doesn't have control of it";
        return null;
    }

    /// <summary>Why the scan can't find people on this computer, or null if it can.</summary>
    public string? Unavailable => AutoTracker.Shared.Detector == null ? AutoTracker.Shared.Unavailable ?? "Auto tracking's person finder isn't loaded, so people can't be found." : Images == null ? "This computer can't read the camera pictures." : null;

    private static Task<T> Main<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.Post(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
        return tcs.Task;
    }
    private static Task Main(Action a) => Main(() => { a(); return 0; });

    /// <summary>Scans with the cameras given (all of them by default). Moves each camera that
    /// isn't blocked to a wide shot and back. Call on the main thread; `done` is called there.</summary>
    public void Scan(List<PTZCamera>? cameras = null, Action? done = null)
    {
        if (Running) { done?.Invoke(); return; }
        if (Unavailable is string why)
        {
            People = new(); Notes = new() { $"Stage scan can't find people: {why}" }; ScannedAt = null; Version++;
            Changed?.Invoke(); done?.Invoke();
            return;
        }
        Running = true; People = new(); Notes = new(); Version++;
        Changed?.Invoke();
        var cams = (cameras ?? B.Ptz.Cameras).Select(c => c.Clone()).ToList();
        var detector = AutoTracker.Shared.Detector!;
        var images = Images!;
        var size = Size;
        var cfg = B.Store.Config;
        Task.Run(async () =>
        {
            var found = new List<Person>();
            void Note(string n) => MainThread.Post(() => { Notes.Add(n); Changed?.Invoke(); });
            void Say(string p) => MainThread.Post(() => { Progress = p; Changed?.Invoke(); });
            try
            {
                foreach (var c in cams)
                {
                    if (await Main(() => Blocked(c)) is string why) { Note($"{c.Name} was left out: {why}."); continue; }
                    Say($"{c.Name}: going wide");
                    if (await CanonState.Read(c) is not CanonState start) { Note($"{c.Name} was left out: it didn't answer."); continue; }

                    var named = c.Shots.FirstOrDefault(s => s.Kind == ShotKind.Position && (s.Name.Equals("stage", StringComparison.OrdinalIgnoreCase) || s.Name.Equals("wide", StringComparison.OrdinalIgnoreCase)));
                    var wide = named?.Position ?? new PTZPosition(start.Position.Pan, start.Position.Tilt, (int)(start.Limits.Fov.Hi * 100));
                    await Main(() => B.Ptz.Go(new PTZShot("", wide), c.Id));
                    var there = await Settle(c);
                    // Let the preview catch up and focus settle before taking the picture.
                    await Task.Delay(900);

                    Say($"{c.Name}: looking for people");
                    var seen = new List<TrackedPerson>();
                    TrackFrame? picture = null;
                    if (await Main(() => Blocked(c)) == null && await CanonState.Still(c) is byte[] jpg && images.Decode(jpg, 1280) is TrackFrame f)
                    {
                        picture = f;
                        seen = TrackedPerson.From(detector.Detect(f), f);
                    }
                    else Note($"{c.Name}: no picture came in from it, so no one could be found.");
                    var view = await CanonState.Read(c) ?? there ?? start;

                    foreach (var p in seen.OrderBy(p => p.Box.MidX))
                    {
                        // Without body points, take the outline as head to waist (about 4.5 times eyes to shoulders).
                        var scale = p.Scale ?? p.Box.H / 4.5;
                        var v = ShotMath.Shot(p.Eye, Math.Max(0.004, scale), view.View, size.EyeLine(cfg), size.Scale(cfg), view.Limits);
                        found.Add(new Person
                        {
                            Id = found.Count + 1, Camera = c.Id, CameraName = c.Name,
                            Thumb = picture != null ? Thumb(images, picture, p.Box) : null,
                            Shot = new PTZPosition((int)Math.Round(v.Pan * 100), (int)Math.Round(v.Tilt * 100), (int)Math.Round(v.Fov * 100)),
                            Rough = p.Scale == null, Look = p.Appearance, Group = -1,
                        });
                    }
                    if (picture != null && seen.Count == 0) Note($"{c.Name} saw no one in its wide shot.");

                    // Back to where it was, unless it went Live in the meantime: nothing moves on Live.
                    if (await Main(() => Blocked(c)) == null) await Main(() => B.Ptz.Go(new PTZShot("", start.Position), c.Id));
                    else Note($"{c.Name} was left on its wide shot, because someone started using it during the scan.");
                }
            }
            catch (Exception e) { Note("The scan stopped: " + e.Message); }
            var grouped = Grouped(found);
            await Main(() =>
            {
                People = grouped; ScannedAt = DateTime.Now; Running = false; Progress = ""; Version++;
                Changed?.Invoke();
                done?.Invoke();
            });
        });
    }

    /// <summary>Waits for a camera to finish moving: two reads in a row at the same place.</summary>
    private static async Task<CanonState?> Settle(PTZCamera c)
    {
        CanonState? last = null;
        for (int n = 0; n < 40; n++)
        {
            await Task.Delay(300);
            if (await CanonState.Read(c) is not CanonState now) continue;
            if (n >= 2 && last != null && last.Position == now.Position) return now;
            last = now;
        }
        return last;
    }

    /// <summary>A small picture of one person, with a little room around them.</summary>
    private static byte[]? Thumb(IStillImages images, TrackFrame f, RectD box)
    {
        var x0 = Math.Max(0, box.X - box.W * 0.25); var y0 = Math.Max(0, box.Y - box.H * 0.15);
        var x1 = Math.Min(1, box.MaxX + box.W * 0.25); var y1 = Math.Min(1, box.MaxY + box.H * 0.15);
        if ((x1 - x0) * f.Width < 8 || (y1 - y0) * f.Height < 8) return null;
        return images.Crop(f, new RectD(x0, y0, x1 - x0, y1 - y0), 240);
    }

    /// <summary>Suggests who is the same person across cameras: each later camera's people are
    /// matched to the people already found by how alike they look, best fits first. Anyone who
    /// looks like no one so far is a new person.</summary>
    public static List<Person> Grouped(List<Person> found)
    {
        var groups = new List<List<Appearance>>();
        foreach (var cam in found.Select(p => p.Camera).Distinct().ToList())
        {
            var mine = found.Where(p => p.Camera == cam).ToList();
            var pairs = new List<(Person P, int G, double D)>();
            foreach (var p in mine)
            {
                if (p.Look == null) continue;
                for (int g = 0; g < groups.Count; g++)
                    if (p.Look.Distance(groups[g]) is double d && d < 0.7) pairs.Add((p, g, d));
            }
            var taken = new HashSet<int>();
            foreach (var (p, g, _) in pairs.OrderBy(x => x.D))
                if (p.Group < 0 && taken.Add(g)) p.Group = g;
            foreach (var p in mine)
            {
                if (p.Group < 0) { p.Group = groups.Count; groups.Add(new()); }
                if (p.Look != null) groups[p.Group].Add(p.Look);
            }
        }
        return found;
    }

    /// <summary>Labels one person. The same label goes to the others the app thinks are the same
    /// person, when they had the same label (or none) before, so one name usually covers all cameras.</summary>
    public void Label(int id, string text)
    {
        if (People.FirstOrDefault(p => p.Id == id) is not Person me) return;
        var before = me.Label;
        foreach (var p in People) if (p.Group == me.Group && p.Label == before) p.Label = text;
        me.Label = text;
        Changed?.Invoke();
    }

    /// <summary>Saves a shot for every labelled person on the camera that saw them, and remembers
    /// what each one looks like for following by name. Shots from an earlier scan are replaced;
    /// a shot you made yourself with the same name is kept. Returns what happened, in plain words.</summary>
    public List<string> Save()
    {
        var said = new List<string>();
        var cast = new Dictionary<string, List<Appearance>>();
        var saved = 0;
        B.Ptz.Edit(list =>
        {
            foreach (var cam in People.Select(p => p.Camera).Distinct())
            {
                if (list.FirstOrDefault(c => c.Id == cam) is not PTZCamera c) continue;
                var shots = c.Shots.Where(s => !s.Auto).ToList();
                var names = new HashSet<string>();
                foreach (var p in People.Where(p => p.Camera == cam))
                {
                    var name = p.Label.Trim();
                    if (name.Length == 0) continue;
                    if (p.Look != null) { if (!cast.TryGetValue(name, out var l)) cast[name] = l = new(); l.Add(p.Look); }
                    if (!names.Add(name.ToLowerInvariant())) { said.Add($"{p.CameraName} saw two people labelled {name}; only the first got a shot."); continue; }
                    if (shots.Any(s => s.Kind == ShotKind.Position && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        said.Add($"{p.CameraName} already has your own shot called {name}, so it was kept.");
                        continue;
                    }
                    shots.Add(new PTZShot(name, p.Shot) { Auto = true });
                    saved++;
                }
                c.Shots = shots;
            }
        });
        StageCast.Shared.Set(cast);
        said.Insert(0, $"Saved {saved} shot{(saved == 1 ? "" : "s")} for {cast.Count} {(cast.Count == 1 ? "person" : "people")}.");
        Changed?.Invoke();
        return said;
    }

    /// <summary>For Claude's tools: everyone in the last scan, and who can be followed by name.</summary>
    public JsonObject Report()
    {
        var people = new JsonArray();
        foreach (var p in People)
            people.Add(new JsonObject
            {
                ["person"] = p.Id, ["camera"] = p.CameraName, ["same_person_letter"] = p.Letter, ["label"] = p.Label,
                ["shot"] = new JsonObject { ["pan"] = p.Shot.Pan, ["tilt"] = p.Shot.Tilt, ["zoom"] = p.Shot.Zoom }, ["size_is_a_guess"] = p.Rough,
            });
        var notes = new JsonArray(); foreach (var n in Notes) notes.Add(n);
        var names = new JsonArray(); foreach (var n in StageCast.Shared.Names) names.Add(n);
        return new JsonObject { ["people"] = people, ["notes"] = notes, ["can_follow_by_name"] = names };
    }
}

/// <summary>The people labelled in the last stage scan, by name, with what each looks like from
/// the cameras that saw them. Kept for the day in memory: it's gone when the app quits.</summary>
public sealed class StageCast
{
    public static StageCast Shared { get; } = new();
    public Dictionary<string, List<Appearance>> People { get; private set; } = new();
    public DateTime? ScannedAt { get; private set; }

    public List<string> Names => People.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    public void Set(Dictionary<string, List<Appearance>> cast)
    {
        People = cast;
        ScannedAt = cast.Count == 0 ? null : DateTime.Now;
    }

    /// <summary>A person by name, however it's capitalised.</summary>
    public (string Name, List<Appearance> Looks)? Look(string name)
    {
        var q = name.Trim();
        var hit = People.FirstOrDefault(kv => kv.Key.Equals(q, StringComparison.OrdinalIgnoreCase));
        return hit.Key == null || hit.Value.Count == 0 ? null : (hit.Key, hit.Value);
    }
}
