using System.IO;
using CueATEM;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CueVideo.App;

/// <summary>
/// Finds people and their body points with RTMO-s (OpenMMLab's one-stage multi-person pose
/// model, ONNX, 640×640, 17 COCO body points), run by ONNX Runtime on the graphics chip through
/// DirectML, or on the processor when that isn't available. Stands in for Apple's Vision.
///
/// The model file isn't in the repo (its weights' training data has mixed licence terms): put
/// rtmo-s.onnx in a "models" folder next to ChurchfrontVideo.exe or in %APPDATA%\CueVideo\models.
/// windows\get-model.ps1 fetches it and build.ps1 copies it into dist.
///
/// The model's own contract (from its deploy files and rtmlib): BGR pixels as 0 to 255 floats,
/// NCHW, the picture scaled to fit 640×640 at the top left with grey (114) filling the rest.
/// Out come boxes (x1, y1, x2, y2, score) and, for each, 17 points (x, y, score), in that
/// 640×640 space.
/// </summary>
public sealed class PoseDetector : IPersonDetector, IDisposable
{
    public const string FileName = "rtmo-s.onnx";
    private const int Size = 640;
    private const double MinScore = 0.5, NmsIou = 0.45;

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly object _lock = new();
    private readonly float[] _buf = new float[3 * Size * Size];
    /// <summary>"graphics chip (DirectML)" or "processor", for Status.</summary>
    public string Device { get; }
    public string ModelPath { get; }

    private PoseDetector(InferenceSession s, string device, string path)
    {
        _session = s; Device = device; ModelPath = path;
        _input = s.InputMetadata.Keys.First();
    }

    /// <summary>Where the model file is looked for, in order.</summary>
    public static IEnumerable<string> Places()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "models", FileName);
        yield return Path.Combine(AppContext.BaseDirectory, FileName);
        yield return Path.Combine(ConfigStore.Folder, "models", FileName);
        // Running from a build in the repo: windows\models.
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && d != null; i++, d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "models", FileName))) { yield return Path.Combine(d.FullName, "models", FileName); yield break; }
    }

    /// <summary>Loads the model, or says why it couldn't. Slow (a second or two): call off the main thread.</summary>
    public static PoseDetector? Load(out string? why)
    {
        why = null;
        var path = Places().FirstOrDefault(File.Exists);
        if (path == null)
        {
            why = $"Auto tracking needs its body-pose model, {FileName}, in a \"models\" folder next to ChurchfrontVideo.exe or in {Path.Combine(ConfigStore.Folder, "models")}. Run get-model.ps1 from the windows folder of the source to fetch it.";
            return null;
        }
        try
        {
            var o = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            o.AppendExecutionProvider_DML(0);
            var d = new PoseDetector(new InferenceSession(path, o), "graphics chip (DirectML)", path);
            d.Warm();
            return d;
        }
        catch (Exception e) { Console.Error.WriteLine("DirectML: " + e.Message); }
        try
        {
            var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
            var d = new PoseDetector(new InferenceSession(path, o), "processor", path);
            d.Warm();
            return d;
        }
        catch (Exception e)
        {
            why = $"Auto tracking's model ({path}) couldn't be loaded: {e.Message}";
            return null;
        }
    }

    /// <summary>The first run sets the graphics chip up; do it before anyone is waiting.</summary>
    private void Warm() => Detect(new TrackFrame { Width = 64, Height = 36, Bgr = new byte[64 * 36 * 3] });

    /// <summary>How long the last frame took, in milliseconds (for Status).</summary>
    public double LastMs { get; private set; }

    public List<PoseBody> Detect(TrackFrame f)
    {
        lock (_lock)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var ratio = Math.Min((double)Size / f.Width, (double)Size / f.Height);
            int w = Math.Max(1, (int)(f.Width * ratio)), h = Math.Max(1, (int)(f.Height * ratio));
            Fill(f, w, h);
            var tensor = new DenseTensor<float>(_buf, new[] { 1, 3, Size, Size });
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, tensor) });
            Tensor<float>? dets = null, kps = null;
            foreach (var r in results)
            {
                if (r.Value is not Tensor<float> t) continue;
                if (t.Rank == 3 && t.Dimensions[2] == 5) dets = t;
                else if (t.Rank == 4 && t.Dimensions[3] == 3) kps = t;
            }
            var found = new List<PoseBody>();
            if (dets == null) return found;
            int n = dets.Dimensions[1];
            var order = Enumerable.Range(0, n).Where(i => dets[0, i, 4] >= MinScore).OrderByDescending(i => dets[0, i, 4]).ToList();
            var kept = new List<(int I, double X1, double Y1, double X2, double Y2)>();
            foreach (var i in order)
            {
                double x1 = dets[0, i, 0], y1 = dets[0, i, 1], x2 = dets[0, i, 2], y2 = dets[0, i, 3];
                if (kept.Any(k => Iou(k.X1, k.Y1, k.X2, k.Y2, x1, y1, x2, y2) > NmsIou)) continue;
                kept.Add((i, x1, y1, x2, y2));
            }
            double sx = 1 / (ratio * f.Width), sy = 1 / (ratio * f.Height);
            foreach (var k in kept)
            {
                var b = new PoseBody
                {
                    Score = dets[0, k.I, 4],
                    Box = new RectD(k.X1 * sx, k.Y1 * sy, Math.Max(0, k.X2 - k.X1) * sx, Math.Max(0, k.Y2 - k.Y1) * sy),
                };
                if (kps != null && k.I < kps.Dimensions[1] && kps.Dimensions[2] >= 17)
                {
                    b.Points = new (double, double, double)[17];
                    for (int j = 0; j < 17; j++) b.Points[j] = (kps[0, k.I, j, 0] * sx, kps[0, k.I, j, 1] * sy, kps[0, k.I, j, 2]);
                }
                found.Add(b);
            }
            LastMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return found;
        }
    }

    /// <summary>The picture scaled (bilinear) to w×h at the top left of the 640×640 input, grey elsewhere.</summary>
    private void Fill(TrackFrame f, int w, int h)
    {
        const int plane = Size * Size;
        Array.Fill(_buf, 114f);
        double fx = (double)f.Width / w, fy = (double)f.Height / h;
        var src = f.Bgr; int stride = f.Width * 3;
        for (int y = 0; y < h; y++)
        {
            var sy = Math.Max(0, (y + 0.5) * fy - 0.5);
            int y0 = Math.Min(f.Height - 1, (int)sy), y1 = Math.Min(f.Height - 1, y0 + 1);
            var wy = sy - y0;
            for (int x = 0; x < w; x++)
            {
                var sx = Math.Max(0, (x + 0.5) * fx - 0.5);
                int x0 = Math.Min(f.Width - 1, (int)sx), x1 = Math.Min(f.Width - 1, x0 + 1);
                var wx = sx - x0;
                int a = y0 * stride + x0 * 3, b = y0 * stride + x1 * 3, c = y1 * stride + x0 * 3, d = y1 * stride + x1 * 3;
                var o = y * Size + x;
                for (int ch = 0; ch < 3; ch++)
                {
                    var top = src[a + ch] + (src[b + ch] - src[a + ch]) * wx;
                    var bot = src[c + ch] + (src[d + ch] - src[c + ch]) * wx;
                    _buf[ch * plane + o] = (float)(top + (bot - top) * wy);   // B, G, R planes, as the model was trained
                }
            }
        }
    }

    private static double Iou(double ax1, double ay1, double ax2, double ay2, double bx1, double by1, double bx2, double by2)
    {
        double iw = Math.Max(0, Math.Min(ax2, bx2) - Math.Max(ax1, bx1)), ih = Math.Max(0, Math.Min(ay2, by2) - Math.Max(ay1, by1));
        var inter = iw * ih;
        var u = (ax2 - ax1) * (ay2 - ay1) + (bx2 - bx1) * (by2 - by1) - inter;
        return u <= 0 ? 0 : inter / u;
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>The stage scan's still pictures: decoded to pixels for the model, and people cut out as small JPEGs.</summary>
public sealed class StillImages : IStillImages
{
    public TrackFrame? Decode(byte[] jpeg, int maxWidth)
    {
        try
        {
            var src = System.Windows.Media.Imaging.BitmapFrame.Create(new MemoryStream(jpeg), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var scale = Math.Min(1.0, (double)maxWidth / src.PixelWidth);
            System.Windows.Media.Imaging.BitmapSource b = scale < 1 ? new System.Windows.Media.Imaging.TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale)) : src;
            var bgr = new System.Windows.Media.Imaging.FormatConvertedBitmap(b, System.Windows.Media.PixelFormats.Bgr24, null, 0);
            int w = bgr.PixelWidth, h = bgr.PixelHeight;
            var px = new byte[w * h * 3];
            bgr.CopyPixels(px, w * 3, 0);
            return new TrackFrame { Width = w, Height = h, Bgr = px };
        }
        catch { return null; }
    }

    public byte[]? Crop(TrackFrame f, RectD box, int maxWidth)
    {
        try
        {
            var src = System.Windows.Media.Imaging.BitmapSource.Create(f.Width, f.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, f.Bgr, f.Width * 3);
            var r = new System.Windows.Int32Rect((int)(box.X * f.Width), (int)(box.Y * f.Height),
                Math.Max(1, Math.Min(f.Width - (int)(box.X * f.Width), (int)(box.W * f.Width))), Math.Max(1, Math.Min(f.Height - (int)(box.Y * f.Height), (int)(box.H * f.Height))));
            System.Windows.Media.Imaging.BitmapSource crop = new System.Windows.Media.Imaging.CroppedBitmap(src, r);
            if (crop.PixelWidth > maxWidth) crop = new System.Windows.Media.Imaging.TransformedBitmap(crop, new System.Windows.Media.ScaleTransform((double)maxWidth / crop.PixelWidth, (double)maxWidth / crop.PixelWidth));
            var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 80 };
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}

/// <summary>Tracking's pictures on Windows: a Canon camera's own preview stream.</summary>
public sealed class TrackPictures : ITrackPictures
{
    public bool Has(PTZCamera c) => c.Proto == CueATEM.VISCAProtocol.CanonXC && c.Host.Length > 0;

    public Action Watch(PTZCamera c, Action<TrackFrame> frame)
    {
        CanonFeedHub.Retain(c);
        var feed = CanonFeedHub.Feed(c);
        feed?.AddTracker(frame);
        return () => { feed?.RemoveTracker(frame); CanonFeedHub.Release(c); };
    }
}
