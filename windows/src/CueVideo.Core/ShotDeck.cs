using CueATEM;

namespace CueVideo;

// A port of the logic in Sources/CueVideo/ShotDeck.swift (the shot deck's state, the keys, the
// drag maths) and of HeadroomStepper in VolunteerView.swift. The deck itself is drawn in the app
// (OperatorView.cs); this part has no UI so the self-test can check it.

/// <summary>One place a camera can be sent: a shot saved in Churchfront Video, or a preset
/// stored on the camera.</summary>
public sealed record DeckItem(PTZShot? Shot, int Preset, string Name)
{
    public static DeckItem Of(PTZShot s) => new(s, 0, s.Name);
    public static DeckItem OfPreset(int n, string name) => new(null, n, name);
    public bool IsPreset => Shot == null;
    /// <summary>What the deck remembers as the camera's last-used place.</summary>
    public string Key => Shot is PTZShot s ? $"shot:{s.Id}" : $"preset:{Preset}";
}

/// <summary>What the shot deck remembers between views and what the keyboard acts on: the page
/// each camera is showing, and the place each camera was last sent to. Main thread.</summary>
public sealed class ShotDeckState
{
    public static ShotDeckState Shared { get; } = new();
    public const int PerPage = 10, Columns = 5;

    public Dictionary<Guid, int> Page { get; } = new();
    public Dictionary<Guid, string> LastUsed { get; } = new();
    /// <summary>Raised when a page turns or a camera is sent somewhere.</summary>
    public event Action? Changed;

    /// <summary>The keys on the tiles, laid out like the grid: the top row on Q W E R T, the
    /// bottom on A S D F G. (1 to 0 already pick sources.)</summary>
    public static readonly string[] Letters = { "Q", "W", "E", "R", "T", "A", "S", "D", "F", "G" };

    /// <summary>The tile a letter key is on, or null.</summary>
    public static int? Slot(char letter) => Array.IndexOf(Letters, char.ToUpperInvariant(letter).ToString()) is var i and >= 0 ? i : null;

    /// <summary>The camera's places in button order: its saved shots, then its own presets.</summary>
    public static List<DeckItem> Items(PTZCamera c, PTZController ptz)
    {
        var names = ptz.PresetNames(c);
        return c.Shots.Where(s => s.Kind == ShotKind.Position).Select(DeckItem.Of)
            .Concat(Presets(c, ptz).Select(n => DeckItem.OfPreset(n, names.TryGetValue(n, out var nm) ? nm : $"Preset {n}")))
            .ToList();
    }

    /// <summary>The camera's own presets in button order (exposure presets live in the exposure
    /// panel). A VISCA camera can't say which presets it holds, so it shows only the ones given a name.</summary>
    public static List<int> Presets(PTZCamera c, PTZController ptz)
    {
        var exp = ptz.ExposurePresets.TryGetValue(c.Id, out var e) ? e : new HashSet<int>();
        return OrderPresets(ptz.PresetNames(c).Keys.Where(n => !exp.Contains(n)), c.PresetOrder);
    }

    /// <summary>Presets in the saved order; ones not in it come after, by number.</summary>
    public static List<int> OrderPresets(IEnumerable<int> presets, IReadOnlyList<int> order)
    {
        var place = new Dictionary<int, int>();
        for (int i = 0; i < order.Count; i++) place.TryAdd(order[i], i);
        return presets.OrderBy(n => place.TryGetValue(n, out var p) ? p : int.MaxValue).ThenBy(n => n).ToList();
    }

    /// <summary>Pages the camera needs: its places, plus room for the save tile.</summary>
    public static int Pages(int items, bool canSave) => Math.Max(1, (items + (canSave ? 1 : 0) + PerPage - 1) / PerPage);
    public static int Pages(PTZCamera c, PTZController ptz) => Pages(Items(c, ptz).Count, ptz.CanSaveShots(c));

    public int PageOf(PTZCamera c, PTZController ptz) => Math.Min(Page.TryGetValue(c.Id, out var p) ? p : 0, Pages(c, ptz) - 1);

    public void Turn(PTZCamera c, int by, PTZController ptz) => SetPage(c, PageOf(c, ptz) + by, ptz);

    public void SetPage(PTZCamera c, int page, PTZController ptz)
    {
        var p = Math.Max(0, Math.Min(Pages(c, ptz) - 1, page));
        if (Page.TryGetValue(c.Id, out var was) && was == p) return;
        Page[c.Id] = p;
        Changed?.Invoke();
    }

    public void Go(DeckItem item, PTZCamera c, PTZController ptz)
    {
        if (item.Shot is PTZShot s) ptz.Go(s, c.Id); else ptz.Recall(item.Preset, c.Id);
        LastUsed[c.Id] = item.Key;
        Changed?.Invoke();
    }

    /// <summary>A tile's key, pressed: sends the camera to that tile on the page it's showing.</summary>
    public void Press(int slot, PTZCamera c, PTZController ptz)
    {
        var items = Items(c, ptz);
        var i = PageOf(c, ptz) * PerPage + slot;
        if (i < items.Count) Go(items[i], c, ptz);
    }

    /// <summary>The slots a tile may be dragged to on its page: its own run (saved shots, or the
    /// camera's presets).</summary>
    public static (int Lo, int Hi) Run(bool preset, int shots, int items, int page)
    {
        var start = page * PerPage;
        return preset ? (Math.Max(shots, start) - start, Math.Min(items, start + PerPage) - 1 - start)
                      : (0, Math.Min(shots, start + PerPage) - 1 - start);
    }

    /// <summary>Moves the place at `from` to `to` (both indexes into Items), within its own run,
    /// and saves the new order. False when they aren't in the same run.</summary>
    public static bool Move(Guid camera, int from, int to, PTZController ptz)
    {
        var c = ptz.Cameras.FirstOrDefault(x => x.Id == camera);
        if (c == null || from == to) return false;
        var items = Items(c, ptz);
        if (from >= items.Count || to >= items.Count || items[from].IsPreset != items[to].IsPreset) return false;
        var nShots = items.Count(x => !x.IsPreset);
        if (items[from].IsPreset)
        {
            var list = Presets(c, ptz);
            var n = list[from - nShots]; list.RemoveAt(from - nShots); list.Insert(to - nShots, n);
            ptz.Edit(l => l.First(x => x.Id == camera).PresetOrder = list);
        }
        else
            ptz.Edit(l => l.First(x => x.Id == camera).Shots = ReorderPositions(l.First(x => x.Id == camera).Shots, from, to));
        return true;
    }

    /// <summary>Exposure shots share the list: reorders the position shots within the places they hold.</summary>
    public static List<PTZShot> ReorderPositions(List<PTZShot> all, int from, int to)
    {
        var slots = Enumerable.Range(0, all.Count).Where(i => all[i].Kind == ShotKind.Position).ToList();
        var list = slots.Select(i => all[i]).ToList();
        var s = list[from]; list.RemoveAt(from); list.Insert(to, s);
        var o = all.ToList();
        for (int k = 0; k < slots.Count; k++) o[slots[k]] = list[k];
        return o;
    }
}

/// <summary>A tile being dragged to a new place on its page: where it started, and how far it has
/// been carried. Sizes are a tile plus the gap to the next one, across and down.</summary>
public sealed class ShotDrag
{
    public Guid Camera;
    public int Page, From, Lo, Hi;
    public double CellW, CellH;
    public double MovedX, MovedY;

    private const int Cols = ShotDeckState.Columns;

    /// <summary>The slot it would land in if dropped now.</summary>
    public int To
    {
        get
        {
            var col = Math.Min(Cols - 1, Math.Max(0, From % Cols + (int)Math.Round(MovedX / CellW, MidpointRounding.AwayFromZero)));
            var row = Math.Min(1, Math.Max(0, From / Cols + (int)Math.Round(MovedY / CellH, MidpointRounding.AwayFromZero)));
            return Math.Min(Hi, Math.Max(Lo, row * Cols + col));
        }
    }

    public bool Holds(int slot) => slot >= Lo && slot <= Hi;

    /// <summary>How far the tile in `slot` sits from its usual place: the dragged one follows the
    /// mouse, the ones it passes step along to make room.</summary>
    public (double X, double Y) Shift(int slot)
    {
        if (slot == From) return (MovedX, MovedY);
        var to = To;
        var dest = From < slot && slot <= to ? slot - 1 : (to <= slot && slot < From ? slot + 1 : slot);
        return ((dest % Cols - slot % Cols) * CellW, (dest / Cols - slot / Cols) * CellH);
    }
}

/// <summary>Headroom for a follow-shot size, a step at a time: moves where the eyes sit in the
/// frame. Shown as steps from the standard ("Std", "+1", "−2"). Changes every camera framing this size.</summary>
public static class HeadroomStepper
{
    public const double Step = 0.02, Low = 0.1, High = 0.45;

    /// <summary>Steps above (more room) or below (less) the standard eye line.</summary>
    public static int Steps(double eyeLine, double standard) => (int)Math.Round((eyeLine - standard) / Step, MidpointRounding.AwayFromZero);
    public static string Label(int n) => n == 0 ? "Std" : (n > 0 ? $"+{n}" : $"−{-n}");

    /// <summary>The eye line one step up (+1) or down (-1) from `eyeLine`, kept in range.</summary>
    public static double Stepped(double eyeLine, int dir) =>
        Math.Round(Math.Min(High, Math.Max(Low, eyeLine + dir * Step)) * 100, MidpointRounding.AwayFromZero) / 100;

    /// <summary>Sets a size's eye line, leaving no change behind when it's back to the standard.</summary>
    public static void Set(CueConfig c, ShotSize size, double v)
    {
        var r = c.Framing.TryGetValue(size.Key(), out var x) ? x : new FramingRule();
        var std = size.Standard().EyeLine ?? v;
        r.EyeLine = Math.Abs(v - std) < 0.001 ? null : v;
        if (r.IsEmpty) c.Framing.Remove(size.Key()); else c.Framing[size.Key()] = r;
    }
}
