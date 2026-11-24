# Windows port: handoff (2026-11-18)

Goal: a Windows version of Churchfront Video (the macOS app in this repo), in C# / .NET 8 WPF.
Scope for v1 ("core first"): ATEM switching, Operator view, PTZ joystick/zoom/shots/exposure
(Canon HTTP + VISCA UDP/TCP), Canon live previews (MJPEG video.cgi), Engineer pages (Setup,
Routing, Full switcher, Status), config.json in %APPDATA%\CueVideo, the 127.0.0.1:9911 control
server and cuevideo-mcp.exe for Claude. Added since: auto tracking, the stage scan, the routing
editor and NDI pictures. Every feature of the Mac app is now on Windows; what's left is testing
on real hardware and packaging. In step with the Mac's 0.1.2 (upstream main 9892525, 2026-11-22):
see "Synced from the Mac 0.1.2" below.

## Next: first tests on real hardware (planned 2026-11-19)
Where things stood at the end of 2026-11-18: everything committed and on the fork's windows-port
branch except this plan; windows\dist rebuilt with all of it (run that build, not the Debug one).
The switcher was off and the Canon cameras on; nothing had yet connected to the real hardware.
Run these in order, with nothing live, and stop at the first surprise. Never during a service.

1. Switcher probe, read-only: `.\probe.ps1` (uses the setup file's switcher). Expect the real model,
   inputs, M/Es, aux and macros, and "commands sent to the switcher: 0". Safe even if something is live.
2. Start dist\ChurchfrontVideo.exe normally (no CUEVIDEO_ settings). Windows Firewall will ask: allow on
   private networks. Status page: switcher online, each Canon "answering, in control".
3. Cameras, one at a time, off Live: joystick and arrow keys (stop on release), zoom, Focus, a shot saved
   and recalled, a camera preset recalled, the Exposure window reading and setting gain. Canon pictures on
   the monitors and tiles. Then camera_snapshots and match_exposure through Claude (step 8).
4. Switching, with nothing live: set Next by tile and number key, Cut (Space), Fade (Return), Black, a key
   button, the T-bar on Full switcher, a route button. Setup: add the volunteer sources and key buttons.
5. Routing page: name the real outputs and screens, mark encoder feeds "not for operators" (they lock),
   drag with the mouse (not tested before), save a route button.
6. Auto tracking on a Canon with someone walking: Track, follow, cross another person, lose and find them.
   Then read %APPDATA%\CueVideo\Logs\tracking.csv to tune latency (the preview's real delay) and check
   shot sizes (the body-measure ratios came from the Mac's model).
7. Stage scan with people in place on both Canons: shots frame each person; same-person letters across
   the two cameras; follow by name.
8. Claude: this repo's .mcp.json is the Mac's (it points at the Mac app, so it fails on Windows). For
   Claude Code here, add a local-scope server pointing at windows\dist\cuevideo-mcp.exe; for the Claude
   desktop app, press Setup > Claude > Connect Claude (not pressed before). Then get_status and a few tools.
9. NDI with real senders, if any (ProPresenter, a PTZOptics/BirdDog camera): pictures, alpha lower thirds.
   Note: the smoke test and FakeDevices --ndi announce fake NDI sources on the network while they run;
   don't run them during a service (an open item: send them in a private NDI group instead).

## Layout

    windows/
      ChurchfrontVideo.sln
      build.ps1                 release build -> dist\ (two single-file exes, models\rtmo-s.onnx, zip). -Test runs the smoke test first.
      get-model.ps1             fetches auto tracking's pose model into models\ (git-ignored; see "Auto tracking")
      probe.ps1                 a read-only look at the real switcher: prints everything it reports, changes nothing
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
      tests/Smoke               116 checks against tools/FakeDevices (and a real NDI round trip when the
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
- Smoke test: all 96 pass (config format both ways incl. Mac files, switching, fades, aux, DSK, macros,
  VISCA + Canon control/presets/exposure/position, shots, Claude's tools incl. the Live guard, auto
  tracking's identity matching, body measures and tools with a stand-in model, the stage scan's
  geometry, grouping, scan_stage / label_stage_scan and following by name, the routing editor's rules,
  NDI: name matching, and with the runtime a real send/find/receive with alpha, an NDI snapshot and status,
  and the probe's read-only switcher refusing commands).
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
- Esc (on Operator) or Ctrl+. (anywhere) stops every tracking camera (the Mac's Command-period).

## Synced from the Mac 0.1.2 (2026-11-23)
Upstream main gained three changes on 2026-11-22 (2f37b92, cd72e1d, a59fb4e); all are ported:
- Tracking (Tracker.cs): lead room comes only from real walking, never from which way the camera pans (that
  swung the shot past someone standing at a music stand); standing, facing only nudges it (35%). Body turn
  (shoulders under 70% of their squared-up width, head the same way, held 0.3 s) confirms a walk that way in
  0.25 s instead of 0.5. Walking is a sureness from -1 to 1 that builds and fades, times a held walking speed,
  instead of an on-off switch. Every speed change is eased (EaseRate); a correction that works out under the
  camera's slowest speed still moves it at 20 (CameraSpeed). Position, identity and the edge alarm use the
  head-and-torso box (TrackedPerson.Core), never the arms; when arms hide the body points for a moment
  (outline only), the head is held where it was last seen for 0.8 s. tracking.csv gains body_turn,
  shoulder_share and walk_sure. The rules are public static methods, checked by Smoke (the Mac's EdgeAlarmTests).
- Shot deck (CueVideo.Core/ShotDeck.cs for the logic, ChurchfrontVideo/ShotDeckView.cs for the view), in place
  of the row-per-camera shots: one camera at a time, tabs (the tab puts that camera on Next; the Live camera's
  tab shows its shots, each asking before it moves), a 5 by 2 grid of tiles, pages instead of scrolling.
  Keys Q W E R T / A S D F G send the camera on Next to a tile, [ and ] turn the page (MainWindow.DeckKey;
  never for a camera on Live). Hold a tile 0.35 s, then drag, to reorder within its run (saved shots, or the
  camera's presets); the presets' order is saved as presetOrder on the camera.
- Headroom stepper beside follow speed in the Frame panel (steps of 0.02 on the eye line for the size being
  followed, "Std"/"+1"/"−2", right-click for the standard). Follow speed also shows over the Live picture
  while the Live camera is tracking. Version 0.1.2.
- Checked: Smoke all pass (20 new checks); the deck with fake cameras (tabs, two pages, Q and ] through
  CUEVIDEO_KEYS, which now also takes plain keys and [ ], the Live tab's warning, long names). Not checked:
  dragging a tile with a real mouse, and the Headroom stepper and Live speed pill on screen (they show only
  while tracking, and no photo with people is in the repo for the fake Canon).

## Keyboard shortcuts
- The Mac's menu shortcuts, Ctrl for ⌘, on every page (MainWindow.Shortcut): Ctrl+1 Operator, Ctrl+, Setup,
  Ctrl+Shift+S Stage scan, Ctrl+Shift+R Routing, Ctrl+Shift+K Full switcher, Ctrl+T track the camera on Next
  (again to stop), Ctrl+. stop all tracking, Ctrl+Shift+F focus the camera on Next. They're in the tooltips.
  Ctrl+C, V, X, Z and A are left to text fields. The booth keys (1-0, Shift+n, Space, Return, arrows) are
  unchanged and still only on Operator.
- Checked with CUEVIDEO_KEYS against the fakes: every shortcut did its job, Ctrl+T toggled tracking, Ctrl+Shift+F
  sent one autofocus to the camera, Ctrl+C was left alone.

## Switcher probe
- `.\probe.ps1` (or CUEVIDEO_PROBE=1 on ChurchfrontVideo.exe), a port of the Mac's CUEVIDEO_PROBE: connects to
  the switcher (-Switcher, else the setup file's atemHost), waits for its whole state, writes it down (model,
  inputs, each M/E's program/preview/style/keys, DSKs, aux, macros, stream and record) to probe.txt with a
  picture of the window, prints it, and quits (exit 1 if no switcher answers in 20 s). Output goes to
  %APPDATA%\CueVideo\Logs\probe unless -Out is given.
- Safe on the real switcher: ATEMClient.ReadOnly refuses every command before it can go out (Smoke checks
  that, and the report counts commands sent: 0); cameras aren't connected, Claude's control server isn't
  started, auto tracking's model isn't loaded, and the setup file and window preferences aren't written.
  Against FakeDevices, the fake switcher received only the connection hello.

## Not done / not checked
- Not tried against real hardware (ATEM, Canon, PTZOptics). First step: `.\probe.ps1` on the booth computer
  (read-only, safe any time; see "Switcher probe"). Then a rehearsal, never during a service.
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
- Quitting after a long run (2026-11-23): the 0.1.1 app, open 31 hours on the booth PC, closed its window and
  saved, then hung with one thread left (Windows unloading the native libraries) until killed. Not reproduced
  in short runs. Now the app stops NDI's receivers on quit (NdiHub.StopAll, Ndi.WaitReceiversClosed) and
  ends its process itself once everything is saved (Program.Main, TerminateProcess), skipping that unloading.
  Short runs exit in 0.5 s; check it after a long run (a Sunday) that closing really ends the process.
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
  screen locked, when nothing is drawn on screen), CUEVIDEO_PROBE=1 runs the switcher probe (CUEVIDEO_PROBE_OUT
  for its folder), CUEVIDEO_KEYS="ctrl+shift+r;ctrl+t" presses shortcuts inside the app every 2 s (printed
  to stdout), so nothing else on the desktop receives them. Self-tests don't write the window preferences.
- Manual test without touching the booth's setup: `FakeDevices.exe 9932 --canon --picture <photo>`, a test
  config with a Canon on 127.0.0.1:8091 (atemInput 2), then the app with CUEVIDEO_CONFIG, CUEVIDEO_HOST=127.0.0.1,
  CUEVIDEO_PORT=9932. Buttons can be pressed with Windows UI Automation (InvokePattern) on a locked screen.
- Keeping up with the Mac: last synced at upstream 9892525 (0.1.2). `git fetch origin` then
  `git log 9892525..origin/main` lists what's new since (upstream isn't merged into windows-port, so
  `windows-port..origin/main` would keep listing the commits already ported).

## Panasonic AG-CX350 + pan/tilt head (started 2026-11-23)
The CX350 now sends NDI|HX (new free firmware), so its picture uses the existing NDI path.
Full plan, command formats and DB3 firmware findings: `windows/CX350-PTZ-NOTES.md`.
- Done (steps 1, 2, 4 of the plan): Control "Panasonic (HTTP)" in Setup (proto panasonicHTTP, port 80),
  PanasonicLink in PTZ.cs: zoom by speed (#Z01-99, 50 stops; a stop is sent twice), one-touch AF (OSE:69:1),
  AF on/off (#D11/#D10), online from the power query (#O -> p1) every 3 s, model and firmware from
  getinfo?FILE=1, and the red tally (#DA1/#DA0) following the switcher's own tally for its input
  (Booth -> PTZController.SetTally; sent again whenever the camera comes back).
- A camera with no pan or tilt (HasPanTilt) greys out the joystick and Track, has no shots, Exposure
  or stage scan, and Claude's move/shot/preset tools say why. Setup shows how to turn control on when it
  isn't answering (the camera 404s everything unless NDI|HX is on or IP REMOTE is ENABLE(HOLD), with the
  Auto/Manual switch on AUTO).
- Checked against FakeDevices --panasonic (127.0.0.1:8092 answering, :8093 with control off): 14 new
  Smoke checks. Not tried on the real camera yet; first, the two browser zoom URLs in the notes.
- Not done: iris (#AXI, #D3) and other picture settings, manual focus speed, the pan/tilt head (composite
  camera), RTSP. The Mac app doesn't know panasonicHTTP and won't open a setup file that has it.

## Canon cameras: waking from standby (2026-11-24, Windows only)
After services the Canons are put in standby from Companion. The app now powers them back on.
- Protocol, as in Bitfocus Companion's Canon PTZ module (not in Canon's own docs we have):
  - `GET /-wvhttp-01-/standby.cgi?cmd=idle` wakes the camera; `cmd=standby` puts it to sleep. No session is needed.
  - The state is `f.standby` in info.cgi: `idle`, `standby`, `switch_idle` or `switch_standby`.
  - CanonLink reads `info.cgi?item=f.standby` every 10 s, and also on each full info read.
  - If the camera answers 401, the wake is retried with the admin login saved in Setup.
- What you see:
  - An amber "⏻ Wake <camera>" or "⏻ Wake N cameras" button in the top bar, shown only while a camera is in standby.
  - A "⏻ Wake" button on that camera's row in Setup → Cameras.
  - A status line in Frame the shot.
- For Claude: the `wake_cameras` tool, and `power` in `get_status`.
- Standby (2026-11-24, after waking worked on the real Canons):
  - Setup → Cameras: each Canon's row has a power button (Wake or Standby), and there's a "Put all cameras in standby…" button.
  - It always asks first. If a camera is on Live, or the switcher is streaming or recording, it warns and the button reads "Standby anyway".
  - Any tracking on the camera is stopped first.
  - Claude has the `standby_cameras` tool, which is refused in those cases unless `allow_live` is set.
  - Real Canons in standby turn down open.cgi but still answer info.cgi, so the app reads f.standby without a session.
- Checked: smoke (7 checks) against the fake Canon, plus the real app's button clicked against the fake Canon.
  Not yet tried on the real Canons.

## Powering devices on (2026-11-24): Kasa plugs are a Claude Code plug-in, not part of the app
- The user chose to keep smart-plug control out of the app. It's in `C:\claude-work\kasa-plugs`, a separate
  repo containing a local marketplace "booth" and the plug-in `kasa-plugs` (an MCP server and a skill).
- Installed in `%USERPROFILE%\.claude\settings.json`.
- Tuya plugs and Companion buttons aren't done.

### Earlier notes (2026-11-23)
The switcher and the Panasonic camera are on Wi-Fi smart plugs: a mix of TP-Link Kasa and generic plugs run by
the Tuya app. All are also in Alexa. Bitfocus Companion (on the Mac mini, which is sometimes off) already
controls OBS, the Canons, the Panasonic, ProPresenter 7 and Lightkey.
- Alexa itself has no official API for an app to switch devices or start routines. Webhook skills (Voice
  Monkey) can start a routine from a URL; unofficial cookie-based access (alexa-remote) is to be avoided.
- Option A (suggested first): press Companion buttons over its HTTP API (Companion 3.x:
  POST http://<mac-mini>:8000/api/location/<page>/<row>/<column>/press; 2.x: /press/bank/<page>/<button>;
  the HTTP remote may need enabling). Setup would list named buttons; a Power area in Engineer and a Claude
  tool press them. Can be built against a fake Companion. Catch: power-on needs the Mac mini on first.
- Option B: switch the plugs directly from this PC, no Mac mini needed. Kasa: local protocol (newer firmware
  changed it; test on the real plugs). Tuya: needs each plug's local key from a free Tuya developer account.
- Either way: power-off asks first and is refused while live, streaming or recording (and for Claude, the
  same kind of guard as allow_live). Next: the user picks A, B or both; check which plug modules Companion has.
