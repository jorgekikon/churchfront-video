using System.Diagnostics;
using System.Text.Json.Nodes;
using CueATEM;
using CueVideo;

// Runs the engine, the cameras, the config file and Claude's tools against tools/FakeDevices
// (started here on UDP 9920, with fake VISCA cameras and a fake Canon). Never point this at a
// real switcher during a service.
//   dotnet run --project tests/Smoke

Environment.SetEnvironmentVariable("CUEVIDEO_SMOKE", "1");   // read-only: never writes the real config.json
var failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
bool Wait(Func<bool> cond, double seconds = 5)
{
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < end) { if (OnMain(cond)) return true; Thread.Sleep(50); }
    return false;
}
T OnMain<T>(Func<T> f)
{
    var done = new ManualResetEventSlim(); T r = default!;
    MainThread.Post(() => { try { r = f(); } finally { done.Set(); } });
    done.Wait();
    return r;
}
JsonObject Tool(string name, JsonObject? args = null)
{
    var done = new ManualResetEventSlim(); JsonObject r = new();
    MainThread.Post(() => ControlTools.Call(name, args ?? new JsonObject(), x => { r = x; done.Set(); }));
    if (!done.Wait(TimeSpan.FromSeconds(10))) return new JsonObject { ["error"] = "timed out" };
    return r;
}

// Start the fake hardware.
var root = AppContext.BaseDirectory;
var fake = Path.GetFullPath(Path.Combine(root, "..", "..", "..", "..", "..", "tools", "FakeDevices", "bin", "Debug", "net8.0-windows", "FakeDevices.exe"));
if (!File.Exists(fake)) { Console.WriteLine($"Build tools/FakeDevices first ({fake} is missing)."); return 2; }
var proc = Process.Start(new ProcessStartInfo(fake, "9920 --cams --canon") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
var fakeLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
proc.OutputDataReceived += (_, e) => { if (e.Data != null) fakeLog.Enqueue(e.Data); if (Environment.GetEnvironmentVariable("SMOKE_VERBOSE") != null) Console.WriteLine("    " + e.Data); };
proc.BeginOutputReadLine();
Thread.Sleep(700);

try
{
    // MARK: Config file format
    var mac = """
    {
      "atemHost" : "192.168.1.240",
      "cameras" : [
        { "atemInput" : 9, "host" : "192.168.1.91", "name" : "Cam 1", "proto" : "canonXC",
          "presetNames" : { "1" : "Pulpit" },
          "shots" : [ { "id" : "0A6C2B41-7A39-4B0C-9E0B-3E1C5A6B7C8D", "kind" : "position", "name" : "Wide", "pan" : -1200, "tilt" : 50, "zoom" : 4000 },
                      { "name" : "Bright", "exposure" : { "gain" : 60, "shutter" : 60 } } ] },
        { "host" : "192.168.1.92" }
      ],
      "me" : 1,
      "sources" : [ { "input" : 9, "label" : "Cam 1" }, { "input" : 3, "ndi" : "GRAPHICS (CueScreens Stream)" } ],
      "keys" : [ { "label" : "Lower thirds", "me" : 1, "keyer" : 1 } ],
      "trackSpeed" : 9,
      "routes" : [ { "label" : "Lobby: stream", "set" : [ { "aux" : 4, "source" : 10010 } ] } ]
    }
    """;
    var cfg = CueConfig.Parse(mac);
    Check(cfg.AtemHost == "192.168.1.240" && cfg.Cameras.Count == 2, "reads a Mac config file");
    Check(cfg.Cameras[0].Port == 80 && cfg.Cameras[1].Port == 52381 && cfg.Cameras[1].Proto == VISCAProtocol.SonyUDP, "camera ports follow the protocol when left out");
    Check(cfg.Cameras[0].Shots[1].Kind == ShotKind.Exposure && cfg.Cameras[0].Shots[1].Exposure?.Gain == 60, "a shot with only exposure is an exposure shot");
    Check(cfg.TrackSpeed == 4, "trackSpeed is kept in range");
    var text = cfg.ToFileText();
    var again = CueConfig.Parse(text);
    Check(again.Signature() == cfg.Signature(), "the file reads back the same");
    Check(text.IndexOf("\"atemHost\"") < text.IndexOf("\"cameras\"") && text.IndexOf("\"cameras\"") < text.IndexOf("\"keys\""), "keys are written in order");
    Check(text.Contains("0A6C2B41-7A39-4B0C-9E0B-3E1C5A6B7C8D"), "UUIDs are written in capitals");
    Check(!text.Contains("\"pan\" : 0") && text.Contains("\"kind\": \"exposure\""), "an exposure shot doesn't write pan, tilt and zoom");
    try { CueConfig.Parse("{\"sources\": [ { \"label\": \"no input\" } ]}"); Check(false, "a source without input is refused"); }
    catch { Check(true, "a source without input is refused"); }

    // MARK: Switcher
    var booth = Booth.Shared;
    Check(booth.Store.ReadOnly, "self-test never writes the real config file");
    OnMain(() => { booth.Start(); return 0; });
    var atem = booth.Atem;
    OnMain(() => { atem.Connect("127.0.0.1", 9920); return 0; });
    Check(Wait(() => atem.Link.Kind == LinkKind.Online && atem.State.Inputs.Count > 10), "connects to the switcher and reads its inputs");
    Check(OnMain(() => atem.State.Product).Contains("fake"), "reads the model");
    Check(OnMain(() => atem.State.ProgramOf(0) == 1 && atem.State.PreviewOf(0) == 2), "reads Live and Next");
    OnMain(() => { atem.SetPreview(3); return 0; });
    Check(Wait(() => atem.State.PreviewOf(0) == 3), "puts a source on Next");
    OnMain(() => { atem.Cut(); return 0; });
    Check(Wait(() => atem.State.ProgramOf(0) == 3 && atem.State.PreviewOf(0) == 1), "cuts");
    OnMain(() => { atem.SetMixRate(10); return 0; });
    Check(Wait(() => atem.State.MixRate.GetValueOrDefault(0) == 10), "sets the mix rate");
    OnMain(() => { atem.Auto(); return 0; });
    Check(Wait(() => atem.State.Get(atem.State.InTransition, 0), 2), "a fade starts");
    Check(Wait(() => !atem.State.Get(atem.State.InTransition, 0) && atem.State.ProgramOf(0) == 1), "and finishes with Next on Live");
    OnMain(() => { atem.SetAux(1, 10010); return 0; });
    Check(Wait(() => atem.State.Aux.GetValueOrDefault(1) == 10010), "routes an aux");
    OnMain(() => { atem.DskAuto(0); return 0; });
    Check(Wait(() => atem.State.Get(atem.State.DskOnAir, 0), 3), "fades a DSK on");
    Check(OnMain(() => atem.State.Macros.Count) == 3, "reads the macros");
    Check(OnMain(() => atem.State.HasStreaming && atem.State.HasRecording), "sees streaming and recording");

    // MARK: Cameras
    var ptz = booth.Ptz;
    var visca = new PTZCamera { Name = "Center", Host = "127.0.0.1", Port = 52381, Proto = VISCAProtocol.SonyUDP, AtemInput = 1 };
    var canon = new PTZCamera { Name = "Canon", Host = "127.0.0.1", Port = 8091, Proto = VISCAProtocol.CanonXC, AtemInput = 2 };
    OnMain(() => { ptz.Cameras = new() { visca, canon }; return 0; });
    Check(Wait(() => ptz.Online.Contains(visca.Id)), "a VISCA over IP camera answers");
    Check(Wait(() => ptz.Online.Contains(canon.Id) && ptz.Controlled.Contains(canon.Id)), "a Canon camera answers and hands over control");
    Check(Wait(() => ptz.ReportedPresets.TryGetValue(canon.Id, out var p) && p.GetValueOrDefault(1) == "Pulpit"), "reads the Canon's preset names");
    Check(Wait(() => ptz.ExposurePresets.TryGetValue(canon.Id, out var e) && e.Contains(3)), "knows which Canon presets hold only exposure");
    Check(Wait(() => ptz.Exposure.TryGetValue(canon.Id, out var x) && x.Value.Gain == 60 && x.GainList.Count == 5), "reads the Canon's exposure");
    OnMain(() => { ptz.RefreshExposure(visca.Id); return 0; });
    Check(Wait(() => ptz.Exposure.TryGetValue(visca.Id, out var x) && x.HasAutoMode && x.Value.Shutter == 180), "reads a VISCA camera's exposure");

    PTZPosition? pos = null; var got = new ManualResetEventSlim();
    OnMain(() => { ptz.ReadPosition(canon.Id, p => { pos = p; got.Set(); }); return 0; });
    Check(got.Wait(3000) && pos?.Zoom == 5000, "reads where the Canon is pointing");

    // Moves through Claude's tools, as the MCP server would send them.
    var r = Tool("get_status");
    Check(r["switcher"]?["connection"]?.ToString() == "online" && r["cameras"]?.AsArray().Count == 2, "get_status");
    r = Tool("set_next", new JsonObject { ["source"] = "left camera" });
    Check(r["ok"] != null && Wait(() => atem.State.PreviewOf(0) == 2), "set_next by name");
    r = Tool("set_next", new JsonObject { ["source"] = "camera" });
    Check(r["error"]?.ToString().Contains("more than one") == true, "a loose name that fits several sources is refused");
    r = Tool("camera_move", new JsonObject { ["camera"] = "Canon", ["pan"] = 0.5, ["duration_ms"] = 100 });
    Check(r["ok"] != null, "camera_move on a camera on Next");
    r = Tool("camera_home", new JsonObject { ["camera"] = "Center" });
    Check(r["error"]?.ToString().Contains("on Live") == true, "a camera on Live won't move without allow_live");
    r = Tool("camera_home", new JsonObject { ["camera"] = "Center", ["allow_live"] = true });
    Check(r["ok"] != null, "it moves with allow_live");
    OnMain(() => { ptz.Recall(1, visca.Id); return 0; });

    // A VISCA shot keeps the camera's own counts.
    OnMain(() => { ptz.TrackDrive(0, 0, visca.Id); return 0; });
    r = Tool("save_shot", new JsonObject { ["camera"] = "Center", ["name"] = "Pulpit" });
    Check(r["ok"] != null && OnMain(() => ptz.Camera(visca.Id)!.Shots.Any(s => s.Name == "Pulpit")), "save_shot on a VISCA camera");
    r = Tool("save_shot", new JsonObject { ["camera"] = "Canon", ["name"] = "Wide" });
    Check(r["ok"] != null && OnMain(() => ptz.Camera(canon.Id)!.Shots.FirstOrDefault(s => s.Name == "Wide")?.Zoom) == 5000, "save_shot on a Canon keeps pan, tilt and zoom");
    r = Tool("recall_shot", new JsonObject { ["camera"] = "Canon", ["shot"] = "wide" });
    Check(r["ok"] != null, "recall_shot");
    r = Tool("rename_shot", new JsonObject { ["camera"] = "Canon", ["shot"] = "Wide", ["new_name"] = "Stage" });
    Check(r["ok"] != null && OnMain(() => ptz.Camera(canon.Id)!.Shots.Any(s => s.Name == "Stage")), "rename_shot");
    r = Tool("set_exposure", new JsonObject { ["camera"] = "Canon", ["gain"] = 95, ["white_balance"] = 4000 });
    Check(r["sent"]?["gain"]?.GetValue<int>() == 90 && r["sent"]?["white_balance"]?.ToString() == "4300 K", "set_exposure snaps to values the camera accepts");
    Check(Wait(() => ptz.Exposure[canon.Id].Value.Gain == 90), "and the camera reads back the new gain");
    r = Tool("match_exposure", new JsonObject { ["from"] = "Canon", ["allow_live"] = true });
    Check(r["changed"]?["Center"] != null, "match_exposure");
    r = Tool("save_shot", new JsonObject { ["camera"] = "Canon", ["name"] = "Bright", ["kind"] = "exposure" });
    Check(r["ok"] != null && OnMain(() => ptz.Camera(canon.Id)!.Shots.Any(s => s.Kind == ShotKind.Exposure)), "save_shot of exposure");
    r = Tool("delete_shot", new JsonObject { ["camera"] = "Canon", ["shot"] = "Bright" });
    Check(r["ok"] != null, "delete_shot");
    r = Tool("recall_camera_preset", new JsonObject { ["camera"] = "Canon", ["preset"] = "pulpit" });
    Check(r["ok"] != null, "recall_camera_preset by name");
    r = Tool("route_screen", new JsonObject { ["screen"] = 2, ["source"] = "M/E 1 preview" });
    Check(r["ok"] != null && Wait(() => atem.State.Aux.GetValueOrDefault(1) == 10011), "route_screen to an M/E's preview");
    r = Tool("set_key_button", new JsonObject { ["button"] = "nope" });
    Check(r["error"] != null, "an unknown key button is refused");
    r = Tool("set_recording", new JsonObject());
    Check(r["error"]?.ToString().Contains("Nothing was changed") == true, "set_recording without on changes nothing");
    // MARK: Auto tracking (the pose model itself is tried by tests/TrackSim)
    var redBlue = Appearance.Solid(200, 30, 30, 30, 30, 160);
    var greenGrey = Appearance.Solid(30, 160, 40, 128, 128, 128);
    Check(redBlue.Distance(Appearance.Solid(190, 35, 25, 35, 30, 150)) < 0.3 && redBlue.Distance(greenGrey) > 0.8, "the same clothes look alike, different ones don't");
    var crossing = IdentityMatch.Choose(new() { new(0.05, 0.9), new(0.08, 0.2) }, 0.3, 0.1, new(0.2, 0.9));
    Check(crossing.Take == 1 && crossing.Sure, "when people cross, it keeps the one who looks like them, not the nearest");
    var stranger = IdentityMatch.Choose(new() { new(0.1, 1.0) }, 0.4, 2, new(0.2, 0.9));
    Check(stranger.Take == null, "after losing sight, it won't take up someone who looks different");
    // A waist-up shot: eyes 0.29 down, shoulders 0.21 below them, hips out of shot.
    var pts = new (double X, double Y, double C)[17];
    void Pt(int i, double x, double y) => pts[i] = (x, y, 0.9);
    Pt(0, 0.70, 0.31); Pt(1, 0.69, 0.29); Pt(2, 0.71, 0.29); Pt(3, 0.66, 0.30); Pt(4, 0.74, 0.30); Pt(5, 0.62, 0.50); Pt(6, 0.78, 0.50);
    var people = TrackedPerson.From(new() { new PoseBody { Box = new(0.55, 0.15, 0.3, 0.85), Score = 0.9, Points = pts } }, null);
    Check(people.Count == 1 && people[0].Size == ShotSize.Waist && Math.Abs(people[0].Eye.X - 0.70) < 0.01, "reads a waist-up shot from the body points");

    // Claude's tools drive a tracking camera, with a stand-in for the picture and the model.
    r = Tool("start_tracking", new JsonObject { ["camera"] = "Canon", ["allow_live"] = true });
    Check(r["error"]?.ToString().Contains("set up") == true || r["error"]?.ToString().Contains("starting") == true, "without the model, tracking says why");
    var stand = new StandIn(pts);
    OnMain(() => { AutoTracker.Shared.Detector = stand; AutoTracker.Shared.Pictures = stand; AutoTracker.Shared.Attach(); return 0; });
    fakeLog.Clear();
    r = Tool("start_tracking", new JsonObject { ["camera"] = "Canon", ["allow_live"] = true });
    Check(r["done"]?.ToString().Contains("waist") == true, "start_tracking finds the person and keeps the waist-up shot");
    Check(Wait(() => fakeLog.Any(l => l.Contains("pan=right"))), "a person right of the mark turns the camera right");
    Check(Tool("get_status")["cameras"]?[1]?["tracking"]?["state"]?.ToString() == "following", "get_status shows it following");
    r = Tool("set_tracking_shot", new JsonObject { ["camera"] = "Canon", ["shot"] = "shoulders", ["allow_live"] = true });
    Check(r["ok"] != null && OnMain(() => AutoTracker.Shared.Sessions[canon.Id].Size) == ShotSize.Shoulders, "set_tracking_shot reframes");
    OnMain(() => { ptz.Drive(0.5, 0, canon.Id); ptz.StopDrive(canon.Id); return 0; });
    Check(Wait(() => !AutoTracker.Shared.IsTracking(canon.Id)), "moving the camera by hand stops tracking");
    Tool("start_tracking", new JsonObject { ["camera"] = "Canon", ["allow_live"] = true });
    fakeLog.Clear();
    r = Tool("stop_tracking", new JsonObject { ["all"] = true });
    Check(r["ok"] != null && Wait(() => fakeLog.Any(l => l.Contains("pan=stop"))), "stop_tracking stops the camera");

    // MARK: NDI (with the runtime installed, a real round trip on this computer)
    Check(Ndi.Match("OLD-PC (Lower third)", new[] { "BOOTH (Lower third)", "BOOTH (Slides)" }) == "BOOTH (Lower third)"
          && Ndi.Match("OLD-PC (Slides)", new[] { "A (Slides)", "B (Slides)" }) == "OLD-PC (Slides)", "an NDI source whose computer name changed is found by its channel, unless that's ambiguous");
    Check(Ndi.ShortName("BOOTH-PC.LOCAL (ATEM Multiview)") == "ATEM Multiview · booth-pc" && Ndi.Channel("No channel") == null, "NDI names read as channel · computer");
    if (Ndi.Load())
    {
        const int nw = 160, nh = 90;
        var npx = new byte[nw * nh * 4];
        for (int i = 0; i < nw * nh; i++) { npx[i * 4] = 30; npx[i * 4 + 1] = 160; npx[i * 4 + 2] = 220; npx[i * 4 + 3] = (byte)(i % nw < nw / 2 ? 255 : 0); }
        var sender = Ndi.SendOpen("Churchfront smoke");
        var sending = true;
        var sendThread = new Thread(() => { while (sending) { Ndi.SendFrame(sender, npx, nw, nh); Thread.Sleep(40); } }) { IsBackground = true };
        sendThread.Start();
        try
        {
            JsonArray? listed = null;
            for (int i = 0; i < 4 && listed?.Any(n => n!.ToString().EndsWith("(Churchfront smoke)")) != true; i++) listed = Tool("list_ndi_sources")["sources"] as JsonArray;
            Check(listed?.Any(n => n!.ToString().EndsWith("(Churchfront smoke)")) == true, "list_ndi_sources finds an NDI source on the network");
            OnMain(() => { NdiHub.Retain("SOMEWHERE-ELSE (Churchfront smoke)"); return 0; });
            var feed = OnMain(() => NdiHub.Feed("SOMEWHERE-ELSE (Churchfront smoke)"));
            Check(Wait(() => feed.IsLive && feed.Frame != null, 8), "receives an NDI picture");
            var nf = feed.Frame;
            Check(nf is { Width: nw, Height: nh, Alpha: true } && nf.Bgra[3] == 255 && nf.Bgra[(nw - 1) * 4 + 3] == 0, "keeps the NDI picture's alpha, for lower thirds");
            Check(nf?.ToTrackFrame() is { Width: nw, Height: nh } tf && tf.Bgr[2] == nf.Bgra[2], "turns it into a picture auto tracking can use");
            // A VISCA camera given that NDI picture: snapshots and tracking can use it.
            OnMain(() => { booth.Store.SetPicture(1, "SOMEWHERE-ELSE (Churchfront smoke)"); return 0; });
            Check(OnMain(() => Snapshots.HasPicture(visca)), "a VISCA camera with an NDI picture has a picture");
            var (wasImaging, wasJpeg) = (Snapshots.Imaging, Snapshots.ToJpeg);
            Snapshots.ToJpeg = f => new byte[] { 1, 2, 3 };
            Snapshots.Imaging = (_, _) => (new JsonObject { ["brightness"] = 99 }, null);
            r = Tool("camera_snapshots", new JsonObject { ["cameras"] = new JsonArray("Center"), ["pictures"] = false });
            Check(r["cameras"]?[0]?["brightness"]?.GetValue<int>() == 99, "camera_snapshots takes a still from an NDI picture");
            (Snapshots.Imaging, Snapshots.ToJpeg) = (wasImaging, wasJpeg);
            Check(Tool("get_status")["ndi_previews"]?.AsArray().Any(p => p?["receiving"]?.ToString() == "true") == true, "get_status says which NDI pictures are coming in");
            OnMain(() => { booth.Store.SetPicture(1, null); NdiHub.Release("SOMEWHERE-ELSE (Churchfront smoke)"); return 0; });
        }
        finally { sending = false; sendThread.Join(); Ndi.SendClose(sender); }
    }
    else Check(Tool("list_ndi_sources")["error"]?.ToString().Contains("NDI Tools") == true, "without the NDI runtime, list_ndi_sources says to install NDI Tools");

    // MARK: Routing editor (the page itself was tried in the app with UI Automation)
    var rc = new CueConfig { Screens = new() { new() { Name = "Side screens", Aux = new() { 1, 2 } }, new() { Name = "Lobby", Aux = new() { 3 }, Volunteer = false, Note = "Converter 1" } } };
    Check(RoutingEdit.ParseAux("4, 5") is [4, 5] && RoutingEdit.ParseAux("4+5 0 x") is [4, 5], "reads aux outputs written as 4, 5 or 4+5");
    Check(RoutingEdit.SaveScreen(rc, null, "Stage left", "2, 4", null) == null && rc.Screens.First(s => s.Name == "Side screens").Aux is [1]
          && rc.Screens.Any(s => s.Name == "Stage left" && s.Aux is [2, 4]), "naming an output takes it away from the screen that had it");
    Check(RoutingEdit.SaveScreen(rc, rc.Screens.First(s => s.Name == "Lobby"), "Lobby TVs", "3", "Converter 2") == null
          && rc.Screens.First(s => s.Name == "Lobby TVs") is { Volunteer: false, Note: "Converter 2" }, "renaming a screen keeps whether operators see it");
    RoutingEdit.SaveScreen(rc, null, "Everything", "1", null);
    Check(!rc.Screens.Any(s => s.Name == "Side screens"), "a screen left with no outputs is dropped");
    Check(RoutingEdit.SaveScreen(rc, null, " ", "1", null) != null && RoutingEdit.SaveScreen(rc, null, "X", "none", null) != null, "a name and an output are needed");
    Check(RoutingEdit.IsSpare("Out 7 (PB 12)") && RoutingEdit.IsSpare("Patch bay 3") && !RoutingEdit.IsSpare("Lobby"), "patch bay ports are spare");
    var rs = new ATEMState(); rs.Aux[0] = 3010; rs.Aux[2] = 4;
    var groups = RoutingEdit.Destinations(rc, rs);
    Check(groups.First(g => g.Group == RoutingEdit.Group.Feeds).Screens.Any(s => s.Name == "Lobby TVs"), "screens hidden from operators are encoders and feeds");
    Check(RoutingEdit.SaveButton(rc, "Lobby: slides", new[] { rc.Screens.First(s => s.Name == "Lobby TVs").Id }, rs) == null
          && rc.Routes.Single().Set is [{ Aux: 3, Source: 4 }], "a button saves what the chosen screens show now");
    RoutingEdit.SaveButton(rc, "Everything", new[] { rc.Screens.First(s => s.Name == "Everything").Id }, rs);
    RoutingEdit.MoveButton(rc, 1, -1);
    Check(rc.Routes[0].Label == "Everything" && RoutingEdit.SaveButton(rc, "Empty", Array.Empty<string>(), rs) != null, "buttons move, and one that sets nothing is refused");

    // MARK: Stage scan (with the real model and a virtual stage: tests/TrackSim scan)
    var straight = ShotMath.Shot(new(0.5, 0.29), 0.21, new(10, 5, 40), 0.29, 0.21);
    Check(Math.Abs(straight.Pan - 10) < 0.01 && Math.Abs(straight.Tilt - 5) < 0.01 && Math.Abs(straight.Fov - 40) < 0.01, "a person already framed right needs no move");
    var edge = ShotMath.Shot(new(1.0, 0.29), 0.105, new(0, 0, 60), 0.29, 0.21);
    Check(Math.Abs(edge.Pan - 30) < 0.01 && Math.Abs(edge.Fov - 32.2) < 0.1, "a person at the edge: pan by half the view, zoom in to twice their size");
    var st = CanonState.Parse("c.1.pan:=-1200\nc.1.tilt==50\nc.1.zoom:=6050\nc.1.zoom.limit.min:=330\nc.1.zoom.limit.max:=6050\n");
    Check(st?.View == new ShotMath.View(-12, 0.5, 60.5) && st.Limits.Fov.Lo == 3.3, "reads where a Canon points and how far it zooms");
    StageScan.Person P(int id, Guid cam, Appearance look) => new() { Id = id, Camera = cam, Look = look, Group = -1 };
    Guid camA = Guid.NewGuid(), camB = Guid.NewGuid();
    var grouped = StageScan.Grouped(new() { P(1, camA, redBlue), P(2, camA, greenGrey), P(3, camB, greenGrey), P(4, camB, redBlue) });
    Check(grouped[0].Letter == grouped[3].Letter && grouped[1].Letter == grouped[2].Letter && grouped[0].Letter != grouped[1].Letter, "the same clothes on two cameras get the same letter");
    Check(IdentityMatch.Find(new() { 0.9, 0.2, null }) == 1 && IdentityMatch.Find(new() { 0.5, 0.55 }) == null, "finding someone by name needs a clear best fit");

    r = Tool("label_stage_scan", new JsonObject { ["labels"] = new JsonArray(new JsonObject { ["person"] = 1, ["label"] = "Pastor" }) });
    Check(r["error"]?.ToString().Contains("scan_stage first") == true, "labelling before a scan is refused");
    OnMain(() => { StageScan.Shared.Images = stand; return 0; });
    fakeLog.Clear();
    r = Tool("scan_stage", new JsonObject { ["cameras"] = new JsonArray("Canon"), ["pictures"] = false });
    Check(r["people"]?.AsArray().Count == 1 && r["people"]?[0]?["shot"] != null, "scan_stage finds the person on the Canon and proposes a shot");
    Check(fakeLog.Count(l => l.Contains("control.cgi") && l.Contains("zoom=")) >= 2, "the camera went to its Stage shot for the scan and came back");
    r = Tool("label_stage_scan", new JsonObject { ["labels"] = new JsonArray(new JsonObject { ["person"] = "Z", ["label"] = "Nobody" }) });
    Check(r["error"]?.ToString().Contains("Nothing was saved") == true, "a label for someone not in the scan saves nothing");
    r = Tool("label_stage_scan", new JsonObject { ["labels"] = new JsonArray(new JsonObject { ["person"] = "A", ["label"] = "Pastor" }) });
    Check(r["done"]?.ToString().Contains("Saved 1 shot") == true && OnMain(() => ptz.Camera(canon.Id)!.Shots.Any(s => s.Name == "Pastor" && s.Auto)), "label_stage_scan saves a shot marked as from the scan");
    Check(Tool("get_status")["stage_scan_names"]?[0]?.ToString() == "Pastor", "get_status lists who can be followed by name");
    r = Tool("start_tracking", new JsonObject { ["camera"] = "Canon", ["person"] = "deacon", ["allow_live"] = true });
    Check(r["error"]?.ToString().Contains("Pastor") == true, "following an unknown name says who can be followed");
    r = Tool("start_tracking", new JsonObject { ["camera"] = "Canon", ["person"] = "pastor", ["allow_live"] = true });
    Check(r["done"]?.ToString().Contains("following Pastor") == true, "start_tracking follows someone by name");
    Tool("stop_tracking", new JsonObject { ["all"] = true });
    r = Tool("set_framing", new JsonObject { ["shot"] = "waist", ["eye_line"] = 0.9 });
    Check(r["framing"]?["waist"]?["now"]?["eye_line"]?.GetValue<double>() == 0.5, "set_framing keeps values in range");
    r = Tool("camera_snapshots", new JsonObject { ["cameras"] = new JsonArray("Canon"), ["pictures"] = false });
    Check(r["cameras"]?[0]?["error"] != null || r["cameras"]?[0]?["brightness"] != null, "camera_snapshots answers (pictures need the app's decoder)");
    r = Tool("run_macro", new JsonObject { ["macro"] = "service start" });
    Check(r["ok"] != null && Wait(() => atem.State.MacroRunning == 1), "run_macro by name");
    r = Tool("get_routing");
    Check(r["outputs"]?.AsArray().Count == 4, "get_routing");
    Check(Tool("definitions_count_check") is var x && x["error"] != null && ControlTools.Definitions().Count >= 45, "every tool is defined");

    // MARK: Device finder (probes every address on this computer's networks, so only when asked)
    if (Environment.GetEnvironmentVariable("SMOKE_FIND") != null)
    {
        var found = new ManualResetEventSlim(); List<DeviceFinder.Found>? devs = null;
        DeviceFinder.Find(2, (l, _) => { devs = l; found.Set(); });
        Check(found.Wait(8000), $"find_devices finishes ({devs?.Count ?? 0} found on this network)");
    }

    // Stop everything moving, as quitting does.
    OnMain(() => { ptz.StopAllManual(); return 0; });
    ptz.Shutdown();
}
finally
{
    try { proc.Kill(); } catch { }
}
Console.WriteLine(failures == 0 ? "\nAll passed." : $"\n{failures} failed.");
return failures == 0 ? 0 : 1;

/// <summary>Stands in for the camera picture (a blank frame ten times a second) and the pose
/// model (the same person every frame), so the tracker can be tried without either.</summary>
sealed class StandIn : IPersonDetector, ITrackPictures, IStillImages
{
    public TrackFrame? Decode(byte[] jpeg, int maxWidth) => new() { Width = 640, Height = 360, Bgr = new byte[640 * 360 * 3] };
    public byte[]? Crop(TrackFrame frame, RectD box, int maxWidth) => new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
    private readonly (double X, double Y, double C)[] _pts;
    public StandIn((double, double, double)[] pts) { _pts = pts; }
    public List<PoseBody> Detect(TrackFrame f) => new() { new PoseBody { Box = new(0.55, 0.15, 0.3, 0.85), Score = 0.9, Points = _pts } };
    public bool Has(PTZCamera c) => c.Proto == VISCAProtocol.CanonXC;
    public Action Watch(PTZCamera c, Action<TrackFrame> frame)
    {
        var f = new TrackFrame { Width = 640, Height = 360, Bgr = new byte[640 * 360 * 3] };
        var t = new Timer(_ => frame(f), null, 0, 100);
        return () => t.Dispose();
    }
}
