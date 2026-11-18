using System.Text.RegularExpressions;
using CueATEM;

namespace CueVideo;

/// <summary>
/// The rules behind the Routing page's editing (a port of the logic in Routing.swift and
/// RoutingPatch.swift), kept apart from the page so they can be tested on their own: which
/// screen an aux output belongs to, how the page groups sources and destinations, and how the
/// routing on screen now becomes an operator button.
/// </summary>
public static class RoutingEdit
{
    public enum Group { Mixes, Cameras, Graphics, Ndi, SpareIn, Inside, Screens, Feeds, SpareOut }

    public static string Title(this Group g) => g switch
    {
        Group.Mixes => "Mixes", Group.Cameras => "Cameras", Group.Graphics => "Graphics", Group.Ndi => "NDI",
        Group.SpareIn => "Spare inputs", Group.Inside => "Media and color", Group.Screens => "Screens",
        Group.Feeds => "Encoders and feeds", _ => "Spare outputs",
    };

    /// <summary>A port that only goes to a patch bay tie line, by its name.</summary>
    public static bool IsSpare(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("patchbay") || n.Contains("patch bay") || Regex.IsMatch(n, @"\(pb ?\d+\)");
    }

    public static bool IsMix(int id) => id >= 10000 && id < 11000;

    /// <summary>Which column group a switcher input goes in on the left.</summary>
    public static Group SourceGroup(ATEMInput i, string label, CueConfig cfg)
    {
        if (!i.IsExternal) return Group.Inside;
        if (IsSpare(i.Long)) return Group.SpareIn;
        var n = (i.Long + " " + label).ToLowerInvariant();
        if (cfg.Cameras.Any(c => c.AtemInput == i.Id) || n.Contains("cam") || n.Contains("ptz")) return Group.Cameras;
        return n.Contains("ndi") ? Group.Ndi : Group.Graphics;
    }

    /// <summary>The destinations on the right, grouped: screens operators see, encoders and feeds
    /// (hidden from operators), and spare outputs (patch bay ports and outputs not named yet).</summary>
    public static List<(Group Group, List<ScreenOutput> Screens)> Destinations(CueConfig cfg, ATEMState s)
    {
        var used = cfg.Screens.SelectMany(x => x.Aux).ToHashSet();
        var unnamed = s.Aux.Keys.OrderBy(k => k).Select(k => k + 1).Where(a => !used.Contains(a))
            .Select(a => new ScreenOutput { Name = $"Aux {a}", Aux = new() { a } }).ToList();
        return new()
        {
            (Group.Screens, cfg.Screens.Where(x => x.ForVolunteers && !IsSpare(x.Name)).ToList()),
            (Group.Feeds, cfg.Screens.Where(x => !x.ForVolunteers && !IsSpare(x.Name)).ToList()),
            (Group.SpareOut, cfg.Screens.Where(x => IsSpare(x.Name)).Concat(unnamed).ToList()),
        };
    }

    /// <summary>Whether a destination is one already named in the setup file.</summary>
    public static bool IsNamed(CueConfig cfg, ScreenOutput sc) => cfg.Screens.Any(x => x.Id == sc.Id);

    /// <summary>What a destination shows now: one source, and whether its outputs disagree.</summary>
    public static (int? Source, bool Mixed) Now(ScreenOutput sc, ATEMState s)
    {
        var set = sc.Aux.Select(a => s.Aux.TryGetValue(a - 1, out var v) ? (int?)v : null).Distinct().ToList();
        return set.Count == 1 ? (set[0], false) : (null, set.Count > 1);
    }

    /// <summary>"4, 5" or "4 5" as aux numbers, counted from 1.</summary>
    public static List<int> ParseAux(string text) =>
        text.Split(new[] { ',', ' ', '+', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => int.TryParse(t, out var n) ? n : 0).Where(n => n >= 1).Distinct().ToList();

    /// <summary>Saves a destination's name, outputs and note: replaces `old` (or adds a new one).
    /// An aux belongs to one screen, so these auxes are taken away from any other, and a screen
    /// left with none is dropped. Returns why not, or null when saved.</summary>
    public static string? SaveScreen(CueConfig c, ScreenOutput? old, string name, string auxText, string? note)
    {
        var n = name.Trim();
        var auxes = ParseAux(auxText);
        if (n.Length == 0) return "Give it a name.";
        if (auxes.Count == 0) return "Give at least one aux output, counted from 1.";
        var i = old == null ? -1 : c.Screens.FindIndex(x => x.Id == old.Id);
        var fresh = new ScreenOutput
        {
            Name = n, Aux = auxes, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Volunteer = i >= 0 ? c.Screens[i].Volunteer : null, Pinned = i >= 0 ? c.Screens[i].Pinned : null,
        };
        for (int j = 0; j < c.Screens.Count; j++) if (j != i) c.Screens[j].Aux.RemoveAll(auxes.Contains);
        if (i >= 0) c.Screens[i] = fresh; else c.Screens.Add(fresh);
        c.Screens.RemoveAll(x => x.Aux.Count == 0);
        return null;
    }

    /// <summary>An operator button that sets the chosen screens to what they show now. A button
    /// with the same label is replaced. Returns why not, or null when saved.</summary>
    public static string? SaveButton(CueConfig c, string label, IEnumerable<string> screenIds, ATEMState s)
    {
        var l = label.Trim();
        var ids = screenIds.ToHashSet();
        if (l.Length == 0) return "Give the button a label.";
        var set = c.Screens.Where(x => ids.Contains(x.Id))
            .SelectMany(x => x.Aux.Where(a => s.Aux.ContainsKey(a - 1)).Select(a => new RouteChange { Aux = a, Source = s.Aux[a - 1] })).ToList();
        if (set.Count == 0) return "Pick at least one screen the switcher has reported.";
        c.Routes.RemoveAll(r => r.Label == l);
        c.Routes.Add(new RouteButton { Label = l, Set = set });
        return null;
    }

    /// <summary>Moves an operator button earlier (-1) or later (+1).</summary>
    public static void MoveButton(CueConfig c, int i, int by)
    {
        var j = i + by;
        if (i < 0 || j < 0 || i >= c.Routes.Count || j >= c.Routes.Count) return;
        (c.Routes[i], c.Routes[j]) = (c.Routes[j], c.Routes[i]);
    }
}
