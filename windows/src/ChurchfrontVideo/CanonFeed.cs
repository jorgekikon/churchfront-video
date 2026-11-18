using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CueATEM;

namespace CueVideo.App;

/// <summary>
/// Live picture from a Canon PTZ camera: the same motion-JPEG preview its web page shows
/// (/-wvhttp-01-/video.cgi). Needs no login, claims no control session and changes nothing on
/// the camera, so it is safe while live. Frames are decoded off the UI thread.
/// </summary>
public sealed class CanonFeed
{
    public BitmapSource? Image { get; private set; }
    public bool IsLive { get; private set; }
    public readonly string Host;
    public readonly ushort Port;
    private CancellationTokenSource? _cts;
    private int _decoding;
    private DateTime _lastFrame;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };

    public CanonFeed(string host, ushort port) { Host = host; Port = port; }

    private Action<TrackFrame>[] _trackers = Array.Empty<Action<TrackFrame>>();
    private readonly object _trackLock = new();
    /// <summary>Auto tracking: also hand each frame over as pixels (called on the decoding thread).</summary>
    public void AddTracker(Action<TrackFrame> f) { lock (_trackLock) _trackers = _trackers.Append(f).ToArray(); }
    public void RemoveTracker(Action<TrackFrame> f) { lock (_trackLock) _trackers = _trackers.Where(x => x != f).ToArray(); }

    private string Url => $"http://{Host}{(Port == 80 ? "" : ":" + Port)}/-wvhttp-01-/video.cgi";

    public void Start()
    {
        if (_cts != null || Host.Length == 0) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        Task.Run(() => Run(cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel(); _cts = null;
        SetLive(false);
    }

    private async Task Run(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stop);
            _lastFrame = DateTime.UtcNow;
            // A stream can stall without closing; reopen it when frames stop.
            _ = Task.Run(async () =>
            {
                while (!attempt.IsCancellationRequested)
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    if ((DateTime.UtcNow - _lastFrame).TotalSeconds > 5) { try { attempt.Cancel(); } catch { } return; }
                }
            });
            try
            {
                using var r = await Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                r.EnsureSuccessStatusCode();
                using var s = await r.Content.ReadAsStreamAsync(attempt.Token);
                await ReadFrames(s, attempt.Token);
            }
            catch { }
            try { attempt.Cancel(); } catch { }
            SetLive(false);
            try { await Task.Delay(2000, stop); } catch { return; }
        }
    }

    /// <summary>Pulls each JPEG (FFD8 … FFD9) out of the multipart stream.</summary>
    private async Task ReadFrames(Stream s, CancellationToken token)
    {
        var buf = new byte[1 << 16];
        var frame = new MemoryStream();
        bool inFrame = false;
        byte prev = 0;
        while (!token.IsCancellationRequested)
        {
            var n = await s.ReadAsync(buf, token);
            if (n <= 0) return;
            for (int i = 0; i < n; i++)
            {
                var b = buf[i];
                if (!inFrame)
                {
                    if (prev == 0xFF && b == 0xD8) { inFrame = true; frame.SetLength(0); frame.WriteByte(0xFF); frame.WriteByte(0xD8); }
                }
                else
                {
                    frame.WriteByte(b);
                    if (prev == 0xFF && b == 0xD9) { inFrame = false; Deliver(frame.ToArray()); b = 0; }
                    else if (frame.Length > 8 << 20) { inFrame = false; }
                }
                prev = b;
            }
        }
    }

    private void Deliver(byte[] jpeg)
    {
        _lastFrame = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _decoding, 1) == 1) return;   // still decoding the last one: skip this frame
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 960;
            bmp.StreamSource = new MemoryStream(jpeg);
            bmp.EndInit();
            bmp.Freeze();
            MainThread.Post(() => { Image = bmp; IsLive = true; CanonFeedHub.FrameArrived?.Invoke(); });
            // Auto tracking gets the same frame as plain pixels.
            var trackers = _trackers;
            if (trackers.Length > 0)
            {
                var bgr = new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0);
                int w = bgr.PixelWidth, h = bgr.PixelHeight;
                var px = new byte[w * h * 3];
                bgr.CopyPixels(px, w * 3, 0);
                var f = new TrackFrame { Width = w, Height = h, Bgr = px };
                foreach (var t in trackers) { try { t(f); } catch { } }
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _decoding, 0); }
    }

    private void SetLive(bool v) => MainThread.Post(() => { if (IsLive != v) { IsLive = v; CanonFeedHub.FrameArrived?.Invoke(); } });
}

/// <summary>One feed per camera address, kept running while anything on screen shows it.</summary>
public static class CanonFeedHub
{
    private static readonly Dictionary<string, (CanonFeed Feed, int Count)> Feeds = new();
    public static Action? FrameArrived;

    private static string Key(PTZCamera c) => $"{c.Host}:{c.Port}";

    public static CanonFeed? Feed(PTZCamera c) => Feeds.TryGetValue(Key(c), out var f) ? f.Feed : null;

    public static void Retain(PTZCamera c)
    {
        if (c.Proto != VISCAProtocol.CanonXC || c.Host.Length == 0) return;
        var k = Key(c);
        if (Feeds.TryGetValue(k, out var f)) { Feeds[k] = (f.Feed, f.Count + 1); return; }
        var feed = new CanonFeed(c.Host, c.Port);
        Feeds[k] = (feed, 1);
        feed.Start();
    }

    public static void Release(PTZCamera c)
    {
        var k = Key(c);
        if (!Feeds.TryGetValue(k, out var f)) return;
        if (f.Count > 1) { Feeds[k] = (f.Feed, f.Count - 1); return; }
        // Keep it a few seconds, so flicking between cameras doesn't restart the stream.
        Feeds[k] = (f.Feed, 0);
        MainThread.After(5, () =>
        {
            if (Feeds.TryGetValue(k, out var g) && g.Count == 0) { g.Feed.Stop(); Feeds.Remove(k); }
        });
    }

    /// <summary>The decoder Claude's camera_snapshots uses: color measurements over a 160×90 copy,
    /// and a JPEG at most 640 wide.</summary>
    public static (JsonObject, string?)? Imaging(byte[] jpeg, bool picture)
    {
        try
        {
            var dec = BitmapDecoder.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var src = dec.Frames[0];
            var small = new TransformedBitmap(src, new ScaleTransform(160.0 / src.PixelWidth, 90.0 / src.PixelHeight));
            var rgb = new FormatConvertedBitmap(small, PixelFormats.Rgb24, null, 0);
            int w = rgb.PixelWidth, h = rgb.PixelHeight, stride = w * 3;
            var px = new byte[stride * h];
            rgb.CopyPixels(px, stride, 0);
            var info = CueVideo.Snapshots.Measure(px, w * h);
            string? out64 = null;
            if (picture)
            {
                var scale = Math.Min(1, 640.0 / src.PixelWidth);
                var enc = new JpegBitmapEncoder { QualityLevel = 75 };
                enc.Frames.Add(BitmapFrame.Create(scale < 1 ? new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src));
                using var ms = new MemoryStream();
                enc.Save(ms);
                out64 = Convert.ToBase64String(ms.ToArray());
            }
            return (info, out64);
        }
        catch { return null; }
    }
}
