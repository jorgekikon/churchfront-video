namespace CueVideo;

/// <summary>
/// What a person looks like in a picture, as a fingerprint of the colours of their clothes:
/// a hue and saturation histogram of their upper body and one of their lower body, with greys
/// counted by brightness. Two pictures of the same person in the same outfit give fingerprints
/// that sit close together; two people dressed differently sit farther apart. It is not face
/// recognition and holds nothing that names anyone: it tells people on the stage apart for one
/// service.
///
/// The Mac uses Apple's Vision feature print here. This is a simpler stand-in, scaled so its
/// distances fall in about the same range (the same person roughly 0.1 to 0.4, someone
/// dressed differently 0.6 and up), which IdentityMatch's limits were set for. People dressed
/// alike (a choir in robes) are harder to tell apart than on the Mac.
/// </summary>
public sealed class Appearance
{
    private const int HueBins = 12, SatBins = 3, GreyBins = 4, Half = HueBins * SatBins + GreyBins;
    private readonly float[] _h;   // two halves, each summing to 1 (or all zero when empty)

    private Appearance(float[] h) { _h = h; }

    /// <summary>How different two looks are: near 0 is the same, about 1 is someone else.</summary>
    public double? Distance(Appearance other)
    {
        double total = 0; int halves = 0;
        for (int part = 0; part < 2; part++)
        {
            double bc = 0, sa = 0, sb = 0;
            for (int i = part * Half; i < (part + 1) * Half; i++) { bc += Math.Sqrt(_h[i] * other._h[i]); sa += _h[i]; sb += other._h[i]; }
            if (sa < 0.5 || sb < 0.5) continue;
            total += Math.Sqrt(Math.Max(0, 1 - bc));   // Hellinger distance, 0 to 1
            halves++;
        }
        return halves == 0 ? null : Math.Min(1.5, total / halves * 1.4);
    }

    /// <summary>The closest this look comes to any of several pictures of one person.</summary>
    public double? Distance(IEnumerable<Appearance> gallery) =>
        gallery.Select(Distance).Where(d => d != null).Min();

    /// <summary>The look of whoever is inside `box` (0 to 1, origin top left). Null when
    /// they're too small to tell.</summary>
    public static Appearance? Of(TrackFrame f, RectD box)
    {
        // The middle of the box: arms held out reach into the background on both sides.
        var core = new RectD(box.X + box.W * 0.2, box.Y, box.W * 0.6, box.H);
        int x0 = Math.Max(0, (int)(core.MinX * f.Width)), x1 = Math.Min(f.Width, (int)Math.Ceiling(core.MaxX * f.Width));
        int y0 = Math.Max(0, (int)(core.MinY * f.Height)), y1 = Math.Min(f.Height, (int)Math.Ceiling(core.MaxY * f.Height));
        if (x1 - x0 < 12 || y1 - y0 < 24) return null;
        // Skip the head (about the top fifth): faces and hair change least between people.
        var bodyTop = y0 + (y1 - y0) / 5;
        var mid = bodyTop + (y1 - bodyTop) * 2 / 5;   // upper body, then lower
        var h = new float[Half * 2];
        // About 3000 samples whatever the crop size.
        var step = Math.Max(1, (int)Math.Sqrt((x1 - x0) * (double)(y1 - bodyTop) / 3000));
        for (int y = bodyTop; y < y1; y += step)
        {
            var part = y < mid ? 0 : Half;
            var row = y * f.Width * 3;
            for (int x = x0; x < x1; x += step)
            {
                var i = row + x * 3;
                h[part + Bin(f.Bgr[i + 2], f.Bgr[i + 1], f.Bgr[i])]++;
            }
        }
        for (int part = 0; part < 2; part++)
        {
            float sum = 0;
            for (int i = part * Half; i < (part + 1) * Half; i++) sum += h[i];
            if (sum > 0) for (int i = part * Half; i < (part + 1) * Half; i++) h[i] /= sum;
        }
        return new Appearance(h);
    }

    /// <summary>Which bin a colour goes in: hue and saturation for colours, brightness for greys.</summary>
    private static int Bin(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), c = max - min;
        var sat = max <= 0 ? 0 : c / max;
        if (sat < 0.2 || max < 0.15) return HueBins * SatBins + Math.Min(GreyBins - 1, (int)(max * GreyBins));
        double hue;
        if (max == r) hue = (g - b) / c % 6; else if (max == g) hue = (b - r) / c + 2; else hue = (r - g) / c + 4;
        if (hue < 0) hue += 6;
        var hb = Math.Min(HueBins - 1, (int)(hue / 6 * HueBins));
        var sb = Math.Min(SatBins - 1, (int)((sat - 0.2) / 0.8 * SatBins));
        return hb * SatBins + sb;
    }

    /// <summary>For tests: a look made of one colour above and one below.</summary>
    public static Appearance Solid(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var h = new float[Half * 2];
        h[Bin(r1, g1, b1)] = 1; h[Half + Bin(r2, g2, b2)] = 1;
        return new Appearance(h);
    }
}

/// <summary>Deciding which person in a new frame is the one being followed, from where everyone
/// is and what they look like. Kept free of pictures and cameras so it can be tested on its own.
/// The same rules as the Mac's IdentityMatch.</summary>
public static class IdentityMatch
{
    /// <summary>One person in the frame: how far they are from where the followed person was
    /// (as a share of the frame), and how different they look (null when it can't be told).</summary>
    public readonly record struct Candidate(double Position, double? Appearance);

    /// <summary>How much the followed person's own look varies, and how different the people
    /// near them look.</summary>
    public readonly record struct Model(double? Own, double? Others)
    {
        /// <summary>The most different someone can look and still be taken for the followed
        /// person when there's no one to compare them with. Halfway between "them" and "the
        /// others" once both are known; before that, generous.</summary>
        public double? Limit => Own is not double own ? null
            : Others is double o && o > own ? Math.Max(0.45, own + (o - own) * 0.5) : Math.Max(0.6, own * 3);
    }

    /// <summary>Take: follow this person (Sure when their look confirms it, safe to learn from).
    /// No Take: none of them is clearly the one; hold the shot and keep looking.</summary>
    public readonly record struct Choice(int? Take, bool Sure);

    public static Choice Choose(List<Candidate> people, double reach, double gone, Model model)
    {
        var near = Enumerable.Range(0, people.Count).Where(i => people[i].Position < reach).ToList();
        if (near.Count == 0) return new(null, false);
        var nearest = near.OrderBy(i => people[i].Position).First();
        var known = near.Where(i => people[i].Appearance != null).OrderBy(i => people[i].Appearance!.Value).ToList();
        // Seen a moment ago: where they were still counts for a lot. After a gap it doesn't.
        var continuous = gone < 0.7;

        if (known.Count >= 2)
        {
            // Two or more people it could be: the one who looks most like the followed person, if clear.
            double best = people[known[0]].Appearance!.Value, next = people[known[1]].Appearance!.Value;
            var clear = best < next * 0.8 && (model.Limit is not double l || best < l * 1.3);
            if (clear) return new(known[0], true);
            return continuous ? new(nearest, false) : new(null, false);
        }
        if (known.Count == 1 && near.Count == 1 && model.Limit is double limit)
        {
            // One person there. If they were lost for a while, they have to look right.
            if (people[known[0]].Appearance!.Value <= limit) return new(nearest, true);
            return continuous ? new(nearest, false) : new(null, false);
        }
        // Nothing to tell them apart by (too small, or nothing learned yet): the nearest.
        return new(nearest, false);
    }

    /// <summary>Finding a named person (from the stage scan) among everyone in the frame: the
    /// best fit, if they're a clearly better fit than the next person, or the only one there.</summary>
    public static int? Find(List<double?> looks)
    {
        var known = Enumerable.Range(0, looks.Count).Where(i => looks[i] != null).OrderBy(i => looks[i]!.Value).ToList();
        if (known.Count == 0) return looks.Count == 1 ? 0 : null;
        var best = known[0];
        if (known.Count == 1) return looks.Count == 1 || looks[best]!.Value < 0.6 ? best : null;
        return looks[best]!.Value < looks[known[1]]!.Value * 0.85 ? best : null;
    }
}
