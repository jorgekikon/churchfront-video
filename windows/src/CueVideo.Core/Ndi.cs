using System.Runtime.InteropServices;
using CueATEM;

namespace CueVideo;

// NDI on Windows, a port of Sources/NDIBridge (NDIBridge.c) and NDIFeed.swift.
//
// The runtime is loaded when first needed, from NDI Tools or the NDI runtime installer
// (NDI_RUNTIME_DIR_V6 / _V5, as NDI documents), so nothing NDI is needed to build and the app
// runs without it: NDI pictures just don't show. Struct layouts follow the NDI SDK headers
// (Processing.NDI.*.h, versions 5 and 6). NDI is a registered trademark of Vizrt NDI AB.

/// <summary>The NDI runtime: finding sources, receiving and (for the self-tests) sending.</summary>
public static class Ndi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Source { public IntPtr Name; public IntPtr Url; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FindCreate { [MarshalAs(UnmanagedType.U1)] public bool ShowLocal; public IntPtr Groups; public IntPtr ExtraIps; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecvCreateV3 { public Source Source; public int ColorFormat; public int Bandwidth; [MarshalAs(UnmanagedType.U1)] public bool AllowFields; public IntPtr RecvName; }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrameV2
    {
        public int Xres, Yres; public uint FourCC; public int FrameRateN, FrameRateD; public float Aspect;
        public int FrameFormat; public long Timecode; public IntPtr Data; public int Stride; public IntPtr Metadata; public long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SendCreate { public IntPtr Name; public IntPtr Groups; [MarshalAs(UnmanagedType.U1)] public bool ClockVideo; [MarshalAs(UnmanagedType.U1)] public bool ClockAudio; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)] private delegate bool InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FindCreateFn(ref FindCreate c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)] private delegate bool FindWaitFn(IntPtr f, uint ms);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FindSourcesFn(IntPtr f, out uint n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr RecvCreateFn(ref RecvCreateV3 c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RecvCaptureFn(IntPtr r, ref VideoFrameV2 v, IntPtr audio, IntPtr meta, uint ms);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RecvFreeFn(IntPtr r, ref VideoFrameV2 v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyFn(IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr SendCreateFn(ref SendCreate c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SendVideoFn(IntPtr s, ref VideoFrameV2 v);

    private static readonly object Lock = new();
    private static bool _tried;
    private static IntPtr _lib, _finder;
    private static FindCreateFn? _findCreate; private static FindWaitFn? _findWait; private static FindSourcesFn? _findSources;
    private static RecvCreateFn? _recvCreate; private static RecvCaptureFn? _recvCapture; private static RecvFreeFn? _recvFree; private static DestroyFn? _recvDestroy;
    private static SendCreateFn? _sendCreate; private static SendVideoFn? _sendVideo; private static DestroyFn? _sendDestroy;

    /// <summary>Where the runtime was loaded from, or null when it isn't installed.</summary>
    public static string? Loaded { get; private set; }

    public const uint FourCCBGRA = 'B' | ('G' << 8) | ('R' << 16) | ((uint)'A' << 24);

    private static IEnumerable<string> Places()
    {
        const string dll = "Processing.NDI.Lib.x64.dll";
        foreach (var v in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" })
            if (Environment.GetEnvironmentVariable(v) is string d && d.Length > 0) yield return Path.Combine(d, dll);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(pf, "NDI", "NDI 6 Runtime", "v6", dll);
        yield return Path.Combine(pf, "NDI", "NDI 5 Runtime", "v5", dll);
        yield return Path.Combine(pf, "NDI", "NDI 6 SDK", "Bin", "x64", dll);
    }

    /// <summary>Loads and starts the runtime once. False when it isn't installed.</summary>
    public static bool Load()
    {
        lock (Lock)
        {
            if (_tried) return Loaded != null;
            _tried = true;
            foreach (var p in Places())
            {
                if (!File.Exists(p) || !NativeLibrary.TryLoad(p, out var lib)) continue;
                T? Fn<T>(string name) where T : Delegate => NativeLibrary.TryGetExport(lib, name, out var a) ? Marshal.GetDelegateForFunctionPointer<T>(a) : null;
                var init = Fn<InitializeFn>("NDIlib_initialize");
                _findCreate = Fn<FindCreateFn>("NDIlib_find_create_v2");
                _findWait = Fn<FindWaitFn>("NDIlib_find_wait_for_sources");
                _findSources = Fn<FindSourcesFn>("NDIlib_find_get_current_sources");
                _recvCreate = Fn<RecvCreateFn>("NDIlib_recv_create_v3");
                _recvCapture = Fn<RecvCaptureFn>("NDIlib_recv_capture_v2");
                _recvFree = Fn<RecvFreeFn>("NDIlib_recv_free_video_v2");
                _recvDestroy = Fn<DestroyFn>("NDIlib_recv_destroy");
                _sendCreate = Fn<SendCreateFn>("NDIlib_send_create");
                _sendVideo = Fn<SendVideoFn>("NDIlib_send_send_video_v2");
                _sendDestroy = Fn<DestroyFn>("NDIlib_send_destroy");
                if (init == null || _findCreate == null || _recvCreate == null || _recvCapture == null || !init()) { NativeLibrary.Free(lib); continue; }
                _lib = lib;
                Loaded = p;
                return true;
            }
            return false;
        }
    }

    private static readonly object ListLock = new();

    /// <summary>The names of the NDI sources on the network now, waiting up to `waitMs` for changes.</summary>
    public static List<string> Sources(int waitMs)
    {
        if (!Load()) return new();
        lock (Lock)
        {
            if (_finder == IntPtr.Zero)
            {
                var fc = new FindCreate { ShowLocal = true };
                _finder = _findCreate!(ref fc);
            }
        }
        if (_finder == IntPtr.Zero) return new();
        if (waitMs > 0) _findWait!(_finder, (uint)waitMs);
        // The list belongs to the finder until the next call, and several threads ask.
        lock (ListLock)
        {
            var list = _findSources!(_finder, out var n);
            var names = new List<string>();
            var size = Marshal.SizeOf<Source>();
            for (int i = 0; i < n; i++)
            {
                var s = Marshal.PtrToStructure<Source>(list + i * size);
                if (s.Name != IntPtr.Zero && Marshal.PtrToStringUTF8(s.Name) is string name) names.Add(name);
            }
            return names;
        }
    }

    /// <summary>A receiver for a source. `lowest`: the sender's small preview stream (about 640 wide).</summary>
    public static IntPtr RecvOpen(string source, bool lowest)
    {
        if (!Load()) return IntPtr.Zero;
        var name = Marshal.StringToCoTaskMemUTF8(source);
        var me = Marshal.StringToCoTaskMemUTF8("Churchfront Video");
        try
        {
            var rc = new RecvCreateV3 { Source = new Source { Name = name }, ColorFormat = 0 /* BGRX, or BGRA with alpha */, Bandwidth = lowest ? 0 : 100, RecvName = me };
            var r = _recvCreate!(ref rc);
            if (r != IntPtr.Zero) Interlocked.Increment(ref _openReceivers);
            return r;
        }
        finally { Marshal.FreeCoTaskMem(name); Marshal.FreeCoTaskMem(me); }
    }

    private static int _openReceivers;

    /// <summary>On quit: waits up to `ms` for every receiver to be closed, so none is still
    /// talking to a sender when the app ends. True when they all were.</summary>
    public static bool WaitReceiversClosed(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Volatile.Read(ref _openReceivers) > 0 && Environment.TickCount64 < until) Thread.Sleep(20);
        return Volatile.Read(ref _openReceivers) == 0;
    }

    /// <summary>Waits up to `ms` for a video frame. True with a frame to release with RecvFree.</summary>
    public static bool RecvFrame(IntPtr recv, int ms, out VideoFrameV2 frame)
    {
        frame = default;
        if (recv == IntPtr.Zero) return false;
        return _recvCapture!(recv, ref frame, IntPtr.Zero, IntPtr.Zero, (uint)ms) == 1 && frame.Data != IntPtr.Zero;
    }

    public static void RecvFree(IntPtr recv, ref VideoFrameV2 frame) { if (recv != IntPtr.Zero) _recvFree?.Invoke(recv, ref frame); }
    public static void RecvClose(IntPtr recv)
    {
        if (recv == IntPtr.Zero) return;
        _recvDestroy?.Invoke(recv);
        Interlocked.Decrement(ref _openReceivers);
    }

    /// <summary>An NDI sender (FakeDevices uses this to stand in for cameras and graphics).</summary>
    public static IntPtr SendOpen(string name)
    {
        if (!Load() || _sendCreate == null) return IntPtr.Zero;
        var n = Marshal.StringToCoTaskMemUTF8(name);
        try { var sc = new SendCreate { Name = n, ClockVideo = true }; return _sendCreate(ref sc); }
        finally { Marshal.FreeCoTaskMem(n); }
    }

    /// <summary>Sends one BGRA frame (straight alpha), progressive.</summary>
    public static void SendFrame(IntPtr send, byte[] bgra, int width, int height, int fpsN = 30, int fpsD = 1)
    {
        if (send == IntPtr.Zero || _sendVideo == null) return;
        var h = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            var v = new VideoFrameV2
            {
                Xres = width, Yres = height, FourCC = FourCCBGRA, FrameRateN = fpsN, FrameRateD = fpsD, Aspect = (float)width / height,
                FrameFormat = 1, Timecode = long.MaxValue, Data = h.AddrOfPinnedObject(), Stride = width * 4,
            };
            _sendVideo(send, ref v);
        }
        finally { h.Free(); }
    }

    public static void SendClose(IntPtr send) { if (send != IntPtr.Zero) _sendDestroy?.Invoke(send); }

    // MARK: Names

    /// <summary>The sender to open for a saved source name. A source is named "COMPUTER (Channel)",
    /// and the computer half isn't stable: the same machine can announce itself by different
    /// names on different days. So when the saved name isn't on the network but exactly one sender
    /// has the same channel, that's the one. Otherwise the saved name, as it is.</summary>
    public static string Match(string wanted, IReadOnlyList<string> names)
    {
        if (names.Contains(wanted)) return wanted;
        if (Channel(wanted) is not string ch) return wanted;
        var same = names.Where(n => Channel(n) == ch).ToList();
        return same.Count == 1 ? same[0] : wanted;
    }

    /// <summary>"COMPUTER (Channel)" gives "Channel".</summary>
    public static string? Channel(string full)
    {
        var open = full.IndexOf('(');
        return open < 0 || !full.EndsWith(')') ? null : full[(open + 1)..^1];
    }

    /// <summary>"BOOTH-PC (ATEM Multiview)" reads as "ATEM Multiview · booth-pc".</summary>
    public static string ShortName(string full)
    {
        var open = full.IndexOf('(');
        if (open < 0 || !full.EndsWith(')')) return full;
        var host = full[..open].Trim().ToLowerInvariant().Replace(".local", "");
        return $"{full[(open + 1)..^1]} · {host}";
    }
}

/// <summary>One frame from an NDI source, copied out of the runtime: BGRA (or BGRX), rows of Width × 4.</summary>
public sealed class NdiFrame
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Bgra { get; init; } = Array.Empty<byte>();
    public bool Alpha { get; init; }
    /// <summary>The shape to show it at (width ÷ height), which differs from Width ÷ Height when
    /// the pixels aren't square. 0 when the sender didn't say.</summary>
    public double Aspect { get; init; }
    public double ShowAspect => Aspect > 0.2 && Aspect < 5 ? Aspect : (double)Width / Height;

    /// <summary>As square-pixel BGR for auto tracking: stretched to the shape the sender says to
    /// show it at, so people aren't squashed.</summary>
    public TrackFrame ToTrackFrame()
    {
        var w = Math.Max(1, (int)Math.Round(Height * ShowAspect));
        var h = Height;
        var px = new byte[w * h * 3];
        var cols = new int[w];
        for (int x = 0; x < w; x++) cols[x] = Math.Min(Width - 1, (int)((x + 0.5) * Width / w));
        for (int y = 0; y < h; y++)
        {
            int src = y * Width * 4, dst = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                var s = src + cols[x] * 4;
                px[dst++] = Bgra[s]; px[dst++] = Bgra[s + 1]; px[dst++] = Bgra[s + 2];
            }
        }
        return new TrackFrame { Width = w, Height = h, Bgr = px };
    }
}

/// <summary>
/// A live picture from one NDI source, such as a PTZOptics camera, Churchfront Screens' graphics
/// or its lower thirds. It asks the sender for NDI's low-bandwidth preview stream (about 640
/// wide), so watching costs little network or processor time, and keeps the alpha channel when
/// the source sends one, so a lower third can be laid over a camera picture. Receives on its own
/// thread; the newest frame is there to read at any time.
/// </summary>
public sealed class NdiFeed
{
    public string Source { get; }
    private readonly object _lock = new();
    private int _generation;
    private NdiFrame? _frame;
    private Action<TrackFrame>[] _trackers = Array.Empty<Action<TrackFrame>>();

    public NdiFeed(string source) { Source = source; }

    /// <summary>The newest frame, and a number that changes with every new one.</summary>
    public NdiFrame? Frame { get { lock (_lock) return _frame; } }
    public int Version { get; private set; }
    public bool IsLive { get; private set; }
    public bool HasAlpha { get; private set; }
    /// <summary>The frames' pixel size and shape, for Status.</summary>
    public string Shape { get; private set; } = "";
    /// <summary>The sender's name it's receiving from now (it may differ from Source; see Ndi.Match).</summary>
    public string? Opened { get; private set; }

    /// <summary>Raised on the receiving thread with each new frame (about 20 a second).</summary>
    public event Action? FrameArrived;

    public void AddTracker(Action<TrackFrame> f) { lock (_lock) _trackers = _trackers.Append(f).ToArray(); }
    public void RemoveTracker(Action<TrackFrame> f) { lock (_lock) _trackers = _trackers.Where(x => x != f).ToArray(); }

    public void Start()
    {
        int gen;
        lock (_lock) gen = ++_generation;
        var t = new Thread(() => Receive(gen)) { IsBackground = true, Name = "NDI preview" };
        t.Start();
    }

    public void Stop() { lock (_lock) _generation++; IsLive = false; }

    private bool Current(int gen) { lock (_lock) return _generation == gen; }

    private void Receive(int gen)
    {
        // The runtime may be missing, or the source not on the network yet: keep trying.
        while (Current(gen))
        {
            var opened = Ndi.Match(Source, Ndi.Sources(800));
            var recv = Ndi.RecvOpen(opened, lowest: true);
            if (recv == IntPtr.Zero) { Thread.Sleep(2000); continue; }
            Opened = opened;
            DateTime lastShown = DateTime.MinValue, lastFrame = DateTime.UtcNow, lastLook = DateTime.UtcNow;
            while (Current(gen))
            {
                if (!Ndi.RecvFrame(recv, 250, out var v))
                {
                    if ((DateTime.UtcNow - lastFrame).TotalSeconds > 2) IsLive = false;
                    // No picture for a while: the sender may be back under another computer name.
                    if ((DateTime.UtcNow - lastFrame).TotalSeconds > 4 && (DateTime.UtcNow - lastLook).TotalSeconds > 3)
                    {
                        lastLook = DateTime.UtcNow;
                        if (Ndi.Match(Source, Ndi.Sources(0)) != opened) break;
                    }
                    continue;
                }
                lastFrame = DateTime.UtcNow;
                try
                {
                    // A preview doesn't need every frame: about 20 a second is plenty.
                    if ((lastFrame - lastShown).TotalSeconds >= 0.05 && v.Xres > 0 && v.Yres > 0)
                    {
                        lastShown = lastFrame;
                        var w = v.Xres; var h = v.Yres;
                        var px = new byte[w * h * 4];
                        for (int y = 0; y < h; y++) Marshal.Copy(v.Data + y * v.Stride, px, y * w * 4, w * 4);
                        var alpha = v.FourCC == Ndi.FourCCBGRA || v.FourCC == ('R' | ('G' << 8) | ('B' << 16) | ((uint)'A' << 24));
                        var f = new NdiFrame { Width = w, Height = h, Bgra = px, Alpha = alpha, Aspect = v.Aspect };
                        Action<TrackFrame>[] trackers;
                        lock (_lock) { if (!Current(gen)) break; _frame = f; Version++; trackers = _trackers; }
                        IsLive = true; HasAlpha = alpha;
                        Shape = $"{w}x{h}" + (v.Aspect > 0 ? $", shown {v.Aspect:0.00}:1" : "");
                        FrameArrived?.Invoke();
                        if (trackers.Length > 0) { var tf = f.ToTrackFrame(); foreach (var t in trackers) { try { t(tf); } catch { } } }
                    }
                }
                finally { Ndi.RecvFree(recv, ref v); }
            }
            Ndi.RecvClose(recv);
        }
    }
}

/// <summary>One receiver per NDI source, shared by everything showing it, running only while
/// shown (and a few seconds more, so flicking between pages doesn't restart it). Main thread.</summary>
public static class NdiHub
{
    private static readonly Dictionary<string, (NdiFeed Feed, int Users)> Feeds = new();

    /// <summary>Raised on the receiving thread when any feed has a new frame.</summary>
    public static event Action? FrameArrived;

    public static NdiFeed Feed(string source)
    {
        if (Feeds.TryGetValue(source, out var f)) return f.Feed;
        var feed = new NdiFeed(source);
        feed.FrameArrived += () => FrameArrived?.Invoke();
        Feeds[source] = (feed, 0);
        return feed;
    }

    public static void Retain(string source)
    {
        var f = Feed(source);
        var users = Feeds[source].Users + 1;
        Feeds[source] = (f, users);
        if (users == 1) f.Start();
    }

    public static void Release(string source)
    {
        if (!Feeds.TryGetValue(source, out var f) || f.Users == 0) return;
        Feeds[source] = (f.Feed, f.Users - 1);
        if (f.Users > 1) return;
        MainThread.After(5, () => { if (Feeds.TryGetValue(source, out var g) && g.Users == 0) g.Feed.Stop(); });
    }

    /// <summary>On quit: stops every feed. Each closes its receiver within a quarter second
    /// (see Ndi.WaitReceiversClosed).</summary>
    public static void StopAll() { foreach (var f in Feeds.Values) f.Feed.Stop(); }

    /// <summary>For status: whether a source's picture is coming in (null when nothing is showing it).</summary>
    public static bool? IsLive(string source) => Feeds.TryGetValue(source, out var f) && f.Users > 0 ? f.Feed.IsLive : null;
}

/// <summary>The NDI sources on the network, for pickers: listed in the background, kept for the
/// next picker to open at once. Main thread.</summary>
public static class NdiDirectory
{
    public static List<string> Sources { get; private set; } = new();
    public static bool Scanning { get; private set; }
    /// <summary>False once a look found the runtime missing.</summary>
    public static bool HasRuntime { get; private set; } = true;
    public static event Action? Changed;

    public static void Scan(Action? done = null)
    {
        if (Scanning) { if (done != null) Changed += Once(done); return; }
        Scanning = true;
        Changed?.Invoke();
        Task.Run(() =>
        {
            var ok = Ndi.Load();
            var list = ok ? Ndi.Sources(1500) : new();
            MainThread.Post(() =>
            {
                HasRuntime = ok; Sources = list.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); Scanning = false;
                Changed?.Invoke();
                done?.Invoke();
            });
        });
    }

    private static Action Once(Action a)
    {
        Action? h = null;
        h = () => { if (!Scanning) { Changed -= h; a(); } };
        return h;
    }
}
