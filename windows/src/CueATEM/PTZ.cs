using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CueATEM;

// PTZ cameras. Three ways in:
// - Canon (HTTP): Canon's own control protocol on port 80 (CR-N series and other Canon PTZs).
//   Works out of the box and reports the camera's preset names.
// - VISCA over IP (UDP 52381): Sony, BirdDog, newer PTZOptics, Panasonic in VISCA mode,
//   Canon when its VISCA setting is on.
// - VISCA over TCP (5678): PTZOptics and most budget cameras.
// A straight port of PTZ.swift. Each link lives on the controller's serial queue.

/// <summary>One camera connection, whatever the protocol. Every member is called on the PTZ queue.</summary>
internal interface IPTZLink
{
    Action<bool>? OnState { get; set; }
    /// <summary>Preset names the camera reports, by number from 1.</summary>
    Action<Dictionary<int, string>>? OnPresets { get; set; }
    Action<string>? OnModel { get; set; }
    /// <summary>Numbers of the camera's presets that hold only exposure, not a position.</summary>
    Action<HashSet<int>>? OnExposurePresets { get; set; }
    Action<CameraExposure>? OnExposure { get; set; }
    /// <summary>Whether this app holds control of the camera (Canon gives control to one session at a time).</summary>
    Action<bool>? OnControl { get; set; }
    void Start();
    void Stop();
    /// <summary>Speeds from -1 to 1; up is positive tilt. Zero stops that axis.</summary>
    void Drive(double pan, double tilt);
    /// <summary>Speeds in the camera's own units, signed (right and up positive), 0 stops.</summary>
    void DriveRaw(int pan, int tilt);
    void Zoom(double v);
    void AutoFocus(bool on);
    void FocusOnce();
    void Home();
    void Recall(int n);
    void Store(int n);
    void ReadPosition(Action<PTZPosition?> done);
    /// <summary>Where the camera is now, in whatever form GoTo takes back exactly, for saving a shot.</summary>
    void ReadShotPosition(Action<PTZPosition?> done);
    void GoTo(PTZPosition p);
    /// <summary>Delete a preset stored on the camera itself. Calls back with null when it worked, or why not.</summary>
    void DeletePreset(int n, Action<string?> done);
    void ReadExposure();
    void SetExposure(PTZExposure e);
    void AutoExposure();
    /// <summary>The camera's own red tally light: on while its switcher input is on Live.
    /// Only the Panasonic camcorder link lights one; the others ignore it.</summary>
    void Tally(bool on) { }
    /// <summary>The camera's power state as it reports it: "idle" (awake), "standby", or
    /// "switch_idle" / "switch_standby" while changing. Only Canon cameras report one.</summary>
    Action<string>? OnPower { get => null; set { } }
    /// <summary>Wakes the camera from standby. Calls back with null when the camera took the
    /// command, or why not.</summary>
    void Wake(Action<string?> done) => done("This camera can't be woken from standby by Churchfront Video.");
}

// MARK: - VISCA

/// <summary>How one make of VISCA camera counts. VISCA fixes the commands but not the units.</summary>
public sealed class VISCAUnits
{
    public double CountsPerDegree;
    public double ZoomMax;
    public double WideFOV;
    public double ZoomRatio;
    public Dictionary<int, int> Shutter = new();
    public Dictionary<int, int> Iris = new();
    public int GainMax;
    public int KelvinBase = 2500, KelvinStep = 100, KelvinTop = 8000;

    public int? PositionOf(int value, Dictionary<int, int> table)
    {
        foreach (var kv in table) if (kv.Value == value) return kv.Key;
        return null;
    }

    /// <summary>PTZOptics (and the other cameras on the same VISCA firmware): 0x0990 counts is
    /// 170 degrees, zoom runs 0 to 0x4000. Shutter and iris tables are PTZOptics' published ones.</summary>
    public static readonly VISCAUnits PtzOptics = new()
    {
        CountsPerDegree = 14.4, ZoomMax = 16384, WideFOV = 58, ZoomRatio = 20,
        Shutter = new() { [1] = 30, [2] = 60, [3] = 90, [4] = 100, [5] = 125, [6] = 180, [7] = 250, [8] = 350, [9] = 500, [10] = 725, [11] = 1000,
                          [12] = 1500, [13] = 2000, [14] = 3000, [15] = 4000, [16] = 6000, [17] = 10000 },
        Iris = new() { [1] = 1100, [2] = 960, [3] = 800, [4] = 680, [5] = 560, [6] = 480, [7] = 400, [8] = 340, [9] = 280, [10] = 240, [11] = 200, [12] = 180 },
        GainMax = 14,
    };

    public double Fov(int zoom) => WideFOV * Math.Pow(1 / ZoomRatio, Math.Min(1, Math.Max(0, zoom / ZoomMax)));

    /// <summary>As a Canon reports it: pan and tilt in hundredths of a degree, zoom as the
    /// horizontal angle of view in hundredths of a degree.</summary>
    public PTZPosition Position(int pan, int tilt, int zoom) =>
        new((int)Math.Round(pan / CountsPerDegree * 100), (int)Math.Round(tilt / CountsPerDegree * 100), (int)Math.Round(Fov(zoom) * 100));
}

internal sealed class VISCALink : IPTZLink
{
    private readonly PTZCamera _camera;
    private readonly SerialQueue _queue;
    private UdpClient? _udp;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    /// <summary>Bumped on every (re)connect: callbacks from an older connection are ignored.</summary>
    private int _gen;
    private uint _seq;
    public Action<bool>? OnState { get; set; }
    public Action<Dictionary<int, string>>? OnPresets { get; set; }
    public Action<string>? OnModel { get; set; }
    public Action<HashSet<int>>? OnExposurePresets { get; set; }
    public Action<CameraExposure>? OnExposure { get; set; }
    public Action<bool>? OnControl { get; set; }
    private bool _online;
    private void SetOnline(bool v) { if (v != _online) { _online = v; OnState?.Invoke(v); } }

    private readonly List<byte> _rx = new();
    private readonly List<(bool Counts, Action<PTZPosition?> Done)> _posWaiters = new();
    private (int, int)? _posPanTilt;
    private int? _posZoom;
    private int _posAsk;

    private enum Inq { PanTilt, Zoom, AeMode, Shutter, Iris, Gain, WbMode, ColorTemp }
    private static int Length(Inq k) => k switch { Inq.PanTilt => 11, Inq.Zoom or Inq.Shutter or Inq.Iris or Inq.Gain => 7, _ => 4 };
    private readonly List<(Inq Kind, DateTime At)> _pending = new();
    private readonly VISCAUnits _units = VISCAUnits.PtzOptics;
    private CameraExposure _reading = new();
    private int _aeMode = -1;
    private CameraExposure? _lastExposure;

    public VISCALink(PTZCamera camera, SerialQueue queue) { _camera = camera; _queue = queue; }

    public void Start()
    {
        _rx.Clear(); _pending.Clear();
        Close();
        var gen = ++_gen;
        if (_camera.Host.Length == 0 || _camera.Port == 0) return;
        if (_camera.Proto == VISCAProtocol.SonyUDP)
        {
            UdpClient c;
            try
            {
                c = new UdpClient(AddressFamily.InterNetwork);
                ATEMClient.IgnoreConnReset(c);
                c.Connect(_camera.Host, _camera.Port);
            }
            catch { Retry(gen); return; }
            _udp = c;
            new Thread(() =>
            {
                var any = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    byte[] d;
                    try { d = c.Receive(ref any); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                    catch { return; }
                    _queue.Post(() => { if (gen == _gen && d.Length > 0) { SetOnline(true); Received(d); } });
                }
            }) { IsBackground = true, Name = "visca-udp" }.Start();
            _seq = 0;
            // Reset the camera's sequence number, then ask for power so we know it answers.
            try { c.Send(new byte[] { 0x02, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0x01 }); } catch { }
            Send(new byte[] { 0x81, 0x09, 0x04, 0x00, 0xFF }, inquiry: true);
            return;
        }
        // VISCA over TCP.
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        _tcp = tcp;
        Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(_camera.Host, _camera.Port, cts.Token);
                // Keepalive notices a camera that lost power or was unplugged, which never closes the stream.
                var s = tcp.Client;
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                try
                {
                    s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
                    s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
                    s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                }
                catch { }
            }
            catch
            {
                _queue.Post(() => { if (gen == _gen) { SetOnline(false); Retry(gen); } });
                return;
            }
            var stream = tcp.GetStream();
            _queue.Post(() =>
            {
                if (gen != _gen) return;
                _stream = stream;
                SetOnline(true);
                ReadExposure();
            });
            var buf = new byte[4096];
            while (true)
            {
                int n;
                try { n = await stream.ReadAsync(buf); } catch { n = 0; }
                if (n <= 0)
                {
                    // A camera that reboots closes the stream: reconnect, or every later command goes nowhere.
                    _queue.Post(() => Reconnect(gen));
                    return;
                }
                var d = buf.AsSpan(0, n).ToArray();
                _queue.Post(() => { if (gen == _gen) Received(d); });
            }
        });
    }

    private void Retry(int gen) => _queue.After(3, () => { if (gen == _gen) Start(); });

    /// <summary>Drops a dead connection and dials again in 3 seconds.</summary>
    private void Reconnect(int gen)
    {
        if (gen != _gen) return;
        SetOnline(false);
        Close();
        Retry(gen);
    }

    private void Close()
    {
        try { _udp?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _udp = null; _tcp = null; _stream = null;
    }

    public void Stop() { _gen++; Close(); SetOnline(false); }

    private void Send(byte[] visca, bool inquiry = false)
    {
        if (_camera.Proto == VISCAProtocol.RawTCP)
        {
            if (_stream is not NetworkStream s) return;
            try { s.Write(visca); }
            catch { Reconnect(_gen); }
            return;
        }
        if (_udp is not UdpClient c) return;
        _seq = unchecked(_seq + 1);
        var n = visca.Length;
        var msg = new byte[8 + n];
        msg[0] = 0x01; msg[1] = (byte)(inquiry ? 0x10 : 0x00); msg[2] = (byte)(n >> 8); msg[3] = (byte)(n & 0xFF);
        msg[4] = (byte)(_seq >> 24); msg[5] = (byte)((_seq >> 16) & 0xFF); msg[6] = (byte)((_seq >> 8) & 0xFF); msg[7] = (byte)(_seq & 0xFF);
        visca.CopyTo(msg, 8);
        try { c.Send(msg); } catch { }
    }

    public void Drive(double pan, double tilt)
    {
        var ps = pan == 0 ? 1 : Math.Max(1, Math.Min(0x18, (int)Math.Round(pan * pan * 24)));
        var ts = tilt == 0 ? 1 : Math.Max(1, Math.Min(0x14, (int)Math.Round(tilt * tilt * 20)));
        byte p = (byte)(pan == 0 ? 0x03 : (pan < 0 ? 0x01 : 0x02));
        byte t = (byte)(tilt == 0 ? 0x03 : (tilt > 0 ? 0x01 : 0x02));
        Send(new byte[] { 0x81, 0x01, 0x06, 0x01, (byte)ps, (byte)ts, p, t, 0xFF });
    }

    public void DriveRaw(int pan, int tilt)
    {
        static double V(int r) => r == 0 ? 0 : (r < 0 ? -1 : 1) * Math.Min(1, Math.Sqrt(Math.Abs(r) / 10000.0) * 1.8);
        Drive(V(pan), V(tilt));
    }

    public void Zoom(double v)
    {
        var p = (byte)Math.Min(7, Math.Max(0, (int)Math.Round(Math.Abs(v) * 7)));
        if (v == 0) Send(new byte[] { 0x81, 0x01, 0x04, 0x07, 0x00, 0xFF });
        else Send(new byte[] { 0x81, 0x01, 0x04, 0x07, (byte)((v > 0 ? 0x20 : 0x30) | p), 0xFF });
    }

    private static byte Clamp8(int v) => (byte)Math.Clamp(v, 0, 255);
    public void AutoFocus(bool on) => Send(new byte[] { 0x81, 0x01, 0x04, 0x38, (byte)(on ? 0x02 : 0x03), 0xFF });
    public void FocusOnce() => Send(new byte[] { 0x81, 0x01, 0x04, 0x18, 0x01, 0xFF });
    public void Home() => Send(new byte[] { 0x81, 0x01, 0x06, 0x04, 0xFF });
    public void Recall(int n) => Send(new byte[] { 0x81, 0x01, 0x04, 0x3F, 0x02, Clamp8(n - 1 + _camera.FirstPreset), 0xFF });
    public void Store(int n) => Send(new byte[] { 0x81, 0x01, 0x04, 0x3F, 0x01, Clamp8(n - 1 + _camera.FirstPreset), 0xFF });

    public void ReadPosition(Action<PTZPosition?> done) => ReadPosition(false, done);
    /// <summary>A shot keeps the camera's own counts, so recalling it doesn't depend on how
    /// well this make's degrees and angle of view are known.</summary>
    public void ReadShotPosition(Action<PTZPosition?> done) => ReadPosition(true, done);

    private void ReadPosition(bool counts, Action<PTZPosition?> done)
    {
        _posWaiters.Add((counts, done));
        if (_posWaiters.Count != 1) return;   // already asking: this one gets the same answer
        _posPanTilt = null; _posZoom = null;
        var n = ++_posAsk;
        Ask(Inq.PanTilt, new byte[] { 0x81, 0x09, 0x06, 0x12, 0xFF });
        Ask(Inq.Zoom, new byte[] { 0x81, 0x09, 0x04, 0x47, 0xFF });
        _queue.After(0.5, () => { if (_posAsk == n && _posWaiters.Count > 0) AnswerPosition(null); });
    }

    private void AnswerPosition(PTZPosition? counts)
    {
        var w = _posWaiters.ToList();
        _posWaiters.Clear();
        PTZPosition? degrees = counts is PTZPosition c ? _units.Position(c.Pan, c.Tilt, c.Zoom) : null;
        foreach (var f in w) f.Done(f.Counts ? counts : degrees);
    }

    private void Ask(Inq kind, byte[] bytes)
    {
        var now = DateTime.UtcNow;
        // Answers come within a few hundredths of a second. Anything still waiting after 1.5 s was lost.
        if (_pending.Count > 0 && (now - _pending[0].At).TotalSeconds > 1.5) _pending.Clear();
        _pending.Add((kind, now));
        Send(bytes, inquiry: true);
    }

    /// <summary>Replies end in FF. y0 50 … answers the oldest inquiry; y0 60 … refuses it.</summary>
    private void Received(byte[] data)
    {
        // VISCA over IP puts an 8-byte header in front of each message.
        if (_camera.Proto == VISCAProtocol.SonyUDP) _rx.AddRange(data.Skip(8)); else _rx.AddRange(data);
        int end;
        while ((end = _rx.IndexOf(0xFF)) >= 0)
        {
            var m = _rx.GetRange(0, end + 1).ToArray();
            _rx.RemoveRange(0, end + 1);
            if (m.Length < 3 || !(m[1] == 0x50 || m[1] == 0x60) || _pending.Count == 0) continue;
            if (m[1] != 0x50) { _pending.RemoveAt(0); continue; }
            // Pan and tilt can answer before or after the lens questions, so an answer goes to
            // the oldest inquiry it has the right shape for.
            var at = _pending.FindIndex(p => Length(p.Kind) == m.Length);
            if (at < 0) continue;
            var kind = _pending[at].Kind;
            _pending.RemoveAt(at);
            int Word(int i)
            {
                int v = 0;
                for (int k = i; k < i + 4; k++) v = (v << 4) | (m[k] & 0x0F);
                return v >= 0x8000 ? v - 0x10000 : v;
            }
            var low = m.Length == 7 ? ((m[4] & 0x0F) << 4) | (m[5] & 0x0F) : m[2];
            switch (kind)
            {
                case Inq.PanTilt:
                case Inq.Zoom:
                    if (kind == Inq.PanTilt) _posPanTilt = (Word(2), Word(6)); else _posZoom = Word(2);
                    if (_posPanTilt is (int pp, int tt) && _posZoom is int z && _posWaiters.Count > 0)
                        AnswerPosition(new PTZPosition(pp, tt, z));
                    break;
                case Inq.AeMode:
                    // 0 full auto, 3 manual, 0A shutter priority, 0B iris priority, 0D bright.
                    _aeMode = low;
                    _reading = new CameraExposure
                    {
                        ShutterAuto = !(low == 0x03 || low == 0x0A),
                        IrisAuto = !(low == 0x03 || low == 0x0B),
                        GainAuto = low != 0x03,
                    };
                    break;
                case Inq.Shutter: _reading.Value.Shutter = _units.Shutter.TryGetValue(low, out var sv) ? sv : null; break;
                case Inq.Iris: _reading.Value.Iris = _units.Iris.TryGetValue(low, out var iv) ? iv : null; break;
                case Inq.Gain: _reading.Value.Gain = low; break;
                case Inq.WbMode:
                    _reading.Value.Wb = low switch { 0x00 => "auto", 0x01 => "tungsten", 0x02 => "daylight", 0x20 => "kelvin", _ => "custom" };
                    break;
                case Inq.ColorTemp:
                    _reading.Value.Kelvin = Math.Min(_units.KelvinTop, _units.KelvinBase + low * _units.KelvinStep);
                    _reading.GainIsLevel = true;
                    _reading.HasAutoMode = true;
                    _reading.GainList = Enumerable.Range(0, _units.GainMax + 1).ToList();
                    _reading.ShutterList = _units.Shutter.Values.OrderBy(x => x).ToList();
                    _reading.IrisList = _units.Iris.Values.OrderBy(x => x).ToList();
                    _reading.KelvinList = new();
                    for (int k = _units.KelvinBase; k <= _units.KelvinTop; k += _units.KelvinStep) _reading.KelvinList.Add(k);
                    _lastExposure = _reading.Clone();
                    OnExposure?.Invoke(_reading.Clone());
                    break;
            }
        }
        if (_rx.Count > 256) _rx.Clear();
    }

    public void ReadExposure()
    {
        Ask(Inq.AeMode, new byte[] { 0x81, 0x09, 0x04, 0x39, 0xFF });
        Ask(Inq.Shutter, new byte[] { 0x81, 0x09, 0x04, 0x4A, 0xFF });
        Ask(Inq.Iris, new byte[] { 0x81, 0x09, 0x04, 0x4B, 0xFF });
        Ask(Inq.Gain, new byte[] { 0x81, 0x09, 0x04, 0x4C, 0xFF });
        Ask(Inq.WbMode, new byte[] { 0x81, 0x09, 0x04, 0x35, 0xFF });
        Ask(Inq.ColorTemp, new byte[] { 0x81, 0x09, 0x04, 0x20, 0xFF });
    }

    public void AutoExposure()
    {
        Send(new byte[] { 0x81, 0x01, 0x04, 0x39, 0x00, 0xFF });
        _queue.After(0.5, ReadExposure);
    }

    /// <summary>One exposure mode for all three: setting any puts the camera in manual, so the
    /// other two are set to what the camera had them at and the picture doesn't jump.</summary>
    public void SetExposure(PTZExposure e)
    {
        void Direct(byte op, int v) => Send(new byte[] { 0x81, 0x01, 0x04, op, 0x00, 0x00, (byte)((v >> 4) & 0x0F), (byte)(v & 0x0F), 0xFF });
        if (e.HasExposure)
        {
            var had = _aeMode == 0x03 ? null : _lastExposure?.Value;
            Send(new byte[] { 0x81, 0x01, 0x04, 0x39, 0x03, 0xFF });
            if ((e.Shutter ?? had?.Shutter) is int s && _units.PositionOf(s, _units.Shutter) is int ps) Direct(0x4A, ps);
            if ((e.Iris ?? had?.Iris) is int i && _units.PositionOf(i, _units.Iris) is int pi) Direct(0x4B, pi);
            if ((e.Gain ?? had?.Gain) is int g) Direct(0x0C, Math.Max(0, Math.Min(_units.GainMax, g)));
        }
        if (e.Kelvin is int k && (e.Wb == null || e.Wb == "kelvin"))
        {
            var p = Math.Max(0, Math.Min((_units.KelvinTop - _units.KelvinBase) / _units.KelvinStep, (k - _units.KelvinBase) / _units.KelvinStep));
            Send(new byte[] { 0x81, 0x01, 0x04, 0x35, 0x20, 0xFF });
            Send(new byte[] { 0x81, 0x01, 0x04, 0x20, (byte)((p >> 4) & 0x0F), (byte)(p & 0x0F), 0xFF });
        }
        else if (e.Wb is string m && (m switch { "auto" => (byte?)0x00, "tungsten" => (byte?)0x01, "daylight" => (byte?)0x02, _ => null }) is byte code)
        {
            Send(new byte[] { 0x81, 0x01, 0x04, 0x35, code, 0xFF });
        }
        _queue.After(0.5, ReadExposure);
    }

    /// <summary>Straight to a saved shot (camera counts): pan and tilt together briskly, and zoom.</summary>
    public void GoTo(PTZPosition p)
    {
        static IEnumerable<byte> Nibbles(int v) => Enumerable.Range(0, 4).Select(i => (byte)((v >> (12 - 4 * i)) & 0x0F));
        Send(new byte[] { 0x81, 0x01, 0x06, 0x02, 0x14, 0x12 }.Concat(Nibbles(p.Pan)).Concat(Nibbles(p.Tilt)).Append((byte)0xFF).ToArray());
        Send(new byte[] { 0x81, 0x01, 0x04, 0x47 }.Concat(Nibbles(Math.Max(0, p.Zoom))).Append((byte)0xFF).ToArray());
    }

    /// <summary>Clears a preset slot on the camera. The camera can't say whether the slot held anything.</summary>
    public void DeletePreset(int n, Action<string?> done)
    {
        Send(new byte[] { 0x81, 0x01, 0x04, 0x3F, 0x00, Clamp8(n - 1 + _camera.FirstPreset), 0xFF });
        done(null);
    }
}

// MARK: - Canon (HTTP)

/// <summary>Canon's control protocol: open a session, claim control, then control.cgi with
/// pan=left|right|stop, tilt=up|down|stop, zoom=tele|wide|stop, p=&lt;preset&gt;.</summary>
internal sealed class CanonLink : IPTZLink
{
    private readonly PTZCamera _camera;
    private readonly SerialQueue _queue;
    private readonly HttpClient _http;
    /// <summary>Position reads (auto tracking asks several times a second) get their own
    /// connection, so they never hold up a joystick or zoom command.</summary>
    private readonly HttpClient _reads;
    private string? _session;
    private bool _opening, _stopped;
    private Timer? _timer;
    private DateTime _lastPresetRead = DateTime.MinValue;
    public Action<bool>? OnState { get; set; }
    public Action<Dictionary<int, string>>? OnPresets { get; set; }
    public Action<string>? OnModel { get; set; }
    public Action<HashSet<int>>? OnExposurePresets { get; set; }
    public Action<CameraExposure>? OnExposure { get; set; }
    public Action<bool>? OnControl { get; set; }
    public Action<string>? OnPower { get; set; }
    private bool _online;
    private void SetOnline(bool v) { if (v != _online) { _online = v; OnState?.Invoke(v); } }
    private string? _power;
    private void SetPower(string? v) { if (v != null && v != _power) { _power = v; OnPower?.Invoke(v); } }
    private bool _hasControl;
    private void SetControl(bool v) { if (v != _hasControl) { _hasControl = v; OnControl?.Invoke(v); } }
    private bool _claimRetry;

    public CanonLink(PTZCamera camera, SerialQueue queue)
    {
        _camera = camera;
        _queue = queue;
        _http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 2, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        _reads = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 1, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    }

    private string Base => $"http://{_camera.Host}{(_camera.Port == 80 ? "" : $":{_camera.Port}")}/-wvhttp-01-/";

    /// <summary>Canon replies with "key:=value" lines ("key==value" while a value is changing).</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var o = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n', '\r'))
        {
            if (raw.Length == 0) continue;
            var i = raw.IndexOf(":=", StringComparison.Ordinal);
            if (i < 0) i = raw.IndexOf("==", StringComparison.Ordinal);
            if (i >= 0) o[raw[..i]] = raw[(i + 2)..];
        }
        return o;
    }

    private static string? Val(Dictionary<string, string> kv, string k) => kv.TryGetValue(k, out var v) ? v : null;
    private static int? IntVal(Dictionary<string, string> kv, string k) => kv.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : null;

    private void Get(string path, Action<string?>? done = null)
    {
        var url = Base + path;
        Task.Run(async () =>
        {
            string? text = null; bool ok = false;
            try
            {
                using var r = await _http.GetAsync(url);
                text = await r.Content.ReadAsStringAsync();
                ok = r.StatusCode == HttpStatusCode.OK;
            }
            catch { }
            _queue.Post(() =>
            {
                if (!ok && (text == null || text.Contains("Error"))) done?.Invoke(null);
                else done?.Invoke(text);
            });
        });
    }

    public void Start()
    {
        _stopped = false;
        if (_camera.Host.Length == 0) return;
        Open();
        _timer = new Timer(_ => _queue.Post(KeepAlive), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Stop()
    {
        _stopped = true;
        _timer?.Dispose(); _timer = null;
        if (_session is string s) Get($"close.cgi?s={s}");
        _session = null;
        SetOnline(false);
        SetControl(false);
    }

    private void Open()
    {
        if (_opening || _stopped) return;
        _opening = true;
        Get("open.cgi", text =>
        {
            _opening = false;
            if (text == null || Val(Parse(text), "s") is not string s)
            {
                SetOnline(false);
                _queue.After(3, Open);
                return;
            }
            _session = s;
            Claim(s, ReadInfo);
        });
    }

    /// <summary>Asks for control and reads the answer. While told to wait, asks again every 1.5 s.</summary>
    private void Claim(string s, Action? then = null)
    {
        Get($"claim.cgi?s={s}", text =>
        {
            if (_session != s) return;
            if (text == null)
            {
                // The session is gone; start a new one.
                _session = null; SetControl(false); SetOnline(false); Open(); return;
            }
            SetOnline(true);
            SetControl((Val(Parse(text), "s.control") ?? "").StartsWith("enabled", StringComparison.Ordinal));
            if (!_hasControl && !_claimRetry)
            {
                _claimRetry = true;
                _queue.After(1.5, () =>
                {
                    _claimRetry = false;
                    if (!_stopped && !_hasControl && _session is string cur) Claim(cur);
                });
            }
            if (_hasControl) Pump();  // anything kept while control was away (a stop)
            then?.Invoke();
        });
    }

    private void KeepAlive()
    {
        if (_stopped) return;
        if (_session is not string s) { Open(); return; }
        Claim(s);
        if ((DateTime.UtcNow - _lastPresetRead).TotalSeconds > 60) ReadInfo();
        else ReadPower();
    }

    /// <summary>Only the standby state (f.standby), not the whole 30 KB info page.</summary>
    private void ReadPower() => Get("info.cgi?item=f.standby", text => { if (text != null) SetPower(Val(Parse(text), "f.standby")); });

    /// <summary>Wakes the camera from standby, as Companion's Canon module does (standby.cgi
    /// cmd=idle; cmd=standby puts it back). Needs no session. If the camera asks for a login,
    /// the admin login saved in Setup is used. The state is read back over the next 20 seconds.</summary>
    public void Wake(Action<string?> done)
    {
        if (_camera.Host.Length == 0) { done("The camera has no address."); return; }
        var url = Base + "standby.cgi?cmd=idle";
        var login = CameraLogin.Load();
        Task.Run(async () =>
        {
            string? why = null;
            try
            {
                using var r = await _http.GetAsync(url);
                if (r.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (login == null) why = "The camera asked for a login. Save the cameras' admin login in Setup, under Cameras.";
                    else
                    {
                        var creds = new CredentialCache();
                        var nc = new NetworkCredential(login.User, login.Password);
                        creds.Add(new Uri(Base), "Digest", nc); creds.Add(new Uri(Base), "Basic", nc);
                        using var admin = new HttpClient(new HttpClientHandler { Credentials = creds, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                        using var r2 = await admin.GetAsync(url);
                        if (r2.StatusCode == HttpStatusCode.Unauthorized) why = "The camera turned down the saved login. Check the user name and password in Setup.";
                        else if (!r2.IsSuccessStatusCode) why = $"The camera said HTTP {(int)r2.StatusCode}.";
                    }
                }
                else if (!r.IsSuccessStatusCode) why = $"The camera said HTTP {(int)r.StatusCode}.";
            }
            catch { why = "The camera didn't answer."; }
            _queue.Post(() =>
            {
                if (why == null) foreach (var t in new[] { 1.0, 4, 10, 20 }) _queue.After(t, () => { if (!_stopped) ReadPower(); });
                done(why);
            });
        });
    }

    private void ReadInfo()
    {
        _lastPresetRead = DateTime.UtcNow;
        Get("info.cgi", text =>
        {
            if (text == null) return;
            var kv = Parse(text);
            if (Val(kv, "s.hardware") is string model) OnModel?.Invoke(model);
            SetPower(Val(kv, "f.standby"));
            var names = new Dictionary<int, string>();
            var exposureOnly = new HashSet<int>();
            foreach (var (k, v) in kv)
            {
                if (!k.StartsWith("p.", StringComparison.Ordinal) || !k.EndsWith(".name.utf8", StringComparison.Ordinal) || v.Length == 0) continue;
                if (!int.TryParse(k[2..^10], out var n) || Val(kv, $"p.{n}.content") == "disabled") continue;
                names[n] = v;
                if (Val(kv, $"p.{n}.content.ptz") == "disabled") exposureOnly.Add(n);
            }
            OnPresets?.Invoke(names);
            OnExposurePresets?.Invoke(exposureOnly);
            OnExposure?.Invoke(Exposure(kv));
        });
    }

    public static CameraExposure Exposure(Dictionary<string, string> kv)
    {
        List<int> List(string k) => (Val(kv, $"c.1.{k}.list") ?? "").Split(',').Select(x => int.TryParse(x, out var i) ? (int?)i : null).OfType<int>().ToList();
        var auto = Val(kv, "c.1.exp") == "auto";
        return new CameraExposure
        {
            Value = new PTZExposure
            {
                Gain = IntVal(kv, "c.1.me.gain"), Shutter = IntVal(kv, "c.1.me.shutter"), Iris = IntVal(kv, "c.1.me.diaphragm"),
                Wb = Val(kv, "c.1.wb"), Kelvin = IntVal(kv, "c.1.wb.kelvin"),
            },
            GainAuto = auto || Val(kv, "c.1.me.gain.mode") == "auto",
            ShutterAuto = auto || Val(kv, "c.1.me.shutter.mode") == "auto",
            IrisAuto = auto || Val(kv, "c.1.me.diaphragm.mode") == "auto",
            GainList = List("me.gain"), ShutterList = List("me.shutter"), IrisList = List("me.diaphragm"), KelvinList = List("wb.kelvin"),
        };
    }

    public void ReadExposure() => Get("info.cgi", text => { if (text != null) OnExposure?.Invoke(Exposure(Parse(text))); });

    // A Canon sets gain, shutter and iris to auto one at a time; this doesn't yet.
    public void AutoExposure() { }

    /// <summary>Exposure goes through the same control channel as pan and tilt. Each value set
    /// also switches that one setting (and the camera) to manual.</summary>
    public void SetExposure(PTZExposure e)
    {
        var q = e.HasExposure ? new List<string> { "exp=manual" } : new List<string>();
        if (e.Gain is int g) q.AddRange(new[] { "me.gain.mode=manual", $"me.gain={g}" });
        if (e.Shutter is int s) q.AddRange(new[] { "me.shutter.mode=speed", $"me.shutter={s}" });
        if (e.Iris is int i) q.AddRange(new[] { "me.diaphragm.mode=manual", $"me.diaphragm={i}" });
        if (e.Kelvin is int k && (e.Wb == null || e.Wb == "kelvin")) q.AddRange(new[] { "wb=kelvin", $"wb.kelvin={k}" });
        else if (e.Wb is string m) q.Add($"wb={Uri.EscapeDataString(m)}");
        if (q.Count == 0) return;
        Control(string.Join("&", q));
        _queue.After(0.4, ReadExposure);
    }

    // One control request at a time, and the newest joystick or zoom move replaces any that is
    // still waiting, so a stop is never overtaken by an older move.
    private bool _sending;
    private readonly List<(string? Key, string Query, int Tries)> _queued = new();
    private string _lastDrive = "", _lastZoom = "";

    private void Control(string query, string? key = null)
    {
        var i = key == null ? -1 : _queued.FindIndex(x => x.Key == key);
        if (i >= 0) _queued[i] = (key, query, 0);
        else _queued.Add((key, query, 0));
        Pump();
    }

    private static bool IsStop(string q) => q == "pan=stop&tilt=stop" || q == "zoom=stop";

    private void Pump()
    {
        if (_sending || _queued.Count == 0) return;
        // No session: moves that were waiting are dropped, but a stop is kept and sent as soon
        // as the camera is back, so it can't be left moving.
        if (_session is not string s) { _queued.RemoveAll(x => !IsStop(x.Query)); Open(); return; }
        var next = _queued[0];
        _queued.RemoveAt(0);
        _sending = true;
        Get($"control.cgi?s={s}&{next.Query}", text =>
        {
            _sending = false;
            if (text == null)
            {
                // Turned down, no answer, or control was taken. Drop waiting moves but never a
                // stop: retry a failed stop a few times unless something newer replaced it.
                _queued.RemoveAll(x => !IsStop(x.Query));
                if (IsStop(next.Query) && next.Tries < 3 && !_queued.Any(x => x.Key == next.Key))
                    _queued.Insert(0, (next.Key, next.Query, next.Tries + 1));
                SetControl(false);
                Claim(s);
            }
            else Pump();
        });
    }

    // Canon speeds run 10 to 10000 for pan and tilt (very fast at the top), 0 to 127 for zoom.
    private static int Speed(double v) => Math.Min(10000, (int)(10 + v * v * 3000));

    public void Drive(double pan, double tilt)
    {
        var p = pan == 0 ? "pan=stop" : $"pan={(pan < 0 ? "left" : "right")}&pan.speed={Speed(pan)}";
        var t = tilt == 0 ? "tilt=stop" : $"tilt={(tilt > 0 ? "up" : "down")}&tilt.speed={Speed(tilt)}";
        var q = p + "&" + t;
        _lastDrive = q;
        Control(q, "drive");
        if (pan == 0 && tilt == 0) ResendStop(q, "drive", () => _lastDrive);
    }

    public void DriveRaw(int pan, int tilt)
    {
        var p = pan == 0 ? "pan=stop" : $"pan={(pan < 0 ? "left" : "right")}&pan.speed={Math.Min(10000, Math.Max(20, Math.Abs(pan)))}";
        var t = tilt == 0 ? "tilt=stop" : $"tilt={(tilt > 0 ? "up" : "down")}&tilt.speed={Math.Min(10000, Math.Max(20, Math.Abs(tilt)))}";
        var q = p + "&" + t;
        _lastDrive = q;
        Control(q, "drive");
        if (pan == 0 && tilt == 0) ResendStop(q, "drive", () => _lastDrive);
    }

    public void Zoom(double v)
    {
        var q = v == 0 ? "zoom=stop" : $"zoom={(v > 0 ? "tele" : "wide")}&zoom.speed={Math.Min(127, (int)(1 + Math.Abs(v) * 60))}";
        _lastZoom = q;
        Control(q, "zoom");
        if (v == 0) ResendStop(q, "zoom", () => _lastZoom);
    }

    /// <summary>Sends a stop once more shortly after, unless something newer was sent.</summary>
    private void ResendStop(string q, string key, Func<string> latest) =>
        _queue.After(0.3, () => { if (latest() == q) Control(q, key); });

    public void AutoFocus(bool on) => Control($"focus={(on ? "auto" : "manual")}");
    public void FocusOnce() => Control("focus.action=one_shot");
    public void Home() => Control("pan=0&tilt=0");
    public void Recall(int n) => Control($"p={n}");
    public void Store(int n) { }

    public void ReadPosition(Action<PTZPosition?> done)
    {
        // Only pan, tilt and zoom: about 1 KB instead of the whole 30 KB info page.
        var url = Base + "info.cgi?item=c.1.pan,c.1.tilt,c.1.zoom";
        Task.Run(async () =>
        {
            Dictionary<string, string> kv;
            try { kv = Parse(await _reads.GetStringAsync(url)); } catch { kv = new(); }
            _queue.Post(() =>
            {
                if (IntVal(kv, "c.1.pan") is int p && IntVal(kv, "c.1.tilt") is int t && IntVal(kv, "c.1.zoom") is int z) done(new PTZPosition(p, t, z));
                else done(null);
            });
        });
    }

    public void ReadShotPosition(Action<PTZPosition?> done) => ReadPosition(done);

    /// <summary>Deletes a preset the way the camera's own web page does: with a logged-in
    /// session (priority 40) opened with the saved admin login, then closed again.</summary>
    public void DeletePreset(int n, Action<string?> done)
    {
        if (CameraLogin.Load() is not CameraLogin.Credential login)
        {
            done("No camera login saved. Add the cameras' admin login in Setup, under PTZ cameras."); return;
        }
        var creds = new CredentialCache();
        var uri = new Uri(Base);
        var nc = new NetworkCredential(login.User, login.Password);
        creds.Add(uri, "Digest", nc);
        creds.Add(uri, "Basic", nc);
        var admin = new HttpClient(new HttpClientHandler { Credentials = creds, PreAuthenticate = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };

        void Call(string path, Action<int, string, string> next)
        {
            Task.Run(async () =>
            {
                int code = 0; string status = "", body = "";
                try
                {
                    using var r = await admin.GetAsync(Base + path);
                    code = (int)r.StatusCode;
                    if (r.Headers.TryGetValues("livescope-status", out var v)) status = v.FirstOrDefault() ?? "";
                    body = await r.Content.ReadAsStringAsync();
                }
                catch { }
                _queue.Post(() => next(code, status, body));
            });
        }
        void Finish(string? why, string? sid)
        {
            if (sid != null) Call($"close.cgi?s={sid}", (_, _, _) => admin.Dispose());
            else admin.Dispose();
            done(why);
            ReadInfo();  // refresh the preset list either way
        }
        Call("open.cgi?s.priority=40", (code, _, body) =>
        {
            if (code == 401) { Finish("The camera turned down the saved login. Check the user name and password in Setup.", null); return; }
            if (Val(Parse(body), "s") is not string sid) { Finish($"The camera wouldn't open a logged-in session (HTTP {code}).", null); return; }
            var tries = 0;
            void ClaimIt()
            {
                Call($"claim.cgi?s={sid}", (_, _, b) =>
                {
                    var control = Val(Parse(b), "s.control") ?? "";
                    if (control.StartsWith("enabled", StringComparison.Ordinal))
                    {
                        Call($"preset/set?p={n}&cmd=delete&s={sid}", (_, status, b2) =>
                        {
                            if (status.StartsWith("0", StringComparison.Ordinal)) Finish(null, sid);
                            else Finish($"The camera said: {(status.Length == 0 ? b2.Trim() : status)}", sid);
                        });
                    }
                    else if (tries < 6) { tries++; _queue.After(0.5, ClaimIt); }
                    else Finish($"Another controller has the camera, so it wouldn't hand over control ({control}).", sid);
                });
            }
            ClaimIt();
        });
    }

    public void GoTo(PTZPosition p)
    {
        var q = $"pan={p.Pan}&tilt={p.Tilt}&zoom={p.Zoom}";
        _lastDrive = q;  // so a joystick stop still waiting to be re-sent can't cut this move short
        Control(q, "drive");
    }
}

// MARK: - Panasonic camcorder

/// <summary>
/// A Panasonic camcorder (AG-CX350, also AJ-UPX360, AG-CX200) over Panasonic's PTZ Control
/// Protocol: HTTP GETs on port 80, "/cgi-bin/aw_ptz?cmd=#..&res=1" for the lens and tally,
/// "/cgi-bin/aw_cam?cmd=..&res=1" for camera settings ("CX350/CX4000 Control Using PTZ Control
/// Protocol", Rev.3.00). The camera answers only while NDI|HX is on or IP REMOTE is
/// ENABLE(HOLD), and with its Auto/Manual switch on AUTO; otherwise every command gets 404.
/// It has no pan or tilt (a motorized head may come later, as a separate link), and can't be
/// sent to a zoom position (only zoom speed, and a zoom position query), so it has no shots.
/// </summary>
internal sealed class PanasonicLink : IPTZLink
{
    private readonly PTZCamera _camera;
    private readonly SerialQueue _queue;
    private readonly HttpClient _http;
    private bool _stopped;
    private Timer? _timer;
    public Action<bool>? OnState { get; set; }
    public Action<Dictionary<int, string>>? OnPresets { get; set; }
    public Action<string>? OnModel { get; set; }
    public Action<HashSet<int>>? OnExposurePresets { get; set; }
    public Action<CameraExposure>? OnExposure { get; set; }
    public Action<bool>? OnControl { get; set; }
    private bool _online;
    private bool? _tally;       // what the light should show; sent again whenever the camera comes back
    private bool _modelRead;

    public PanasonicLink(PTZCamera camera, SerialQueue queue)
    {
        _camera = camera;
        _queue = queue;
        _http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 2, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    }

    private string Root => $"http://{_camera.Host}{(_camera.Port == 80 ? "" : $":{_camera.Port}")}/cgi-bin/";
    /// <summary>A lens or tally command ("#Z50"). Only "#" is escaped (as %23), the way the
    /// manual's own examples write it; the colons in camera commands stay as they are.</summary>
    public static string PtzPath(string cmd) => "aw_ptz?cmd=" + cmd.Replace("#", "%23") + "&res=1";
    /// <summary>A camera command ("OSE:69:1").</summary>
    public static string CamPath(string cmd) => "aw_cam?cmd=" + cmd.Replace("#", "%23") + "&res=1";

    /// <summary>Zoom speed, -1 (wide, fastest) to 1 (tele, fastest): #Z01 to #Z99, #Z50 stops.</summary>
    public static string ZoomCommand(double v)
    {
        if (v == 0) return "#Z50";
        var n = (int)Math.Round(50 + Math.Clamp(v, -1, 1) * 49, MidpointRounding.AwayFromZero);
        if (n == 50) n = v > 0 ? 51 : 49;   // the gentlest move still moves
        return $"#Z{n:00}";
    }

    private void SetOnline(bool v)
    {
        if (v == _online) return;
        _online = v;
        OnState?.Invoke(v);
        OnControl?.Invoke(v);
        if (v)
        {
            if (_tally is bool t) Send(PtzPath(t ? "#DA1" : "#DA0"), "tally");
            if (!_modelRead) ReadModel();
        }
    }

    /// <summary>GET a path under /cgi-bin/. Calls back on the queue with the reply body, or null
    /// when there was no answer or not 200 (404: control is off on the camera, see above).</summary>
    private void Get(string path, Action<string?>? done = null)
    {
        var url = Root + path;
        Task.Run(async () =>
        {
            string? text = null;
            try
            {
                using var r = await _http.GetAsync(url);
                if (r.StatusCode == HttpStatusCode.OK) text = (await r.Content.ReadAsStringAsync()).Trim();
            }
            catch { }
            _queue.Post(() => done?.Invoke(text));
        });
    }

    public void Start()
    {
        _stopped = false;
        if (_camera.Host.Length == 0) return;
        Poll();
        _timer = new Timer(_ => _queue.Post(Poll), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public void Stop()
    {
        _stopped = true;
        _timer?.Dispose(); _timer = null;
        _queued.Clear();
        SetOnline(false);
    }

    /// <summary>"Is it there and listening": the power query, which always answers p1.</summary>
    private void Poll()
    {
        if (_stopped) return;
        Get(PtzPath("#O"), text => { if (!_stopped) SetOnline(text != null && text.StartsWith("p", StringComparison.Ordinal)); });
    }

    /// <summary>Model and firmware, from "NAME=AG-CX350" and "VERSION=7.10" lines.</summary>
    private void ReadModel()
    {
        Get("getinfo?FILE=1", text =>
        {
            if (text == null) return;
            var kv = ParseInfo(text);
            if (kv.TryGetValue("NAME", out var name) && name.Length > 0)
            {
                _modelRead = true;
                OnModel?.Invoke(kv.TryGetValue("VERSION", out var v) && v.Length > 0 ? $"{name} (firmware {v})" : name);
            }
        });
    }

    public static Dictionary<string, string> ParseInfo(string text)
    {
        var o = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', '\r'))
        {
            var i = line.IndexOf('=');
            if (i > 0) o[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return o;
    }

    // One request at a time; a newer zoom replaces one still waiting, so a stop is never
    // overtaken by an older move, and a stop that fails is tried again.
    private bool _sending;
    private readonly List<(string? Key, string Path, int Tries)> _queued = new();
    private string _lastZoom = "";

    private static bool IsStop(string path) => path == PtzPath("#Z50");

    private void Send(string path, string? key = null)
    {
        var i = key == null ? -1 : _queued.FindIndex(x => x.Key == key);
        if (i >= 0) _queued[i] = (key, path, 0); else _queued.Add((key, path, 0));
        Pump();
    }

    private void Pump()
    {
        if (_sending || _queued.Count == 0 || _stopped) return;
        var next = _queued[0];
        _queued.RemoveAt(0);
        _sending = true;
        Get(next.Path, text =>
        {
            _sending = false;
            if (text == null)
            {
                // No answer: drop waiting moves, but keep trying a stop unless something newer replaced it.
                _queued.RemoveAll(x => !IsStop(x.Path) && x.Key != "tally");
                if (IsStop(next.Path) && next.Tries < 3 && !_queued.Any(x => x.Key == next.Key))
                    _queued.Insert(0, (next.Key, next.Path, next.Tries + 1));
                SetOnline(false);
                if (_queued.Count > 0) _queue.After(0.3, Pump);
                return;
            }
            SetOnline(true);
            Pump();
        });
    }

    public void Zoom(double v)
    {
        var path = PtzPath(ZoomCommand(v));
        _lastZoom = path;
        Send(path, "zoom");
        // Sent once more shortly after, unless something newer was: a lost stop would leave it zooming.
        if (v == 0) _queue.After(0.3, () => { if (_lastZoom == path) Send(path, "zoom"); });
    }

    public void AutoFocus(bool on) => Send(PtzPath(on ? "#D11" : "#D10"));
    public void FocusOnce() => Send(CamPath("OSE:69:1"));

    public void Tally(bool on)
    {
        if (_tally == on) return;
        _tally = on;
        if (_online) Send(PtzPath(on ? "#DA1" : "#DA0"), "tally");
    }

    // No pan or tilt, no presets, and no zoom position to go to.
    public void Drive(double pan, double tilt) { }
    public void DriveRaw(int pan, int tilt) { }
    public void Home() { }
    public void Recall(int n) { }
    public void Store(int n) { }
    public void GoTo(PTZPosition p) { }
    public void ReadPosition(Action<PTZPosition?> done) => done(null);
    public void ReadShotPosition(Action<PTZPosition?> done) => done(null);
    public void DeletePreset(int n, Action<string?> done) => done("This camera has no presets.");
    // Iris, gain and white balance come in a later step.
    public void ReadExposure() { }
    public void SetExposure(PTZExposure e) { }
    public void AutoExposure() { }
}

/// <summary>The Panasonic link's command building, for the self-test.</summary>
public static class PanasonicLinkInfo
{
    public static string Zoom(double v) => PanasonicLink.ZoomCommand(v);
    public static string Ptz(string cmd) => PanasonicLink.PtzPath(cmd);
    public static string Cam(string cmd) => PanasonicLink.CamPath(cmd);
}

// MARK: - Controller

/// <summary>Every camera, and which one the joystick drives. Public members are used from the
/// main thread; <see cref="Changed"/> is raised there.</summary>
public sealed class PTZController
{
    private List<PTZCamera> _cameras = new();
    /// <summary>The cameras. Setting this saves (through <see cref="CamerasChanged"/>) and
    /// reconnects only the cameras whose address or protocol changed.</summary>
    public List<PTZCamera> Cameras
    {
        get => _cameras;
        set
        {
            var old = _cameras;
            _cameras = value.Select(c => c.Clone()).ToList();
            CamerasChanged?.Invoke();
            if (LinksDiffer(old)) Restart();
            Notify();
        }
    }

    private Guid? _selected;
    /// <summary>The camera the joystick, zoom and arrow keys drive. When it changes while the
    /// old camera is being moved by hand, the old camera is stopped.</summary>
    public Guid? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            var old = _selected;
            _selected = value;
            _lastDrive = null;
            if (old is Guid o) StopManual(o);
            Notify();
        }
    }

    public HashSet<Guid> Online { get; } = new();
    public Dictionary<Guid, Dictionary<int, string>> ReportedPresets { get; } = new();
    public Dictionary<Guid, string> Models { get; } = new();
    public Dictionary<Guid, CameraExposure> Exposure { get; } = new();
    public Dictionary<Guid, HashSet<int>> ExposurePresets { get; } = new();
    /// <summary>Cameras this app currently holds control of.</summary>
    public HashSet<Guid> Controlled { get; } = new();
    /// <summary>Each Canon camera's power state as it reports it ("idle", "standby", "switch_idle", "switch_standby").</summary>
    public Dictionary<Guid, string> Power { get; } = new();

    public bool InStandby(PTZCamera c) => Power.TryGetValue(c.Id, out var p) && p is "standby" or "switch_standby";
    public bool Waking(PTZCamera c) => Power.TryGetValue(c.Id, out var p) && p == "switch_idle";
    /// <summary>Cameras that are asleep (or going to sleep) and can be woken.</summary>
    public List<PTZCamera> Sleeping => _cameras.Where(c => c.Host.Length > 0 && InStandby(c)).ToList();

    /// <summary>Wakes a camera from standby (Canon only). Calls back on main with null when the camera took it.</summary>
    public void Wake(Guid id, Action<string?>? done = null)
    {
        _queue.Post(() =>
        {
            if (!_links.TryGetValue(id, out var l)) { MainThread.Post(() => done?.Invoke("That camera isn't set up.")); return; }
            l.Wake(why => MainThread.Post(() =>
            {
                if (why == null) { Power[id] = "switch_idle"; Notify(); }
                done?.Invoke(why);
            }));
        });
    }

    /// <summary>Raised on the main thread whenever anything above changes.</summary>
    public event Action? Changed;
    /// <summary>Raised when <see cref="Cameras"/> is set, so the config file can be written.</summary>
    public event Action? CamerasChanged;

    private readonly SerialQueue _queue = new("cuevideo.ptz");
    private Dictionary<Guid, IPTZLink> _links = new();
    private (int, int)? _lastDrive;
    private readonly HashSet<Guid> _moving = new(), _zooming = new();

    public PTZController() { }

    private void Notify() => Changed?.Invoke();

    public static string Signature(IEnumerable<PTZCamera> cams) => JsonSerializer.Serialize(cams, CueJson.Options);

    private bool LinksDiffer(List<PTZCamera> old) =>
        !old.Select(c => c.LinkSignature).SequenceEqual(_cameras.Select(c => c.LinkSignature));

    private void Restart()
    {
        var cams = _cameras.Select(c => c.Clone()).ToList();
        var tally = new Dictionary<Guid, bool>(_tally);
        _queue.Post(() =>
        {
            foreach (var l in _links.Values) l.Stop();
            _links = new();
            foreach (var c in cams)
            {
                var id = c.Id;
                IPTZLink l = c.Proto switch
                {
                    VISCAProtocol.CanonXC => new CanonLink(c, _queue),
                    VISCAProtocol.PanasonicHTTP => new PanasonicLink(c, _queue),
                    _ => new VISCALink(c, _queue),
                };
                if (tally.TryGetValue(id, out var lit)) l.Tally(lit);
                l.OnState = on => MainThread.Post(() => { if (on) Online.Add(id); else Online.Remove(id); Notify(); });
                l.OnPresets = names => MainThread.Post(() => { ReportedPresets[id] = names; Notify(); });
                l.OnModel = m => MainThread.Post(() => { Models[id] = m; Notify(); });
                l.OnExposure = e => MainThread.Post(() => { Exposure[id] = e; Notify(); });
                l.OnControl = on => MainThread.Post(() => { if (on) Controlled.Add(id); else Controlled.Remove(id); Notify(); });
                l.OnExposurePresets = s => MainThread.Post(() => { ExposurePresets[id] = s; Notify(); });
                l.OnPower = v => MainThread.Post(() => { Power[id] = v; Notify(); });
                _links[id] = l;
                l.Start();
            }
        });
        if (_selected is Guid s && !_cameras.Any(c => c.Id == s)) Selected = _cameras.FirstOrDefault()?.Id;
        if (_selected == null) Selected = _cameras.FirstOrDefault()?.Id;
    }

    /// <summary>Stops every camera link (on quit), after a last stop to anything moving.</summary>
    public void Shutdown()
    {
        StopAllManual();
        var done = new ManualResetEventSlim();
        _queue.After(0.35, () => { foreach (var l in _links.Values) l.Stop(); done.Set(); });
        done.Wait(TimeSpan.FromSeconds(1.5));
    }

    public PTZCamera? Current => _cameras.FirstOrDefault(c => c.Id == _selected);
    public PTZCamera? Camera(Guid id) => _cameras.FirstOrDefault(c => c.Id == id);

    /// <summary>Changes the cameras from a copy, then saves and reconnects what changed.</summary>
    public void Edit(Action<List<PTZCamera>> change)
    {
        var list = _cameras.Select(c => c.Clone()).ToList();
        change(list);
        if (Signature(list) != Signature(_cameras)) Cameras = list;
    }

    /// <summary>Preset names for a camera: what you typed wins, then what the camera reports.</summary>
    public Dictionary<int, string> PresetNames(PTZCamera c)
    {
        var o = ReportedPresets.TryGetValue(c.Id, out var r) ? new Dictionary<int, string>(r) : new();
        foreach (var (k, v) in c.PresetNames) o[k] = v;
        return o;
    }

    /// <summary>Picks the camera wired to this ATEM input, if there is one.</summary>
    public void Follow(int? atemInput)
    {
        if (atemInput is not int i) return;
        var c = _cameras.FirstOrDefault(x => x.AtemInput == i);
        if (c == null || c.Id == _selected) return;
        Selected = c.Id;
    }

    /// <summary>Called (on main) with the camera whenever a person or Claude moves it.</summary>
    public Action<Guid>? OnManualMove;
    /// <summary>Overall speed for manual control: 1 normal, 0.25 very slow, 2 twice as fast.</summary>
    public double ManualSpeed = 1.0;
    public Action<Guid>? OnManualZoom;

    private void Manual(Guid? camera)
    {
        if ((camera ?? _selected) is not Guid id) return;
        MainThread.Run(() => OnManualMove?.Invoke(id));
    }

    private void With(Guid? id, Action<IPTZLink> f)
    {
        if ((id ?? _selected) is not Guid x) return;
        _queue.Post(() => { if (_links.TryGetValue(x, out var l)) f(l); });
    }

    private readonly Dictionary<Guid, bool> _tally = new();

    /// <summary>Lights each camera's own red tally while its switcher input is on Live (only
    /// cameras that have one act on it). Called on main with the inputs on Live now; sends only changes.</summary>
    public void SetTally(Func<int, bool> onLive)
    {
        foreach (var c in _cameras)
        {
            var on = c.AtemInput is int i && onLive(i);
            if (_tally.TryGetValue(c.Id, out var was) && was == on) continue;
            _tally[c.Id] = on;
            var id = c.Id;
            With(id, l => l.Tally(on));
        }
    }

    public void TrackZoom(double v, Guid camera) => With(camera, l => l.Zoom(Math.Clamp(v, -1, 1)));
    public void TrackDriveRaw(int pan, int tilt, Guid camera) => With(camera, l => l.DriveRaw(pan, tilt));
    public void TrackDrive(double pan, double tilt, Guid camera) => With(camera, l => l.Drive(Math.Clamp(pan, -1, 1), Math.Clamp(tilt, -1, 1)));

    /// <summary>Where a camera is now (Canon units), on main; null if it can't say.</summary>
    public void ReadPosition(Guid camera, Action<PTZPosition?> done) =>
        With(camera, l => l.ReadPosition(p => MainThread.Post(() => done(p))));

    /// <summary>Pan and tilt from a joystick: x and y from -1 to 1, up is positive y.</summary>
    public void Drive(double x, double y, Guid? camera = null)
    {
        const double dead = 0.08;
        var px = Math.Abs(x) < dead ? 0 : Math.Clamp(x, -1, 1);
        var py = Math.Abs(y) < dead ? 0 : Math.Clamp(y, -1, 1);
        // Only send when the direction or speed step changes, so a drag doesn't flood the camera.
        var step = ((int)Math.Round(px * 12), (int)Math.Round(py * 12));
        if (px != 0 || py != 0) Manual(camera);
        if ((camera ?? _selected) is Guid id)
        {
            if (px != 0 || py != 0) _moving.Add(id); else _moving.Remove(id);
        }
        if (camera == null && _lastDrive == step) return;
        if (camera == null) _lastDrive = step;
        var k = ManualSpeed;
        With(camera, l => l.Drive(px * k, py * k));
    }

    public void StopDrive(Guid? camera = null)
    {
        if (camera == null) _lastDrive = null;
        if ((camera ?? _selected) is Guid id) _moving.Remove(id);
        With(camera, l => l.Drive(0, 0));
    }

    private void StopManual(Guid id)
    {
        if (_moving.Remove(id)) With(id, l => l.Drive(0, 0));
        if (_zooming.Remove(id)) With(id, l => l.Zoom(0));
    }

    /// <summary>Stops every camera being moved or zoomed by hand. Called on quit or when the
    /// app loses focus, when a key or mouse release may never arrive.</summary>
    public void StopAllManual()
    {
        _lastDrive = null;
        foreach (var id in _moving.Union(_zooming).ToList()) StopManual(id);
    }

    /// <summary>Zoom speed from -1 (wide) to 1 (tele); 0 stops.</summary>
    public void Zoom(double v, Guid? camera = null)
    {
        var z = Math.Abs(v) < 0.05 ? 0 : v;
        if ((camera ?? _selected) is Guid id)
        {
            if (z != 0) _zooming.Add(id); else _zooming.Remove(id);
            MainThread.Run(() => OnManualZoom?.Invoke(id));
        }
        var k = ManualSpeed;
        With(camera, l => l.Zoom(z * k));
    }

    public void AutoFocus(bool on, Guid? camera = null) => With(camera, l => l.AutoFocus(on));
    public void FocusOnce(Guid? camera = null) => With(camera, l => l.FocusOnce());
    public void Home(Guid? camera = null) { Manual(camera); With(camera, l => l.Home()); }
    /// <summary>Presets are numbered from 1 here, whatever the camera counts from.</summary>
    public void Recall(int n, Guid? camera = null) { Manual(camera); With(camera, l => l.Recall(n)); }
    public void Store(int n) => With(null, l => l.Store(n));

    /// <summary>Recall a saved shot: move to its position, or set its exposure.</summary>
    public void Go(PTZShot shot, Guid? camera = null)
    {
        if (shot.Kind == ShotKind.Position) { Manual(camera); var p = shot.Position; With(camera, l => l.GoTo(p)); }
        else if (shot.Exposure != null) SetExposure(shot.Exposure, camera);
    }

    public void SetExposure(PTZExposure e, Guid? camera = null)
    {
        // Show the new value straight away; the camera's read-back confirms it.
        if ((camera ?? _selected) is Guid id)
        {
            var x = Exposure.TryGetValue(id, out var cur) ? cur.Clone() : new CameraExposure();
            if (e.Gain is int g) { x.Value.Gain = g; x.GainAuto = false; }
            if (e.Shutter is int s) { x.Value.Shutter = s; x.ShutterAuto = false; }
            if (e.Iris is int i) { x.Value.Iris = i; x.IrisAuto = false; }
            if (e.Kelvin is int k && (e.Wb == null || e.Wb == "kelvin")) { x.Value.Wb = "kelvin"; x.Value.Kelvin = k; }
            else if (e.Wb is string m) x.Value.Wb = m;
            Exposure[id] = x;
            Notify();
        }
        var copy = e.Clone();
        With(camera, l => l.SetExposure(copy));
    }

    public void AutoExposure(Guid? camera = null)
    {
        if ((camera ?? _selected) is Guid id && Exposure.TryGetValue(id, out var cur) && cur.HasAutoMode)
        {
            var x = cur.Clone();
            x.GainAuto = x.ShutterAuto = x.IrisAuto = true;
            Exposure[id] = x;
            Notify();
        }
        With(camera, l => l.AutoExposure());
    }

    public void RefreshExposure(Guid? camera = null) => With(camera, l => l.ReadExposure());

    /// <summary>Reads the camera now and saves it as a shot: a new one, or over <paramref name="replacing"/>.
    /// Calls back on the main thread with false if the camera couldn't say.</summary>
    public void SaveShot(string name, Guid id, ShotKind kind = ShotKind.Position, Guid? replacing = null, Action<bool>? done = null)
    {
        if (kind == ShotKind.Exposure)
        {
            RefreshExposure(id);
            MainThread.After(0.6, () =>
            {
                if (!Exposure.TryGetValue(id, out var x) || !x.Value.HasExposure || Camera(id) == null) { done?.Invoke(false); return; }
                var e = x.Value.Clone();
                Edit(list =>
                {
                    var c = list.First(c => c.Id == id);
                    var j = replacing is Guid r ? c.Shots.FindIndex(s => s.Id == r) : -1;
                    if (j >= 0)
                    {
                        c.Shots[j].Kind = ShotKind.Exposure;
                        c.Shots[j].Exposure = e;
                        if (name.Length > 0) c.Shots[j].Name = name;
                    }
                    else
                    {
                        var n = c.Shots.Count(s => s.Kind == ShotKind.Exposure) + 1;
                        c.Shots.Add(new PTZShot(name.Length == 0 ? $"Exposure {n}" : name, e));
                    }
                });
                done?.Invoke(true);
            });
            return;
        }
        _queue.Post(() =>
        {
            if (!_links.TryGetValue(id, out var l)) { MainThread.Post(() => done?.Invoke(false)); return; }
            l.ReadShotPosition(p => MainThread.Post(() =>
            {
                if (p is not PTZPosition pos || Camera(id) == null) { done?.Invoke(false); return; }
                Edit(list =>
                {
                    var c = list.First(c => c.Id == id);
                    var j = replacing is Guid r ? c.Shots.FindIndex(s => s.Id == r) : -1;
                    if (j >= 0)
                    {
                        var s = c.Shots[j];
                        s.Kind = ShotKind.Position; s.Pan = pos.Pan; s.Tilt = pos.Tilt; s.Zoom = pos.Zoom;
                        if (name.Length > 0) s.Name = name;
                    }
                    else c.Shots.Add(new PTZShot(name.Length == 0 ? $"Shot {c.Shots.Count + 1}" : name, pos));
                });
                done?.Invoke(true);
            }));
        });
    }

    /// <summary>Deletes a preset stored on the camera. Calls back on main with null when it worked.</summary>
    public void DeleteCameraPreset(int n, Guid id, Action<string?> done)
    {
        _queue.Post(() =>
        {
            if (!_links.TryGetValue(id, out var l)) { MainThread.Post(() => done("That camera isn't set up.")); return; }
            l.DeletePreset(n, why => MainThread.Post(() => done(why)));
        });
    }

    /// <summary>Whether moves will reach the camera: Canon needs this app to hold control;
    /// VISCA cameras take commands from anyone, so answering is enough.</summary>
    public bool HasControl(PTZCamera c) => c.Proto == VISCAProtocol.CanonXC ? Controlled.Contains(c.Id) : Online.Contains(c.Id);

    public bool CanSaveShots(PTZCamera c) => c.Host.Length > 0 && c.Proto.HasPanTilt();
}
