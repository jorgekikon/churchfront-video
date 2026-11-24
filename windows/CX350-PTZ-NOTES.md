# Panasonic AG-CX350 + PTZ Head Integration Notes

Handoff notes for the Churchfront Video **Windows port** (C# / .NET 8 WPF).
Source: planning chat on 2026-11-22/23. Lives at `windows/CX350-PTZ-NOTES.md`; linked from `CLAUDE.md` and `windows/HANDOFF-WINDOWS.md`.

---

## 1. Decisions made

| Topic | Decision |
|---|---|
| Video from CX350 | **NDI\|HX** (camera firmware updated; NDI now free and enabled). RTSP work is **no longer needed**. |
| Windows app video input | **Already built** (`Ndi.cs`: NdiFeed/NdiHub, preview stream). Just pick the CX350's NDI source for its switcher input in Setup/Routing and test with the real camera. |
| Camera control | Panasonic **HTTP CGI** "PTZ Control Protocol" (zoom/focus/iris/tally/picture). |
| Pan/tilt | Add a motorized head. Leading candidate: **Digital Bird DB3** (DIY, open source, VISCA over UDP). Commercial alternative: **SalrayWorks V BOT** (~$1,695, VISCA-over-IP, 7 lb / 3.2 kg load). |
| App model | Treat CX350 + head as **one composite camera**: pan/tilt → head (VISCA/UDP); zoom/focus/iris/tally → camera (HTTP). |

Fallback notes (if ever needed): RTSP URL is `rtsp://<camera-ip>:554/stream` (LibVLCSharp with `:rtsp-tcp` and `:network-caching=200`). RTMPS is not suitable for local preview.

---

## 2. CX350 camera control (Panasonic HTTP)

Reference PDF: https://eww.pass.panasonic.co.jp/pro-av/support/content/guide/DEF/CX350_CX4000_Command_for_PTZ_Control_Protocol.pdf

### Requirements
- Control works only when **NDI|HX mode is ON** (our case) or `IP REMOTE = ENABLE(HOLD)`. Otherwise every command returns **404**.
- Camera body **Auto/Manual switch must be set to AUTO**.
- HTTP/1.1, port **80**, GET requests. No auth documented — verify on the real camera.
- Encode `#` as `%23` in URLs.

### URL formats
- Lens / head-style commands: `http://<ip>/cgi-bin/aw_ptz?cmd=<Command>&res=1`
  - Example: `cmd=%23Z50` = zoom stop → reply `zS50`. `%23Z70` = zoom in (speed); `%23Z30` = zoom out. (01–99, 50 = stop.)
- Camera/picture commands: `http://<ip>/cgi-bin/aw_cam?cmd=<Command>&res=1` (use `res=0` for AWB/ABB execute — no response).
- Streaming / recording / status: `http://<ip>/cgi-bin/<Command>?<Param>=<Value>`.

### Controllable
- **Lens:** zoom (speed + position), focus (speed, position, auto/manual, one-touch AF), iris (position, auto/manual, offset), tally (R-Tally), power status (query only).
- **Picture:** ND, color bars, scene files, shutter, gain/AGC max, matrix, R/B gain & pedestal, chroma, AWB/ABB, color temp, detail, gamma, knee, white clip, iZoom, OIS, red/green tally, skin tone detail.
- **Other:** device info, camera status, JPEG snapshot, SD rec start/stop, RTMP/SRT start/stop.

### Gotchas
- Auto iris rejects iris position commands → switch iris to manual first.
- V-Log locks most color/gamma/knee/detail settings.
- Snapshot, camera status, SRT settings need firmware **≥ 7.00** (we're on the newest).
- Confirm on the new firmware whether SD recording still works while NDI is on (older NDI mode disabled recording/streaming/4K).

### Quick manual test (browser)
1. `http://<camera-ip>/cgi-bin/aw_ptz?cmd=%23Z70&res=1` → zoom starts
2. `http://<camera-ip>/cgi-bin/aw_ptz?cmd=%23Z50&res=1` → zoom stops

---

## 3. Implementation plan (app)

1. **`PanasonicLink` client** in `src/CueATEM/PTZ.cs` alongside `VISCALink` / `CanonLink`: async `HttpClient` GETs to `aw_ptz` / `aw_cam`, URL-encode `#`, short timeouts, parse text replies.
2. **Zoom/focus** speed control (joystick/slider), one-touch AF, AF/MF toggle.
3. **Iris** with auto/manual toggle.
4. **Tally:** when the CX350's ATEM input is on Program → send tally ON; off otherwise. (Existing ATEM program state drives it.)
5. **Status polling:** connection state, iris, gain, etc.
6. **Presets:** store head pan/tilt position + camera zoom/focus position together; recall both. Don't rely on the head's internal preset slots.
7. Later: picture settings (WB, gain, ND).
8. **Source abstraction:** keep video sources (NDI now; RTSP/SRT optional later) behind one interface.

Priority for first useful service: steps 1, 2 and 4.

**Status 2026-11-23:** steps 1, 2 (zoom speed, one-touch AF, AF on/off; no manual focus speed yet) and 4
are built and checked against a fake camera (see HANDOFF-WINDOWS.md, "Panasonic AG-CX350 + pan/tilt head").
Read from the protocol PDF (Rev.3.00): the CX350 has **no zoom position set** (only #Z speed and the #GZ
query), so presets with zoom need the head plus focus/zoom by other means; tally is #DA1/#DA0 on aw_ptz
(TLR:1/0 on aw_cam does the same); getinfo?FILE=1 gives NAME= and VERSION=.

---

## 4. Camera weight / head requirements
- CX350 shooting weight ≈ **2.3 kg (5.07 lb)** incl. hood, battery, mic holder. Body only ≈ 1.9 kg.
- CX350 power: 12 V DC (AC adapter); up to ~17 W with LCD. **Power the camera from its own adapter**, not from the head.

---

## 5. Digital Bird DB3 head (findings from its firmware)

- Repo: https://github.com/digitalbird01/DigitalBird-Camera-Slider → folder `DB3/`
  - `DB3_VISCA_Decoder_v6_02.ino` (ESP32 PoE ethernet board: VISCA UDP → UART)
  - `DB3_PTZ_v7_07.ino` (head motor controller)
- Build instructions: https://www.instructables.com/PTZ-PAN-TILT-HEAD-DB3-DB3-VISCA/
- Print files: https://makerworld.com/en/models/396866 · Kits: https://digital-bird-motion-control.myshopify.com
- Payload rating up to **3.5 kg** (CX350 ≈ 2.3 kg → ~1.2 kg margin; print load-bearing parts solid).
- Features: pan, tilt (+ optional zoom/focus ring motors — **skip for CX350**, lens is controlled via HTTP), front/rear tally LEDs, HDMI/SDI passthrough, encoders, magnetic homing.
- Power: PoE 12 V side limited to ~30 W total → don't power camera from the head. Head's D-Tap passes ~14 V → **not** for the CX350.

### VISCA decoder specifics (important for the app)
- Listens on **UDP port 1259** (not 52381, which the app's `VISCALink` uses). → Make the VISCA UDP port a per-camera setting, or change `localPort` in the firmware.
- **Static IP hard-coded** in firmware: `myIP(192,168,137,80)`, gateway `192.168.137.1`. Edit for the church network before flashing.
- Not a full VISCA implementation. It's tuned to what **OBS and vMix** send, and it partly identifies commands **by packet length and byte position**. → The app must send messages byte-for-byte like OBS/vMix (capture them with Wireshark), or adapt the decoder.
- Handles: pan/tilt moves, presets (16 for OBS), vMix-style **absolute position moves**, **pan/tilt position inquiry** (reply `90 50 … FF`), zoom/focus position inquiries (placeholder replies), **tally command**, focus home, manual focus.
- "Unlimited presets" (vMix style) = the controller stores absolute positions and sends them back. Do the same in the app.

### Next step
Read `DB3_VISCA_Decoder_v6_02.ino` lines ~280–500 (command dispatch) and write the exact byte sequences for: pan/tilt drive & stop, absolute move, position inquiry, preset recall/set, tally on/off. Build a `Db3Head` class around them.

---

## 6. SalrayWorks V BOT (commercial alternative)
- ~$1,695; 7 lb / 3.2 kg load; 340° pan, −20° to +30° tilt; 10 presets; PoE+; HDMI 4K60 passthrough; control via Canon XC, VISCA-over-IP, LANC.
- Before buying: confirm it answers **VISCA pan/tilt position inquiries** (needed for app-side presets).
