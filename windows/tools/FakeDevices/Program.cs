using System.IO;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Pretend hardware for testing Churchfront Video on Windows with nothing plugged in.
//   FakeDevices [port] [--cams] [--canon]
// ATEM on UDP <port> (9910), --cams: VISCA over IP cameras on UDP 52381-52383 that also answer
// position and exposure inquiries, --canon: a Canon camera's HTTP interface on 127.0.0.1:8091
// (open, claim, control, info and an MJPEG video.cgi). Prints every command received.
//   --picture <photo>  (one or more) the Canon looks at a virtual stage made from photos with people in them,
//                      and pans, tilts and zooms over it as told, for trying auto tracking and the stage scan.
//   --ndi              NDI sources "Stage camera" (what the fake Canon sees) and "Lower third" (alpha).
//   --panasonic        a Panasonic AG-CX350's PTZ Control Protocol (HTTP) on 127.0.0.1:8092, and on :8093 one
//                      with control off (NDI|HX and IP REMOTE both off), which answers 404 to everything.

var port = args.FirstOrDefault(a => int.TryParse(a, out _)) is string p ? int.Parse(p) : 9910;
for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--picture") FakeCanon.Stage.Add(args[i + 1]);
if (args.Contains("--cams")) FakeCams.Start();
if (args.Contains("--panasonic")) { FakePanasonic.Start(8092, on: true); FakePanasonic.Start(8093, on: false); }
if (args.Contains("--ndi")) FakeNdi.Start();
if (args.Contains("--canon") || args.Contains("--ndi")) FakeCanon.Start(8091);
new Switcher(port).Run();

sealed class Switcher
{
    const int AckReq = 0x01, Hello = 0x02, Resend = 0x04, Ack = 0x10;
    static readonly (int Id, string Long, string Short)[] Inputs =
    {
        (0, "Black", "BLK"), (1, "Center Camera", "CAM1"), (2, "Left Camera", "CAM2"), (3, "Right Camera", "CAM3"),
        (4, "ProPresenter", "PRO"), (5, "Walk-in Loop", "LOOP"), (6, "Stage Camera", "STG"),
        (1000, "Color Bars", "BARS"), (2001, "Color 1", "COL1"), (2002, "Color 2", "COL2"),
        (3010, "Media Player 1", "MP1"), (3011, "Media Player 1 Key", "MP1K"), (3020, "Media Player 2", "MP2"),
        (7001, "Clean Feed 1", "CFD1"), (10010, "Program", "PGM"), (10011, "Preview", "PVW"),
    };

    readonly UdpClient _sock;
    readonly object _lock = new();
    IPEndPoint? _client;
    int _pid;
    readonly int _session = 0x8001;
    int _prog = 1, _prev = 2, _style, _nextsel = 1, _rate = 30;
    double _pos; bool _intrans;
    readonly Dictionary<int, bool> _keys = new() { [0] = false, [1] = false };
    readonly Dictionary<int, bool[]> _dsk = new() { [0] = new bool[3], [1] = new bool[3] };   // on air, in transition, tie
    bool _ftb, _ftbtrans;
    readonly Dictionary<int, int> _aux = new() { [0] = 4, [1] = 3, [2] = 3, [3] = 10010 };
    readonly Dictionary<int, string> _macros = new() { [0] = "Walk-in", [1] = "Service Start", [3] = "Pastor Wide" };
    int? _macro;
    int _stream = 1, _record;

    public Switcher(int port)
    {
        _sock = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        try { _sock.Client.IOControl(-1744830452, new byte[4], null); } catch { }
        Console.WriteLine($"Fake ATEM listening on UDP {port}");
    }

    static byte[] Cmd(string name, params byte[] data)
    {
        var o = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(o, (ushort)o.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(o, 4);
        data.CopyTo(o, 8);
        return o;
    }
    static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };
    static byte[] Cat(params object[] parts)
    {
        var l = new List<byte>();
        foreach (var x in parts) { if (x is byte[] b) l.AddRange(b); else l.Add(Convert.ToByte(x)); }
        return l.ToArray();
    }
    static byte[] Pad(string s, int n) { var b = new byte[n]; Encoding.ASCII.GetBytes(s).AsSpan(0, Math.Min(n, s.Length)).CopyTo(b); return b; }

    void Packet(int flags, byte[]? payload = null, int ackid = 0)
    {
        lock (_lock)
        {
            payload ??= Array.Empty<byte>();
            if ((flags & AckReq) != 0) _pid = (_pid + 1) % 32768;
            var pid = (flags & AckReq) != 0 ? _pid : 0;
            var n = 12 + payload.Length;
            var hdr = new byte[] { (byte)((flags << 3) | ((n >> 8) & 7)), (byte)n, (byte)(_session >> 8), (byte)_session, (byte)(ackid >> 8), (byte)ackid, 0, 0, 0, 0, (byte)(pid >> 8), (byte)pid };
            if (_client != null) _sock.Send(hdr.Concat(payload).ToArray(), n, _client);
        }
    }

    void Send(IEnumerable<byte[]> cmds)
    {
        var buf = new List<byte>();
        foreach (var c in cmds)
        {
            if (buf.Count + c.Length > 1300) { Packet(AckReq, buf.ToArray()); buf.Clear(); }
            buf.AddRange(c);
        }
        if (buf.Count > 0) Packet(AckReq, buf.ToArray());
    }

    IEnumerable<byte[]> CInputs() => Inputs.Select(i => Cmd("InPr", Cat(U16(i.Id), Pad(i.Long, 20), Pad(i.Short, 4), new byte[10])));
    IEnumerable<byte[]> CBus() => new[] { Cmd("PrgI", Cat(0, 0, U16(_prog))), Cmd("PrvI", Cat(0, 0, U16(_prev), 0, 0, 0, 0)) };
    IEnumerable<byte[]> CTrps() => new[] { Cmd("TrPs", Cat(0, _intrans ? 1 : 0, 0, 0, U16((int)(_pos * 10000)), 0, 0)) };
    IEnumerable<byte[]> CTrss() => new[] { Cmd("TrSS", Cat(0, _style, _nextsel, _style, _nextsel, 0, 0, 0)) };
    IEnumerable<byte[]> CRate() => new[] { Cmd("TMxP", Cat(0, _rate, 0, 0)) };
    IEnumerable<byte[]> CKeys() => _keys.Select(kv => Cmd("KeOn", Cat(0, kv.Key, kv.Value ? 1 : 0, 0)));
    IEnumerable<byte[]> CDsk() => _dsk.SelectMany(kv => new[]
    {
        Cmd("DskS", Cat(kv.Key, kv.Value[0] ? 1 : 0, kv.Value[1] ? 1 : 0, kv.Value[1] ? 1 : 0, 0, 0, 0, 0)),
        Cmd("DskP", Cat(kv.Key, kv.Value[2] ? 1 : 0, 25, 0, 0, 0, 0, 0)),
    });
    IEnumerable<byte[]> CFtb() => new[] { Cmd("FtbS", Cat(0, _ftb ? 1 : 0, _ftbtrans ? 1 : 0, 0)) };
    IEnumerable<byte[]> CAux() => _aux.Select(kv => Cmd("AuxS", Cat(kv.Key, 0, U16(kv.Value))));
    IEnumerable<byte[]> CMacros() => Enumerable.Range(0, 4).Select(i =>
    {
        var n = Encoding.ASCII.GetBytes(_macros.GetValueOrDefault(i, ""));
        return Cmd("MPrp", Cat(U16(i), _macros.ContainsKey(i) ? 1 : 0, 0, U16(n.Length), U16(0), n, new byte[(4 - n.Length % 4) % 4]));
    });
    IEnumerable<byte[]> CMrun() => new[] { Cmd("MRPr", Cat(_macro != null ? 1 : 0, 0, U16(_macro ?? 0xFFFF))) };
    IEnumerable<byte[]> CStream() => new[] { Cmd("StRS", Cat(U16(_stream), U16(0))) };
    IEnumerable<byte[]> CRecord() => new[] { Cmd("RTMS", Cat(U16(_record), U16(0), 0, 0, 0, 0)) };
    IEnumerable<byte[]> CTally()
    {
        var ids = Inputs.Select(i => i.Id).Where(i => i < 10000).OrderBy(i => i).ToList();
        var d = new List<byte>(U16(ids.Count));
        foreach (var i in ids)
        {
            var f = (i == _prog ? 1 : 0) | (i == _prev ? 2 : 0);
            if (_keys[0] && i == 3010) f |= 1;
            d.AddRange(U16(i)); d.Add((byte)f);
        }
        while (d.Count % 4 != 0) d.Add(0);
        return new[] { Cmd("TlSr", d.ToArray()) };
    }

    void Dump() => Send(new[] { Cmd("_ver", Cat(U16(2), U16(30))), Cmd("_pin", Pad("ATEM Constellation HD (fake)", 44)) }
        .Concat(CInputs()).Concat(CBus()).Concat(CTrps()).Concat(CTrss()).Concat(CRate()).Concat(CKeys()).Concat(CDsk()).Concat(CFtb())
        .Concat(CAux()).Concat(CMacros()).Concat(CMrun()).Concat(CStream()).Concat(CRecord()).Concat(CTally()).Append(Cmd("InCm", new byte[4])));

    static void Later(double s, Action a) => Task.Delay(TimeSpan.FromSeconds(s)).ContinueWith(_ => a());
    void Swap() => (_prog, _prev) = (_prev, _prog);

    void AutoTrans()
    {
        if (_intrans) return;
        Task.Run(() =>
        {
            _intrans = true;
            var frames = Math.Max(1, _rate);
            for (int f = 1; f <= frames; f++) { _pos = (double)f / frames; Send(CTrps()); Thread.Sleep(33); }
            _intrans = false; _pos = 0; Swap();
            Send(CBus().Concat(CTrps()).Concat(CTally()));
        });
    }

    void Handle(string name, byte[] d)
    {
        int U(int i) => (d[i] << 8) | d[i + 1];
        Console.WriteLine($"RX {name} {Convert.ToHexString(d)}");
        switch (name)
        {
            case "CPgI": _prog = U(2); Send(CBus().Concat(CTally())); break;
            case "CPvI": _prev = U(2); Send(CBus().Concat(CTally())); break;
            case "DCut": Swap(); Send(CBus().Concat(CTally())); break;
            case "DAut": AutoTrans(); break;
            case "CTPs":
            {
                var p = U(2) / 10000.0;
                if (p >= 1) { _pos = 1; Send(CTrps()); _intrans = false; _pos = 0; Swap(); Send(CBus().Concat(CTrps()).Concat(CTally())); }
                else if (p <= 0) { _intrans = false; _pos = 0; Send(CTrps()); }
                else { _intrans = true; _pos = p; Send(CTrps()); }
                break;
            }
            case "CTTp":
                if ((d[0] & 1) != 0) _style = d[2];
                if ((d[0] & 2) != 0) _nextsel = d[3];
                Send(CTrss()); break;
            case "CTMx": _rate = d[1]; Send(CRate()); break;
            case "CKOn": _keys[d[1]] = d[2] != 0; Send(CKeys().Concat(CTally())); break;
            case "CDsL": _dsk[d[0]][0] = d[1] != 0; Send(CDsk()); break;
            case "CDsT": _dsk[d[0]][2] = d[1] != 0; Send(CDsk()); break;
            case "DDsA":
            {
                var i = d[0]; _dsk[i][1] = true; Send(CDsk());
                Later(0.8, () => { _dsk[i][1] = false; _dsk[i][0] = !_dsk[i][0]; Send(CDsk()); });
                break;
            }
            case "FtbA":
                _ftbtrans = true; Send(CFtb());
                Later(0.8, () => { _ftbtrans = false; _ftb = !_ftb; Send(CFtb()); });
                break;
            case "CAuS": _aux[d[1]] = U(2); Send(CAux()); break;
            case "MAct":
            {
                var i = U(0);
                if (i == 0xFFFF) { _macro = null; Send(CMrun()); }
                else if (_macros.ContainsKey(i)) { _macro = i; Send(CMrun()); Later(1.5, () => { _macro = null; Send(CMrun()); }); }
                break;
            }
            case "StrR":
                if (d[0] != 0) { _stream = 2; Send(CStream()); Later(1, () => { _stream = 4; Send(CStream()); }); }
                else { _stream = 1; Send(CStream()); }
                break;
            case "RcTM": _record = d[0] != 0 ? 1 : 0; Send(CRecord()); break;
        }
    }

    public void Run()
    {
        new Thread(() => { while (true) { Thread.Sleep(500); if (_client != null) Packet(AckReq); } }) { IsBackground = true }.Start();
        while (true)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] b;
            try { b = _sock.Receive(ref from); } catch { continue; }
            if (b.Length < 12) continue;
            var flags = b[0] >> 3;
            var n = ((b[0] & 7) << 8) | b[1];
            if ((flags & Hello) != 0)
            {
                _client = from; _pid = 0;
                Console.WriteLine($"HELLO from {from}");
                var hdr = new byte[] { (byte)(Hello << 3), 20, 0x53, 0xAB, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 1, 0, 0, 0, 0 };
                _sock.Send(hdr, hdr.Length, from);
                Later(0.05, Dump);
                continue;
            }
            if ((flags & AckReq) != 0)
            {
                var cid = (b[10] << 8) | b[11];
                Packet(Ack, ackid: cid);
                if ((flags & Resend) != 0) Console.WriteLine("(resent packet)");
                var payload = b.AsSpan(12, Math.Max(0, Math.Min(n, b.Length) - 12)).ToArray();
                var off = 0;
                while (off + 8 <= payload.Length)
                {
                    var ln = (payload[off] << 8) | payload[off + 1];
                    if (ln < 8 || off + ln > payload.Length) break;
                    Handle(Encoding.ASCII.GetString(payload, off + 4, 4), payload.AsSpan(off + 8, ln - 8).ToArray());
                    off += ln;
                }
            }
        }
    }
}

/// <summary>Three VISCA over IP cameras that also answer inquiries, so position reads and exposure work.</summary>
static class FakeCams
{
    public static void Start()
    {
        foreach (var port in new[] { 52381, 52382, 52383 })
        {
            var p = port;
            new Thread(() => Cam(p)) { IsBackground = true }.Start();
        }
        Console.WriteLine("Fake VISCA cameras on UDP 52381-52383");
    }

    static byte[] Nib(int v) => Enumerable.Range(0, 4).Select(i => (byte)((v >> (12 - 4 * i)) & 0x0F)).ToArray();

    static void Cam(int port)
    {
        var s = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        try { s.Client.IOControl(-1744830452, new byte[4], null); } catch { }
        int pan = 0, tilt = 0, zoom = 0, ae = 0, shutter = 6, iris = 7, gain = 2, wb = 0, ct = 30;
        while (true)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] b;
            try { b = s.Receive(ref from); } catch { continue; }
            if (b.Length >= 2 && b[0] == 0x02 && b[1] == 0x00) { s.Send(new byte[] { 0x02, 0x01, 0, 1, 0, 0, 0, 0, 0x01 }, 9, from); continue; }
            if (b.Length < 9 || b[0] != 0x01) continue;
            var body = b.AsSpan(8).ToArray();
            var seq = b.AsSpan(4, 4).ToArray();
            Console.WriteLine($"CAM{port - 52380} VISCA {Convert.ToHexString(body)}");
            void Reply(params byte[] m) { var h = new byte[] { 0x01, 0x11, 0, (byte)m.Length }.Concat(seq).Concat(m).ToArray(); s.Send(h, h.Length, from); }
            if (b[1] == 0x10)   // inquiry
            {
                var q = Convert.ToHexString(body);
                switch (q)
                {
                    case "81090612FF": Reply(new byte[] { 0x90, 0x50 }.Concat(Nib(pan)).Concat(Nib(tilt)).Append((byte)0xFF).ToArray()); break;
                    case "81090447FF": Reply(new byte[] { 0x90, 0x50 }.Concat(Nib(zoom)).Append((byte)0xFF).ToArray()); break;
                    case "81090439FF": Reply(0x90, 0x50, (byte)ae, 0xFF); break;
                    case "8109044AFF": Reply(0x90, 0x50, 0, 0, (byte)(shutter >> 4), (byte)(shutter & 15), 0xFF); break;
                    case "8109044BFF": Reply(0x90, 0x50, 0, 0, (byte)(iris >> 4), (byte)(iris & 15), 0xFF); break;
                    case "8109044CFF": Reply(0x90, 0x50, 0, 0, (byte)(gain >> 4), (byte)(gain & 15), 0xFF); break;
                    case "81090435FF": Reply(0x90, 0x50, (byte)wb, 0xFF); break;
                    case "81090420FF": Reply(0x90, 0x50, (byte)ct, 0xFF); break;
                    default: Reply(0x90, 0x50, 0x02, 0xFF); break;
                }
                continue;
            }
            Reply(0x90, 0x41, 0xFF);
            // Remember what was set, so reading back shows it.
            if (body.Length >= 15 && body[1] == 0x01 && body[2] == 0x06 && body[3] == 0x02)
            {
                int W(int i) { var v = (body[i] << 12) | (body[i + 1] << 8) | (body[i + 2] << 4) | body[i + 3]; return v >= 0x8000 ? v - 0x10000 : v; }
                pan = W(6); tilt = W(10);
            }
            if (body.Length >= 9 && body[1] == 0x01 && body[2] == 0x04 && body[3] == 0x47) zoom = (body[4] << 12) | (body[5] << 8) | (body[6] << 4) | body[7];
            if (body.Length >= 6 && body[1] == 0x01 && body[2] == 0x04 && body[3] == 0x39) ae = body[4];
            if (body.Length >= 9 && body[1] == 0x01 && body[2] == 0x04 && body[3] == 0x4A) shutter = (body[6] << 4) | body[7];
            if (body.Length >= 9 && body[1] == 0x01 && body[2] == 0x04 && body[3] == 0x4B) iris = (body[6] << 4) | body[7];
            if (body.Length >= 9 && body[1] == 0x01 && body[2] == 0x04 && body[3] == 0x0C) gain = (body[6] << 4) | body[7];
            if (body.Length >= 6 && body[1] == 0x01 && body[2] == 0x04 && body[3] == 0x35) wb = body[4];
            if (body.Length >= 7 && body[1] == 0x01 && body[2] == 0x04 && body[3] == 0x20) ct = (body[4] << 4) | body[5];
            if (body.Length >= 5 && body[1] == 0x01 && body[2] == 0x06 && body[3] == 0x04) { pan = 0; tilt = 0; }
            Reply(0x90, 0x51, 0xFF);
        }
    }
}

/// <summary>Enough of a Canon PTZ's HTTP interface for the app: sessions, control, info, video.</summary>
/// <summary>A Panasonic AG-CX350 as its PTZ Control Protocol describes it (Rev.3.00): the lens and
/// tally on /cgi-bin/aw_ptz, camera commands on /cgi-bin/aw_cam, getinfo for the model. Zoom moves
/// at the speed it was given, between 555h (wide) and FFFh (tele).</summary>
static class FakePanasonic
{
    static double _zoom = 0x555, _zoomSpeed;   // position, and steps a second (+ tele)
    static DateTime _zoomAt = DateTime.UtcNow;
    static int _af = 1, _tally;
    static readonly object Lock = new();

    public static void Start(int port, bool on)
    {
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        Console.WriteLine($"Fake Panasonic AG-CX350 on http://127.0.0.1:{port}/cgi-bin/ ({(on ? "control on" : "control off: 404")})");
        Task.Run(async () =>
        {
            while (true)
            {
                var c = await l.AcceptTcpClientAsync();
                _ = Task.Run(() => Serve(c, on));
            }
        });
    }

    static int ZoomNow()
    {
        var now = DateTime.UtcNow;
        _zoom = Math.Clamp(_zoom + _zoomSpeed * (now - _zoomAt).TotalSeconds, 0x555, 0xFFF);
        _zoomAt = now;
        return (int)_zoom;
    }

    /// <summary>The reply body for a command, or null for one it doesn't know (404, as the camera does).</summary>
    public static string? Reply(string cgi, string cmd)
    {
        lock (Lock)
        {
            if (cgi == "aw_ptz")
            {
                if (cmd == "#O") return "p1";
                if (cmd.StartsWith("#Z") && cmd.Length == 4 && int.TryParse(cmd[2..], out var z) && z is >= 1 and <= 99)
                {
                    ZoomNow();
                    _zoomSpeed = (z - 50) * 20.0;       // #Z99: about 1000 steps a second
                    return $"zS{z:00}";
                }
                if (cmd == "#GZ") return $"gz{ZoomNow():X3}";
                if (cmd is "#D11" or "#D10") { _af = cmd[3] - '0'; return $"d1{_af}"; }
                if (cmd == "#D1") return $"d1{_af}";
                if (cmd is "#DA1" or "#DA0") { _tally = cmd[3] - '0'; return $"dA{_tally}"; }
                if (cmd == "#DA") return $"dA{_tally}";
                return null;
            }
            if (cgi == "aw_cam")
            {
                if (cmd == "OSE:69:1") return "OSE:69:1";
                if (cmd is "OAF:0" or "OAF:1") { _af = cmd[4] - '0'; return cmd; }
                if (cmd == "QAF") return $"OAF:{_af}";
                return null;
            }
            if (cgi == "getinfo") return "MAC=00-00-00-00-00-00\r\nSERIAL=FAKE0001\r\nVERSION=7.10\r\nNAME=AG-CX350\r\n";
            return null;
        }
    }

    static async Task Serve(TcpClient c, bool on)
    {
        using (c)
        {
            var s = c.GetStream();
            var buf = new byte[4096];
            var n = await s.ReadAsync(buf);
            if (n <= 0) return;
            var path = (Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0].Split(' ').ElementAtOrDefault(1)) ?? "/";
            var cgi = path.StartsWith("/cgi-bin/") ? path[9..].Split('?')[0] : "";
            var q = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "";
            var cmd = q.Split('&').Where(kv => kv.StartsWith("cmd=")).Select(kv => Uri.UnescapeDataString(kv[4..])).FirstOrDefault() ?? "";
            var body = on ? Reply(cgi, cgi == "getinfo" ? "" : cmd) : null;
            Console.WriteLine($"PANASONIC{(on ? "" : " (off)")} {cgi} {cmd} -> {body ?? "404"}");
            var bytes = Encoding.ASCII.GetBytes(body ?? "Not Found");
            var head = $"HTTP/1.1 {(body == null ? "404 Not Found" : "200 OK")}\r\nContent-Type: text/plain\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(head));
            await s.WriteAsync(bytes);
        }
    }
}

static class FakeCanon
{
    static int _pan, _tilt, _zoom = 5000, _gain = 60, _shutter = 60, _iris = 400, _kelvin = 5600;
    static string _wb = "auto";
    static string _power = "idle";
    static readonly Dictionary<int, string> Presets = new() { [1] = "Pulpit", [2] = "Wide", [3] = "Exposure" };
    static byte[]? _frame;

    public static void Start(int port)
    {
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        Console.WriteLine($"Fake Canon camera on http://127.0.0.1:{port}/-wvhttp-01-/");
        var t = new Thread(MakeFrames) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Task.Run(async () =>
        {
            while (true)
            {
                var c = await l.AcceptTcpClientAsync();
                _ = Task.Run(() => Serve(c));
            }
        });
    }

    /// <summary>--picture (one or more): photos side by side make an 80° wide stage. The camera
    /// starts seeing a 40° window of it and really pans, tilts and zooms when told to (pan=left/right
    /// with pan.speed in hundredths of a degree a second; zoom as the angle of view, or tele/wide),
    /// so auto tracking and the stage scan can be tried in a closed loop.</summary>
    public static List<string> Stage = new();
    static double _vPan, _vTilt, _vZoom;          // degrees a second
    static readonly object Move = new();
    const int ZoomMin = 300, ZoomMax = 7000, PanMax = 17000, TiltMax = 3000;

    static void MakeStageFrames()
    {
        // The photos are the middle of the stage, side by side; the rest is dark curtain. One
        // photo is 40° wide; more share about 70°.
        const double stageDeg = 80;
        int sw = 2560, sh = 1440;
        var dv0 = new DrawingVisual();
        using (var dc = dv0.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 26)), null, new Rect(0, 0, sw, sh));
            var each = Stage.Count == 1 ? 40.0 : 70.0 / Stage.Count;
            for (int i = 0; i < Stage.Count; i++)
            {
                var src = new BitmapImage();
                src.BeginInit(); src.UriSource = new Uri(Path.GetFullPath(Stage[i])); src.CacheOption = BitmapCacheOption.OnLoad; src.EndInit();
                var pw = sw * each / stageDeg; var ph = pw * src.PixelHeight / src.PixelWidth;
                var centre = sw / 2 + (i - (Stage.Count - 1) / 2.0) * pw;
                dc.DrawImage(src, new Rect(centre - pw / 2, (sh - ph) / 2, pw, ph));
            }
        }
        var stage = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
        stage.Render(dv0);
        stage.Freeze();
        var stagePx = new byte[sw * sh * 4];
        stage.CopyPixels(stagePx, sw * 4, 0);
        const int ow = 960, oh = 540;
        var outPx = new byte[ow * oh * 4];
        int[] cols = new int[ow], rows = new int[oh];
        _zoom = 4000;
        var last = DateTime.UtcNow;
        while (true)
        {
            var now = DateTime.UtcNow; var dt = (now - last).TotalSeconds; last = now;
            double panDeg, tiltDeg, viewDeg;
            lock (Move)
            {
                _pan = (int)Math.Clamp(_pan + _vPan * dt * 100, -PanMax, PanMax);
                _tilt = (int)Math.Clamp(_tilt + _vTilt * dt * 100, -TiltMax, TiltMax);
                _zoom = (int)Math.Clamp(_zoom + _vZoom * dt * 100, ZoomMin, ZoomMax);
                panDeg = _pan / 100.0; tiltDeg = _tilt / 100.0; viewDeg = _zoom / 100.0;
            }
            // The stage is a curved backdrop (each degree the same width) and the camera a real
            // lens: a point x across the picture is atan(x · tan(half the view)) off its axis.
            double degPx = stageDeg / sw, rad = Math.PI / 180, th = Math.Tan(viewDeg / 2 * rad), tv = th * 9 / 16;
            for (int x = 0; x < ow; x++)
                cols[x] = (int)Math.Round(sw / 2 + (panDeg + Math.Atan(((x + 0.5) / ow * 2 - 1) * th) / rad) / degPx);
            for (int y = 0; y < oh; y++)
                rows[y] = (int)Math.Round(sh / 2 - (tiltDeg + Math.Atan((1 - (y + 0.5) / oh * 2) * tv) / rad) / degPx);
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++)
                {
                    int o = (y * ow + x) * 4, c = cols[x], r = rows[y];
                    if (c < 0 || c >= sw || r < 0 || r >= sh) { outPx[o] = 26; outPx[o + 1] = 18; outPx[o + 2] = 18; outPx[o + 3] = 255; continue; }
                    Buffer.BlockCopy(stagePx, (r * sw + c) * 4, outPx, o, 4);
                }
            var bmp = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Pbgra32, null, outPx, ow * 4);
            var enc = new JpegBitmapEncoder { QualityLevel = 80 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            _frame = ms.ToArray();
            FakeNdi.Camera(outPx, ow, oh);
            Thread.Sleep(40);
        }
    }

    /// <summary>A moving test picture, as JPEG, ten times a second.</summary>
    static void MakeFrames()
    {
        if (Stage.Count > 0) { MakeStageFrames(); return; }
        var n = 0;
        while (true)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 60, 90)), null, new Rect(0, 0, 640, 360));
                var x = 320 + 200 * Math.Sin(n / 10.0);
                dc.DrawEllipse(Brushes.Orange, null, new Point(x, 180), 40, 40);
                var ft = new FormattedText($"Fake Canon  pan {_pan} tilt {_tilt} zoom {_zoom}", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 22, Brushes.White, 1.0);
                dc.DrawText(ft, new Point(20, 20));
            }
            var bmp = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            if (FakeNdi.On) { var px = new byte[640 * 360 * 4]; bmp.CopyPixels(px, 640 * 4, 0); FakeNdi.Camera(px, 640, 360); }
            var enc = new JpegBitmapEncoder { QualityLevel = 70 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            _frame = ms.ToArray();
            n++;
            Thread.Sleep(100);
        }
    }

    /// <summary>Degrees a second from a drive command (speed in hundredths of a degree a second).</summary>
    static double Speed(string dir, string? speed, string plus, string minus)
    {
        var v = int.TryParse(speed, out var s) ? s / 100.0 : 10;
        return dir == plus ? v : dir == minus ? -v : 0;
    }

    static async Task Serve(TcpClient c)
    {
        using (c)
        {
            var s = c.GetStream();
            var buf = new byte[8192];
            var n = await s.ReadAsync(buf);
            if (n <= 0) return;
            var line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];
            var path = line.Split(' ').ElementAtOrDefault(1) ?? "/";
            Console.WriteLine($"CANON {path}");
            var q = path.Contains('?') ? path[(path.IndexOf('?') + 1)..].Split('&').Select(kv => kv.Split('=')).ToDictionary(a => a[0], a => a.Length > 1 ? Uri.UnescapeDataString(a[1]) : "") : new();
            string body;
            if (path.Contains("video.cgi"))
            {
                var head = "HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace;boundary=frame\r\n\r\n";
                await s.WriteAsync(Encoding.ASCII.GetBytes(head));
                try
                {
                    while (true)
                    {
                        if (_frame is byte[] f)
                        {
                            await s.WriteAsync(Encoding.ASCII.GetBytes($"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {f.Length}\r\n\r\n"));
                            await s.WriteAsync(f);
                            await s.WriteAsync("\r\n"u8.ToArray());
                        }
                        await Task.Delay(100);
                    }
                }
                catch { return; }
            }
            if (path.Contains("image.cgi"))
            {
                var f = _frame ?? Array.Empty<byte>();
                await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: {f.Length}\r\nConnection: close\r\n\r\n"));
                await s.WriteAsync(f);
                return;
            }
            // In standby a real Canon turns down sessions and control but still answers info.cgi.
            bool asleep; lock (Move) asleep = _power != "idle";
            if (asleep && (path.Contains("open.cgi") || path.Contains("claim.cgi") || path.Contains("control.cgi")))
            {
                await s.WriteAsync("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 14\r\nConnection: close\r\n\r\nError: standby"u8.ToArray());
                return;
            }
            if (path.Contains("open.cgi")) body = "s:=fake-session\n";
            else if (path.Contains("claim.cgi")) body = "s.control:=enabled:0\n";
            else if (path.Contains("close.cgi")) body = "";
            else if (path.Contains("control.cgi"))
            {
                lock (Move)
                {
                    if (q.TryGetValue("pan", out var p)) { if (int.TryParse(p, out var pv)) { _pan = pv; _vPan = 0; } else _vPan = Speed(p, q.GetValueOrDefault("pan.speed"), "right", "left"); }
                    if (q.TryGetValue("tilt", out var t)) { if (int.TryParse(t, out var tv)) { _tilt = tv; _vTilt = 0; } else _vTilt = Speed(t, q.GetValueOrDefault("tilt.speed"), "up", "down"); }
                }
                lock (Move)
                    if (q.TryGetValue("zoom", out var z))
                    {
                        if (int.TryParse(z, out var zv)) { _zoom = Stage.Count > 0 ? Math.Clamp(zv, ZoomMin, ZoomMax) : zv; _vZoom = 0; }
                        else { var v = int.TryParse(q.GetValueOrDefault("zoom.speed"), out var zs) ? zs * 0.1 : 2; _vZoom = z == "tele" ? -v : z == "wide" ? v : 0; }
                    }
                if (q.TryGetValue("me.gain", out var g)) _gain = int.Parse(g);
                if (q.TryGetValue("me.shutter", out var sh)) _shutter = int.Parse(sh);
                if (q.TryGetValue("me.diaphragm", out var ir)) _iris = int.Parse(ir);
                if (q.TryGetValue("wb", out var wb)) _wb = wb;
                if (q.TryGetValue("wb.kelvin", out var k)) _kelvin = int.Parse(k);
                body = "";
            }
            else if (path.Contains("standby.cgi"))
            {
                // Power, as Companion's Canon module sends it: cmd=standby, or cmd=idle to wake.
                // Changing takes 1.5 s (switch_idle / switch_standby on the way).
                var cmd = q.GetValueOrDefault("cmd");
                if (cmd is "idle" or "standby")
                {
                    lock (Move) _power = cmd == "idle" ? "switch_idle" : "switch_standby";
                    _ = Task.Delay(1500).ContinueWith(_ => { lock (Move) _power = cmd; });
                    body = "";
                }
                else { await s.WriteAsync("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()); return; }
            }
            else if (path.Contains("info.cgi") && q.GetValueOrDefault("item") == "f.standby")
            {
                lock (Move) body = $"f.standby:={_power}\n";
            }
            else if (path.Contains("info.cgi"))
            {
                var sb = new StringBuilder();
                lock (Move) sb.Append($"f.standby:={_power}\n");
                sb.Append($"s.hardware:=CR-N500 (fake)\nc.1.name.utf8:=Fake Canon\nc.1.pan:={_pan}\nc.1.tilt:={_tilt}\nc.1.zoom:={_zoom}\n");
                if (Stage.Count > 0) sb.Append($"c.1.zoom.limit.min:={ZoomMin}\nc.1.zoom.limit.max:={ZoomMax}\nc.1.pan.limit.min:={-PanMax}\nc.1.pan.limit.max:={PanMax}\nc.1.tilt.limit.min:={-TiltMax}\nc.1.tilt.limit.max:={TiltMax}\n");
                sb.Append($"c.1.exp:=manual\nc.1.me.gain:={_gain}\nc.1.me.gain.mode:=manual\nc.1.me.shutter:={_shutter}\nc.1.me.shutter.mode:=speed\nc.1.me.diaphragm:={_iris}\nc.1.me.diaphragm.mode:=manual\n");
                sb.Append("c.1.me.gain.list:=0,30,60,90,120\nc.1.me.shutter.list:=30,60,100,120,250\nc.1.me.diaphragm.list:=180,280,400,560,800\n");
                sb.Append($"c.1.wb:={_wb}\nc.1.wb.kelvin:={_kelvin}\nc.1.wb.kelvin.list:=3200,4300,5600,6500\n");
                foreach (var (i, nm) in Presets)
                {
                    sb.Append($"p.{i}.name.utf8:={nm}\np.{i}.content:=enabled\n");
                    if (nm == "Exposure") sb.Append($"p.{i}.content.ptz:=disabled\n");
                }
                body = sb.ToString();
            }
            else { await s.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()); return; }
            var data = Encoding.UTF8.GetBytes(body);
            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n"));
            await s.WriteAsync(data);
        }
    }
}

/// <summary>--ndi: NDI sources standing in for the network's cameras and graphics. "Stage
/// camera" sends what the fake Canon sees (the virtual stage with --picture, else the moving
/// test picture), so a VISCA camera can be given it as its NDI picture; "Lower third" sends a
/// name bar on a transparent background (straight alpha), for key overlays.</summary>
static class FakeNdi
{
    public static bool On;
    static IntPtr _camera;
    static DateTime _last;

    public static void Start()
    {
        if (!CueVideo.Ndi.Load()) { Console.WriteLine("NDI isn't installed, so no fake NDI sources (install NDI Tools)."); return; }
        On = true;
        _camera = CueVideo.Ndi.SendOpen("Stage camera");
        Console.WriteLine($"Fake NDI sources: Stage camera, Lower third (runtime {CueVideo.Ndi.Loaded})");
        var t = new Thread(LowerThird) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>A camera frame, BGRA (opaque). At most 25 a second.</summary>
    public static void Camera(byte[] bgra, int w, int h)
    {
        if (!On || (DateTime.UtcNow - _last).TotalMilliseconds < 38) return;
        _last = DateTime.UtcNow;
        CueVideo.Ndi.SendFrame(_camera, bgra, w, h, 25, 1);
    }

    static void LowerThird()
    {
        var send = CueVideo.Ndi.SendOpen("Lower third");
        const int w = 640, h = 360;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(20, 70, 160)), null, new Rect(40, 270, 360, 56), 6, 6);
            var ft = new FormattedText("Pastor Sam · Welcome", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 26, Brushes.White, 1.0);
            dc.DrawText(ft, new Point(56, 282));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        // WPF draws premultiplied; NDI's BGRA is straight alpha.
        for (int i = 0; i < px.Length; i += 4)
        {
            var a = px[i + 3];
            if (a is 0 or 255) continue;
            for (int c = 0; c < 3; c++) px[i + c] = (byte)Math.Min(255, px[i + c] * 255 / a);
        }
        while (true) { CueVideo.Ndi.SendFrame(send, px, w, h, 25, 1); Thread.Sleep(40); }
    }
}
