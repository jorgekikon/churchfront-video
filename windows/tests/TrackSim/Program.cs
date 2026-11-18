using System.Diagnostics;
using System.IO;
using System.Net.Http;
using CueATEM;
using CueVideo;
using CueVideo.App;

// Auto tracking in a closed loop, with nothing plugged in: FakeDevices' Canon looks at a virtual
// stage made from a photo (a 40° view of a 60° stage) and really pans when told to. The camera
// starts turned off to one side; the real pose model and tracker should bring the person to the
// middle and settle there, without hunting.
//   dotnet run --project tests/TrackSim -- <photo with one person> [start pan in degrees, default 6]

if (args.Length < 1 || !File.Exists(args[0])) { Console.WriteLine("Give a photo with a person in it."); return 2; }
var startPan = args.Length > 1 && double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 6;
Environment.SetEnvironmentVariable("CUEVIDEO_TRACKSIM", "1");   // read-only: never writes the real config.json

var fake = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "FakeDevices", "bin", "Debug", "net8.0-windows", "FakeDevices.exe"));
if (!File.Exists(fake)) { Console.WriteLine($"Build tools/FakeDevices first ({fake} is missing)."); return 2; }
var proc = Process.Start(new ProcessStartInfo(fake, $"9931 --canon --picture \"{Path.GetFullPath(args[0])}\"") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
proc.OutputDataReceived += (_, _) => { };
proc.BeginOutputReadLine();
var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8091/-wvhttp-01-/") };
async Task<int> FakePan() => int.Parse((await http.GetStringAsync("info.cgi")).Split('\n').First(l => l.StartsWith("c.1.pan:=")).Split(":=")[1]);

try
{
    Thread.Sleep(1500);
    await http.GetStringAsync($"control.cgi?pan={(int)(startPan * 100)}");

    T OnMain<T>(Func<T> f) { var done = new ManualResetEventSlim(); T r = default!; MainThread.Post(() => { try { r = f(); } finally { done.Set(); } }); done.Wait(); return r; }
    var booth = Booth.Shared;
    var cam = new PTZCamera { Name = "Canon", Host = "127.0.0.1", Port = 8091, Proto = VISCAProtocol.CanonXC, AtemInput = 2 };
    OnMain(() => { booth.Start(); booth.Ptz.Cameras = new() { cam }; return 0; });
    for (int i = 0; i < 50 && !OnMain(() => booth.Ptz.Controlled.Contains(cam.Id)); i++) Thread.Sleep(100);

    var d = PoseDetector.Load(out var why);
    if (d == null) { Console.WriteLine(why); return 2; }
    Console.WriteLine($"Model on the {d.Device}");
    var tracker = AutoTracker.Shared;
    string? result = "waiting";
    OnMain(() =>
    {
        tracker.Detector = d; tracker.Pictures = new TrackPictures(); tracker.Attach();
        tracker.Start(cam, done: w => result = w);
        return 0;
    });

    var eyes = new List<double>();
    for (int t = 0; t < 24; t++)
    {
        Thread.Sleep(500);
        var (phase, eye, size, sent) = OnMain(() => tracker.Sessions.TryGetValue(cam.Id, out var s)
            ? (s.Phase.ToString(), s.Target?.Eye.X, s.Size.ToString(), s.SentPan) : ("stopped", (double?)null, "", 0.0));
        var pan = await FakePan();
        Console.WriteLine($"{t * 0.5 + 0.5,4:0.0} s  {phase,-10} {size,-9} eye x {eye,5:0.00}  sent pan {sent,6:0}  camera at {pan / 100.0,6:0.00}°  ({d.LastMs:0} ms a frame)");
        if (eye is double e) eyes.Add(e);
    }
    var settled = eyes.TakeLast(6).ToList();
    var final = await FakePan();
    var ok = result == null && settled.Count == 6 && settled.All(e => Math.Abs(e - 0.5) < 0.13) && settled.Max() - settled.Min() < 0.05;
    Console.WriteLine(ok ? $"\nPASS: found the person, brought them to the middle and settled (camera turned from {startPan:0.0}° to {final / 100.0:0.00}°)."
                         : $"\nFAIL: start said {result ?? "found"}; last eye positions {string.Join(", ", settled.Select(e => e.ToString("0.00")))}.");
    OnMain(() => { tracker.StopAll(); return 0; });
    Thread.Sleep(300);
    return ok ? 0 : 1;
}
finally { try { proc.Kill(); } catch { } }
