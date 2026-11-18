using System.Windows.Media;
using System.Windows.Media.Imaging;
using CueATEM;

namespace CueVideo.App;

/// <summary>
/// A source's live picture, wherever one is shown (a port of CameraPicture / SourcePicture on the
/// Mac): a Canon camera's own preview, or the NDI picture set for that switcher input (a
/// PTZOptics camera, a graphics computer). A mix output shows whatever is live on that mix.
/// Main thread.
/// </summary>
public static class Pictures
{
    private static Booth B => Booth.Shared;

    /// <summary>A mix output (10010, 10020…) as what's live on it; anything else as itself.</summary>
    public static int? Resolve(int? input)
    {
        if (input is not int id) return null;
        if (id >= 10000 && id < 11000 && id % 10 == 0) return B.Atem.State.ProgramOf((id - 10000) / 10 - 1) ?? id;
        return id;
    }

    /// <summary>The Canon camera wired to this input, whose own preview is its picture.</summary>
    public static PTZCamera? Canon(int? input) =>
        Resolve(input) is int id ? B.Ptz.Cameras.FirstOrDefault(c => c.AtemInput == id && c.Proto == VISCAProtocol.CanonXC && c.Host.Length > 0) : null;

    /// <summary>The NDI source showing this input, when it isn't a Canon camera.</summary>
    public static string? Ndi(int? input) => Canon(input) == null ? B.Store.Ndi(Resolve(input)) : null;

    public static bool Has(int? input) => Canon(input) != null || Ndi(input) != null;

    /// <summary>The picture now, or null while it isn't coming in.</summary>
    public static ImageSource? Image(int? input)
    {
        if (Canon(input) is PTZCamera c) return CanonFeedHub.Feed(c) is { IsLive: true } f ? f.Image : null;
        return Ndi(input) is string n ? NdiImage(n) : null;
    }

    private static readonly Dictionary<string, (WriteableBitmap? Bmp, int Version)> Cache = new();

    /// <summary>An NDI source's newest frame as a bitmap, kept and updated in place. Pixels that
    /// aren't square (a 16:9 picture sent as 720x480) are shown at the shape the sender states.</summary>
    public static ImageSource? NdiImage(string source, bool liveOnly = true)
    {
        var feed = NdiHub.Feed(source);
        if (liveOnly && !feed.IsLive) return null;
        var f = feed.Frame;
        if (f == null) return null;
        Cache.TryGetValue(source, out var c);
        if (c.Bmp != null && c.Version == feed.Version) return c.Bmp;
        var dpiX = 96.0 * f.Width / (f.Height * f.ShowAspect);
        var bmp = c.Bmp;
        if (bmp == null || bmp.PixelWidth != f.Width || bmp.PixelHeight != f.Height || Math.Abs(bmp.DpiX - dpiX) > 0.01 || (bmp.Format == PixelFormats.Bgra32) != f.Alpha)
            bmp = new WriteableBitmap(f.Width, f.Height, dpiX, 96, f.Alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);
        Cache[source] = (bmp, feed.Version);
        return bmp;
    }

    /// <summary>An NDI frame as a JPEG (for camera_snapshots), at the shape the sender says to show it.</summary>
    public static byte[]? Jpeg(NdiFrame f)
    {
        try
        {
            BitmapSource src = BitmapSource.Create(f.Width, f.Height, 96, 96, PixelFormats.Bgr32, null, f.Bgra, f.Width * 4);
            var w = Math.Round(f.Height * f.ShowAspect);
            if (Math.Abs(w - f.Width) > 1) src = new TransformedBitmap(src, new ScaleTransform(w / f.Width, 1));
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new System.IO.MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>Keeps the pictures a screen shows running, and lets go of the rest.</summary>
    public sealed class Keeper
    {
        private readonly HashSet<Guid> _canon = new();
        private readonly HashSet<string> _ndi = new();

        /// <summary>The pictures wanted now: these inputs' (Canon or NDI), plus NDI sources by name.</summary>
        public void Want(IEnumerable<int?> inputs, IEnumerable<string>? ndi = null)
        {
            var cams = new HashSet<Guid>(); var names = new HashSet<string>();
            foreach (var i in inputs)
            {
                if (Canon(i) is PTZCamera c) cams.Add(c.Id);
                else if (Ndi(i) is string n) names.Add(n);
            }
            if (ndi != null) foreach (var n in ndi) if (!string.IsNullOrEmpty(n)) names.Add(n);
            foreach (var id in _canon.Except(cams).ToList()) { if (B.Ptz.Camera(id) is PTZCamera c) CanonFeedHub.Release(c); _canon.Remove(id); }
            foreach (var id in cams.Except(_canon).ToList()) { if (B.Ptz.Camera(id) is PTZCamera c) CanonFeedHub.Retain(c); _canon.Add(id); }
            foreach (var n in _ndi.Except(names).ToList()) { NdiHub.Release(n); _ndi.Remove(n); }
            foreach (var n in names.Except(_ndi).ToList()) { NdiHub.Retain(n); _ndi.Add(n); }
        }

        public void Clear() => Want(Array.Empty<int?>());
    }
}
