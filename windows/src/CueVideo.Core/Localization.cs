using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CueVideo;

/// <summary>
/// The app's languages. English is written in the code; every other language is one JSON file
/// that maps each English text to its translation:
///   { "code": "es", "name": "Español", "strings": { "Track": "Seguir", "Frame {camera}": "Encuadrar {camera}" } }
/// Texts with changing parts (names, numbers) are patterns: a {word} matches anything, and what
/// it matched is translated too and put in the same {word} of the translation. Anything with no
/// translation stays in English, so a half-done language still works.
///
/// Built-in languages are embedded (Lang/*.json in this project). A file of the same shape in a
/// "lang" folder beside the app, or in %APPDATA%\CueVideo\lang, adds a language or overrides
/// one, so a new language can be added without rebuilding.
///
/// The screens call <see cref="Tr"/> through their shared helpers (text, buttons, tooltips,
/// menus); the few places that change text while running call it themselves. What Claude's tools
/// return stays in English.
/// </summary>
public static class L
{
    public sealed record Language(string Code, string Name);

    private sealed class Table
    {
        public Dictionary<string, string> Exact = new(StringComparer.Ordinal);
        public List<(Regex Rx, string[] Names, string To, int Weight)> Patterns = new();
    }

    private static readonly Dictionary<string, (Language Lang, JsonObject Strings)> Files = new(StringComparer.OrdinalIgnoreCase);
    private static Table _table = new();
    private static readonly Dictionary<string, string> Memo = new(StringComparer.Ordinal);
    private static readonly object Lock = new();
    private static readonly Regex Token = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static string? _missingFile = Environment.GetEnvironmentVariable("CUEVIDEO_LANG_MISSING");
    private static readonly HashSet<string> MissingSeen = new(StringComparer.Ordinal);

    /// <summary>The language in use: "en", "es", …</summary>
    public static string Code { get; private set; } = "en";
    public static bool IsEnglish => Code == "en";

    /// <summary>Raised (on the caller's thread) after the language changes.</summary>
    public static event Action? Changed;

    /// <summary>English, then every language found, by name.</summary>
    public static List<Language> Available
    {
        get
        {
            LoadFiles();
            var list = new List<Language> { new("en", "English") };
            list.AddRange(Files.Values.Select(f => f.Lang).Where(l => l.Code != "en").OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase));
            return list;
        }
    }

    public static string NameOf(string code) => Available.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Name ?? "English";

    /// <summary>Switches language ("en" for English). An unknown code means English.</summary>
    public static void Use(string? code)
    {
        LoadFiles();
        code = string.IsNullOrWhiteSpace(code) ? "en" : code.Trim().ToLowerInvariant();
        var table = new Table();
        if (code != "en" && Files.TryGetValue(code, out var f))
        {
            foreach (var (en, node) in f.Strings)
            {
                if (node is not JsonValue v || !v.TryGetValue<string>(out var to) || to.Length == 0) continue;
                if (!Token.IsMatch(en)) { table.Exact[en] = to; continue; }
                // "Frame {camera}" → ^Frame (?<camera>.+?)$ ; the more fixed text, the more specific.
                var names = new List<string>();
                var rx = "^" + Token.Replace(Regex.Escape(en).Replace(@"\{", "{"), m => { names.Add(m.Groups[1].Value); return $"(?<{m.Groups[1].Value}>.+?)"; }) + "$";
                try { table.Patterns.Add((new Regex(rx, RegexOptions.Singleline | RegexOptions.CultureInvariant), names.ToArray(), to, Token.Replace(en, "").Length)); }
                catch (ArgumentException) { }
            }
            table.Patterns.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        }
        else code = "en";
        lock (Lock) { _table = table; Memo.Clear(); Code = code; }
        Changed?.Invoke();
    }

    /// <summary>The text in the language in use, or as it is when there's no translation.</summary>
    public static string Tr(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        if (IsEnglish) return s;
        lock (Lock)
        {
            if (Memo.TryGetValue(s, out var hit)) return hit;
            var r = Translate(s, 0);
            if (Memo.Count > 5000) Memo.Clear();
            Memo[s] = r;
            return r;
        }
    }

    private static string Translate(string s, int depth)
    {
        // Keep surrounding spaces and line breaks; translate what's inside.
        var core = s.Trim();
        if (core.Length == 0 || depth > 4) return s;
        var lead = s[..s.IndexOf(core, StringComparison.Ordinal)];
        var trail = s[(lead.Length + core.Length)..];
        if (_table.Exact.TryGetValue(core, out var exact)) return lead + exact + trail;
        foreach (var (rx, names, to, _) in _table.Patterns)
        {
            var m = rx.Match(core);
            if (!m.Success) continue;
            var outText = to;
            foreach (var n in names.Distinct()) outText = outText.Replace("{" + n + "}", Translate(m.Groups[n].Value, depth + 1));
            return lead + outText + trail;
        }
        if (depth == 0) NoteMissing(core);
        return s;
    }

    /// <summary>CUEVIDEO_LANG_MISSING=file: every text shown with no translation is written to
    /// the file once, for whoever completes a language.</summary>
    private static void NoteMissing(string s)
    {
        if (_missingFile == null || !s.Any(char.IsLetter) || !MissingSeen.Add(s)) return;
        try { File.AppendAllText(_missingFile, JsonSerializer.Serialize(s) + "\n"); } catch { }
    }

    private static bool _loaded;
    private static void LoadFiles()
    {
        if (_loaded) return;
        _loaded = true;
        var asm = typeof(L).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && n.Contains(".Lang.", StringComparison.Ordinal)))
        {
            try { using var st = asm.GetManifestResourceStream(res)!; Add(JsonNode.Parse(st, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })); } catch { }
        }
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "lang"), Path.Combine(ConfigStore.Folder, "lang") })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                    try { Add(JsonNode.Parse(File.ReadAllText(f), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })); } catch { }
            }
            catch { }
        }
    }

    /// <summary>Adds a language file; a later file for the same language adds to it and wins.</summary>
    private static void Add(JsonNode? n)
    {
        if (n is not JsonObject o || o["code"]?.GetValue<string>() is not string code || o["strings"] is not JsonObject strings) return;
        code = code.Trim().ToLowerInvariant();
        var name = o["name"]?.GetValue<string>() ?? code;
        if (Files.TryGetValue(code, out var have))
        {
            foreach (var (k, v) in strings) have.Strings[k] = v?.DeepClone();
            return;
        }
        Files[code] = (new Language(code, name), (JsonObject)strings.DeepClone());
    }
}
