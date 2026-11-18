# Windows port: handoff (2026-11-18)

Goal: a Windows version of Churchfront Video (the macOS app in this repo), in C# / .NET 8 WPF.
Scope for v1 ("core first"): ATEM switching, Operator view, PTZ joystick/zoom/shots/exposure
(Canon HTTP + VISCA UDP/TCP), Canon live previews (MJPEG video.cgi), Engineer pages (Setup,
Routing, Full switcher, Status), config.json in %APPDATA%\CueVideo, the 127.0.0.1:9911 control
server and cuevideo-mcp.exe for Claude. Added since: auto tracking, the stage scan, the routing
editor and NDI pictures. Every feature of the Mac app is now on Windows; what's left is testing
on real hardware and packaging.

## Layout

    windows/
      ChurchfrontVideo.sln
      build.ps1                 release build -> dist\ (two single-file exes, models\rtmo-s.onnx, zip). -Test runs the smoke test first.
      get-model.ps1             fetches auto tracking's pose model into models\ (git-ignored; see "Auto tracking")
      NuGet.config              nuget.org (this machine had no package source; self-contained publish needs runtime packs)
      src/CueATEM               engine, plain net8.0: ATEM.cs, PTZ.cs (VISCALink, CanonLink, PTZController),
                                PTZModels.cs, CameraLogin.cs (Windows Credential Manager), SerialQueue.cs
      src/CueVideo.Core         net8.0: Config.cs (CueConfig + ConfigStore), Booth.cs, ControlTools.cs,
                                ControlServer.cs, DeviceFinder.cs (ATEM hello, Canon info.cgi, mDNS), ClaudeConnect.cs, Snapshots.cs,
                                Tracker.cs (AutoTracker, port of Tracker.swift), Identity.cs (Appearance, IdentityMatch),
                                StageScan.cs (ShotMath, CanonState, StageScan, StageCast; port of StageScan.swift),
                                RoutingEdit.cs (the Routing page's rules: groups, aux ownership, buttons),
                                Ndi.cs (NDI runtime loader, NdiFeed, NdiHub, NdiDirectory; port of NDIBridge.c / NDIFeed.swift)
      src/CueVideoMcp           cuevideo-mcp.exe (port of Sources/CueVideoMCP/main.swift)
      src/ChurchfrontVideo      ChurchfrontVideo.exe, WPF in code (no XAML): Theme, Controls (Joystick, ZoomRocker,
                                dialogs, LiveView), OperatorView, EngineerView (+ Stage scan, Switcher, Status),
                                RoutingPage (schematic + editors, port of RoutingPatch.swift / Routing.swift),
                                SetupPage, ExposureWindow, CanonFeed (MJPEG), PoseDetector (RTMO through ONNX Runtime,
                                TrackPictures, StillImages), Pictures (a source's picture: Canon or NDI),
                                TopMonitors (top-bar previews), NdiPicker
      tests/Smoke               95 checks against tools/FakeDevices (and a real NDI round trip when the
                                NDI runtime is installed). dotnet run --project tests/Smoke
      tests/TrackSim            closed loop with the real model and the fake Canon over photos:
                                TrackSim.exe <photo with a person> [start pan degrees]   (auto tracking)
                                TrackSim.exe scan <photo A> <photo B>                    (stage scan, labels, shots, follow by name)
      tools/FakeDevices         C# port of Tools/fake_atem.py (no Python on Windows): fake ATEM, VISCA cams that
                                answer inquiries, and a fake Canon on 127.0.0.1:8091 with MJPEG video.
                                --picture <photo> (one or more, side by side): the Canon views a virtual stage
                                through a real lens's geometry and really pans, tilts and zooms over it.
                                --ndi: NDI sources "Stage camera" (what the fake Canon sees) and "Lower third"
                                (alpha), for NDI pictures, overlays and tracking on a non-Canon camera.

## Done and checked (2026-11-17; stage scan, routing editor and NDI 2026-11-18)
- Everything builds; `.\build.ps1` makes dist\ChurchfrontVideo.exe (83 MB, ONNX Runtime + DirectML inside)
  and dist\cuevideo-mcp.exe (34 MB), self-contained and compressed, dist\models\rtmo-s.onnx, plus a zip.
- Smoke test: all 95 pass (config format both ways incl. Mac files, switching, fades, aux, DSK, macros,
  VISCA + Canon control/presets/exposure/position, shots, Claude's tools incl. the Live guard, auto
  tracking's identity matching, body measures and tools with a stand-in model, the stage scan's
  geometry, grouping, scan_stage / label_stage_scan and following by name, the routing editor's rules,
  and NDI: name matching, and with the runtime a real send/find/receive with alpha, an NDI snapshot and status).
- Ran the real app against FakeDevices: Operator view (monitors with the Canon picture, source tiles, shots
  grid, joystick, zoom, Fade/Cut/Black, key and route buttons), Full switcher, Setup tabs, Status.
  Keys 1-0, Shift+n, Space, arrows (stop on release, resent stop) and joystick drag all verified in the
  fake's log. Exposure window steps gain on the camera. Broken config.json: error shown, file set aside,
  last good setup kept.
- cuevideo-mcp.exe end to end: initialize, tools/list (cached while the app is closed), tools/call
  (opens the app if closed), camera_snapshots returns MCP image content.
- Auto tracking: TrackSim passes from both sides (camera starts 6-8° off, person centred in ~2 s, settles
  with no hunting). In the app (Debug and the dist exe) Track found the person, outlined them, showed the
  FOLLOWING badge and size/speed controls, turned the camera onto them, and pressing it again stopped the camera.
  RTMO-s runs at about 36 ms a frame on this PC's graphics chip through DirectML.
- Stage scan: `TrackSim scan` on a two-person virtual stage passes every check: both found with pictures,
  camera back where it was, labels saved as scan shots, each shot recalled puts the eyes at (0.50, 0.29)
  waist up, and following the second person by name turns to them, not the first. In the app: Engineer >
  Stage scan found both, names typed and saved, the shots appeared on Operator and framed the person, and
  Follow by name shows under Track.
- Routing editor, in the app against the fake switcher with UI Automation: click a source then a destination
  routes both outputs of a paired screen; the mix onto a screen; the locked Encoders and feeds refuses until
  unlocked; Undo puts it back; folding groups; naming a spare output (Aux 4 → Lobby TVs) moves it to Screens;
  saving the current routing as a button (an empty label or no screens is refused) and Run now. Dragging
  with a real mouse wasn't tried here (the screen was locked): it uses WPF drag and drop on the same rows.
- NDI, in the app against FakeDevices --ndi with the NDI 6 runtime: a VISCA camera's NDI picture on Next,
  its tile, the Routing rows and mix slots and the top bar (found by channel though the saved computer name
  was wrong); the lower third laid over Live and Next with its transparency; auto tracking followed the
  person in that NDI picture and drove the VISCA camera the right way; Setup's pickers list the network's
  sources and set one.

## NDI (Windows)
- The NDI runtime is loaded at run time from NDI Tools / the NDI runtime (NDI_RUNTIME_DIR_V6 or _V5, then
  Program Files\NDI\NDI 6 Runtime), as on the Mac: nothing NDI is needed to build, and without it the app
  runs with names in place of NDI pictures (list_ndi_sources and get_status "ndi_runtime" say so).
- Receivers ask for NDI's low-bandwidth preview stream (about 640 wide), keep alpha (BGRA, straight), and
  show pictures with non-square pixels at the shape the sender states. A saved source whose computer half
  changed is found by its channel when only one sender has it (Ndi.Match, as NDIFeed.match on the Mac).
- Where: Operator monitors and tiles, key overlays (a key's ndi, when the key is on Live or Next), Routing
  thumbnails and its picture pencil (pictures map), the top bar (Live/Next off Operator, pinned screens),
  auto tracking on non-Canon cameras, and camera_snapshots. The stage scan stays Canon-only, as on the Mac
  (it needs a camera that reports its angles exactly).

## Routing editor (Windows)
- Engineer > Routing is the Mac's schematic: mixes and inputs on the left, outputs on the right, a coloured
  wire per routing, drawn from where WPF placed each row. Every row is a Button, so a click, a screen reader
  or UI Automation can choose and route; dragging uses WPF drag and drop. The pencil on an output edits its
  name, aux outputs (an aux belongs to one screen), wiring note, top-bar preview and "Show to operators";
  operators' buttons are saved from the current routing, renamed, reordered, run and removed.
- Encoders and feeds (screens hidden from operators) are locked until unlocked on the page, as on the Mac.
- The pencil on a switcher input chooses its NDI picture (inputs that aren't Canon cameras).

## Stage scan (Windows)
- StageScan.swift ported as is: Canon cameras not on Live, not tracking and under control go to their Stage
  or Wide shot (else zoomed all the way out), the model finds people in a still (image.cgi), ShotMath turns
  each into a pan/tilt/zoom, the camera goes back. Same-person letters across cameras come from the
  clothes-colour appearance (weaker than the Mac's), so check them before saving.
- Saved shots are marked auto and replaced by the next scan; hand-made shots with the same name are kept.
  Names and looks (StageCast) last until the app quits, as on the Mac.

## Auto tracking (Windows)
- Same rules as the Mac (Tracker.swift is ported line for line: framing, steering, zoom hold, speed learning,
  identity). Only finding people differs: RTMO-s (OpenMMLab, 17 COCO body points, ONNX) in place of Vision.
- Appearance is a hue/saturation histogram of the upper and lower body, scaled to the Mac's distance range,
  in place of Vision's feature print. People dressed alike are harder to tell apart than on the Mac.
- Pictures: a Canon's own MJPEG preview, or for other cameras the NDI picture set for their switcher input
  (stretched to square pixels for the model). Following by name uses
  the people labelled in the stage scan (Operator: Follow by name under Track; Claude: start_tracking person).
- The model is not in git: its training data (body7) includes datasets with non-commercial terms, so it is
  fetched by get-model.ps1 and copied into dist by build.ps1. The app looks in models\ next to the exe, then
  %APPDATA%\CueVideo\models, then windows\models when run from the repo. Without it, Track is greyed out and
  says why; get_status reports "auto_tracking".
- Log for tuning: %APPDATA%\CueVideo\Logs\tracking.csv (same columns as the Mac's).
- Esc stops every tracking camera (the Mac's Command-period).

## Not done / not checked
- Not tried against real hardware (ATEM, Canon, PTZOptics). Next step: a rehearsal, never during a service.
- Auto tracking with a real Canon and people walking: check the body-measure ratios (eyes to ankles 7.8x,
  eyes to hips 3.7x, measured on the Mac with Vision's joints), the clothes-colour identity on a real stage
  (crossing people, similar outfits), and the latency setting (0.45 s default) against the real preview delay.
- Stage scan with real Canons: their reported pan/tilt/zoom limits, a full stage of people, and same-person
  letters across two cameras (the virtual stage has one camera).
- "Connect Claude" (Setup > Claude) writes %APPDATA%\Claude\claude_desktop_config.json (and the MS Store
  package copy if present). Logic is ported but it was not pressed here, to leave this machine's Claude settings alone.
- Camera admin login save/delete preset with a real Canon (Digest/Basic via HttpClientHandler) is untested.
- mDNS in DeviceFinder is a hand-rolled one-shot query; check it finds NDI/PTZOptics devices on a real network.
- NDI with real senders: a PTZOptics/BirdDog camera's preview stream (and its delay, for tracking's latency
  setting), Churchfront Screens' lower thirds with alpha, and many sources at once on a busy network.
- Routing editor: drag and drop with a real mouse, and the page with a real switcher's many outputs and
  patch bay names.
- Group headings written in letter-spaced capitals (T.Caps) read to screen readers letter by letter
  ("S p a r e  o u t p u t s"), across the app.
- No installer or code signing: SmartScreen will warn on first run of the unsigned exe.
- Windows Firewall will ask on first run (UDP to the switcher/cameras); allow on private networks.

## Notes for whoever continues
- Threads: each engine part has a SerialQueue; results reach the UI through MainThread.Post (the app sets it
  to the WPF Dispatcher in Program.cs). PTZController, ConfigStore and AutoTracker are used from the main
  thread only; AutoTracker runs the model on the thread pool, one frame per camera at a time.
- Screens are LiveView subclasses: Build() runs when StructureKey() changes (and never while a TextBox in
  that page has focus); Live(...) registers in-place updates run ~30x a second by MainWindow's timer.
- Buttons whose light changes (T.LitButton) are updated in place, never rebuilt, so clicks aren't lost.
- WPF needs real globalization: Directory.Build.props sets InvariantGlobalization=true for the engine, and
  ChurchfrontVideo.csproj turns it off again.
- Any CUEVIDEO_ environment variable makes the config read-only and skips the control server (self-tests);
  a self-test then doesn't apply the file's switcher or cameras.
  CUEVIDEO_HOST/CUEVIDEO_PORT pick a switcher, CUEVIDEO_FAKECAMS=127.0.0.1 adds three VISCA cams,
  CUEVIDEO_VIEW=engineer:switcher opens a page, CUEVIDEO_CONFIG=<file> uses that setup file in full
  (cameras included, never written), CUEVIDEO_SCREENSHOT=<png> saves the window every 2 s (works with the
  screen locked, when nothing is drawn on screen).
- Manual test without touching the booth's setup: `FakeDevices.exe 9932 --canon --picture <photo>`, a test
  config with a Canon on 127.0.0.1:8091 (atemInput 2), then the app with CUEVIDEO_CONFIG, CUEVIDEO_HOST=127.0.0.1,
  CUEVIDEO_PORT=9932. Buttons can be pressed with Windows UI Automation (InvokePattern) on a locked screen.
