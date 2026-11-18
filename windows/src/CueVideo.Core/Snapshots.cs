using System.Net.Http;
using System.Text.Json.Nodes;
using CueATEM;

namespace CueVideo;

/// <summary>Still pictures from the cameras (a Canon's own, or the NDI picture set for a camera's
/// switcher input), measured for color, so Claude can compare them. Decoding and encoding
/// pictures is the app's job (it has the imaging code): it sets <see cref="Imaging"/> and <see cref="ToJpeg"/>.</summary>
public static class Snapshots
{
    public sealed record Result(JsonObject Info, JsonObject? Image);

    /// <summary>Given a JPEG, returns its measurements and, if asked, a JPEG at most 640 wide
    /// (base64), or null if it isn't a picture. Set by the app.</summary>
    public static Func<byte[], bool, (JsonObject Measure, string? Jpeg640)?>? Imaging;

    /// <summary>An NDI frame as a JPEG. Set by the app.</summary>
    public static Func<NdiFrame, byte[]?>? ToJpeg;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Whether a camera gives a picture: a Canon, or one with an NDI picture. Main thread.</summary>
    public static bool HasPicture(PTZCamera c) => (c.Proto == VISCAProtocol.CanonXC && c.Host.Length > 0) || NdiFor(c) != null;

    private static string? NdiFor(PTZCamera c) => c.Proto == VISCAProtocol.CanonXC && c.Host.Length > 0 ? null : Booth.Shared.Store.Ndi(c.AtemInput);

    /// <summary>Takes a still from each camera. Call on the main thread.</summary>
    public static void Take(List<PTZCamera> cams, bool pictures, Action<List<Result>> done)
    {
        // NDI pictures: start receiving now (NdiHub is the main thread's), let go when done.
        var ndi = cams.ToDictionary(c => c.Id, NdiFor);
        var feeds = new Dictionary<Guid, NdiFeed>();
        foreach (var (id, n) in ndi) if (n != null) { NdiHub.Retain(n); feeds[id] = NdiHub.Feed(n); }
        Task.Run(async () =>
        {
            var tasks = cams.Select(async c =>
            {
                JsonObject Err(string m) => new() { ["camera"] = c.Name, ["error"] = m };
                byte[]? data = null;
                if (feeds.TryGetValue(c.Id, out var feed))
                {
                    // Wait up to 4 seconds for a picture that's coming in now.
                    for (int i = 0; i < 40 && !(feed.IsLive && feed.Frame != null); i++) await Task.Delay(100);
                    if (feed.IsLive && feed.Frame is NdiFrame f) data = ToJpeg?.Invoke(f);
                    if (data == null) return new Result(Err($"No picture from {c.Name}'s NDI source ({ndi[c.Id]}). Check it's on the network with list_ndi_sources."), null);
                }
                else if (c.Proto != VISCAProtocol.CanonXC || c.Host.Length == 0)
                    return new Result(Err($"{c.Name} has no picture: it isn't a Canon, and no NDI picture is set for its switcher input."), null);
                else
                {
                    var port = c.Port == 80 ? "" : ":" + c.Port;
                    try { data = await Http.GetByteArrayAsync("http://" + c.Host + port + "/-wvhttp-01-/image.cgi"); }
                    catch { return new Result(Err($"No picture from {c.Name}."), null); }
                }
                if (Imaging?.Invoke(data!, pictures) is not (JsonObject info, var jpg)) return new Result(Err($"No picture from {c.Name}."), null);
                info["camera"] = c.Name;
                JsonObject? image = pictures && jpg != null ? new JsonObject { ["label"] = c.Name, ["mimeType"] = "image/jpeg", ["data"] = jpg } : null;
                return new Result(info, image);
            });
            var results = (await Task.WhenAll(tasks)).ToList();
            MainThread.Post(() => { foreach (var n in ndi.Values) if (n != null) NdiHub.Release(n); });
            done(results);
        });
    }

    /// <summary>Averages over pixels (RGB bytes, 3 per pixel): color, cast, brightness, clipping.</summary>
    public static JsonObject Measure(byte[] rgb, int count)
    {
        double r = 0, g = 0, b = 0, nr = 0, ng = 0, nb = 0, nn = 0;
        int clipped = 0, crushed = 0;
        for (int i = 0; i + 2 < rgb.Length && i / 3 < count; i += 3)
        {
            double R = rgb[i], G = rgb[i + 1], B = rgb[i + 2];
            r += R; g += G; b += B;
            var y = 0.2126 * R + 0.7152 * G + 0.0722 * B;
            if (y > 250) clipped++; else if (y < 8) crushed++;
            if (y > 40 && y < 220) { nr += R; ng += G; nb += B; nn++; }
        }
        double n = Math.Max(1, count);
        static double R2(double v) => Math.Round(v, 2);
        var d = new JsonObject
        {
            ["average_rgb"] = new JsonArray((int)(r / n), (int)(g / n), (int)(b / n)),
            ["brightness"] = (int)((0.2126 * r + 0.7152 * g + 0.0722 * b) / n),
            ["clipped_percent"] = R2(clipped / n * 100),
            ["crushed_percent"] = R2(crushed / n * 100),
        };
        if (nn > 50 && ng > 0)
        {
            double rg = nr / ng, bg = nb / ng;
            d["red_over_green"] = R2(rg);
            d["blue_over_green"] = R2(bg);
            d["cast"] = rg - bg > 0.06 ? "warm (orange)" : (bg - rg > 0.06 ? "cool (blue)" : "neutral");
        }
        return d;
    }
}
