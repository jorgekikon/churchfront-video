using System.Net.Sockets;
using System.Text;

namespace CueATEM;

// Blackmagic ATEM control over the network (UDP 9910), with no Blackmagic software
// installed. The switcher sends its whole state when we connect and every change after,
// so the state here always matches the panel. A straight port of ATEM.swift.

public sealed record ATEMInput(int Id, string Long, string Short)
{
    /// <summary>Sources a program or preview bus can take: black, cameras and other
    /// inputs, bars, colors, media players and SuperSource.</summary>
    public bool IsBusSource =>
        Id == 0 || (Id >= 1 && Id <= 999) || Id == 1000 || Id == 2001 || Id == 2002
        || (Id >= 3000 && Id < 4000 && Id % 10 == 0) || (Id >= 6000 && Id < 6100);

    /// <summary>Aux outputs can also take clean feeds and M/E program and preview.</summary>
    public bool IsAuxSource => IsBusSource || (Id >= 7000 && Id < 7100) || (Id >= 10000 && Id < 11000);
    public bool IsExternal => Id >= 1 && Id <= 999;
    internal (int, int) SortKey => IsExternal ? (0, Id) : (1, Id);
}

public sealed class ATEMState
{
    public string Product = "";
    public string Version = "";
    public Dictionary<int, ATEMInput> Inputs = new();
    public Dictionary<int, int> Program = new();
    public Dictionary<int, int> Preview = new();
    /// <summary>Transition handle, 0 to 1, while a transition is under way.</summary>
    public Dictionary<int, double> Position = new();
    public Dictionary<int, bool> InTransition = new();
    /// <summary>Style of the next transition: 0 mix, 1 dip, 2 wipe, 3 DVE, 4 sting.</summary>
    public Dictionary<int, int> Style = new();
    /// <summary>Next transition: bit 0 background, bit 1 key 1, bit 2 key 2 and so on.</summary>
    public Dictionary<int, int> NextSelection = new();
    public Dictionary<int, int> MixRate = new();
    public Dictionary<int, Dictionary<int, bool>> KeyOnAir = new();
    public Dictionary<int, Dictionary<int, int>> KeyFill = new();
    public Dictionary<int, Dictionary<int, int>> KeyCut = new();
    public Dictionary<int, int> DskFill = new();
    public Dictionary<int, int> DskCut = new();
    public Dictionary<int, bool> DskOnAir = new();
    public Dictionary<int, bool> DskInTransition = new();
    public Dictionary<int, bool> DskTie = new();
    public Dictionary<int, bool> FtbBlack = new();
    public Dictionary<int, bool> FtbInTransition = new();
    public Dictionary<int, int> Aux = new();
    /// <summary>Recorded macros by index (0 based).</summary>
    public Dictionary<int, string> Macros = new();
    public int? MacroRunning;
    public int StreamStatus;
    public int RecordStatus;
    /// <summary>Only switchers with a built-in encoder or recorder report these.</summary>
    public bool HasStreaming;
    public bool HasRecording;
    /// <summary>Tally by source: bit 0 program, bit 1 preview (keys and DSKs included).</summary>
    public Dictionary<int, byte> Tally = new();

    public int MeCount => Math.Max(1, (Program.Count == 0 ? 0 : Program.Keys.Max()) + 1);
    public int KeyerCount => KeyOnAir.TryGetValue(0, out var k) ? k.Count : 0;
    public int DskCount => DskOnAir.Count;
    public int AuxCount => Aux.Count;
    public bool Streaming => (StreamStatus & 4) != 0;
    public bool StreamConnecting => (StreamStatus & 2) != 0;
    public bool Recording => (RecordStatus & 1) != 0;

    public List<ATEMInput> BusSources => Inputs.Values.Where(i => i.IsBusSource).OrderBy(i => i.SortKey).ToList();
    public List<ATEMInput> AuxSources => Inputs.Values.Where(i => i.IsAuxSource).OrderBy(i => i.SortKey).ToList();
    public string Name(int? id) => id is int i ? (Inputs.TryGetValue(i, out var x) ? x.Short : i.ToString()) : "?";

    public bool OnLive(int input) => Tally.TryGetValue(input, out var t) && (t & 1) != 0;
    public bool OnNext(int input) => Tally.TryGetValue(input, out var t) && (t & 2) != 0;

    public int? ProgramOf(int me) => Program.TryGetValue(me, out var v) ? v : null;
    public int? PreviewOf(int me) => Preview.TryGetValue(me, out var v) ? v : null;
    public bool Get(Dictionary<int, bool> d, int k) => d.TryGetValue(k, out var v) && v;

    public ATEMState Clone()
    {
        static Dictionary<int, Dictionary<int, T>> Deep<T>(Dictionary<int, Dictionary<int, T>> d) =>
            d.ToDictionary(kv => kv.Key, kv => new Dictionary<int, T>(kv.Value));
        var c = (ATEMState)MemberwiseClone();
        c.Inputs = new(Inputs); c.Program = new(Program); c.Preview = new(Preview); c.Position = new(Position);
        c.InTransition = new(InTransition); c.Style = new(Style); c.NextSelection = new(NextSelection); c.MixRate = new(MixRate);
        c.KeyOnAir = Deep(KeyOnAir); c.KeyFill = Deep(KeyFill); c.KeyCut = Deep(KeyCut);
        c.DskFill = new(DskFill); c.DskCut = new(DskCut); c.DskOnAir = new(DskOnAir); c.DskInTransition = new(DskInTransition);
        c.DskTie = new(DskTie); c.FtbBlack = new(FtbBlack); c.FtbInTransition = new(FtbInTransition); c.Aux = new(Aux);
        c.Macros = new(Macros); c.Tally = new(Tally);
        return c;
    }
}

public enum LinkKind { Idle, Connecting, Online, Offline }

public sealed record ATEMLink(LinkKind Kind, string Reason = "")
{
    public static readonly ATEMLink Idle = new(LinkKind.Idle);
    public static readonly ATEMLink Connecting = new(LinkKind.Connecting);
    public static readonly ATEMLink Online = new(LinkKind.Online);
    public static ATEMLink Offline(string why) => new(LinkKind.Offline, why);
}

public sealed class ATEMLogEntry
{
    public DateTime Time { get; init; }
    /// <summary>The four-letter ATEM command, such as CPvI (set preview).</summary>
    public string Command { get; init; } = "";
    public byte[] Data { get; init; } = Array.Empty<byte>();
    /// <summary>False when the switcher wasn't connected, so nothing went out.</summary>
    public bool Sent { get; init; }

    /// <summary>A plain description, such as "Preview, M/E 1: input 9".</summary>
    public string Describe(ATEMState s)
    {
        int U16(int i) => i + 1 < Data.Length ? Data[i] << 8 | Data[i + 1] : 0;
        int B(int i) => Data.Length > i ? Data[i] : 0;
        string In(int id) => s.Inputs.TryGetValue(id, out var x) ? x.Long : $"input {id}";
        var me = $"M/E {B(0) + 1}";
        return Command switch
        {
            "CPgI" => $"Program, {me}: {In(U16(2))}",
            "CPvI" => $"Preview, {me}: {In(U16(2))}",
            "DCut" => $"Cut, {me}",
            "DAut" => $"Auto transition, {me}",
            "CTPs" => $"T-bar, {me}: {U16(2) / 100}%",
            "CTTp" => $"Transition settings, M/E {B(1) + 1}",
            "CTMx" => $"Mix rate, {me}: {B(1)} frames",
            "CKOn" => $"Key {B(1) + 1} {(B(2) != 0 ? "on" : "off")}, {me}",
            "CDsL" => $"DSK {B(0) + 1} {(B(1) != 0 ? "on" : "off")}",
            "CDsT" => $"DSK {B(0) + 1} tie {(B(1) != 0 ? "on" : "off")}",
            "DDsA" => $"DSK {B(0) + 1} auto",
            "FtbA" => $"Fade to black, {me}",
            "CAuS" => $"Aux {B(1) + 1}: {In(U16(2))}",
            "MAct" => U16(0) == 0xFFFF ? "Stop macro" : $"Run macro {U16(0) + 1}",
            "StrR" => $"Streaming {(B(0) == 1 ? "start" : "stop")}",
            "RcTM" => $"Recording {(B(0) == 1 ? "start" : "stop")}",
            _ => Command,
        };
    }
}

public sealed class ATEMClient
{
    // Published on the main thread.
    public ATEMState State { get; private set; } = new();
    public ATEMLink Link { get; private set; } = ATEMLink.Idle;
    public string Host { get; private set; } = "";
    /// <summary>Which end of the T-bar is at rest, per M/E. Flips when a transition finishes, as on a hardware panel.</summary>
    public Dictionary<int, bool> TbarAtTop { get; } = new();
    /// <summary>The last commands this app sent (or tried to send while offline), newest last.</summary>
    public List<ATEMLogEntry> SentLog { get; } = new();
    /// <summary>Read-only (the probe): every command is refused here, before it can reach the
    /// switcher, and counted. Connecting and acknowledging what the switcher sends still happen;
    /// they change nothing on it.</summary>
    public bool ReadOnly { get; set; }
    private int _commandsSent, _commandsBlocked;
    /// <summary>Commands that went out to the switcher, and ones refused because of ReadOnly.</summary>
    public int CommandsSent => Volatile.Read(ref _commandsSent);
    public int CommandsBlocked => Volatile.Read(ref _commandsBlocked);
    /// <summary>Raised on the main thread whenever anything above changes.</summary>
    public event Action? Changed;

    private readonly SerialQueue _queue = new("cuevideo.atem");
    private UdpClient? _conn;
    private string _target = "";
    private int _port = 9910;
    private ushort _session = 0x53AB;
    private ushort _localId;
    private readonly List<(ushort Id, byte[] Data, double Sent, int Tries)> _inFlight = new();
    private double _lastInbound, _lastHello;
    private ushort? _lastRemoteId;
    private bool _connected, _ready;
    private Timer? _timer;
    private int _ticking;
    private ATEMState _s = new();
    private readonly Dictionary<int, double> _lastPosition = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static readonly HashSet<string>? Trace =
        Environment.GetEnvironmentVariable("CUEVIDEO_TRACE") is string t ? new HashSet<string>(t.Split(',')) : null;

    private const byte AckRequest = 0x01, NewSession = 0x02, Retransmit = 0x04, RetransmitRequest = 0x08, AckReply = 0x10;
    private static readonly byte[] Hello = { 0x10, 0x14, 0x53, 0xAB, 0, 0, 0, 0, 0, 0x3A, 0, 0, 0x01, 0, 0, 0, 0, 0, 0, 0 };

    private void Notify() => Changed?.Invoke();

    // MARK: Connection

    public void Connect(string host, int port = 9910)
    {
        var h = host.Trim();
        Host = h;
        Link = h.Length == 0 ? ATEMLink.Idle : ATEMLink.Connecting;
        Notify();
        _queue.Post(() =>
        {
            _target = h;
            _port = port;
            _s = new ATEMState();
            Publish();
            _timer ??= new Timer(_ =>
            {
                // Skip a tick if the last one hasn't run yet, so they never pile up.
                if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
                _queue.Post(() => { try { Tick(); } finally { Interlocked.Exchange(ref _ticking, 0); } });
            }, null, 0, 20);
            if (h.Length > 0) Open();
        });
    }

    public void Disconnect()
    {
        Host = "";
        Link = ATEMLink.Idle;
        Notify();
        _queue.Post(() =>
        {
            _target = "";
            _timer?.Dispose(); _timer = null;
            _conn?.Dispose(); _conn = null;
            _connected = false; _ready = false;
        });
    }

    private static double Now() => Clock.Elapsed.TotalSeconds;

    private void Open()
    {
        _conn?.Dispose();
        _conn = null;
        _connected = false;
        _ready = false;
        _inFlight.Clear();
        _session = 0x53AB;
        _localId = 0;
        _lastHello = Now();
        _lastInbound = Now();
        if (_target.Length == 0) return;
        UdpClient c;
        try
        {
            c = new UdpClient(System.Net.Sockets.AddressFamily.InterNetwork);
            IgnoreConnReset(c);
            c.Connect(_target, _port);
        }
        catch (Exception e)
        {
            SetLink(ATEMLink.Offline($"Can't reach {_target}: {e.Message}"));
            return;
        }
        _conn = c;
        StartReceiving(c);
        Send(c, Hello);
    }

    /// <summary>Windows reports an ICMP "port unreachable" as an error on the next UDP receive; ignore it.</summary>
    public static void IgnoreConnReset(UdpClient c)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { c.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0, 0, 0, 0 }, null); } catch { }
    }

    private void StartReceiving(UdpClient c)
    {
        new Thread(() =>
        {
            var any = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
            while (true)
            {
                byte[] data;
                try { data = c.Receive(ref any); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                catch { return; }  // closed
                if (data.Length >= 12) _queue.Post(() => { if (c == _conn) Handle(data); });
            }
        }) { IsBackground = true, Name = "cuevideo.atem.rx" }.Start();
    }

    private static void Send(UdpClient? c, byte[] d)
    {
        try { c?.Send(d, d.Length); } catch { }
    }

    private void SetLink(ATEMLink l)
    {
        MainThread.Post(() => { if (Link != l && _target.Length > 0) { Link = l; Notify(); } });
    }

    private void Tick()
    {
        if (_target.Length == 0) return;
        var t = Now();
        if (t - _lastInbound > 3)
        {
            if (t - _lastHello > 1.5)
            {
                SetLink(ATEMLink.Offline($"No answer from {_target}"));
                Open();
            }
            return;
        }
        for (int i = 0; i < _inFlight.Count; i++)
        {
            var p = _inFlight[i];
            if (t - p.Sent <= 0.06) continue;
            if (p.Tries >= 10) { SetLink(ATEMLink.Offline("The switcher stopped answering")); Open(); return; }
            var d = (byte[])p.Data.Clone();
            d[0] |= Retransmit << 3;
            Send(_conn, d);
            _inFlight[i] = (p.Id, p.Data, t, p.Tries + 1);
        }
    }

    private void Handle(byte[] b)
    {
        _lastInbound = Now();
        int flags = b[0] >> 3;
        int length = (b[0] & 0x07) << 8 | b[1];
        _session = (ushort)(b[2] << 8 | b[3]);
        var remoteId = (ushort)(b[10] << 8 | b[11]);

        if ((flags & NewSession) != 0)
        {
            _connected = true;
            _lastRemoteId = null;
            SendAck(remoteId);
            SetLink(ATEMLink.Connecting);
            return;
        }
        if ((flags & AckRequest) != 0)
        {
            SendAck(remoteId);
            // The switcher resends packets it isn't sure we got. Apply each packet once,
            // or an old update can land after a newer one and undo it.
            int fresh = _lastRemoteId is ushort last ? (remoteId - last + 32768) % 32768 : 1;
            if (fresh > 0 && fresh < 16384)
            {
                _lastRemoteId = remoteId;
                if (length > 12 && b.Length >= length) Parse(b.AsSpan(12, length - 12).ToArray());
            }
        }
        if ((flags & AckReply) != 0)
        {
            var acked = (ushort)(b[4] << 8 | b[5]);
            _inFlight.RemoveAll(p => (acked - p.Id + 32768) % 32768 < 16384);
        }
        if ((flags & RetransmitRequest) != 0)
            foreach (var p in _inFlight) Send(_conn, p.Data);
    }

    private void SendAck(ushort id)
    {
        var p = new byte[12];
        p[0] = AckReply << 3;
        p[1] = 12;
        p[2] = (byte)(_session >> 8); p[3] = (byte)(_session & 0xFF);
        p[4] = (byte)(id >> 8); p[5] = (byte)(id & 0xFF);
        Send(_conn, p);
    }

    // MARK: State from the switcher

    private static int U16(byte[] d, int i) => i + 1 < d.Length ? d[i] << 8 | d[i + 1] : 0;

    private static string Str(byte[] d, int from, int to)
    {
        if (from >= d.Length) return "";
        to = Math.Min(to, d.Length);
        int end = from;
        while (end < to && d[end] != 0) end++;
        return Encoding.UTF8.GetString(d, from, end - from);
    }

    private void Parse(byte[] payload)
    {
        int off = 0;
        while (off + 8 <= payload.Length)
        {
            int len = U16(payload, off);
            if (len < 8 || off + len > payload.Length) break;
            var name = Encoding.ASCII.GetString(payload, off + 4, 4);
            Apply(name, payload.AsSpan(off + 8, len - 8).ToArray());
            off += len;
        }
        if (_ready) Publish();
    }

    private void Apply(string name, byte[] d)
    {
        if (Trace != null && Trace.Contains(name))
            Console.WriteLine($"TRACE {Now():F3} {name} {Convert.ToHexString(d)}");

        Dictionary<int, T> Sub<T>(Dictionary<int, Dictionary<int, T>> m, int k)
        {
            if (!m.TryGetValue(k, out var x)) m[k] = x = new();
            return x;
        }

        switch (name)
        {
            case "InCm":
                _ready = true;
                SetLink(ATEMLink.Online);
                break;
            case "_ver" when d.Length >= 4: _s.Version = $"{U16(d, 0)}.{U16(d, 2)}"; break;
            case "_pin": _s.Product = Str(d, 0, 44); break;
            case "InPr" when d.Length >= 26:
            {
                int id = U16(d, 0);
                _s.Inputs[id] = new ATEMInput(id, Str(d, 2, 22), Str(d, 22, 26));
                break;
            }
            case "PrgI" when d.Length >= 4:
            {
                int me = d[0], src = U16(d, 2);
                // A finished transition changes program; the switcher doesn't always send a
                // last "not in transition" update after it, so end it here.
                if (_s.Program.TryGetValue(me, out var was) && was != src && _s.Get(_s.InTransition, me)) EndTransition(me);
                _s.Program[me] = src;
                break;
            }
            case "PrvI" when d.Length >= 4: _s.Preview[d[0]] = U16(d, 2); break;
            case "TrPs" when d.Length >= 6:
            {
                int me = d[0];
                bool was = _s.Get(_s.InTransition, me);
                bool isOn = d[1] != 0;
                double pos = U16(d, 4) / 10000.0;
                if (was && !isOn) EndTransition(me);
                _s.InTransition[me] = isOn;
                _s.Position[me] = pos;
                _lastPosition[me] = isOn ? pos : 0;
                break;
            }
            case "TrSS" when d.Length >= 5:
                _s.Style[d[0]] = d[3];
                _s.NextSelection[d[0]] = d[4];
                break;
            case "TMxP" when d.Length >= 2: _s.MixRate[d[0]] = d[1]; break;
            case "KeOn" when d.Length >= 3: Sub(_s.KeyOnAir, d[0])[d[1]] = d[2] != 0; break;
            case "DskS" when d.Length >= 3:
                _s.DskOnAir[d[0]] = d[1] != 0;
                _s.DskInTransition[d[0]] = d[2] != 0;
                break;
            case "DskP" when d.Length >= 2: _s.DskTie[d[0]] = d[1] != 0; break;
            case "KeBP" when d.Length >= 10:
                Sub(_s.KeyFill, d[0])[d[1]] = U16(d, 6);
                Sub(_s.KeyCut, d[0])[d[1]] = U16(d, 8);
                break;
            case "DskB" when d.Length >= 6:
                _s.DskFill[d[0]] = U16(d, 2);
                _s.DskCut[d[0]] = U16(d, 4);
                break;
            case "FtbS" when d.Length >= 3:
                _s.FtbBlack[d[0]] = d[1] != 0;
                _s.FtbInTransition[d[0]] = d[2] != 0;
                break;
            case "AuxS" when d.Length >= 4: _s.Aux[d[0]] = U16(d, 2); break;
            case "MPrp" when d.Length >= 8:
            {
                int i = U16(d, 0);
                if (d[2] != 0)
                {
                    var n = Str(d, 8, 8 + U16(d, 4));
                    _s.Macros[i] = n.Length == 0 ? $"Macro {i + 1}" : n;
                }
                else _s.Macros.Remove(i);
                break;
            }
            case "MRPr" when d.Length >= 4: _s.MacroRunning = (d[0] & 1) != 0 ? U16(d, 2) : null; break;
            case "StRS" when d.Length >= 2: _s.StreamStatus = U16(d, 0); _s.HasStreaming = true; break;
            case "RTMS" when d.Length >= 2: _s.RecordStatus = U16(d, 0); _s.HasRecording = true; break;
            case "TlSr" when d.Length >= 2:
            {
                int n = U16(d, 0);
                var t = new Dictionary<int, byte>();
                for (int i = 0; i < n; i++)
                    if (2 + i * 3 + 2 < d.Length) t[U16(d, 2 + i * 3)] = d[2 + i * 3 + 2];
                _s.Tally = t;
                break;
            }
        }
    }

    /// <summary>The T-bar's rest end flips when a transition ran past halfway, as on a hardware panel.</summary>
    private void EndTransition(int me)
    {
        if (_lastPosition.TryGetValue(me, out var p) && p > 0.5)
            MainThread.Post(() => { TbarAtTop[me] = !(TbarAtTop.TryGetValue(me, out var v) && v); Notify(); });
        _s.InTransition[me] = false;
        _s.Position[me] = 0;
        _lastPosition[me] = 0;
    }

    private void Publish()
    {
        var snap = _s.Clone();
        MainThread.Post(() => { State = snap; Notify(); });
    }

    // MARK: Commands

    private static byte[] Command(string name, byte[] data)
    {
        int len = 8 + data.Length;
        var o = new byte[len];
        o[0] = (byte)(len >> 8); o[1] = (byte)(len & 0xFF);
        Encoding.ASCII.GetBytes(name, 0, 4, o, 4);
        data.CopyTo(o, 8);
        return o;
    }

    private void SendCommand(string name, byte[] data)
    {
        if (ReadOnly)
        {
            Interlocked.Increment(ref _commandsBlocked);
            Console.Error.WriteLine($"read-only: {name} not sent");
            return;
        }
        _queue.Post(() =>
        {
            bool ok = _connected && _ready;
            if (ok) Interlocked.Increment(ref _commandsSent);
            var entry = new ATEMLogEntry { Time = DateTime.Now, Command = name, Data = data, Sent = ok };
            MainThread.Post(() =>
            {
                SentLog.Add(entry);
                if (SentLog.Count > 60) SentLog.RemoveRange(0, SentLog.Count - 60);
                Notify();
            });
            if (!ok) return;
            var commands = Command(name, data);
            _localId = (ushort)((_localId + 1) % 32768);
            int len = 12 + commands.Length;
            var p = new byte[len];
            p[0] = (byte)((AckRequest << 3) | ((len >> 8) & 0x07));
            p[1] = (byte)(len & 0xFF);
            p[2] = (byte)(_session >> 8); p[3] = (byte)(_session & 0xFF);
            p[10] = (byte)(_localId >> 8); p[11] = (byte)(_localId & 0xFF);
            commands.CopyTo(p, 12);
            _inFlight.Add((_localId, p, Now(), 0));
            Send(_conn, p);
        });
    }

    private static byte[] HL(int v) { var c = (ushort)Math.Clamp(v, 0, 0xFFFF); return new[] { (byte)(c >> 8), (byte)(c & 0xFF) }; }
    private static byte B(int v) => (byte)Math.Clamp(v, 0, 255);
    private static byte[] Cat(params object[] parts)
    {
        var o = new List<byte>();
        foreach (var p in parts) { if (p is byte b) o.Add(b); else if (p is byte[] a) o.AddRange(a); else if (p is int i) o.Add(B(i)); }
        return o.ToArray();
    }

    public void SetProgram(int source, int me = 0) => SendCommand("CPgI", Cat(B(me), 0, HL(source)));
    public void SetPreview(int source, int me = 0) => SendCommand("CPvI", Cat(B(me), 0, HL(source)));
    public void Cut(int me = 0) => SendCommand("DCut", Cat(B(me), 0, 0, 0));
    public void Auto(int me = 0) => SendCommand("DAut", Cat(B(me), 0, 0, 0));
    /// <summary>0 to 1 from the start of the transition.</summary>
    public void SetPosition(double v, int me = 0) => SendCommand("CTPs", Cat(B(me), 0, HL((int)Math.Round(Math.Clamp(v, 0, 1) * 10000))));
    public void SetStyle(int style, int me = 0) => SendCommand("CTTp", Cat(1, B(me), B(style), 0));
    public void SetNextSelection(int mask, int me = 0) => SendCommand("CTTp", Cat(2, B(me), 0, B(Math.Max(1, mask))));
    public void SetMixRate(int frames, int me = 0) => SendCommand("CTMx", Cat(B(me), B(Math.Clamp(frames, 1, 250)), 0, 0));
    public void SetKeyOnAir(int keyer, bool on, int me = 0) => SendCommand("CKOn", Cat(B(me), B(keyer), on ? 1 : 0, 0));
    public void SetDSKOnAir(int dsk, bool on) => SendCommand("CDsL", Cat(B(dsk), on ? 1 : 0, 0, 0));
    public void SetDSKTie(int dsk, bool on) => SendCommand("CDsT", Cat(B(dsk), on ? 1 : 0, 0, 0));
    public void DskAuto(int dsk) => SendCommand("DDsA", Cat(B(dsk), 0, 0, 0));
    public void FadeToBlack(int me = 0) => SendCommand("FtbA", Cat(B(me), 0, 0, 0));
    public void SetAux(int aux, int source) => SendCommand("CAuS", Cat(1, B(aux), HL(source)));
    public void RunMacro(int index) => SendCommand("MAct", Cat(HL(index), 0, 0));
    public void StopMacro() => SendCommand("MAct", new byte[] { 0xFF, 0xFF, 1, 0 });
    public void SetStreaming(bool on) => SendCommand("StrR", Cat(on ? 1 : 0, 0, 0, 0));
    public void SetRecording(bool on) => SendCommand("RcTM", Cat(on ? 1 : 0, 0, 0, 0));
}
