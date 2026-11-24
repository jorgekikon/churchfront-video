using System.Collections.Concurrent;
using System.Globalization;
using CueATEM;

namespace CueVideo;

// A port of Sources/CueVideo/Tracker.swift. The framing rules, how the camera is steered and
// how it keeps hold of one person are the Mac's, line for line. What differs is only how people
// are found in a picture: the Mac asks Apple's Vision; here the app hands in an IPersonDetector
// (the RTMO pose model through ONNX Runtime), and everything after its body points is shared.

/// <summary>A point or box in a picture, 0 to 1 across and down, origin top left.</summary>
public readonly record struct PointD(double X, double Y);

public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double MinX => X; public double MaxX => X + W; public double MinY => Y; public double MaxY => Y + H;
    public double MidX => X + W / 2; public double MidY => Y + H / 2;
    public PointD Center => new(MidX, MidY);
    public bool Contains(PointD p) => p.X >= X && p.X <= MaxX && p.Y >= Y && p.Y <= MaxY;
    public bool Intersects(RectD o) => X < o.MaxX && o.X < MaxX && Y < o.MaxY && o.Y < MaxY;
}

/// <summary>One picture for tracking: packed 24-bit BGR rows, Width × 3 bytes each.</summary>
public sealed class TrackFrame
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Bgr { get; init; } = Array.Empty<byte>();
}

/// <summary>A body a detector found, in picture coordinates (0 to 1, origin top left).
/// Points are the 17 COCO body points in COCO order, each with its confidence (0 to 1);
/// null when the detector only gives an outline.</summary>
public sealed class PoseBody
{
    public RectD Box;
    public double Score;
    public (double X, double Y, double C)[]? Points;
}

/// <summary>Finds people in a picture. Called off the main thread, one frame at a time.</summary>
public interface IPersonDetector
{
    List<PoseBody> Detect(TrackFrame frame);
}

/// <summary>Where tracking gets each camera's picture. Set by the app.</summary>
public interface ITrackPictures
{
    /// <summary>Whether this camera has a picture to track from.</summary>
    bool Has(PTZCamera camera);
    /// <summary>Starts sending this camera's frames (on any thread). Returns what stops them.</summary>
    Action Watch(PTZCamera camera, Action<TrackFrame> frame);
}

public static class ShotSizeTracking
{
    public static double EyeLine(this ShotSize z, CueConfig c) => z.Rule(c).EyeLine ?? 0.3;
    public static double Scale(this ShotSize z, CueConfig c) => z.Rule(c).Scale ?? 0.2;
    public static double LeadRoom(this ShotSize z, CueConfig c) => z.Rule(c).LeadRoom ?? 0.1;
    public static double DeadX(this ShotSize z, CueConfig c) => z.Rule(c).DeadX ?? 0.07;
    public static double DeadY(this ShotSize z, CueConfig c) => z.Rule(c).DeadY ?? 0.08;
    public static string Short(this ShotSize z) => z switch { ShotSize.Shoulders => "Close", ShotSize.Waist => "Waist", _ => "Full" };

    /// <summary>The size a framing is closest to, from its head scale and what's in shot.</summary>
    public static ShotSize? Classify(double? scale, bool anklesInShot, bool hipsInShot)
    {
        if (scale is double s)
        {
            if (s < 0.145) return ShotSize.Full;      // between the full and waist standards
            if (s < 0.275) return anklesInShot ? ShotSize.Full : ShotSize.Waist;
            return hipsInShot ? ShotSize.Waist : ShotSize.Shoulders;
        }
        if (anklesInShot) return ShotSize.Full;
        return hipsInShot ? ShotSize.Waist : null;
    }
}

/// <summary>One person found in a frame. Coordinates are normalized with the origin top left.</summary>
public sealed class TrackedPerson
{
    /// <summary>Everything of them in view, arms and legs included (for the outline and their look).</summary>
    public RectD Box;
    /// <summary>Head and torso only: where they are, however their arms move.</summary>
    public RectD Core;
    /// <summary>Between the eyes (or the nose, or the ears).</summary>
    public PointD Eye;
    /// <summary>Eyes to shoulders, as a share of frame height. Null when the body points aren't clear.</summary>
    public double? Scale;
    public bool HipsInShot, AnklesInShot;
    /// <summary>-1 facing left in the picture, 1 facing right, 0 toward the camera or unclear.</summary>
    public int Facing;
    /// <summary>Shoulder to shoulder against the eyes-to-shoulders drop, both in frame heights.
    /// Narrower than usual for them means the whole body has turned, not just the head.</summary>
    public double? ShoulderSpan;
    /// <summary>What they look like, for telling people apart. Null when they're too small to tell.</summary>
    public Appearance? Appearance;
    /// <summary>Found by outline alone, with no body points: the eye point is a guess from the outline.</summary>
    public bool Outline;

    public ShotSize? Size => ShotSizeTracking.Classify(Scale, AnklesInShot, HipsInShot);

    // COCO body points.
    private const int Nose = 0, LEye = 1, REye = 2, LEar = 3, REar = 4, LShoulder = 5, RShoulder = 6,
        LHip = 11, RHip = 12, LAnkle = 15, RAnkle = 16;

    /// <summary>People in a frame from what the detector found: the same measures the Mac
    /// takes from Vision's body points.</summary>
    public static List<TrackedPerson> From(List<PoseBody> bodies, TrackFrame? frame)
    {
        var found = new List<TrackedPerson>();
        var outlines = new List<RectD>();
        foreach (var b in bodies)
        {
            if (b.Points is not { Length: >= 17 } pts) { if (b.Score > 0.3) outlines.Add(b.Box); continue; }
            PointD? At(int j, double min = 0.3) => pts[j].C >= min ? new PointD(pts[j].X, pts[j].Y) : null;
            static PointD? Mid(PointD? a, PointD? c) => a is PointD x && c is PointD y ? new PointD((x.X + y.X) / 2, (x.Y + y.Y) / 2) : a ?? c;

            var eyes = Mid(At(LEye), At(REye));
            var ears = Mid(At(LEar), At(REar));
            if ((eyes ?? At(Nose) ?? ears) is not PointD eye) { if (b.Score > 0.3) outlines.Add(b.Box); continue; }
            var ls = At(LShoulder); var rs = At(RShoulder);
            var shoulders = Mid(ls, rs);
            var all = pts.Where(p => p.C >= 0.3).ToList();
            if (all.Count == 0) continue;
            double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), maxY = all.Max(p => p.Y);
            // Shot size in one unit (the eyes-to-shoulders drop), measured from the longest span in
            // view, since a small span is only a few pixels on a wide shot: eyes to ankles (about
            // 7.8 times eyes to shoulders on a standing adult), eyes to hips (about 3.7 times; both
            // measured on the Mac with Vision's joints, to be checked against this model's), else
            // eyes to shoulders. Taking the larger of the long spans keeps someone sitting down
            // (whose eyes-to-ankles is shorter) from looking far away.
            var hip = Mid(At(LHip), At(RHip));
            var ankle = Mid(At(LAnkle), At(RAnkle));
            var lng = new List<double>();
            if (ankle is PointD a && a.Y - eye.Y > 0.05) lng.Add((a.Y - eye.Y) / 7.8);
            if (hip is PointD h && h.Y - eye.Y > 0.03) lng.Add((h.Y - eye.Y) / 3.7);
            double? shrt = shoulders is PointD sh && sh.Y - eye.Y > 0.01 ? sh.Y - eye.Y : null;
            double? scale = lng.Count > 0 ? lng.Max() : shrt;
            // Top of the head is about half the eyes-to-shoulders drop above the eyes.
            var top = eye.Y - (scale ?? 0.05) * 0.6;
            // Head and torso, without the arms or legs: a raised or reaching arm widens the whole
            // body's box but leaves this where it is, so a gesture never reads as moving.
            var trunk = new[] { eye, shoulders, ls, rs, hip, At(LHip), At(RHip), At(Nose), At(LEar), At(REar) }
                .Where(q => q != null).Select(q => q!.Value).ToList();
            double tMinX = trunk.Min(q => q.X), tMaxX = trunk.Max(q => q.X), tMaxY = trunk.Max(q => q.Y);
            // With only the head in view, allow for the shoulders (about the eyes-to-shoulders drop each side).
            var pad = shoulders == null ? (scale ?? 0.05) : 0;
            var p = new TrackedPerson
            {
                Box = new RectD(minX, top, Math.Max(0.02, maxX - minX), Math.Max(0.02, maxY - top)),
                Core = new RectD(tMinX - pad, top, Math.Max(0.02, tMaxX - tMinX + pad * 2), Math.Max(0.02, tMaxY - top)),
                Eye = eye, Scale = scale,
                HipsInShot = At(LHip) != null || At(RHip) != null,
                AnklesInShot = At(LAnkle) != null || At(RAnkle) != null,
            };
            // Facing: the nose sits off the line between the shoulders on the side they face.
            if (At(Nose) is PointD nose && ls is PointD l && rs is PointD r)
            {
                var w = Math.Abs(l.X - r.X);
                var off = (nose.X - (l.X + r.X) / 2) / Math.Max(w, 0.01);
                if (w > 0.02 && Math.Abs(off) > 0.3) p.Facing = off > 0 ? 1 : -1;
            }
            else if (At(LEar) is PointD le && At(REar) == null && At(Nose) is PointD n1) p.Facing = n1.X > le.X ? 1 : -1;   // profile: only one ear shows
            else if (At(REar) is PointD re && At(LEar) == null && At(Nose) is PointD n2) p.Facing = n2.X > re.X ? 1 : -1;
            // Frame x is in widths; a 16:9 width is 16/9 heights.
            if (ls is PointD sl && rs is PointD sr && scale is double sc && sc > 0.005) p.ShoulderSpan = Math.Abs(sl.X - sr.X) * 16 / 9 / sc;
            found.Add(p);
        }
        // People too small or turned away for body points still show as an outline: follow them
        // by it (no shot size or zoom from these).
        foreach (var box in outlines)
        {
            if (found.Any(f => f.Box.Intersects(box) && box.Contains(f.Eye))) continue;
            found.Add(new TrackedPerson { Box = box, Core = box, Eye = new PointD(box.MidX, box.MinY + box.H * 0.12), Outline = true });
        }
        // What each person looks like (the biggest few in the shot), to tell them apart.
        if (frame != null)
            foreach (var f in found.OrderByDescending(f => f.Box.W * f.Box.H).Take(6))
                f.Appearance = Appearance.Of(frame, f.Box);
        return found;
    }

    public TrackedPerson Copy() => (TrackedPerson)MemberwiseClone();
}

/// <summary>
/// Auto tracking: a camera follows one person around the stage.
///
/// It watches the same preview picture the volunteer sees, finds people and their body points
/// in each frame (the RTMO pose model on Windows), keeps hold of the one it was told to follow,
/// works out whether the shot is shoulders up, waist up or full length, and pans, tilts and
/// (gently) zooms to frame them the way a broadcast operator would. Any manual move of that
/// camera (joystick, shot, preset, home) ends tracking at once. Zooming by hand keeps tracking
/// going and makes that zoom the size to hold. Everything here runs on the main thread except
/// finding the people.
/// </summary>
public sealed class AutoTracker
{
    public static AutoTracker Shared { get; } = new();

    public enum Phase { Starting, Following, Searching, Lost }

    public sealed class Session
    {
        public Guid Camera;
        public Phase Phase = Phase.Starting;
        public TrackedPerson? Target;
        /// <summary>The shot it's framing to, and the head scale to hold (the standard for the
        /// size, or whatever the volunteer zoomed to).</summary>
        public ShotSize Size = ShotSize.Waist;
        public double? HoldScale;
        /// <summary>What was asked for; null means work it out from the shot when tracking starts.</summary>
        public ShotSize? Asked;
        /// <summary>What the picture is right now.</summary>
        public ShotSize? Seen;
        public PointD? Pick;
        public List<TrackedPerson> People = new();
        public PointD? Eye;            // smoothed
        public double? Scale;          // smoothed
        public double Lead;            // smoothed lead-room offset
        public DateTime LastSeen = DateTime.UtcNow, Started = DateTime.UtcNow, LastFrame = DateTime.UtcNow;
        public DateTime? ManualZoomAt;
        public bool Panning, Tilting, Zooming;
        /// <summary>Pan and tilt last sent, in the camera's units (hundredths of a degree a second).</summary>
        public double SentPan, SentTilt, SentZoom;

        // Motion, measured in real angles from the camera's reported angle of view.
        public double Fov = 40;                        // horizontal angle of view now, degrees
        public (double Pan, double Tilt, DateTime At)? LastPos;
        public double GainPan = 1, GainTilt = 1;       // how fast it really turns ÷ how fast it was asked
        public DateTime SentAt = DateTime.UtcNow;
        public List<(DateTime At, double Pan, double Tilt)> SentLog = new();
        public (PointD P, DateTime At)? LastEye;
        public double ImgVX, ImgVY;                    // the person's motion in the picture, frames a second
        public double MoveX, MoveY;                    // their real motion (right and up), frames a second
        public bool Polling;
        public List<(DateTime At, double Pan, double Tilt)> Polls = new();
        public DateTime? ZoomWrongSince;
        public DateTime ZoomEnded = DateTime.MinValue;
        public int ZoomDir;
        public DateTime HoldLearnUntil = DateTime.MinValue;
        /// <summary>Walking: how sure we are they're walking each way (-1 to 1; the sign is the
        /// direction), and their steady walking speed in frames a second, held through a moment's dip.</summary>
        public double WalkW, WalkSpeed, WalkWY, WalkSpeedY;
        /// <summary>Their shoulder span squared up to the camera (the widest lately), and which way
        /// their whole body has been turned (-1 left, 1 right, 0 square on) and since when.</summary>
        public double? SquareSpan; public int Turn; public DateTime TurnSince = DateTime.UtcNow;

        // Who it's following: a few pictures of what they look like (the first `Anchors` kept
        // for good, the rest the most recent), so the camera stays with them when people cross
        // and waits for them, not the nearest person, after losing sight.
        public string? Person;
        public List<Appearance> Gallery = new();
        public int Anchors = 1;
        public DateTime GalleryAt = DateTime.MinValue;
        public double? OwnLook, OtherLook;
        public DateTime? AloneSince;
        /// <summary>Since when their body points have been hidden and only their outline shows.</summary>
        public DateTime? HeadAt;
    }

    private readonly Dictionary<Guid, Session> _sessions = new();
    public IReadOnlyDictionary<Guid, Session> Sessions => _sessions;

    /// <summary>Raised on the main thread whenever a session starts, stops or changes phase.</summary>
    public event Action? Changed;

    /// <summary>Set by the app.</summary>
    public IPersonDetector? Detector;
    public ITrackPictures? Pictures;
    /// <summary>Why tracking can't run on this computer (no model file, say), if it can't.</summary>
    public string? Unavailable;

    private readonly Dictionary<Guid, Action> _feeds = new();
    private readonly Dictionary<Guid, List<Action<string?>>> _waiters = new();
    private readonly ConcurrentDictionary<Guid, byte> _active = new(), _busy = new();
    private Timer? _timer;

    private static PTZController Ptz => Booth.Shared.Ptz;
    private static CueConfig Config => Booth.Shared.Store.Config;

    private AutoTracker() { }

    /// <summary>Hooks the tracker to the camera controls. Called once at launch, on main.</summary>
    public void Attach()
    {
        var move = Ptz.OnManualMove; var zoom = Ptz.OnManualZoom;
        Ptz.OnManualMove = id => { move?.Invoke(id); Stop(id); };
        Ptz.OnManualZoom = id => { zoom?.Invoke(id); ManualZoom(id); };
    }

    public bool IsTracking(Guid id) => _sessions.ContainsKey(id);

    /// <summary>Whether this camera can track: there's a detector and a picture for it.</summary>
    public bool CanTrack(PTZCamera c) => Detector != null && c.Proto.HasPanTilt() && Pictures?.Has(c) == true;

    /// <summary>
    /// Start following someone on a camera. `point` (0 to 1, top left) picks the person nearest
    /// it; without one, the person nearest the middle. `size` frames them as that shot; without
    /// one, as whichever shot the camera is closest to now. `done` gets null once someone is
    /// found, or the reason no one was.
    /// </summary>
    public void Start(PTZCamera camera, PointD? point = null, ShotSize? size = null, string? person = null, Action<string?>? done = null)
    {
        Stop(camera.Id, quiet: true);
        if (Detector == null) { done?.Invoke(Unavailable ?? "Auto tracking isn't set up on this computer."); return; }
        if (!camera.Proto.HasPanTilt()) { done?.Invoke($"{camera.Name} can't pan or tilt, so it can't follow anyone."); return; }
        if (Pictures?.Has(camera) != true) { done?.Invoke("Auto tracking needs the camera's picture: a Canon camera, or an NDI picture set for its switcher input in Setup."); return; }
        var s = new Session { Camera = camera.Id, Pick = point, Asked = size };
        if (person != null)
        {
            if (StageCast.Shared.Look(person) is not { } hit)
            {
                var names = StageCast.Shared.Names;
                done?.Invoke(names.Count == 0 ? "No one has been labelled yet. Run a stage scan first."
                                              : $"No one called {person} in the last stage scan. It has: {string.Join(", ", names)}.");
                return;
            }
            s.Person = hit.Name;
            s.Gallery = hit.Looks.Take(4).ToList();
            s.Anchors = s.Gallery.Count;
        }
        if (size is ShotSize a) s.Size = a;
        _sessions[camera.Id] = s;
        _active[camera.Id] = 0;
        if (done != null) { if (!_waiters.TryGetValue(camera.Id, out var w)) _waiters[camera.Id] = w = new(); w.Add(done); }
        var id = camera.Id;
        _feeds[id] = Pictures.Watch(camera, f => Frame(f, id));
        _timer ??= new Timer(_ => MainThread.Post(Tick), null, 250, 250);
        Changed?.Invoke();
    }

    /// <summary>Reframe a tracking camera as a different shot size.</summary>
    public void SetSize(Guid id, ShotSize size)
    {
        if (!_sessions.TryGetValue(id, out var s)) return;
        s.Asked = size; s.Size = size;
        s.HoldScale = size.Scale(Config);
        s.ManualZoomAt = null;
        // A size picked on purpose reframes now, without the usual wait.
        s.ZoomWrongSince = DateTime.MinValue;
        s.ZoomEnded = DateTime.MinValue;
        s.ZoomDir = 0;
        Changed?.Invoke();
    }

    public void Stop(Guid id) => Stop(id, quiet: false);

    private void Stop(Guid id, bool quiet)
    {
        if (!_sessions.Remove(id, out var s)) return;
        _active.TryRemove(id, out _);
        _busy.TryRemove(id, out _);
        if (_feeds.Remove(id, out var stop)) stop();
        if (s.SentPan != 0 || s.SentTilt != 0) Ptz.TrackDrive(0, 0, id);
        if (s.SentZoom != 0) Ptz.TrackZoom(0, id);
        Finish(id, "Tracking was stopped.");
        if (_sessions.Count == 0) { _timer?.Dispose(); _timer = null; }
        if (!quiet) Changed?.Invoke();
    }

    public void StopAll() { foreach (var id in _sessions.Keys.ToList()) Stop(id); }

    /// <summary>The volunteer (or Claude) zoomed: stop holding the old size for a moment, then hold the new one.</summary>
    private void ManualZoom(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return;
        s.ManualZoomAt = DateTime.UtcNow;
        s.Zooming = false;
        s.SentZoom = 0;
    }

    // MARK: Frames

    /// <summary>A frame arrived (any thread). One per camera is looked at at a time; the rest are skipped.</summary>
    private void Frame(TrackFrame f, Guid id)
    {
        if (!_active.ContainsKey(id) || !_busy.TryAdd(id, 0)) return;
        var detector = Detector;
        Task.Run(() =>
        {
            List<TrackedPerson> people;
            try { people = detector == null ? new() : TrackedPerson.From(detector.Detect(f), f); }
            catch (Exception e) { Console.Error.WriteLine("tracking: " + e.Message); people = new(); }
            MainThread.Post(() => { _busy.TryRemove(id, out _); Update(id, people); });
        });
    }

    /// <summary>People found in a camera's frame (main thread). Public so the self-test can feed it.</summary>
    public void Update(Guid id, List<TrackedPerson> people)
    {
        if (!_sessions.TryGetValue(id, out var s)) return;
        var now = DateTime.UtcNow;
        s.People = people;
        s.LastFrame = now;
        var was = s.Phase;

        if (s.Target == null)
        {
            TrackedPerson? pick;
            if (s.Person != null)
            {
                // Someone named in the stage scan: whoever looks like them, wherever they stand.
                pick = IdentityMatch.Find(people.Select(x => x.Appearance?.Distance(s.Gallery)).ToList()) is int k ? people[k] : null;
            }
            else
            {
                // First sight: take the person under the click, or nearest it (the middle by default).
                var p = s.Pick ?? new PointD(0.5, 0.5);
                pick = people.FirstOrDefault(x => x.Box.Contains(p)) ?? people.OrderBy(x => Dist(x.Core.Center, p)).FirstOrDefault();
            }
            if (pick != null)
            {
                s.Target = pick;
                s.Eye = pick.Eye;
                s.Scale = pick.Scale;
                s.Seen = pick.Size;
                // Frame as asked; otherwise keep the shot the volunteer set up: its size class for
                // headroom and lead room, and its exact size to hold (no zoom at the start).
                s.Size = s.Asked ?? pick.Size ?? ShotSize.Waist;
                s.HoldScale = s.Asked != null ? s.Size.Scale(Config) : pick.Scale;
                s.HoldLearnUntil = now.AddSeconds(1.5);
                s.LastSeen = now;
                s.Phase = Phase.Following;
                if (s.Gallery.Count == 0 && pick.Appearance is Appearance look) { s.Gallery = new() { look }; s.Anchors = 1; s.GalleryAt = now; }
                Finish(id, null);
                Changed?.Invoke();
                return;
            }
            if ((now - s.Started).TotalSeconds > 4) Finish(id, NotFound(s));
            return;
        }

        // Keep hold of the same person: by where they were and by what they look like.
        var last = s.Target;
        var gone = (now - s.LastSeen).TotalSeconds;
        var reach = Math.Min(0.5, 0.2 + Math.Max(last.Core.W, last.Core.H) * 0.6 + gone * 0.15);
        var candidates = people.Select(p => new IdentityMatch.Candidate(Dist(p.Core.Center, last.Core.Center),
            s.Gallery.Count == 0 ? null : p.Appearance?.Distance(s.Gallery))).ToList();
        var choice = IdentityMatch.Choose(candidates, reach, gone, new IdentityMatch.Model(s.OwnLook, s.OtherLook));
        if (choice.Take is int i)
        {
            var next = people[i];
            // Arms up or across the body can hide the body points for a moment, leaving only an
            // outline whose middle shifts with the arms. For that moment keep the head where it
            // was last really seen; after it, follow the outline as before.
            if (next.Outline && (!last.Outline || s.HeadAt != null))
            {
                s.HeadAt ??= now;
                if ((now - s.HeadAt.Value).TotalSeconds < 0.8)
                {
                    next = next.Copy();
                    next.Eye = last.Eye; next.Core = last.Core; next.Outline = last.Outline;
                }
            }
            else s.HeadAt = null;
            Learn(s, next, candidates, i, choice.Sure, reach, gone, now);
            s.Target = next;
            s.LastSeen = now;
            s.Phase = Phase.Following;
            var o = s.Eye ?? next.Eye;
            s.Eye = new PointD(o.X + (next.Eye.X - o.X) * 0.5, o.Y + (next.Eye.Y - o.Y) * 0.4);
            if (next.Scale is double n) s.Scale = s.Scale is double sc ? sc + (n - sc) * 0.08 : n;
            if (next.Size is ShotSize z) s.Seen = z;
            Steer(s, next);
        }
        else
        {
            if (gone > 0.6) Hold(s);
            s.Phase = gone > 5 ? Phase.Lost : Phase.Searching;
        }
        if (s.Phase != was) Changed?.Invoke();
    }

    /// <summary>Learns what the followed person looks like, only from frames where it's certain
    /// who they are: their look confirmed it, or they've been the only person around for a
    /// second. Two people crossing never teaches it the wrong one.</summary>
    private static void Learn(Session s, TrackedPerson next, List<IdentityMatch.Candidate> candidates, int i, bool sure, double reach, double gone, DateTime now)
    {
        var alone = candidates.Count(c => c.Position < reach * 1.5) == 1;
        if (!alone) s.AloneSince = null; else s.AloneSince ??= now;
        var settled = gone < 0.7 && s.AloneSince is DateTime since && (now - since).TotalSeconds > 1;
        if (!(sure || settled) || next.Appearance is not Appearance look) return;
        if (candidates[i].Appearance is double d) s.OwnLook = s.OwnLook is double ol ? ol + (d - ol) * 0.2 : d;
        if (sure)
            for (int j = 0; j < candidates.Count; j++)
                if (j != i && candidates[j].Position < reach * 1.5 && candidates[j].Appearance is double dj)
                    s.OtherLook = s.OtherLook is double ot ? ot + (dj - ot) * 0.2 : dj;
        if ((now - s.GalleryAt).TotalSeconds <= 0.7) return;
        s.GalleryAt = now;
        if (s.Gallery.Count >= 6 && s.Gallery.Count > s.Anchors) s.Gallery.RemoveAt(s.Anchors);
        if (s.Gallery.Count < 6) s.Gallery.Add(look);
    }

    private static string NotFound(Session s) => s.Person is string p ? $"Can't see {p} in the picture." : "No one is in the picture.";

    /// <summary>Pan, tilt and zoom toward the framing for this shot size.</summary>
    private void Steer(Session s, TrackedPerson person)
    {
        if (s.Eye is not PointD eye) return;
        var now = DateTime.UtcNow;
        var cfg = Config;

        // After a manual zoom settles, hold whatever size the volunteer chose.
        if (s.ManualZoomAt is DateTime m && (now - m).TotalSeconds > 0.8)
        {
            s.ManualZoomAt = null;
            if (s.Scale is double sc)
            {
                s.HoldScale = sc;
                s.Size = ShotSizeTracking.Classify(sc, person.AnklesInShot, person.HipsInShot) ?? s.Size;
            }
        }
        var size = s.Size;
        if (s.Asked == null && now < s.HoldLearnUntil && s.Scale is double sc2) s.HoldScale = sc2;

        var ey = size.EyeLine(cfg) - eye.Y;     // eyes above the line: tilt up
        var k = cfg.TrackSpeed;
        double fovX = s.Fov, fovY = s.Fov * 9 / 16;
        var tune = cfg.TrackTuning;
        var latency = tune.Latency ?? 0.45;     // the preview picture (and finding people) run about this far behind

        // How fast they're really moving: their motion in the picture plus the camera's own
        // motion at the time that picture was taken. In frames a second, right and up.
        var dtFrame = Math.Min(0.3, Math.Max(0.03, s.LastEye is { } le0 ? (now - le0.At).TotalSeconds : 0.07));
        if (s.LastEye is { } le)
        {
            var dt = (now - le.At).TotalSeconds;
            if (dt > 0.02 && dt < 0.6)
            {
                double vx = (person.Eye.X - le.P.X) / dt, vy = (person.Eye.Y - le.P.Y) / dt;
                s.ImgVX += (vx - s.ImgVX) * 0.35;
                s.ImgVY += (vy - s.ImgVY) * 0.35;
            }
            else { s.ImgVX = 0; s.ImgVY = 0; }
        }
        s.LastEye = (person.Eye, now);
        // How fast the camera was really turning when that picture was taken, from its position
        // reads (never assumed from what it was told), over the last three reads.
        var ta = now.AddSeconds(-latency + 0.05);
        var before = s.Polls.Where(p => p.At <= ta).ToList();
        double camRX = 0, camRY = 0;
        if (before.Count >= 2)
        {
            var a = before[Math.Max(0, before.Count - 3)]; var b = before[^1];
            var dt = (b.At - a.At).TotalSeconds;
            if (dt > 0.05 && dt < 1) { camRX = (b.Pan - a.Pan) / dt / fovX; camRY = (b.Tilt - a.Tilt) / dt / fovY; }
        }
        s.MoveX += ((s.ImgVX + camRX) - s.MoveX) * 0.2;
        s.MoveY += ((camRY - s.ImgVY) - s.MoveY) * 0.2;
        // Which way their whole body is turned: shoulders narrower than usual for them, and the
        // head pointing the same way, held a moment. A head turn alone (looking to one side of
        // the room from a music stand) leaves the shoulders square, so it doesn't count.
        if (person.ShoulderSpan is double span)
            // The widest lately, slowly forgotten (about halving in ten seconds) as zoom and pose drift.
            s.SquareSpan = Math.Max(span, (s.SquareSpan ?? span) * 0.995);
        var turnNow = BodyTurn(person.Facing, person.ShoulderSpan, s.SquareSpan);
        if (turnNow != s.Turn) { s.Turn = turnNow; s.TurnSince = now; }
        var bodyDir = (now - s.TurnSince).TotalSeconds > 0.3 ? s.Turn : 0;

        // Walking, as a steady speed the camera keeps up, not an on-off switch. Clear movement
        // one way builds up the sureness that they're walking (fully sure in half a second, or a
        // quarter the way their body is turned); anything else lets it fade over 0.6 s. The camera
        // is given their walking speed, smoothed and held through a moment's dip, in proportion
        // to that sureness. An on-off switch here sent the camera fast, then slow, then fast
        // again, as their measured speed wobbled either side of the line.
        double Walking(double v, ref double w, ref double speed, double min, int toward)
        {
            var d = v > min ? 1 : (v < -min ? -1 : 0);
            w = WalkWeight(w, d, dtFrame, WalkConfirm(d, toward));
            if (w == 0) { speed = 0; return 0; }
            if (d != 0 && (d > 0) == (w > 0)) speed += (v - speed) * 0.15;
            if ((speed > 0) != (w > 0)) return 0;
            var a = Math.Abs(w);
            return speed * a * a * (3 - 2 * a);   // eased in and out
        }
        var walkMin = tune.WalkThreshold ?? 0.12;
        var walkVX = Walking(s.MoveX, ref s.WalkW, ref s.WalkSpeed, walkMin, bodyDir);
        var walkVY = Walking(s.MoveY, ref s.WalkWY, ref s.WalkSpeedY, walkMin * 1.25, 0);
        bool walkingX = Math.Abs(s.WalkW) > 0.3, walkingY = Math.Abs(s.WalkWY) > 0.3;

        // Lead room: only when they're really walking does the full lead go ahead of them.
        // Never from which way the camera is panning: that fed on itself, the lead pushing the
        // camera on, the move reading as a walk, and the shot swinging past someone standing
        // still. Standing, which way they face (turning to each side of the room) only nudges it.
        // Eased so the frame doesn't jump when they turn.
        s.Lead += (LeadTarget(Math.Abs(s.WalkW) > 0.5 ? walkVX : 0, bodyDir, size.LeadRoom(cfg)) - s.Lead) * 0.06;
        var ex = eye.X - (0.5 + s.Lead);        // right of the mark: pan right

        // Edge alarm: how close they are to the side of the frame they're heading for, 0
        // (comfortable) to 1 (touching it). Their head and torso's edge (never an arm: gestures
        // don't move the camera), or on a tight shot (torso wider than half the frame) their head.
        var box = person.Core;
        var urgency = EdgeUrgency(box, eye.X, ex > 0);

        // Move while they're off the mark or walking; settle once they stand still on it.
        if (Math.Abs(ex) > size.DeadX(cfg) || walkingX || urgency > 0) s.Panning = true;
        else if (Math.Abs(ex) < size.DeadX(cfg) * 0.5 && !walkingX) s.Panning = false;
        var tiltOn = cfg.TrackTilt;
        if (tiltOn && (Math.Abs(ey) > size.DeadY(cfg) || walkingY)) s.Tilting = true;
        else if (!tiltOn || (Math.Abs(ey) < size.DeadY(cfg) * 0.5 && !walkingY)) s.Tilting = false;

        // The camera speed to ask for, in frames a second: their own speed, plus a gentle pull
        // toward the mark (stronger near the edge). Every change of speed, starting, stopping or
        // catching up, is eased (see EaseRate), so the camera glides instead of lurching.
        double Rate(double e, double move, bool on, double prev)
        {
            var kp = Math.Min(2.5, Math.Min(1.6, (tune.Correction ?? 1.2) * k) * (1 + 1.5 * urgency));
            var want = on ? move + kp * e : 0;
            return EaseRate(prev, want, dtFrame, urgency > 0.3, k);
        }
        double camNowX = s.SentPan / 100 * s.GainPan / fovX, camNowY = s.SentTilt / 100 * s.GainTilt / fovY;
        var rx = Rate(ex, walkVX, s.Panning, camNowX);
        var ry = Rate(ey, walkVY, s.Tilting, camNowY);
        // Back into the camera's units: degrees a second (× 100), allowing for how it really turns.
        static double Units(double r, double fov, double g, bool correcting) => CameraSpeed(r * fov * 100 / Math.Max(0.05, g), correcting);
        var pan = Units(rx, fovX, s.GainPan, s.Panning);
        var tilt = Units(ry, fovY, s.GainTilt, s.Tilting);
        var share = person.ShoulderSpan is double sp && s.SquareSpan is double sq ? sp / Math.Max(sq, 0.001) : -1;
        TrackLog.Shared.Write(string.Join(",", new object[] {
            (now - DateTime.UnixEpoch).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture), Ptz.Camera(s.Camera)?.Name ?? "?",
            F(eye.X), F(ex), F(ey), F(s.ImgVX), F(s.MoveX), F(camRX), F(rx), F(s.Fov, "0.00"), F(s.GainPan, "0.00"),
            F(pan, "0"), F(tilt, "0"), F(urgency, "0.00"), s.Panning ? 1 : 0, F(box.MinX), F(box.MaxX),
            bodyDir, F(share, "0.00"), F(s.WalkW, "0.00") }));

        // Hold the shot size as they come nearer or move away, calmly: only when the size has
        // been clearly wrong (over about a third) for 1.5 s, slowly, with a rest of 2 s after
        // each zoom and no reversing within 5 s, so turning, bending or nodding never sets it
        // hunting in and out.
        double zoom = 0;
        if (cfg.TrackZoom && s.ManualZoomAt == null && s.HoldScale is double hold && s.Scale is double scl && person.Scale != null)
        {
            var r = Math.Log(hold / scl);          // positive: they look too small, zoom in
            var zdir = r > 0 ? 1 : -1;
            if (s.Zooming)
            {
                if (Math.Abs(r) < 0.08 || zdir != s.ZoomDir) { s.Zooming = false; s.ZoomEnded = now; s.ZoomWrongSince = null; }
            }
            else if (Math.Abs(r) > (tune.ZoomThreshold ?? 0.3))
            {
                s.ZoomWrongSince ??= now;
                var settled = (now - s.ZoomEnded).TotalSeconds > 2;
                var reversing = zdir != s.ZoomDir && (now - s.ZoomEnded).TotalSeconds < 5;
                if ((now - s.ZoomWrongSince.Value).TotalSeconds > (tune.ZoomWait ?? 1.5) && settled && !reversing) { s.Zooming = true; s.ZoomDir = zdir; }
            }
            else s.ZoomWrongSince = null;
            if (s.Zooming)
            {
                var v = Math.Min(0.45, Math.Max(0.04, Math.Abs(r) * 0.3));  // faster when far off, gentle near the size
                zoom = Math.Round((r > 0 ? v : -v) * 50) / 50;
            }
        }
        else if (s.Zooming) { s.Zooming = false; s.ZoomEnded = now; }
        Send(s, pan, tilt, zoom);
    }

    /// <summary>Where the lead room puts the mark, off the middle (negative: left of it, leaving
    /// room on the right). The full lead ahead of someone walking; a little toward where someone
    /// standing faces.</summary>
    public static double LeadTarget(double walkVX, int facing, double leadRoom)
    {
        if (walkVX != 0) return walkVX > 0 ? -leadRoom : leadRoom;
        return -facing * leadRoom * 0.35;
    }

    /// <summary>Which way someone's whole body is turned: their head's way, when their shoulders
    /// show under 70% of their squared-up width. 0 when square on or unclear.</summary>
    public static int BodyTurn(int facing, double? span, double? square)
    {
        if (facing == 0 || span is not double sp || square is not double sq || sq <= 0) return 0;
        return sp < sq * 0.7 ? facing : 0;
    }

    /// <summary>How sure we are someone is walking, -1 to 1 (the sign is the direction), after
    /// one frame: clear movement one way (`moving`) builds it to full in `rise` seconds; no clear
    /// movement, or movement the other way, lets it fade to nothing over `fall` seconds first.</summary>
    public static double WalkWeight(double w, int moving, double dt, double rise, double fall = 0.6)
    {
        var dir = w > 0 ? 1 : (w < 0 ? -1 : 0);
        if (moving != 0 && (dir == 0 || dir == moving)) return Math.Max(-1, Math.Min(1, w + moving * dt / rise));
        var faded = w - dir * dt / fall;
        return dir > 0 ? Math.Max(0, faded) : Math.Min(0, faded);
    }

    /// <summary>The next camera speed (frames a second) on the way from `prev` to `want`: at most
    /// 1.5 frames a second more each second (3 near the edge of frame, or when slowing), and only
    /// part of the way there each frame, so it eases into the new speed.</summary>
    public static double EaseRate(double prev, double want, double dt, bool urgent, double k)
    {
        var slowing = Math.Abs(want) < Math.Abs(prev) || want * prev < 0;
        var limit = (slowing || urgent ? 3.0 : 1.5) * Math.Max(1, k) * dt;
        // Slows a little more readily than it speeds up, so it doesn't coast past someone stopping.
        var step = Math.Min(limit, Math.Max(Math.Abs(want - prev) * (slowing ? 0.5 : 0.35), 0.005));
        return prev + Math.Max(-step, Math.Min(step, want - prev));
    }

    /// <summary>A speed for the camera, in its units (hundredths of a degree a second; 20 is its
    /// slowest). Settled, anything under 15 is no move, so it doesn't twitch. Correcting (they're
    /// outside the dead zone), anything that isn't nothing is at least the slowest speed: zoomed
    /// in tight, a correction can work out under 15, and rounding it to nothing left the camera
    /// never fixing it (a head held against the top of the frame, the tilt never moving).</summary>
    public static double CameraSpeed(double raw, bool correcting)
    {
        if (Math.Abs(raw) < (correcting ? 1 : 15)) return 0;
        return Math.Round(Math.Min(10000, Math.Max(20, Math.Abs(raw))) / 10, MidpointRounding.AwayFromZero) * 10 * (raw < 0 ? -1 : 1);
    }

    /// <summary>Seconds of steady movement before it counts as a walk: sooner the way they're turned.</summary>
    public static double WalkConfirm(int direction, int bodyTurn) => direction != 0 && direction == bodyTurn ? 0.25 : 0.5;

    /// <summary>How close someone is to the side of the frame they're heading for: 0 comfortable, 1 touching.</summary>
    public static double EdgeUrgency(RectD core, double eyeX, bool right)
    {
        var gap = core.W < 0.5 ? (right ? 1 - core.MaxX : core.MinX) : (right ? 1 - eyeX : eyeX) - 0.1;
        return Math.Min(1, Math.Max(0, (0.2 - gap) / 0.2));
    }

    private static string F(double v, string f = "0.000") => v.ToString(f, CultureInfo.InvariantCulture);

    private static void Hold(Session s)
    {
        s.Panning = false; s.Tilting = false; s.Zooming = false;
        Send(s, 0, 0, 0);
    }

    private static void Send(Session s, double pan, double tilt, double zoom)
    {
        if (pan != s.SentPan || tilt != s.SentTilt)
        {
            s.SentPan = pan; s.SentTilt = tilt;
            s.SentAt = DateTime.UtcNow;
            s.SentLog.Add((DateTime.UtcNow, pan, tilt));
            if (s.SentLog.Count > 40) s.SentLog.RemoveRange(0, s.SentLog.Count - 40);
            Ptz.TrackDriveRaw((int)pan, (int)tilt, s.Camera);
        }
        if (zoom != s.SentZoom)
        {
            s.SentZoom = zoom;
            Ptz.TrackZoom(zoom, s.Camera);
        }
    }

    /// <summary>Stops a camera whose picture has stopped coming in, so it never drifts on old frames.</summary>
    private void Tick()
    {
        var now = DateTime.UtcNow;
        foreach (var id in _sessions.Keys.ToList()) Poll(id);
        foreach (var s in _sessions.Values)
            if ((now - s.LastFrame).TotalSeconds > 0.6 && (s.SentPan != 0 || s.SentTilt != 0 || s.SentZoom != 0))
            {
                Hold(s);
                if (s.Phase != Phase.Searching) { s.Phase = Phase.Searching; Changed?.Invoke(); }
            }
        foreach (var (id, s) in _sessions.ToList())
            if (s.Target == null && (now - s.Started).TotalSeconds > 4)
                Finish(id, s.Person is string n ? $"Can't see {n} in the picture, or the picture isn't coming in." : "No one is in the picture, or the picture isn't coming in.");
    }

    /// <summary>Reads where the camera really is: its angle of view, and how fast it's really
    /// turning compared with the speed it was asked for.</summary>
    private void Poll(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var s) || s.Polling) return;
        s.Polling = true;
        Ptz.ReadPosition(id, pos =>
        {
            if (!_sessions.TryGetValue(id, out var s2) || !ReferenceEquals(s, s2)) return;
            s.Polling = false;
            if (pos is not PTZPosition p) return;
            var now = DateTime.UtcNow;
            if (p.Zoom > 50) s.Fov = p.Zoom / 100.0;
            double pan = p.Pan / 100.0, tilt = p.Tilt / 100.0;
            if (s.LastPos is { } last)
            {
                var dt = (now - last.At).TotalSeconds;
                if (dt > 0.1)
                {
                    // Degrees it was asked to turn over that time (the camera reacts about 0.15 s
                    // late), against how far it really turned. Learn only from clear moves one way.
                    double Asked(Func<double, double, double> axis)
                    {
                        DateTime from = last.At.AddSeconds(-0.15), to = now.AddSeconds(-0.15);
                        double total = 0;
                        var log = s.SentLog;
                        for (int i = 0; i < log.Count; i++)
                        {
                            var start = from > log[i].At ? from : log[i].At;
                            var end = i + 1 < log.Count ? (to < log[i + 1].At ? to : log[i + 1].At) : to;
                            if (end > start) total += axis(log[i].Pan, log[i].Tilt) / 100 * (end - start).TotalSeconds;
                        }
                        return total;
                    }
                    static void LearnGain(ref double g, double moved, double want)
                    {
                        if (Math.Abs(want) <= 0.3 || moved * want <= 0) return;
                        var seen = Math.Abs(moved) / Math.Abs(want);
                        if (double.IsFinite(seen)) g += (Math.Min(1.5, Math.Max(0.05, seen)) - g) * 0.35;
                    }
                    LearnGain(ref s.GainPan, pan - last.Pan, Asked((pa, _) => pa));
                    LearnGain(ref s.GainTilt, tilt - last.Tilt, Asked((_, ti) => ti));
                }
            }
            s.LastPos = (pan, tilt, now);
            s.Polls.Add((now, pan, tilt));
            if (s.Polls.Count > 16) s.Polls.RemoveRange(0, s.Polls.Count - 16);
        });
        // A camera that never answers mustn't block later reads.
        MainThread.After(1.5, () => { if (_sessions.TryGetValue(id, out var s3) && ReferenceEquals(s, s3)) s.Polling = false; });
    }

    private void Finish(Guid id, string? why)
    {
        if (!_waiters.Remove(id, out var w)) return;
        foreach (var f in w) f(why);
    }

    private static double Dist(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static string? Words(Phase? p) => p switch
    {
        Phase.Starting => "Looking for someone",
        Phase.Following => "Following",
        Phase.Searching => "Lost sight, holding",
        Phase.Lost => "Lost them, looking",
        _ => null,
    };

    /// <summary>For get_status: false when not tracking.</summary>
    public System.Text.Json.Nodes.JsonNode Report(Guid id) =>
        _sessions.TryGetValue(id, out var t)
            ? new System.Text.Json.Nodes.JsonObject
            {
                ["state"] = t.Phase.ToString().ToLowerInvariant(), ["framing"] = t.Size.Key(),
                ["picture_is"] = t.Seen?.Key() ?? "unknown", ["person"] = t.Person ?? "",
            }
            : false;
}

/// <summary>A frame-by-frame record of what tracking saw and did, for tuning:
/// %APPDATA%\CueVideo\Logs\tracking.csv (the last few minutes of tracking).</summary>
public sealed class TrackLog
{
    public static TrackLog Shared { get; } = new();
    private readonly string _path = Path.Combine(ConfigStore.Folder, "Logs", "tracking.csv");
    private StreamWriter? _w;
    private int _lines;
    /// <summary>Self-tests (any CUEVIDEO_ variable) leave the log alone.</summary>
    private static readonly bool SelfTest = Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(k => k.StartsWith("CUEVIDEO_", StringComparison.Ordinal));

    public void Write(string line)
    {
        if (SelfTest) return;
        try
        {
            if (_w == null || _lines > 20000)
            {
                _w?.Dispose();
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                _w = new StreamWriter(_path, false) { AutoFlush = true };
                _w.WriteLine("time,camera,eye_x,err_x,err_y,picture_vx,person_vx,camera_vx_then,want_vx,fov_deg,pan_gain,pan_sent,tilt_sent,edge_urgency,panning,box_min_x,box_max_x,body_turn,shoulder_share,walk_sure");
                _lines = 0;
            }
            _w.WriteLine(line);
            _lines++;
        }
        catch { }
    }
}
