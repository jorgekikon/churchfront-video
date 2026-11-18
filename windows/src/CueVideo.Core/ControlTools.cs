using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CueATEM;

namespace CueVideo;

/// <summary>
/// Everything a person can do in the app's window, as tools for Claude (through cuevideo-mcp
/// and ControlServer). One place defines both the tool list and what each tool does. A port
/// of ControlTools.swift: the same tools, names and arguments. Stage scan and NDI aren't on
/// Windows yet, so those tools say so.
///
/// Sources can be named by input number or by any of their names. Cameras by name, IP address
/// or switcher input; left out, a camera tool acts on the camera on Next. A camera on Live
/// won't move or change unless the call says allow_live: true. All of this runs on the main thread.
/// </summary>
public static class ControlTools
{
    private static Booth B => Booth.Shared;
    private static ATEMClient Atem => B.Atem;
    private static PTZController Ptz => B.Ptz;
    private static ConfigStore Store => B.Store;

    /// <summary>Set by the app: shows a view or Engineer page ("operator", "main", "scan", …).</summary>
    public static Action<string>? ShowWindow;

    private const string NotYet = " Not available in the Windows version yet.";
    private static readonly HashSet<string> NotOnWindows = new();

    // MARK: Tool list

    private static JsonObject Tool(string name, string description, JsonObject? props = null, params string[] required)
    {
        if (NotOnWindows.Contains(name)) description += NotYet;
        var req = new JsonArray(); foreach (var r in required) req.Add(r);
        return new JsonObject
        {
            ["name"] = name, ["description"] = description,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props ?? new JsonObject(), ["required"] = req },
        };
    }
    private static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
    private static JsonObject Num(string d) => new() { ["type"] = "number", ["description"] = d };
    private static JsonObject Int(string d) => new() { ["type"] = "integer", ["description"] = d };
    private static JsonObject Bool(string d) => new() { ["type"] = "boolean", ["description"] = d };
    private static JsonObject Choice(string d, params string[] values) { var a = new JsonArray(); foreach (var v in values) a.Add(v); return new() { ["type"] = "string", ["enum"] = a, ["description"] = d }; }
    private static JsonObject Any(string d) => new() { ["type"] = new JsonArray("string", "integer"), ["description"] = d };
    private static JsonObject AnyList(string d) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = new JsonArray("string", "integer") }, ["description"] = d };

    private static JsonObject SourceP() => Any("Input number, or a name: the volunteer label, camera name, or the switcher's long or short name.");
    private static JsonObject CameraP() => Any("Camera name, IP address or switcher input. Leave out for the camera on Next.");
    private static JsonObject MeP() => Int("M/E, counted from 1. Leave out for the M/E volunteers run.");
    private static JsonObject AllowLive() => Bool("Required as true to change a camera that's on Live (program).");

    private static JsonObject P(params (string, JsonNode)[] kv) { var o = new JsonObject(); foreach (var (k, v) in kv) o[k] = v; return o; }

    public static JsonArray Definitions() => new()
    {
        // Status and setup
        Tool("get_status", "Everything Churchfront Video knows right now: switcher connection, what's Live and Next on each M/E, keys, DSKs, aux, macros, recording and streaming, every source, the volunteer source row and key buttons, and each camera's connection, control, exposure, saved shots and on-camera presets."),
        Tool("get_config", "The setup as saved in Churchfront Video's config file (JSON): switcher address, volunteer M/E, source row, key buttons, cameras and their saved shots."),
        Tool("update_config", "Change the setup. Give any of the top-level fields (atemHost, me, sources, keys, cameras, trackSpeed 0.5 to 4, ptzSpeed 0.25 to 2 (manual camera control speed), trackTilt, trackZoom). Sources and keys take an optional ndi: an NDI source name from list_ndi_sources. pictures ({input number: NDI source name}, a live picture for a switcher input that is not in sources, such as a stage display). Routing: mixNames (what each M/E is for, from M/E 1), screens ([{name, aux: [numbers from 1], note, volunteer, pinned (a small preview in the top bar)}]), routes (volunteer routing buttons: [{label, set: [{aux, source}]}], source as an input number; M/E 1 program is 10010, M/E 2 program 10020); each one given replaces that whole field. Read get_config first and send back the edited field.",
            P(("config", new JsonObject { ["type"] = "object", ["description"] = "Fields to replace, in the config file's format." })), "config"),
        Tool("show_window", "Show a view of Churchfront Video's window on screen: operator (the booth screen, also called main), or a page of the Engineer view: scan (stage scan), routing, switcher (the full switcher), setup or status.",
            P(("window", Choice("Which view or page.", "operator", "main", "scan", "routing", "switcher", "setup", "status"))), "window"),

        // Volunteer screen
        Tool("set_next", "Put a source on Next (preview), like tapping its tile.", P(("source", SourceP()), ("me", MeP())), "source"),
        Tool("set_live", "Put a source straight on Live (program) with no transition, like Shift plus its number key.", P(("source", SourceP()), ("me", MeP())), "source"),
        Tool("cut", "Switch Next to Live instantly (Cut).", P(("me", MeP()))),
        Tool("fade", "Fade from Live to Next (Fade, the auto transition).", P(("me", MeP()))),
        Tool("fade_to_black", "Fade everything to black, or back up if already black (Black).", P(("me", MeP()))),
        Tool("set_key_button", "Turn one of the volunteer key buttons (such as Lower thirds) on or off. Leave out on to toggle.",
            P(("button", Str("The button's label, such as Lower thirds.")), ("on", Bool("true to put it on air, false to take it off."))), "button"),
        Tool("set_recording", "Start or stop the switcher's recording.", P(("on", Bool("true to record."))), "on"),
        Tool("set_streaming", "Start or stop the switcher's stream.", P(("on", Bool("true to stream."))), "on"),

        // Cameras
        Tool("camera_move", "Pan and tilt a camera for a short time, like holding the joystick, then stop. Speeds run -1 to 1 (right and up are positive); small values are slow and smooth.",
            P(("camera", CameraP()), ("pan", Num("-1 (left) to 1 (right).")), ("tilt", Num("-1 (down) to 1 (up).")),
              ("duration_ms", Int("How long to move, 50 to 5000 ms. Default 500.")), ("allow_live", AllowLive()))),
        Tool("camera_zoom", "Zoom a camera for a short time, then stop. Speed -1 (wide) to 1 (tight).",
            P(("camera", CameraP()), ("speed", Num("-1 (wide) to 1 (tight).")), ("duration_ms", Int("50 to 5000 ms. Default 500.")), ("allow_live", AllowLive())), "speed"),
        Tool("camera_stop", "Stop any pan, tilt and zoom on a camera.", P(("camera", CameraP()))),
        Tool("camera_focus", "Focus a camera: once (one-shot autofocus, like the Focus button), auto, or manual.",
            P(("camera", CameraP()), ("mode", Choice("Focus mode.", "once", "auto", "manual")), ("allow_live", AllowLive())), "mode"),
        Tool("get_routing", "Video routing in plain words: what each M/E mix is for and shows, every aux output with the screen it feeds, its wiring note and the picture it gets now, and the volunteer routing buttons (lit or not)."),
        Tool("route_screen", "Send a picture to a screen (all of its linked aux outputs) or to one aux. Changes what that screen shows right away. Screens hidden from volunteers (encoders, recorders, patch bay feeds) need allow_hidden: true; confirm with the person first, since one may feed the live stream.",
            P(("screen", Any("Screen name, or aux number (from 1).")), ("source", Any("Input number or name, a mix name (such as Live stream or Side screens), or M/E 1 / M/E 2 (their program). Add 'preview' for an M/E's preview.")),
              ("allow_hidden", Bool("Required as true to change a screen hidden from volunteers."))), "screen", "source"),
        Tool("run_route_button", "Press one of the volunteer routing buttons, by label.", P(("button", Str("The button's label."))), "button"),
        Tool("find_devices", "Find the switcher and cameras on this computer's network, for first-time setup: ATEM switchers (they answer on UDP 9910), Canon PTZ cameras (their HTTP control page), and everything announcing itself over mDNS/Bonjour (NDI sources, which include most PTZOptics and BirdDog cameras, Blackmagic devices, device web pages), each with its IP address. Takes about 4 seconds and changes nothing. Then confirm with the person which is which and save it with update_config (atemHost, cameras with name, host, proto and atemInput)."),
        Tool("list_ndi_sources", "The NDI sources on the network now, by their exact names, for the ndi field of a volunteer source or key button."),
        Tool("start_tracking", "Auto tracking: make a camera follow a person, by watching its preview picture (a Canon's own, or for other cameras the NDI picture set for their switcher input). It frames them as a broadcast follow shot: shoulders up, waist up or full length (the shot the camera is closest to unless shot is given), eyes near the upper third, lead room on the side they face or walk toward, eased pan and tilt, and slow zooms to hold the size. Picks the person nearest the middle, or nearest x/y if given (0 to 1 across and down the picture), or with person, someone labelled in the last stage scan, by name, wherever they are in the shot. It keeps to that person by the colours of their clothes, so it stays with them when people cross and waits for them after losing sight. Waits up to 4 seconds for someone to be found. Any manual pan, tilt, shot or preset stops tracking; a manual zoom keeps it and holds the new size.",
            P(("camera", CameraP()), ("shot", Choice("Follow shot size.", "shoulders", "waist", "full")), ("x", Num("0 (left) to 1 (right) across the picture.")), ("y", Num("0 (top) to 1 (bottom) down the picture.")), ("person", Str("A name given in the last stage scan.")), ("allow_live", AllowLive()))),
        Tool("scan_stage", "Stage scan, for before a service with everyone in place: each Canon camera that isn't on Live, tracking or out of Churchfront Video's control goes to a wide shot (its shot named Stage or Wide, else where it points now zoomed all the way out), Churchfront Video finds the people in it and works out the shot that frames each one from that camera, and the camera goes back. Returns every person each camera saw, with a number, a small picture, the proposed shot, and a letter: the same letter is a guess (by the colours of their clothes) at the same person on another camera. Nothing is saved until label_stage_scan. This moves cameras, so confirm with the person first.",
            P(("cameras", AnyList("Cameras to scan. Leave out for all.")), ("shot", Choice("How to frame each person. Default waist.", "shoulders", "waist", "full")), ("pictures", Bool("Include each person's picture (default true).")))),
        Tool("label_stage_scan", "Name the people from the last scan_stage and save: a shot per labelled person on each camera that saw them (replacing shots from earlier scans, keeping hand-made ones), and their look, so start_tracking can follow them by name until Churchfront Video quits. Label each number, or a letter to label everyone with that letter at once. Unlabelled people are skipped.",
            P(("labels", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object", ["properties"] = P(("person", Any("A person's number, or a letter.")), ("label", Str("A name or role."))) }, ["description"] = "Who is who." })), "labels"),
        Tool("get_framing", "The follow-shot framing rules auto tracking uses for each shot size (shoulders, waist, full), the built-in standard and any changes, plus the tracking response settings. Use with camera_snapshots to judge framing."),
        Tool("set_framing", "Change how a follow shot is framed, live, while Churchfront Video runs: takes effect on tracking cameras within a frame. All measured as a share of the frame. eye_line: where the eyes sit, down from the top (less = less headroom). size: eyes-to-shoulders drop against frame height (bigger = tighter). lead_room: offset toward where they face or walk. dead_x / dead_y: how far they can drift before the camera moves. reset: true returns this size to the standard.",
            P(("shot", Choice("Shot size.", "shoulders", "waist", "full")), ("eye_line", Num("0.1 to 0.5.")), ("size", Num("Shoulders about 0.36, waist 0.21, full 0.095.")),
              ("lead_room", Num("0 to 0.2.")), ("dead_x", Num("0.02 to 0.2.")), ("dead_y", Num("0.02 to 0.2.")), ("reset", Bool("Back to the standard."))), "shot"),
        Tool("set_tracking_tuning", "Change how auto tracking responds, live. correction: pull toward the mark (default 1.2; higher reacts harder, too high wobbles). latency: seconds the preview runs behind (0.45). walk_threshold: speed in frames a second that counts as walking (0.12; higher ignores more small moves). zoom_threshold: how wrong the shot size must look before zooming (0.3, about a third). zoom_wait: seconds it must stay wrong first (1.5). speed: overall follow speed 0.5 to 4. reset: true returns all to defaults.",
            P(("correction", Num("0.4 to 2.5.")), ("latency", Num("0.2 to 0.8.")), ("walk_threshold", Num("0.05 to 0.4.")), ("zoom_threshold", Num("0.1 to 0.8.")),
              ("zoom_wait", Num("0.5 to 5.")), ("speed", Num("0.5 to 4.")), ("reset", Bool("Back to defaults.")))),
        Tool("set_tracking_shot", "Reframe a tracking camera as a different follow shot: shoulders up, waist up or full length.",
            P(("camera", CameraP()), ("shot", Choice("Follow shot size.", "shoulders", "waist", "full")), ("allow_live", AllowLive())), "shot"),
        Tool("stop_tracking", "Stop auto tracking on a camera, or on every camera with all: true.", P(("camera", CameraP()), ("all", Bool("Stop every camera.")))),
        Tool("camera_home", "Send a camera to its home position (pan and tilt 0).", P(("camera", CameraP()), ("allow_live", AllowLive()))),
        Tool("set_exposure", "Set a camera's gain, shutter, iris and/or white balance. Gain, shutter and iris each switch that setting to manual. Units: gain in tenths of a dB on a Canon (65 = 6.5 dB) or a plain level on a VISCA camera (get_status gives gain_unit), shutter as 1/x (60 = 1/60), iris as f-number times 100 (450 = f/4.5). On a VISCA camera, setting any of the three puts all three in manual, holding the other two where they were. auto: true hands gain, shutter and iris back to the camera (VISCA cameras only for now). White balance: auto, daylight, tungsten, or a color temperature in kelvin (4300); higher kelvin looks warmer. Values snap to the nearest the camera accepts; get_status lists them.",
            P(("camera", CameraP()), ("gain", Int("Tenths of a dB.")), ("shutter", Int("Shutter speed denominator.")), ("iris", Int("f-number times 100.")),
              ("white_balance", Any("auto, daylight, tungsten, or kelvin as a number such as 4300.")),
              ("auto", Bool("Auto exposure: the camera sets gain, shutter and iris itself. Not with gain, shutter or iris.")), ("allow_live", AllowLive()))),
        Tool("camera_snapshots", "A still picture from each camera that has one (a Canon's own, or the NDI picture set for its switcher input), or the ones named, with color measurements, to compare and match them: average red, green and blue, the color cast (red÷green and blue÷green; balanced is about 1), brightness (0 to 255), and the share of the picture clipped to white or crushed to black. Point the cameras at the same scene to compare fairly. Fix differences with set_exposure or match_exposure.",
            P(("cameras", AnyList("Cameras to look at. Leave out for all.")), ("pictures", Bool("Include the pictures themselves (default true). False gives only the measurements.")))),
        Tool("match_exposure", "Make cameras match one camera's exposure: copies gain, shutter, iris and white balance (or only the settings named) from one camera to the others, each value snapped to the nearest the other camera accepts (models differ). Leave out to for every other camera. Cameras on Live are skipped unless allow_live is true.",
            P(("from", Any("The camera to copy from (name, IP or input).")), ("to", AnyList("Cameras to change. Leave out for all the others.")),
              ("settings", new JsonObject { ["type"] = "array", ["items"] = Choice("", "gain", "shutter", "iris", "white_balance"), ["description"] = "Which settings to copy. Leave out for all four." }),
              ("allow_live", AllowLive())), "from"),
        Tool("recall_shot", "Recall a shot saved in Churchfront Video: move to its position, or set its exposure.",
            P(("camera", CameraP()), ("shot", Str("The shot's name.")), ("allow_live", AllowLive())), "shot"),
        Tool("save_shot", "Save the camera as it is now as a Churchfront Video shot: its position (pan, tilt, zoom, the default) or its exposure (gain, shutter, iris).",
            P(("camera", CameraP()), ("name", Str("A short name, such as Pulpit.")), ("kind", Choice("What to save. Default position.", "position", "exposure")),
              ("replace", Bool("true to overwrite an existing shot with this name instead of adding another."))), "name"),
        Tool("rename_shot", "Rename a Churchfront Video shot.", P(("camera", CameraP()), ("shot", Str("Current name.")), ("new_name", Str("New name."))), "shot", "new_name"),
        Tool("delete_shot", "Delete a Churchfront Video shot (from the config file, not the camera).", P(("camera", CameraP()), ("shot", Str("The shot's name."))), "shot"),
        Tool("recall_camera_preset", "Recall a preset stored on the camera itself.",
            P(("camera", CameraP()), ("preset", Any("Preset number or name.")), ("allow_live", AllowLive())), "preset"),
        Tool("delete_camera_preset", "Delete a preset stored on the camera itself, for every controller. Can't be undone. Needs the camera admin login saved in Setup.",
            P(("camera", CameraP()), ("preset", Any("Preset number or name."))), "preset"),

        // Full switcher
        Tool("switcher_set_program", "Full switcher: put a source on program on any M/E.", P(("source", SourceP()), ("me", MeP())), "source"),
        Tool("switcher_set_preview", "Full switcher: put a source on preview on any M/E.", P(("source", SourceP()), ("me", MeP())), "source"),
        Tool("set_transition_style", "Full switcher: transition style.", P(("me", MeP()), ("style", Choice("Style.", "mix", "dip", "wipe", "dve"))), "style"),
        Tool("set_mix_rate", "Full switcher: mix transition length in frames (30 = 1 second at 30 fps).", P(("me", MeP()), ("frames", Int("1 to 250."))), "frames"),
        Tool("set_tbar", "Full switcher: move the T-bar, 0 to 1.", P(("me", MeP()), ("position", Num("0 to 1."))), "position"),
        Tool("set_next_transition", "Full switcher: what the next transition takes: background and/or upstream keys.",
            P(("me", MeP()), ("background", Bool("Include the background.")), ("keys", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "Keys to include, counted from 1." }))),
        Tool("set_upstream_key", "Full switcher: put an upstream key on or off air.", P(("me", MeP()), ("keyer", Int("Key, counted from 1.")), ("on", Bool("On air."))), "keyer", "on"),
        Tool("dsk", "Full switcher: a downstream key. on/off cut it, auto fades it, tie_on/tie_off tie it to the next transition.",
            P(("dsk", Int("DSK, counted from 1.")), ("action", Choice("What to do.", "on", "off", "auto", "tie_on", "tie_off"))), "dsk", "action"),
        Tool("set_aux", "Full switcher: route a source to an aux output.", P(("aux", Int("Aux, counted from 1.")), ("source", SourceP())), "aux", "source"),
        Tool("run_macro", "Full switcher: run a recorded macro.", P(("macro", Any("Macro number (from 1) or name."))), "macro"),
        Tool("stop_macro", "Full switcher: stop the running macro."),
    };

    // MARK: Running a tool

    public sealed class Failure : Exception { public Failure(string m) : base(m) { } }
    private static Failure Fail(string m) => new(m);

    public static void Call(string name, JsonObject a, Action<JsonObject> done)
    {
        var once = 0;
        void Done(JsonObject r) { if (Interlocked.Exchange(ref once, 1) == 0) done(r); }
        try { Run(name, a, Done); }
        catch (Failure f) { Done(new JsonObject { ["error"] = f.Message }); }
        catch (Exception e) { Done(new JsonObject { ["error"] = e.Message }); }
    }

    private static JsonObject Ok(string what, JsonObject? extra = null)
    {
        var o = extra ?? new JsonObject();
        o["ok"] = true; o["done"] = what;
        return o;
    }

    private static JsonObject Warn(PTZCamera c) { var o = new JsonObject(); if (ControlWarning(c) is string w) o["warning"] = w; return o; }

    private static void Run(string name, JsonObject a, Action<JsonObject> done)
    {
        var s = Atem.State;
        if (NotOnWindows.Contains(name)) throw Fail($"{name} isn't available in the Windows version of Churchfront Video yet.");
        switch (name)
        {
            case "get_status": done(Status()); break;
            case "get_config": done(new JsonObject { ["config"] = Store.Config.ToNode(), ["file"] = Store.FilePath }); break;
            case "update_config":
            {
                if (a["config"] is not JsonObject patch) throw Fail("Give config as an object.");
                var obj = Store.Config.ToNode();
                string[] allowed = { "atemHost", "me", "sources", "keys", "cameras", "trackSpeed", "ptzSpeed", "framing", "trackTuning", "trackTilt", "trackZoom", "mixNames", "pictures", "screens", "routes" };
                foreach (var (k, v) in patch) if (allowed.Contains(k)) obj[k] = v?.DeepClone();
                CueConfig c;
                try { c = CueConfig.Parse(obj); } catch (Exception e) { throw Fail($"That config doesn't fit the format: {e.Message}"); }
                Store.Replace(c);
                done(Ok("Config updated", new JsonObject { ["config"] = Store.Config.ToNode() }));
                break;
            }
            case "show_window":
            {
                var w = CueJson.Str(a["window"]) ?? "main";
                ShowWindow?.Invoke(w);
                done(Ok($"Showing {w}"));
                break;
            }

            case "set_next":
            { var m = Me(a); var i = Source(a["source"]); Atem.SetPreview(i, m); done(Ok($"{Store.Label(i, s)} is on Next, M/E {m + 1}")); break; }
            case "set_live":
            case "switcher_set_program":
            { var m = Me(a); var i = Source(a["source"]); Atem.SetProgram(i, m); done(Ok($"{Store.Label(i, s)} is on Live, M/E {m + 1}")); break; }
            case "switcher_set_preview":
            { var m = Me(a); var i = Source(a["source"]); Atem.SetPreview(i, m); done(Ok($"{Store.Label(i, s)} is on preview, M/E {m + 1}")); break; }
            case "cut": { var m = Me(a); Atem.Cut(m); done(Ok($"Cut on M/E {m + 1}")); break; }
            case "fade": { var m = Me(a); Atem.Auto(m); done(Ok($"Fading on M/E {m + 1}")); break; }
            case "fade_to_black": { var m = Me(a); Atem.FadeToBlack(m); done(Ok($"Fade to black on M/E {m + 1}")); break; }
            case "set_key_button":
            {
                var label = (CueJson.Str(a["button"]) ?? "").ToLowerInvariant();
                var k = Store.Config.Keys.FirstOrDefault(x => x.Label.ToLowerInvariant() == label)
                    ?? throw Fail($"No key button called that. Buttons: {string.Join(", ", Store.Config.Keys.Select(x => x.Label))}.");
                var want = CueJson.Bool(a["on"]) ?? !k.IsOn(s);
                if (want != k.IsOn(s)) k.Toggle(Atem);
                done(Ok($"{k.Label} {(want ? "on" : "off")} air"));
                break;
            }
            case "set_recording":
            {
                if (!s.HasRecording) throw Fail("This switcher doesn't record.");
                // Never guess: a call without a clear true or false must not stop the recording.
                if (CueJson.Bool(a["on"]) is not bool on) throw Fail("Give on as true (record) or false (stop). Nothing was changed.");
                Atem.SetRecording(on); done(Ok($"Recording {(on ? "started" : "stopped")}"));
                break;
            }
            case "set_streaming":
            {
                if (!s.HasStreaming) throw Fail("This switcher doesn't stream.");
                if (CueJson.Bool(a["on"]) is not bool on) throw Fail("Give on as true (stream) or false (stop). Nothing was changed.");
                Atem.SetStreaming(on); done(Ok($"Streaming {(on ? "started" : "stopped")}"));
                break;
            }

            case "camera_move":
            {
                var c = Cam(a, changing: true);
                var ms = ClampMs(a["duration_ms"]);
                Ptz.Drive(Dbl(a["pan"]), Dbl(a["tilt"]), c.Id);
                MainThread.After(ms / 1000.0, () => { Ptz.StopDrive(c.Id); done(Ok($"Moved {c.Name} for {ms} ms", Warn(c))); });
                break;
            }
            case "camera_zoom":
            {
                var c = Cam(a, changing: true);
                var ms = ClampMs(a["duration_ms"]);
                Ptz.Zoom(Dbl(a["speed"]), c.Id);
                MainThread.After(ms / 1000.0, () => { Ptz.Zoom(0, c.Id); done(Ok($"Zoomed {c.Name} for {ms} ms", Warn(c))); });
                break;
            }
            case "camera_stop":
            { var c = Cam(a, changing: false); AutoTracker.Shared.Stop(c.Id); Ptz.StopDrive(c.Id); Ptz.Zoom(0, c.Id); done(Ok($"Stopped {c.Name}")); break; }
            case "start_tracking":
            {
                var c = Cam(a, changing: true);
                PointD? pt = a["x"] == null && a["y"] == null ? null : new PointD(CueJson.Double(a["x"]) ?? 0.5, CueJson.Double(a["y"]) ?? 0.5);
                var size = ShotSizes.Parse(CueJson.Str(a["shot"]));
                var person = CueJson.Str(a["person"]) is string pn && pn.Trim().Length > 0 ? pn.Trim() : null;
                AutoTracker.Shared.Start(c, pt, size, person, why =>
                {
                    if (why == null)
                    {
                        var t = AutoTracker.Shared.Sessions.TryGetValue(c.Id, out var ts) ? ts : null;
                        done(Ok($"{c.Name} is following {t?.Person ?? "someone"}, framed {t?.Size.Name().ToLowerInvariant()}", Warn(c)));
                    }
                    else done(new JsonObject { ["error"] = AutoTracker.Shared.IsTracking(c.Id) ? $"{c.Name}: {why} It keeps looking until stop_tracking." : $"{c.Name}: {why}" });
                });
                break;
            }
            case "scan_stage":
            {
                var list = a["cameras"] as JsonArray;
                var cams = list?.Select(x => Cam(new JsonObject { ["camera"] = x?.DeepClone() }, changing: false)).ToList();
                var pictures = CueJson.Bool(a["pictures"]) ?? true;
                var scan = StageScan.Shared;
                if (scan.Running) throw Fail("A stage scan is already running.");
                if (ShotSizes.Parse(CueJson.Str(a["shot"])) is ShotSize z) scan.Size = z;
                scan.Scan(cams, () =>
                {
                    var o = scan.Report();
                    if (pictures)
                    {
                        var imgs = new JsonArray();
                        foreach (var p in scan.People)
                            if (p.Thumb != null)
                                imgs.Add(new JsonObject { ["label"] = $"Person {p.Id} ({p.Letter}) on {p.CameraName}", ["mimeType"] = "image/jpeg", ["data"] = Convert.ToBase64String(p.Thumb) });
                        o["images"] = imgs;
                    }
                    done(o);
                });
                break;
            }
            case "label_stage_scan":
            {
                if (a["labels"] is not JsonArray labels || labels.Count == 0) throw Fail("Give labels as [{person, label}].");
                var scan = StageScan.Shared;
                if (scan.People.Count == 0) throw Fail("There's no scan to label. Run scan_stage first.");
                // Check every one first, so a mistake saves nothing.
                var plan = new List<(List<int> Ids, string Text)>();
                var unknown = new List<string>();
                foreach (var l in labels.OfType<JsonObject>())
                {
                    var text = (CueJson.Str(l["label"]) ?? "").Trim();
                    if (IntValue(l["person"]) is int n && scan.People.Any(p => p.Id == n)) plan.Add((new() { n }, text));
                    else if (CueJson.Str(l["person"])?.Trim().ToUpperInvariant() is string letter && scan.People.Where(p => p.Letter == letter).Select(p => p.Id).ToList() is { Count: > 0 } ids)
                        plan.Add((ids, text));
                    else unknown.Add(l["person"]?.ToString() ?? "?");
                }
                if (unknown.Count > 0) throw Fail($"No one in the scan is {string.Join(", ", unknown)}. Nothing was saved.");
                foreach (var (ids, text) in plan) foreach (var id in ids) scan.Label(id, text);
                var said = scan.Save();
                done(Ok(string.Join(" ", said), scan.Report()));
                break;
            }
            case "set_tracking_shot":
            {
                var z = ShotSizes.Parse(CueJson.Str(a["shot"])) ?? throw Fail("shot is shoulders, waist or full.");
                var c = Cam(a, changing: true);
                if (!AutoTracker.Shared.IsTracking(c.Id)) throw Fail($"{c.Name} isn't tracking. Use start_tracking.");
                AutoTracker.Shared.SetSize(c.Id, z); done(Ok($"{c.Name} framing {z.Name().ToLowerInvariant()}"));
                break;
            }
            case "camera_focus":
            {
                var c = Cam(a, changing: true);
                var mode = CueJson.Str(a["mode"]);
                switch (mode)
                {
                    case "once": Ptz.FocusOnce(c.Id); break;
                    case "auto": Ptz.AutoFocus(true, c.Id); break;
                    case "manual": Ptz.AutoFocus(false, c.Id); break;
                    default: throw Fail("mode is once, auto or manual.");
                }
                done(Ok($"Focus {mode} on {c.Name}"));
                break;
            }
            case "get_routing": done(Routing()); break;
            case "route_screen":
            {
                List<int> auxes; string what; bool hidden;
                if (IntValue(a["screen"]) is int n)
                {
                    if (!s.Aux.ContainsKey(n - 1)) throw Fail($"The switcher has no aux {n}. It has {s.AuxCount}.");
                    auxes = new() { n }; what = Store.Screen(n)?.Name ?? $"Aux {n}"; hidden = !(Store.Screen(n)?.ForVolunteers ?? true);
                }
                else
                {
                    var q = (CueJson.Str(a["screen"]) ?? "").Trim().ToLowerInvariant();
                    var sc = q.Length == 0 ? null : Store.Config.Screens.FirstOrDefault(x => x.Name.ToLowerInvariant() == q)
                        ?? OnlyOne(Store.Config.Screens.Where(x => x.Name.ToLowerInvariant().Contains(q)).ToList(), q, "screen", x => x.Name);
                    if (sc == null) throw Fail($"No screen called {Text(a["screen"])}. Screens: {string.Join(", ", Store.Config.Screens.Select(x => x.Name))}, or use an aux number.");
                    auxes = sc.Aux; what = sc.Name; hidden = !sc.ForVolunteers;
                }
                if (hidden && CueJson.Bool(a["allow_hidden"]) != true) throw Fail($"{what} is hidden from volunteers (it may feed an encoder or the stream). Send allow_hidden: true to change it.");
                var src = RouteSource(a["source"]);
                foreach (var x in auxes) Atem.SetAux(x - 1, src);
                done(Ok($"{what} now shows {Store.RouteLabel(src, s)}"));
                break;
            }
            case "run_route_button":
            {
                var q = (CueJson.Str(a["button"]) ?? "").ToLowerInvariant();
                var r = q.Length == 0 ? null : Store.Config.Routes.FirstOrDefault(x => x.Label.ToLowerInvariant() == q)
                    ?? OnlyOne(Store.Config.Routes.Where(x => x.Label.ToLowerInvariant().Contains(q)).ToList(), q, "routing button", x => x.Label);
                if (r == null) throw Fail($"No routing button called {Text(a["button"])}. Buttons: {string.Join(", ", Store.Config.Routes.Select(x => x.Label))}.");
                r.Run(Atem);
                done(Ok($"Ran {r.Label}: {Store.RouteDetail(r, s).Replace("\n", "; ")}"));
                break;
            }
            case "list_ndi_sources":
                NdiDirectory.Scan(() =>
                {
                    if (!NdiDirectory.HasRuntime) { done(new JsonObject { ["error"] = "NDI isn't installed on this computer. Install NDI Tools (it includes the NDI runtime)." }); return; }
                    var list = new JsonArray(); foreach (var n in NdiDirectory.Sources) list.Add(n);
                    done(new JsonObject { ["sources"] = list });
                });
                break;
            case "find_devices":
                DeviceFinder.Find(3, (list, nets) => MainThread.Post(() =>
                {
                    var devs = new JsonArray();
                    foreach (var f in list) devs.Add(new JsonObject { ["kind"] = f.Kind, ["name"] = f.Name, ["host"] = f.Host });
                    var netsA = new JsonArray(); foreach (var n in nets) netsA.Add(n);
                    done(new JsonObject
                    {
                        ["networks"] = netsA, ["devices"] = devs,
                        ["note"] = list.Count == 0
                            ? "Nothing answered. Check this computer is on the same network as the switcher and cameras, and that Windows Firewall allows Churchfront Video on private networks."
                            : "Ask the person to confirm which device is which before saving.",
                    });
                }));
                break;
            case "get_framing": done(FramingReport()); break;
            case "set_framing":
            {
                var z = ShotSizes.Parse(CueJson.Str(a["shot"])) ?? throw Fail("shot is shoulders, waist or full.");
                double? Clamp(JsonNode? v, double lo, double hi) => CueJson.Double(v) is double d ? Math.Clamp(d, lo, hi) : null;
                Store.Update(c =>
                {
                    if (CueJson.Bool(a["reset"]) == true) c.Framing.Remove(z.Key());
                    var r = c.Framing.TryGetValue(z.Key(), out var x) ? x : new FramingRule();
                    if (Clamp(a["eye_line"], 0.1, 0.5) is double e) r.EyeLine = e;
                    if (Clamp(a["size"], 0.04, 0.7) is double sz) r.Scale = sz;
                    if (Clamp(a["lead_room"], 0, 0.2) is double l) r.LeadRoom = l;
                    if (Clamp(a["dead_x"], 0.02, 0.2) is double dx) r.DeadX = dx;
                    if (Clamp(a["dead_y"], 0.02, 0.2) is double dy) r.DeadY = dy;
                    if (r.IsEmpty) c.Framing.Remove(z.Key()); else c.Framing[z.Key()] = r;
                });
                // Tracking cameras on this size reframe to it now.
                foreach (var (id, t) in AutoTracker.Shared.Sessions.ToList()) if (t.Size == z && t.Asked != null) AutoTracker.Shared.SetSize(id, z);
                done(Ok($"{z.Name()} framing updated", FramingReport()));
                break;
            }
            case "set_tracking_tuning":
            {
                double? Clamp(JsonNode? v, double lo, double hi) => CueJson.Double(v) is double d ? Math.Clamp(d, lo, hi) : null;
                Store.Update(c =>
                {
                    if (CueJson.Bool(a["reset"]) == true) c.TrackTuning = new TrackTuning();
                    if (Clamp(a["correction"], 0.4, 2.5) is double v1) c.TrackTuning.Correction = v1;
                    if (Clamp(a["latency"], 0.2, 0.8) is double v2) c.TrackTuning.Latency = v2;
                    if (Clamp(a["walk_threshold"], 0.05, 0.4) is double v3) c.TrackTuning.WalkThreshold = v3;
                    if (Clamp(a["zoom_threshold"], 0.1, 0.8) is double v4) c.TrackTuning.ZoomThreshold = v4;
                    if (Clamp(a["zoom_wait"], 0.5, 5) is double v5) c.TrackTuning.ZoomWait = v5;
                    if (Clamp(a["speed"], 0.5, 4) is double v6) c.TrackSpeed = v6;
                });
                done(Ok("Tracking tuning updated", FramingReport()));
                break;
            }
            case "stop_tracking":
            {
                if (CueJson.Bool(a["all"]) == true) { AutoTracker.Shared.StopAll(); done(Ok("Stopped tracking on every camera")); break; }
                var c = Cam(a, changing: false);
                AutoTracker.Shared.Stop(c.Id); done(Ok($"{c.Name} stopped tracking"));
                break;
            }
            case "camera_home": { var c = Cam(a, changing: true); Ptz.Home(c.Id); done(Ok($"{c.Name} going home")); break; }
            case "set_exposure":
            {
                var c = Cam(a, changing: true);
                var x = Ptz.Exposure.TryGetValue(c.Id, out var xe) ? xe : new CameraExposure();
                var e = new PTZExposure { Gain = Snap(a["gain"], x.GainList), Shutter = Snap(a["shutter"], x.ShutterList), Iris = Snap(a["iris"], x.IrisList) };
                if (a["white_balance"] is JsonNode w)
                {
                    if ((Snap(w, x.KelvinList) ?? IntValue(w)) is int k) { e.Wb = "kelvin"; e.Kelvin = k; }
                    else if (CueJson.Str(w)?.ToLowerInvariant() is string m && (m == "auto" || m == "daylight" || m == "tungsten")) e.Wb = m;
                    else throw Fail("white_balance is auto, daylight, tungsten, or a kelvin number such as 4300.");
                }
                var auto = CueJson.Bool(a["auto"]) == true;
                if (auto)
                {
                    if (e.HasExposure) throw Fail("Give auto, or gain, shutter and iris, not both.");
                    if (!x.HasAutoMode) throw Fail($"{c.Name} has no single auto exposure mode Churchfront Video can set. That works on VISCA cameras.");
                    Ptz.AutoExposure(c.Id);
                }
                if (!auto && !e.HasExposure && !e.HasWhiteBalance) throw Fail("Give gain, shutter, iris, white_balance and/or auto.");
                if (e.HasExposure || e.HasWhiteBalance) Ptz.SetExposure(e, c.Id);
                var sent = Expo(e, x.GainIsLevel);
                if (auto) sent["exposure"] = "auto";
                done(Ok($"Exposure set on {c.Name}", new JsonObject { ["sent"] = sent }));
                break;
            }
            case "camera_snapshots":
            {
                var cams = a["cameras"] is JsonArray list ? list.Select(v => Cam(new JsonObject { ["camera"] = v?.DeepClone() }, changing: false)).ToList()
                    : Ptz.Cameras.Where(Snapshots.HasPicture).ToList();
                var pictures = CueJson.Bool(a["pictures"]) ?? true;
                Snapshots.Take(cams, pictures, results => MainThread.Post(() =>
                {
                    var info = new JsonArray(); var images = new JsonArray();
                    foreach (var r in results) { info.Add(r.Info); if (r.Image != null) images.Add(r.Image); }
                    var o = new JsonObject { ["cameras"] = info };
                    if (pictures) o["images"] = images;
                    done(o);
                }));
                break;
            }
            case "match_exposure":
            {
                var src = Cam(new JsonObject { ["camera"] = a["from"]?.DeepClone() }, changing: false);
                if (!Ptz.Exposure.TryGetValue(src.Id, out var sx)) throw Fail($"{src.Name} hasn't reported its exposure yet. Try again in a moment.");
                var x = sx.Value;
                var want = a["settings"] is JsonArray st ? st.Select(v => CueJson.Str(v) ?? "").ToHashSet() : new HashSet<string> { "gain", "shutter", "iris", "white_balance" };
                var targets = a["to"] is JsonArray tl ? tl.Select(v => Cam(new JsonObject { ["camera"] = v?.DeepClone() }, changing: false)).ToList()
                    : Ptz.Cameras.Where(c => c.Id != src.Id && c.Host.Length > 0).ToList();
                var changed = new JsonObject(); var skipped = new JsonArray();
                foreach (var t in targets)
                {
                    if (t.Id == src.Id) continue;
                    if (t.AtemInput is int i && s.OnLive(i) && CueJson.Bool(a["allow_live"]) != true) { skipped.Add($"{t.Name} (on Live)"); continue; }
                    var tx = Ptz.Exposure.TryGetValue(t.Id, out var te) ? te : new CameraExposure();
                    var e = new PTZExposure();
                    // Gain only carries between cameras that count it the same way (dB or levels).
                    if (want.Contains("gain") && x.Gain is int g && tx.GainIsLevel == sx.GainIsLevel) e.Gain = Snap(g, tx.GainList);
                    if (want.Contains("shutter") && x.Shutter is int sh) e.Shutter = Snap(sh, tx.ShutterList);
                    if (want.Contains("iris") && x.Iris is int ir) e.Iris = Snap(ir, tx.IrisList);
                    if (want.Contains("white_balance"))
                    {
                        if ((x.Wb == "kelvin" || x.Wb == null) && x.Kelvin is int k) { e.Wb = "kelvin"; e.Kelvin = Snap(k, tx.KelvinList); }
                        else if (x.Wb is string m) e.Wb = m;
                    }
                    if (!e.HasExposure && !e.HasWhiteBalance) continue;
                    Ptz.SetExposure(e, t.Id);
                    changed[t.Name] = Expo(e, tx.GainIsLevel);
                }
                done(Ok($"Matched {changed.Count} camera{(changed.Count == 1 ? "" : "s")} to {src.Name}",
                    new JsonObject { ["source"] = Expo(x, sx.GainIsLevel), ["changed"] = changed, ["skipped"] = skipped }));
                break;
            }
            case "recall_shot":
            { var c = Cam(a, changing: true); var sh = Shot(c, a["shot"]); Ptz.Go(sh, c.Id); done(Ok($"{c.Name}: {sh.Name}")); break; }
            case "save_shot":
            {
                var c = Cam(a, changing: false);
                var nm = (CueJson.Str(a["name"]) ?? "").Trim();
                if (nm.Length == 0) throw Fail("Give the shot a name.");
                var kind = string.Equals(CueJson.Str(a["kind"]), "exposure", StringComparison.OrdinalIgnoreCase) ? ShotKind.Exposure : ShotKind.Position;
                var existing = CueJson.Bool(a["replace"]) == true ? c.Shots.FirstOrDefault(x => x.Name.ToLowerInvariant() == nm.ToLowerInvariant()) : null;
                Ptz.SaveShot(nm, c.Id, kind, existing?.Id, worked =>
                    done(worked ? Ok($"Saved {kind.ToString().ToLowerInvariant()} shot {nm} on {c.Name}")
                                : new JsonObject { ["error"] = $"{c.Name} didn't report its settings, so nothing was saved." }));
                break;
            }
            case "rename_shot":
            {
                var c = Cam(a, changing: false); var sh = Shot(c, a["shot"]);
                var n = (CueJson.Str(a["new_name"]) ?? "").Trim();
                if (n.Length == 0) throw Fail("Give new_name.");
                EditShots(c, shots => { var x = shots.FirstOrDefault(y => y.Id == sh.Id); if (x != null) x.Name = n; });
                done(Ok($"Renamed {sh.Name} to {n}"));
                break;
            }
            case "delete_shot":
            {
                var c = Cam(a, changing: false); var sh = Shot(c, a["shot"]);
                EditShots(c, shots => shots.RemoveAll(y => y.Id == sh.Id));
                done(Ok($"Deleted {sh.Name} from {c.Name}"));
                break;
            }
            case "recall_camera_preset":
            {
                var c = Cam(a, changing: true); var n = Preset(c, a["preset"]);
                Ptz.Recall(n, c.Id);
                done(Ok($"{c.Name}: preset {n} {(Ptz.PresetNames(c).TryGetValue(n, out var pn) ? pn : "")}".TrimEnd()));
                break;
            }
            case "delete_camera_preset":
            {
                var c = Cam(a, changing: false); var n = Preset(c, a["preset"]);
                var label = Ptz.PresetNames(c).TryGetValue(n, out var pn) ? pn : $"preset {n}";
                Ptz.DeleteCameraPreset(n, c.Id, why => done(why == null ? Ok($"Deleted {label} from {c.Name}") : new JsonObject { ["error"] = why }));
                break;
            }

            case "set_transition_style":
            {
                var styles = new Dictionary<string, int> { ["mix"] = 0, ["dip"] = 1, ["wipe"] = 2, ["dve"] = 3 };
                var st = CueJson.Str(a["style"]) ?? "";
                if (!styles.TryGetValue(st, out var v)) throw Fail("style is mix, dip, wipe or dve.");
                var m = Me(a); Atem.SetStyle(v, m); done(Ok($"Style {st} on M/E {m + 1}"));
                break;
            }
            case "set_mix_rate":
            {
                var m = Me(a); var f = (int)Dbl(a["frames"]);
                if (f < 1 || f > 250) throw Fail("frames is 1 to 250.");
                Atem.SetMixRate(f, m); done(Ok($"Mix rate {f} frames on M/E {m + 1}"));
                break;
            }
            case "set_tbar":
            {
                var m = Me(a); var p = Dbl(a["position"]);
                Atem.SetPosition(Math.Clamp(p, 0, 1), m); done(Ok($"T-bar at {(int)(p * 100)}% on M/E {m + 1}"));
                break;
            }
            case "set_next_transition":
            {
                var m = Me(a);
                var mask = (CueJson.Bool(a["background"]) ?? true) ? 1 : 0;
                if (a["keys"] is JsonArray ks) foreach (var k in ks) { var n = (int)(CueJson.Double(k) ?? 0); if (n >= 1) mask |= 2 << (n - 1); }
                if (mask == 0) throw Fail("Include the background or at least one key.");
                Atem.SetNextSelection(mask, m); done(Ok($"Next transition set on M/E {m + 1}"));
                break;
            }
            case "set_upstream_key":
            {
                var m = Me(a); var k = (int)Dbl(a["keyer"]);
                if (k < 1 || k > Math.Max(1, s.KeyerCount)) throw Fail($"keyer is 1 to {s.KeyerCount}.");
                var on = CueJson.Bool(a["on"]) ?? false;
                Atem.SetKeyOnAir(k - 1, on, m); done(Ok($"M/E {m + 1} key {k} {(on ? "on" : "off")} air"));
                break;
            }
            case "dsk":
            {
                var d = (int)Dbl(a["dsk"]);
                if (d < 1 || d > Math.Max(1, s.DskCount)) throw Fail($"dsk is 1 to {s.DskCount}.");
                var act = CueJson.Str(a["action"]);
                switch (act)
                {
                    case "on": Atem.SetDSKOnAir(d - 1, true); break;
                    case "off": Atem.SetDSKOnAir(d - 1, false); break;
                    case "auto": Atem.DskAuto(d - 1); break;
                    case "tie_on": Atem.SetDSKTie(d - 1, true); break;
                    case "tie_off": Atem.SetDSKTie(d - 1, false); break;
                    default: throw Fail("action is on, off, auto, tie_on or tie_off.");
                }
                done(Ok($"DSK {d}: {act}"));
                break;
            }
            case "set_aux":
            {
                var x = (int)Dbl(a["aux"]); var i = Source(a["source"], aux: true);
                if (x < 1 || x > Math.Max(1, s.AuxCount)) throw Fail($"aux is 1 to {s.AuxCount}.");
                Atem.SetAux(x - 1, i); done(Ok($"Aux {x}: {(s.Inputs.TryGetValue(i, out var inp) ? inp.Long : i.ToString())}"));
                break;
            }
            case "run_macro":
            {
                int i;
                if (IntValue(a["macro"]) is int n) i = n - 1;
                else if (CueJson.Str(a["macro"]) is string mn && s.Macros.FirstOrDefault(kv => kv.Value.ToLowerInvariant() == mn.ToLowerInvariant()) is { Value: not null } hit) i = hit.Key;
                else throw Fail($"No macro by that number or name. Macros: {string.Join(", ", s.Macros.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key + 1} {kv.Value}"))}");
                if (!s.Macros.ContainsKey(i)) throw Fail($"There's no macro {i + 1}.");
                Atem.RunMacro(i); done(Ok($"Running macro {i + 1} {s.Macros[i]}"));
                break;
            }
            case "stop_macro": Atem.StopMacro(); done(Ok("Stopped the macro")); break;
            default: throw Fail($"No tool called {name}.");
        }
    }

    // MARK: Lookups

    private static int? IntValue(JsonNode? v) => v is JsonValue x && (x.TryGetValue<bool>(out _)) ? null : CueJson.Int(v);
    private static double Dbl(JsonNode? v) => CueJson.Double(v) ?? 0;
    private static int ClampMs(JsonNode? v) => Math.Clamp(IntValue(v) ?? 500, 50, 5000);
    private static string Text(JsonNode? v) => v == null ? "" : CueJson.Str(v) ?? v.ToJsonString();

    private static int Me(JsonObject a)
    {
        if (IntValue(a["me"]) is not int m) return B.Me;
        if (m < 1 || m > Atem.State.MeCount) throw Fail($"me is 1 to {Atem.State.MeCount}.");
        return m - 1;
    }

    private static int Source(JsonNode? v, bool aux = false)
    {
        var s = Atem.State;
        var pool = aux ? s.AuxSources : s.BusSources;
        if (IntValue(v) is int n)
        {
            if (!s.Inputs.ContainsKey(n)) throw Fail($"The switcher has no input {n}.");
            return n;
        }
        var q = (CueJson.Str(v) ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0) throw Fail("Name a source.");
        if (Store.Config.Sources.FirstOrDefault(x => x.Label?.ToLowerInvariant() == q) is VolunteerSource hit) return hit.Input;
        if (Store.Config.Cameras.FirstOrDefault(c => c.Name.ToLowerInvariant() == q) is { AtemInput: int ci }) return ci;
        if (pool.FirstOrDefault(i => i.Long.ToLowerInvariant() == q || i.Short.ToLowerInvariant() == q) is ATEMInput ex) return ex.Id;
        if (OnlyOne(pool.Where(i => i.Long.ToLowerInvariant().Contains(q)).ToList(), q, "source", i => i.Long) is ATEMInput part) return part.Id;
        throw Fail($"No source called {Text(v)}. Use get_status to see the sources.");
    }

    /// <summary>A part of a name is accepted only when it fits exactly one thing, so a loose
    /// name can't put the wrong picture on Live or on a screen.</summary>
    private static T? OnlyOne<T>(List<T> hits, string asked, string what, Func<T, string> name) where T : class
    {
        if (hits.Count <= 1) return hits.FirstOrDefault();
        throw Fail($"\"{asked}\" fits more than one {what}: {string.Join(", ", hits.Select(name))}. Use the full name. Nothing was changed.");
    }

    private static PTZCamera Cam(JsonObject a, bool changing)
    {
        PTZCamera c;
        if (a["camera"] is JsonNode v)
        {
            var q = Text(v).Trim().ToLowerInvariant();
            PTZCamera? hit = Ptz.Cameras.FirstOrDefault(x => x.Name.ToLowerInvariant() == q || x.Host == q);
            if (hit == null && IntValue(v) is int i) hit = Ptz.Cameras.FirstOrDefault(x => x.AtemInput == i);
            if (hit == null) { try { var si = Source(v); hit = Ptz.Cameras.FirstOrDefault(x => x.AtemInput == si); } catch (Failure) { } }
            c = hit ?? throw Fail($"No camera called {Text(v)}. Cameras: {string.Join(", ", Ptz.Cameras.Select(x => x.Name))}.");
        }
        else
        {
            var next = Atem.State.PreviewOf(B.Me);
            c = Ptz.Cameras.FirstOrDefault(x => x.AtemInput == next && x.Host.Length > 0) ?? throw Fail("No camera is on Next. Name one with camera.");
        }
        if (changing && c.AtemInput is int ai && Atem.State.OnLive(ai) && CueJson.Bool(a["allow_live"]) != true)
            throw Fail($"{c.Name} is on Live. Send allow_live: true to change it anyway.");
        return c;
    }

    private static PTZShot Shot(PTZCamera c, JsonNode? v)
    {
        var q = Text(v).ToLowerInvariant();
        return c.Shots.FirstOrDefault(s => s.Name.ToLowerInvariant() == q)
            ?? throw Fail($"{c.Name} has no shot called {Text(v)}. Shots: {string.Join(", ", c.Shots.Select(s => s.Name))}.");
    }

    private static int Preset(PTZCamera c, JsonNode? v)
    {
        var names = Ptz.PresetNames(c);
        if (IntValue(v) is int n) return n;
        var q = (CueJson.Str(v) ?? "").ToLowerInvariant();
        foreach (var (k, nm) in names) if (nm.ToLowerInvariant() == q) return k;
        throw Fail($"{c.Name} has no preset called {Text(v)}. Presets: {string.Join(", ", names.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))}.");
    }

    private static void EditShots(PTZCamera c, Action<List<PTZShot>> f)
    {
        if (Ptz.Camera(c.Id) == null) throw Fail("That camera is gone.");
        Ptz.Edit(list => f(list.First(x => x.Id == c.Id).Shots));
    }

    /// <summary>The accepted value nearest to the one asked for.</summary>
    private static int? Snap(JsonNode? v, List<int> list) => IntValue(v) is int n ? Snap(n, list) : null;
    private static int Snap(int n, List<int> list) => list.Count == 0 ? n : list.MinBy(x => Math.Abs(x - n));

    private static string? ControlWarning(PTZCamera c) =>
        Ptz.HasControl(c) ? null : $"{c.Name} isn't under Churchfront Video's control right now (another controller has it), so the camera may have ignored this.";

    // MARK: Status

    private static JsonObject Expo(PTZExposure e, bool level = false)
    {
        var d = new JsonObject();
        if (e.Gain is int g) { d["gain"] = g; d["gain_text"] = level ? $"Level {g}" : CameraExposure.GainText(g); }
        if (e.Shutter is int s) { d["shutter"] = s; d["shutter_text"] = CameraExposure.ShutterText(s); }
        if (e.Iris is int i) { d["iris"] = i; d["iris_text"] = CameraExposure.IrisText(i); }
        if (e.HasWhiteBalance) d["white_balance"] = CameraExposure.WbText(e.Wb, e.Kelvin);
        return d;
    }

    /// <summary>A routing source from a number, a mix name, "M/E 2" (with "preview"), or any source name.</summary>
    private static int RouteSource(JsonNode? v)
    {
        if (IntValue(v) is int n)
        {
            if (!Atem.State.Inputs.ContainsKey(n)) throw Fail($"The switcher has no source {n}.");
            return n;
        }
        var q = (CueJson.Str(v) ?? "").Trim().ToLowerInvariant();
        var preview = q.Contains("preview");
        var bas = q.Replace("preview", "").Trim();
        for (int i = 0; i < Store.Config.MixNames.Count; i++)
        {
            var mn = Store.Config.MixNames[i];
            if (mn.Length > 0 && mn.ToLowerInvariant() == bas) return 10000 + (i + 1) * 10 + (preview ? 1 : 0);
        }
        var digits = new string(bas.Where(char.IsDigit).ToArray());
        if ((bas.StartsWith("m/e") || bas.StartsWith("me")) && int.TryParse(digits, out var m) && m >= 1) return 10000 + m * 10 + (preview ? 1 : 0);
        return Source(v, aux: true);
    }

    public static JsonObject FramingReport()
    {
        JsonObject D(FramingRule r) => new()
        {
            ["eye_line"] = r.EyeLine ?? 0, ["size"] = r.Scale ?? 0, ["lead_room"] = r.LeadRoom ?? 0, ["dead_x"] = r.DeadX ?? 0, ["dead_y"] = r.DeadY ?? 0,
        };
        var cfg = Store.Config;
        var shots = new JsonObject();
        foreach (var z in ShotSizes.All)
            shots[z.Key()] = new JsonObject { ["name"] = z.Name(), ["now"] = D(z.Rule(cfg)), ["standard"] = D(z.Standard()), ["changed"] = cfg.Framing.ContainsKey(z.Key()) };
        var t = cfg.TrackTuning;
        return new JsonObject
        {
            ["framing"] = shots,
            ["tracking"] = new JsonObject
            {
                ["speed"] = cfg.TrackSpeed, ["tilt"] = cfg.TrackTilt, ["zoom_to_hold_size"] = cfg.TrackZoom,
                ["correction"] = t.Correction ?? 1.2, ["latency"] = t.Latency ?? 0.45, ["walk_threshold"] = t.WalkThreshold ?? 0.12,
                ["zoom_threshold"] = t.ZoomThreshold ?? 0.3, ["zoom_wait"] = t.ZoomWait ?? 1.5,
            },
        };
    }

    public static JsonObject Routing()
    {
        var s = Atem.State;
        var mixes = new JsonArray();
        foreach (var me in s.Program.Keys.OrderBy(x => x))
            mixes.Add(new JsonObject
            {
                ["me"] = me + 1, ["for"] = me < Store.Config.MixNames.Count ? Store.Config.MixNames[me] : "",
                ["live"] = Store.Label(s.ProgramOf(me), s), ["next"] = Store.Label(s.PreviewOf(me), s), ["volunteer_mix"] = me == B.Me,
            });
        var outs = new JsonArray();
        foreach (var i in s.Aux.Keys.OrderBy(x => x))
        {
            var sc = Store.Screen(i + 1);
            outs.Add(new JsonObject
            {
                ["aux"] = i + 1, ["screen"] = sc?.Name ?? "", ["note"] = sc?.Note ?? "", ["for_volunteers"] = sc?.ForVolunteers ?? false,
                ["showing"] = new JsonObject { ["source"] = s.Aux[i], ["name"] = Store.RouteLabel(s.Aux[i], s) },
            });
        }
        var buttons = new JsonArray();
        foreach (var r in Store.Config.Routes)
        {
            var sets = new JsonArray(); foreach (var l in Store.RouteDetail(r, s).Split('\n')) sets.Add(l);
            buttons.Add(new JsonObject { ["label"] = r.Label, ["on"] = r.IsActive(s), ["sets"] = sets });
        }
        return new JsonObject { ["mixes"] = mixes, ["outputs"] = outs, ["buttons"] = buttons };
    }

    public static string LinkText(ATEMLink l) => l.Kind switch
    {
        LinkKind.Online => "online",
        LinkKind.Connecting => "connecting",
        LinkKind.Offline => $"offline: {l.Reason}",
        _ => "not set up",
    };

    public static JsonObject Status()
    {
        var s = Atem.State;
        JsonNode? Src(int? i) => i is int x ? new JsonObject { ["input"] = x, ["name"] = Store.Label(x, s) } : null;
        var mes = new JsonArray();
        for (int m = 0; m < s.MeCount; m++)
        {
            var keys = new JsonArray();
            if (s.KeyFill.TryGetValue(m, out var kf))
                foreach (var k in kf.Keys.OrderBy(x => x))
                    keys.Add(new JsonObject
                    {
                        ["keyer"] = k + 1, ["on_air"] = s.KeyOnAir.TryGetValue(m, out var ko) && ko.TryGetValue(k, out var on) && on,
                        ["fill"] = Src(kf[k]), ["key"] = Src(s.KeyCut.TryGetValue(m, out var kc) && kc.TryGetValue(k, out var cut) ? cut : null),
                    });
            string[] styles = { "mix", "dip", "wipe", "dve", "sting" };
            mes.Add(new JsonObject
            {
                ["me"] = m + 1, ["live"] = Src(s.ProgramOf(m)), ["next"] = Src(s.PreviewOf(m)), ["in_transition"] = s.Get(s.InTransition, m),
                ["faded_to_black"] = s.Get(s.FtbBlack, m), ["style"] = styles[Math.Clamp(s.Style.TryGetValue(m, out var st) ? st : 0, 0, 4)],
                ["mix_rate_frames"] = s.MixRate.TryGetValue(m, out var mr) ? mr : 0, ["keys"] = keys,
            });
        }
        var dsks = new JsonArray();
        foreach (var d in s.DskFill.Keys.OrderBy(x => x))
            dsks.Add(new JsonObject
            {
                ["dsk"] = d + 1, ["on_air"] = s.Get(s.DskOnAir, d), ["tie"] = s.Get(s.DskTie, d), ["fill"] = Src(s.DskFill[d]),
                ["key"] = Src(s.DskCut.TryGetValue(d, out var dc) ? dc : null),
            });
        var cams = new JsonArray();
        foreach (var c in Ptz.Cameras)
        {
            Ptz.Exposure.TryGetValue(c.Id, out var x);
            var presets = new JsonArray();
            foreach (var (n, nm) in Ptz.PresetNames(c).OrderBy(kv => kv.Key))
                presets.Add(new JsonObject { ["number"] = n, ["name"] = nm, ["kind"] = Ptz.ExposurePresets.TryGetValue(c.Id, out var ep) && ep.Contains(n) ? "exposure" : "position" });
            var shots = new JsonArray();
            foreach (var sh in c.Shots)
            {
                if (sh.Kind == ShotKind.Position) shots.Add(new JsonObject { ["name"] = sh.Name, ["kind"] = "position", ["pan"] = sh.Pan, ["tilt"] = sh.Tilt, ["zoom"] = sh.Zoom });
                else { var o = Expo(sh.Exposure ?? new PTZExposure()); o["name"] = sh.Name; o["kind"] = "exposure"; shots.Add(o); }
            }
            var d = new JsonObject
            {
                ["name"] = c.Name, ["host"] = c.Host, ["protocol"] = c.Proto.Key(), ["atem_input"] = c.AtemInput,
                ["answering"] = Ptz.Online.Contains(c.Id), ["has_control"] = Ptz.HasControl(c), ["model"] = Ptz.Models.TryGetValue(c.Id, out var md) ? md : "",
                ["on_live"] = c.AtemInput is int li && s.OnLive(li), ["on_next"] = c.AtemInput is int ni && s.OnNext(ni),
                ["shots"] = shots, ["camera_presets"] = presets, ["tracking"] = AutoTracker.Shared.Report(c.Id),
            };
            if (x != null)
            {
                var e = Expo(x.Value, x.GainIsLevel);
                e["gain_unit"] = x.GainIsLevel ? "level" : "tenths of a dB"; e["can_auto"] = x.HasAutoMode;
                e["gain_auto"] = x.GainAuto; e["shutter_auto"] = x.ShutterAuto; e["iris_auto"] = x.IrisAuto;
                e["gain_values"] = Arr(x.GainList); e["shutter_values"] = Arr(x.ShutterList); e["iris_values"] = Arr(x.IrisList);
                e["white_balance_mode"] = x.Value.Wb ?? ""; e["kelvin_values"] = Arr(x.KelvinList);
                d["exposure"] = e;
            }
            cams.Add(d);
        }
        var aux = new JsonArray(); foreach (var i in s.Aux.Keys.OrderBy(k => k)) aux.Add(new JsonObject { ["aux"] = i + 1, ["source"] = Src(s.Aux[i]) });
        var macros = new JsonArray(); foreach (var i in s.Macros.Keys.OrderBy(k => k)) macros.Add(new JsonObject { ["macro"] = i + 1, ["name"] = s.Macros[i] });
        var sources = new JsonArray(); foreach (var i in s.BusSources) sources.Add(new JsonObject { ["input"] = i.Id, ["short"] = i.Short, ["long"] = i.Long });
        var vs = new JsonArray(); foreach (var v in Store.VolunteerSources(s)) vs.Add(new JsonObject { ["input"] = v.Input, ["label"] = Store.Label(v.Input, s) });
        var kb = new JsonArray(); foreach (var k in Store.Config.Keys) kb.Add(new JsonObject { ["label"] = k.Label, ["target"] = k.Target, ["on_air"] = k.IsOn(s) });
        return new JsonObject
        {
            ["switcher"] = new JsonObject
            {
                ["connection"] = LinkText(Atem.Link), ["host"] = Atem.Host, ["model"] = s.Product, ["protocol"] = s.Version,
                ["volunteer_me"] = B.Me + 1, ["mes"] = mes, ["dsks"] = dsks, ["aux"] = aux, ["macros"] = macros,
                ["macro_running"] = s.MacroRunning is int running ? running + 1 : null,
                ["recording"] = s.HasRecording ? s.Recording : null,
                ["streaming"] = s.HasStreaming ? s.Streaming : null,
                ["sources"] = sources,
            },
            ["volunteer"] = new JsonObject { ["sources"] = vs, ["key_buttons"] = kb },
            ["ndi_previews"] = NdiPreviews(s),
            ["ndi_runtime"] = Ndi.Loaded ?? (NdiDirectory.HasRuntime ? "not loaded yet" : "not installed (install NDI Tools)"),
            ["cameras"] = cams,
            ["stage_scan_names"] = new JsonArray(StageCast.Shared.Names.Select(n => (JsonNode?)n).ToArray()),
            ["platform"] = "windows",
            ["auto_tracking"] = AutoTracker.Shared.Detector != null ? "ready" : AutoTracker.Shared.Unavailable ?? "not set up",
            ["config_file"] = Store.FilePath,
        };
    }

    /// <summary>Every NDI picture set up, and whether it's coming in.</summary>
    private static JsonArray NdiPreviews(ATEMState s)
    {
        var a = new JsonArray();
        JsonNode Receiving(string n) => NdiHub.IsLive(n) is bool b ? b : "not shown now";
        foreach (var src in Store.Config.Sources.Where(x => !string.IsNullOrEmpty(x.Ndi)))
        {
            var f = NdiHub.Feed(src.Ndi!);
            a.Add(new JsonObject { ["for"] = Store.Label(src.Input, s), ["source"] = src.Ndi, ["receiving"] = Receiving(src.Ndi!), ["alpha"] = f.HasAlpha, ["picture"] = f.Shape.Length > 0 ? f.Shape : null });
        }
        foreach (var (input, n) in Store.Config.Pictures.Where(kv => kv.Value.Length > 0))
            a.Add(new JsonObject { ["for"] = int.TryParse(input, out var i) ? Store.Label(i, s) : input, ["source"] = n, ["receiving"] = Receiving(n) });
        foreach (var k in Store.Config.Keys.Where(x => !string.IsNullOrEmpty(x.Ndi)))
            a.Add(new JsonObject { ["for"] = k.Label, ["source"] = k.Ndi, ["receiving"] = Receiving(k.Ndi!), ["alpha"] = NdiHub.Feed(k.Ndi!).HasAlpha, ["on_live"] = k.IsOn(s), ["on_next"] = k.OnNext(s) });
        return a;
    }

    private static JsonArray Arr(IEnumerable<int> xs) { var a = new JsonArray(); foreach (var x in xs) a.Add(x); return a; }
}
