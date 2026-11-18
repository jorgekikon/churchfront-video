using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using CueATEM;
using CueVideo;
using CueVideo.App;

// Auto tracking and the stage scan in a closed loop, with nothing plugged in: FakeDevices' Canon
// looks at a virtual stage made from photos and really pans, tilts and zooms when told to, and
// the real pose model does the finding.
//
//   TrackSim <photo with one person> [start pan in degrees, default 6]
//     The camera starts turned off to one side; the tracker should bring the person to the
//     middle and settle there, without hunting.
//   TrackSim scan <photo of person A> <photo of person B>
//     The two photos side by side make a stage with two people. Scans it through Claude's
//     scan_stage, labels them with label_stage_scan, recalls each saved shot and checks it
//     frames that person, then follows the second one by name from the wide shot.

Environment.SetEnvironmentVariable("CUEVIDEO_TRACKSIM", "1");   // read-only: never writes the real config.json
var scanMode = args.Length > 0 && args[0] == "scan";
var photos = (scanMode ? args.Skip(1).Take(2) : args.Take(1)).ToList();
if (photos.Count == 0 || (scanMode && photos.Count < 2) || photos.Any(p => !File.Exists(p)))
{
    Console.WriteLine("TrackSim <photo with a person> [start pan]   or   TrackSim scan <photo A> <photo B>");
    return 2;
}
var startPan = !scanMode && args.Length > 1 && double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 6;

var fake = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "FakeDevices", "bin", "Debug", "net8.0-windows", "FakeDevices.exe"));
if (!File.Exists(fake)) { Console.WriteLine($"Build tools/FakeDevices first ({fake} is missing)."); return 2; }
var pics = string.Join(" ", photos.Select(p => $"--picture \"{Path.GetFullPath(p)}\""));
var proc = Process.Start(new ProcessStartInfo(fake, $"9931 --canon {pics}") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
proc.OutputDataReceived += (_, _) => { };
proc.BeginOutputReadLine();
var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8091/-wvhttp-01-/") };
async Task<CanonState> Fake() => CanonState.Parse(await http.GetStringAsync("info.cgi"))!;

T OnMain<T>(Func<T> f) { var done = new ManualResetEventSlim(); T r = default!; MainThread.Post(() => { try { r = f(); } finally { done.Set(); } }); done.Wait(); return r; }
JsonObject Tool(string name, JsonObject a)
{
    var done = new ManualResetEventSlim(); JsonObject r = new();
    MainThread.Post(() => ControlTools.Call(name, a, x => { r = x; done.Set(); }));
    return done.Wait(TimeSpan.FromSeconds(120)) ? r : new JsonObject { ["error"] = "timed out" };
}

try
{
    Thread.Sleep(1500);
    var booth = Booth.Shared;
    var cam = new PTZCamera { Name = "Canon", Host = "127.0.0.1", Port = 8091, Proto = VISCAProtocol.CanonXC, AtemInput = 2 };
    OnMain(() => { booth.Start(); booth.Ptz.Cameras = new() { cam }; return 0; });
    for (int i = 0; i < 50 && !OnMain(() => booth.Ptz.Controlled.Contains(cam.Id)); i++) Thread.Sleep(100);

    var d = PoseDetector.Load(out var why);
    if (d == null) { Console.WriteLine(why); return 2; }
    Console.WriteLine($"Model on the {d.Device}");
    var tracker = AutoTracker.Shared;
    var stills = new StillImages();
    OnMain(() => { tracker.Detector = d; tracker.Pictures = new TrackPictures(); tracker.Attach(); StageScan.Shared.Images = stills; return 0; });

    // Who the camera sees now, from its own still picture.
    async Task<List<TrackedPerson>> Look()
    {
        var jpg = await http.GetByteArrayAsync("image.cgi");
        var f = stills.Decode(jpg, 1280)!;
        return TrackedPerson.From(d.Detect(f), f);
    }
    async Task Settle() { CanonState? a = null; for (int i = 0; i < 40; i++) { await Task.Delay(250); var b = await Fake(); if (a != null && a.Position == b.Position) break; a = b; } await Task.Delay(500); }

    if (!scanMode) return await Track();

    // MARK: Stage scan
    var failures = 0;
    void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

    await http.GetStringAsync("control.cgi?pan=0&tilt=0&zoom=4000");
    await Settle();
    var r = Tool("scan_stage", new JsonObject { ["shot"] = "waist" });
    var people = r["people"]?.AsArray() ?? new JsonArray();
    Console.WriteLine($"scan_stage: {people.Count} people, {(r["images"] as JsonArray)?.Count ?? 0} pictures; notes: {r["notes"]?.ToJsonString()}");
    foreach (var p in people) Console.WriteLine("  " + p!.ToJsonString());
    Check(people.Count == 2, "the scan finds both people");
    Check((r["images"] as JsonArray)?.Count == 2, "with a picture of each, for Claude");
    var back = await Fake();
    Check(back.Position == new PTZPosition(0, 0, 4000), $"the camera went back to where it was ({back.Position})");
    if (people.Count != 2) { Console.WriteLine("\nStopping: the rest needs two people."); return 1; }
    Check(people[0]!["same_person_letter"]!.ToString() != people[1]!["same_person_letter"]!.ToString(), "two people get two letters");

    r = Tool("label_stage_scan", new JsonObject { ["labels"] = new JsonArray(
        new JsonObject { ["person"] = 1, ["label"] = "Host" }, new JsonObject { ["person"] = people[1]!["same_person_letter"]!.ToString(), ["label"] = "Guest" }) });
    Console.WriteLine("label_stage_scan: " + r["done"]);
    var shots = OnMain(() => booth.Ptz.Camera(cam.Id)!.Shots.ToList());
    Check(shots.Count(s => s.Auto) == 2 && shots.Any(s => s.Name == "Host") && shots.Any(s => s.Name == "Guest"), "a shot is saved for each, marked as from the scan");
    Check(r["can_follow_by_name"]?.AsArray().Count == 2, "both can be followed by name");

    // Each saved shot should frame its person waist up: eyes near the middle across, near 0.29 down.
    foreach (var name in new[] { "Host", "Guest" })
    {
        var shot = shots.First(s => s.Name == name);
        OnMain(() => { booth.Ptz.Go(shot, cam.Id); return 0; });
        await Settle();
        var seen = await Look();
        var p = seen.OrderBy(x => Math.Abs(x.Eye.X - 0.5)).FirstOrDefault();
        Console.WriteLine($"  {name}: shot pan {shot.Pan / 100.0:0.0}° tilt {shot.Tilt / 100.0:0.0}° fov {shot.Zoom / 100.0:0.0}°; eyes at ({p?.Eye.X:0.00}, {p?.Eye.Y:0.00}), size {p?.Size}");
        Check(p != null && Math.Abs(p.Eye.X - 0.5) < 0.08 && Math.Abs(p.Eye.Y - 0.29) < 0.08 && p.Size == ShotSize.Waist, $"the {name} shot frames them waist up");
    }

    // Follow the Guest by name, starting from the wide shot with both people in it.
    await http.GetStringAsync("control.cgi?pan=0&tilt=0&zoom=7000");
    await Settle();
    string? result = "waiting";
    OnMain(() => { tracker.Start(cam, person: "guest", done: w => result = w); return 0; });
    for (int i = 0; i < 20 && result == "waiting"; i++) await Task.Delay(250);
    await Task.Delay(5000);
    var now = await Fake();
    var guestPan = shots.First(s => s.Name == "Guest").Pan;
    Console.WriteLine($"  following by name: {result ?? "found"}; camera at {now.Position.Pan / 100.0:0.0}° (the Guest shot is at {guestPan / 100.0:0.0}°)");
    Check(result == null && OnMain(() => tracker.Sessions[cam.Id].Person) == "Guest", "start_tracking by name finds the Guest");
    Check(Math.Sign(now.Position.Pan) == Math.Sign(guestPan) && Math.Abs(now.Position.Pan - guestPan) < 600, "and turns to them, not the Host");
    OnMain(() => { tracker.StopAll(); return 0; });
    Thread.Sleep(300);
    Console.WriteLine(failures == 0 ? "\nAll passed." : $"\n{failures} failed.");
    return failures == 0 ? 0 : 1;

    // MARK: Tracking
    async Task<int> Track()
    {
        await http.GetStringAsync($"control.cgi?pan={(int)(startPan * 100)}");
        string? result = "waiting";
        OnMain(() => { tracker.Start(cam, done: w => result = w); return 0; });
        var eyes = new List<double>();
        for (int t = 0; t < 24; t++)
        {
            Thread.Sleep(500);
            var (phase, eye, size, sent) = OnMain(() => tracker.Sessions.TryGetValue(cam.Id, out var s)
                ? (s.Phase.ToString(), s.Target?.Eye.X, s.Size.ToString(), s.SentPan) : ("stopped", (double?)null, "", 0.0));
            var pan = (await Fake()).Position.Pan;
            Console.WriteLine($"{t * 0.5 + 0.5,4:0.0} s  {phase,-10} {size,-9} eye x {eye,5:0.00}  sent pan {sent,6:0}  camera at {pan / 100.0,6:0.00}°  ({d.LastMs:0} ms a frame)");
            if (eye is double e) eyes.Add(e);
        }
        var settled = eyes.TakeLast(6).ToList();
        var final = (await Fake()).Position.Pan;
        var ok = result == null && settled.Count == 6 && settled.All(e => Math.Abs(e - 0.5) < 0.13) && settled.Max() - settled.Min() < 0.05;
        Console.WriteLine(ok ? $"\nPASS: found the person, brought them to the middle and settled (camera turned from {startPan:0.0}° to {final / 100.0:0.00}°)."
                             : $"\nFAIL: start said {result ?? "found"}; last eye positions {string.Join(", ", settled.Select(e => e.ToString("0.00")))}.");
        OnMain(() => { tracker.StopAll(); return 0; });
        Thread.Sleep(300);
        return ok ? 0 : 1;
    }
}
finally { try { proc.Kill(); } catch { } }
