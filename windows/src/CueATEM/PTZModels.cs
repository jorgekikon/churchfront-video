using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CueATEM;

// The camera records kept in the config file. Same JSON as the Mac app, so one config.json
// works on both: { "name": "Cam 1", "host": "192.168.1.91", "proto": "canonXC", "atemInput": 9 }.

public enum VISCAProtocol { CanonXC, SonyUDP, RawTCP }

public static class VISCAProtocolInfo
{
    public static string Label(this VISCAProtocol p) => p switch
    {
        VISCAProtocol.CanonXC => "Canon (HTTP)",
        VISCAProtocol.SonyUDP => "VISCA over IP (UDP)",
        _ => "VISCA over TCP",
    };

    public static ushort DefaultPort(this VISCAProtocol p) => p switch
    {
        VISCAProtocol.CanonXC => 80,
        VISCAProtocol.SonyUDP => 52381,
        _ => 5678,
    };

    /// <summary>Canon's HTTP protocol can't save presets without an admin login.</summary>
    public static bool CanStorePresets(this VISCAProtocol p) => p != VISCAProtocol.CanonXC;

    /// <summary>The name in the config file: canonXC, sonyUDP or rawTCP.</summary>
    public static string Key(this VISCAProtocol p) => JsonNamingPolicy.CamelCase.ConvertName(p.ToString());
}

public sealed class PTZCamera
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Camera";
    public string Host { get; set; } = "";
    /// <summary>0 in the file means "the protocol's usual port".</summary>
    public ushort Port { get; set; }
    public VISCAProtocol Proto { get; set; } = VISCAProtocol.SonyUDP;
    /// <summary>The ATEM input this camera feeds, so selecting it on preview picks the camera.</summary>
    public int? AtemInput { get; set; }
    /// <summary>Some VISCA cameras count presets from 1 instead of 0.</summary>
    public int FirstPreset { get; set; }
    /// <summary>Names for preset buttons, by preset number (1 based).</summary>
    public Dictionary<int, string> PresetNames { get; set; } = new();
    /// <summary>Shots saved in Churchfront Video (not on the camera), in button order.</summary>
    public List<PTZShot> Shots { get; set; } = new();
    /// <summary>The order the camera's own presets are shown in, by preset number. Ones not
    /// listed come after, by number.</summary>
    public List<int> PresetOrder { get; set; } = new();

    /// <summary>Fills in what a hand-written camera left out: the port follows the protocol.</summary>
    public PTZCamera Normalized()
    {
        if (Port == 0) Port = Proto.DefaultPort();
        Name ??= "Camera";
        Host ??= "";
        PresetNames ??= new();
        Shots ??= new();
        PresetOrder ??= new();
        return this;
    }

    public PTZCamera Clone() => JsonSerializer.Deserialize<PTZCamera>(JsonSerializer.Serialize(this, CueJson.Options), CueJson.Options)!.Normalized();

    /// <summary>What a connection depends on: only these changing reconnects the camera.</summary>
    internal string LinkSignature => $"{Id}|{Host}|{Port}|{Proto}|{FirstPreset}";
}

/// <summary>Where a camera is pointing, in the camera's own units. For Canon: pan and tilt in
/// hundredths of a degree (pan -17000 to 17000), zoom in the camera's zoom steps.</summary>
public readonly record struct PTZPosition(int Pan, int Tilt, int Zoom);

/// <summary>Manual exposure in the camera's own units. For Canon: gain in tenths of a dB (65 is
/// 6.5 dB), shutter as the speed's denominator (60 is 1/60), iris as the f-number times 100
/// (450 is f/4.5). White balance: a mode (auto, daylight, tungsten or kelvin) and, for kelvin,
/// the color temperature (4300). Any left out stay as they are.</summary>
public sealed class PTZExposure
{
    public int? Gain { get; set; }
    public int? Shutter { get; set; }
    public int? Iris { get; set; }
    public string? Wb { get; set; }
    public int? Kelvin { get; set; }
    [JsonIgnore] public bool HasExposure => Gain != null || Shutter != null || Iris != null;
    [JsonIgnore] public bool HasWhiteBalance => Wb != null || Kelvin != null;
    public PTZExposure Clone() => (PTZExposure)MemberwiseClone();
}

/// <summary>What a camera's exposure is now, and the values it accepts, for the controls.</summary>
public sealed class CameraExposure
{
    public PTZExposure Value { get; set; } = new();
    /// <summary>Which of gain, shutter and iris the camera is setting itself.</summary>
    public bool GainAuto, ShutterAuto, IrisAuto;
    public List<int> GainList = new(), ShutterList = new(), IrisList = new(), KelvinList = new();
    /// <summary>Gain is a plain level (0, 1, 2…) as the camera's own menu shows it, not tenths of a dB.</summary>
    public bool GainIsLevel;
    /// <summary>The camera has one auto exposure mode that takes gain, shutter and iris back together.</summary>
    public bool HasAutoMode;
    public bool AllAuto => GainAuto && ShutterAuto && IrisAuto;

    public CameraExposure Clone()
    {
        var c = (CameraExposure)MemberwiseClone();
        c.Value = Value.Clone();
        c.GainList = new(GainList); c.ShutterList = new(ShutterList); c.IrisList = new(IrisList); c.KelvinList = new(KelvinList);
        return c;
    }

    public string GainLabel(int? v) => GainIsLevel ? (v is int x ? $"Level {x}" : "?") : GainText(v);

    /// <summary>"Auto", "Daylight", "Tungsten" or "4300 K".</summary>
    public static string WbText(string? mode, int? kelvin) => mode switch
    {
        "auto" => "Auto",
        "daylight" => "Daylight",
        "tungsten" => "Tungsten",
        "kelvin" => kelvin is int k ? $"{k} K" : "Kelvin",
        null => kelvin is int k2 ? $"{k2} K" : "?",
        _ => "Custom",
    };

    public static string GainText(int? v) => v is int x ? (x / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + " dB" : "?";
    public static string ShutterText(int? v) => v is int x ? $"1/{x}" : "?";
    public static string IrisText(int? v) => v is int x ? "f/" + (x / 100.0).ToString("0.0", CultureInfo.InvariantCulture) : "?";
}

public enum ShotKind { Position, Exposure }

/// <summary>A named shot saved in the config file, so it can be made and edited from any
/// computer, by hand or by Claude, without the camera's admin login. A shot holds either a
/// position (pan, tilt, zoom) or an exposure (gain, shutter, iris).</summary>
[JsonConverter(typeof(PTZShotConverter))]
public sealed class PTZShot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Shot";
    public ShotKind Kind { get; set; } = ShotKind.Position;
    public int Pan { get; set; }
    public int Tilt { get; set; }
    public int Zoom { get; set; }
    public PTZExposure? Exposure { get; set; }
    /// <summary>Made by a stage scan, not by hand. The next scan replaces these; yours are kept.</summary>
    public bool Auto { get; set; }
    public PTZPosition Position => new(Pan, Tilt, Zoom);

    public PTZShot() { }
    public PTZShot(string name, PTZPosition p) { Name = name; Pan = p.Pan; Tilt = p.Tilt; Zoom = p.Zoom; }
    public PTZShot(string name, PTZExposure e) { Name = name; Kind = ShotKind.Exposure; Exposure = e; }
}

/// <summary>Lenient on the way in (only a name is needed), and on the way out writes just the
/// fields the shot's kind uses, as the Mac app does.</summary>
public sealed class PTZShotConverter : JsonConverter<PTZShot>
{
    public override PTZShot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = JsonNode.Parse(ref reader)?.AsObject() ?? throw new JsonException("A shot must be an object.");
        var s = new PTZShot();
        if (Guid.TryParse(CueJson.Str(o["id"]), out var g)) s.Id = g;
        s.Name = CueJson.Str(o["name"]) ?? "Shot";
        if (o["exposure"] is JsonNode e) s.Exposure = e.Deserialize<PTZExposure>(options);
        s.Kind = CueJson.Str(o["kind"]) is string k
            ? (k.Equals("exposure", StringComparison.OrdinalIgnoreCase) ? ShotKind.Exposure : ShotKind.Position)
            : (s.Exposure != null ? ShotKind.Exposure : ShotKind.Position);
        s.Pan = CueJson.Int(o["pan"]) ?? 0;
        s.Tilt = CueJson.Int(o["tilt"]) ?? 0;
        s.Zoom = CueJson.Int(o["zoom"]) ?? 0;
        s.Auto = CueJson.Bool(o["auto"]) ?? false;
        return s;
    }

    public override void Write(Utf8JsonWriter w, PTZShot s, JsonSerializerOptions options)
    {
        w.WriteStartObject();
        w.WriteString("id", s.Id.ToString().ToUpperInvariant());
        w.WriteString("name", s.Name);
        w.WriteString("kind", s.Kind == ShotKind.Exposure ? "exposure" : "position");
        if (s.Auto) w.WriteBoolean("auto", true);
        if (s.Kind == ShotKind.Position)
        {
            w.WriteNumber("pan", s.Pan); w.WriteNumber("tilt", s.Tilt); w.WriteNumber("zoom", s.Zoom);
        }
        else if (s.Exposure != null)
        {
            w.WritePropertyName("exposure");
            JsonSerializer.Serialize(w, s.Exposure, options);
        }
        w.WriteEndObject();
    }
}

/// <summary>UUIDs written in capitals, as the Mac app writes them.</summary>
public sealed class UpperGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.TryParse(reader.GetString(), out var g) ? g : Guid.NewGuid();
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToUpperInvariant());
}

/// <summary>Numbers written by hand sometimes arrive as strings ("9") or with a decimal point (9.0).</summary>
public sealed class LenientIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out var i)) return i;
            return (int)Math.Round(reader.GetDouble());
        }
        throw new JsonException($"Expected a whole number, found {reader.TokenType}.");
    }
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

public static class CueJson
{
    public static readonly JsonSerializerOptions Options = Make(indented: false);
    public static readonly JsonSerializerOptions Pretty = Make(indented: true);

    private static JsonSerializerOptions Make(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.Converters.Add(new UpperGuidConverter());
        o.Converters.Add(new LenientIntConverter());
        return o;
    }

    public static int? Int(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public static double? Double(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A copy with every object's keys in order, so the file reads the same as the Mac's.</summary>
    public static JsonNode? Sorted(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Sorted(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => n.DeepClone(),
    };
}
