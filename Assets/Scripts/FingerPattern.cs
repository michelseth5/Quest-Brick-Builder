using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Hinge fingers, as the LDCad shadow library describes them: a row of segments along the hinge axis that
/// alternate between finger and gap, starting with the given one (e.g. a locking hinge's dual finger is
/// finger 4.5, gap 8, finger 4.5; the single finger that goes with it is one 8-wide finger). Two hinges
/// connect when, lined up centre to centre, every finger of each lands in a gap of the other (or past its end).
/// Pure C# so it can be tested outside Unity.
/// </summary>
public static class FingerPattern
{
    const float Slack = 0.6f; // LDU of overlap allowed where fingers meet (patterns are rounded)

    struct Segment
    {
        public float from, to;
        public bool finger;
    }

    /// <summary>
    /// Whether two finger connectors interlock. Profiles are "F&lt;radius&gt;|&lt;M or F&gt;|&lt;widths&gt;"
    /// (older imports have just "F&lt;radius&gt;"; those fit when their genders are opposite).
    /// </summary>
    public static bool Interlock(string profileA, bool maleA, string profileB, bool maleB)
    {
        string[] a = (profileA ?? "").Split('|');
        string[] b = (profileB ?? "").Split('|');
        if (a[0] != b[0])
            return false; // different finger radius
        if (a.Length < 3 || b.Length < 3)
            return maleA != maleB;

        List<Segment> first = Segments(a[1] == "M", a[2]);
        List<Segment> second = Segments(b[1] == "M", b[2]);
        if (first.Count == 0 || second.Count == 0)
            return false;
        // Patterns are usually symmetric; if not, either way round will do (the part can be flipped over).
        return Fits(first, second) || Fits(first, Reversed(second));
    }

    // Centred on zero.
    static List<Segment> Segments(bool startsWithFinger, string widths)
    {
        var values = new List<float>();
        foreach (string w in widths.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (float.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && value > 0f)
                values.Add(value);
        }

        float total = 0f;
        foreach (float v in values)
            total += v;

        var segments = new List<Segment>();
        float at = -total * 0.5f;
        bool finger = startsWithFinger;
        foreach (float v in values)
        {
            segments.Add(new Segment { from = at, to = at + v, finger = finger });
            at += v;
            finger = !finger;
        }
        return segments;
    }

    static List<Segment> Reversed(List<Segment> segments)
    {
        var result = new List<Segment>();
        for (int i = segments.Count - 1; i >= 0; i--)
            result.Add(new Segment { from = -segments[i].to, to = -segments[i].from, finger = segments[i].finger });
        return result;
    }

    // No finger of one overlaps a finger of the other, and at least one finger of each meets a gap of the
    // other or the other's open end (so the two actually mesh rather than just sitting side by side).
    static bool Fits(List<Segment> a, List<Segment> b)
    {
        foreach (Segment x in a)
        {
            if (!x.finger)
                continue;
            foreach (Segment y in b)
            {
                if (y.finger && Math.Min(x.to, y.to) - Math.Max(x.from, y.from) > Slack)
                    return false;
            }
        }
        return HasFinger(a) && HasFinger(b);
    }

    static bool HasFinger(List<Segment> segments)
    {
        foreach (Segment s in segments)
        {
            if (s.finger)
                return true;
        }
        return false;
    }
}
