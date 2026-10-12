using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A connection point on a part: a stud or socket, pin or hole, bar, clip, hinge, or a special shape
/// (from the LDCad shadow library). Stored in the part's local space, in meters.
/// A shape starts at its position and extends along its axis for its length (or is centered on its
/// position, if center is set).
/// </summary>
[Serializable]
public struct Connector
{
    public enum Kind { Cylinder, Clip, Finger, Generic }

    public Kind kind;
    public bool male;
    public Vector3 position;
    public Vector3 axis;
    public Vector3 reference;  // sideways direction, for lining up rotation (studs snap to quarter turns)
    public float length;
    public bool center;
    public bool slide;
    public bool openEnded;
    public string profile;     // what it fits: "R6" round radius 6 LDU, "S6" square, "A6" axle, "F6" hinge, "G:name" special
    public string group;
    public string sections;    // cylinders: each section from the start as <shape><radius>:<width> in LDU (see Connections.Fouls)

    public Vector3 Start => center ? position - axis * (length * 0.5f) : position;

    /// <summary>This connector moved by a pose (e.g. from a part's local space into the world).</summary>
    public Connector Moved(Vector3 offset, Quaternion rotation)
    {
        Connector moved = this;
        moved.position = offset + rotation * position;
        moved.axis = rotation * axis;
        moved.reference = rotation * reference;
        return moved;
    }

    public Connector InWorld(Transform t) => Moved(t.position, t.rotation);

    /// <summary>A plain stud or stud socket, for parts without shadow-library data.</summary>
    public static Connector Stud(Vector3 position, bool male) => new Connector
    {
        kind = Kind.Cylinder,
        male = male,
        position = position,
        axis = Vector3.up,
        reference = Vector3.right,
        length = 0.0016f,
        profile = "R6",
    };
}

/// <summary>Rules for which connectors fit together and how to line one part up with another.</summary>
public static class Connections
{
    const float Tolerance = 0.0006f;

    static readonly Dictionary<(Connector.Kind, bool, string, string, Connector.Kind, bool, string, string), bool> FitCache =
        new Dictionary<(Connector.Kind, bool, string, string, Connector.Kind, bool, string, string), bool>();
    static readonly Dictionary<string, (char shape, float radius)[]> Tokens = new Dictionary<string, (char, float)[]>();

    /// <summary>Whether two connectors can plug into each other (ignoring where they are).</summary>
    public static bool Fit(in Connector a, in Connector b)
    {
        // (A tuple key: no string building in this very hot path.)
        var key = (a.kind, a.male, a.profile, a.group, b.kind, b.male, b.profile, b.group);
        if (FitCache.TryGetValue(key, out bool cached))
            return cached;
        bool fits = ComputeFit(a, b);
        FitCache[key] = fits;
        return fits;
    }

    static bool ComputeFit(in Connector a, in Connector b)
    {
        if (a.kind == Connector.Kind.Generic || b.kind == Connector.Kind.Generic)
            return a.kind == b.kind && a.profile == b.profile && a.male != b.male;
        if (a.kind == Connector.Kind.Finger || b.kind == Connector.Kind.Finger)
            return a.kind == b.kind && string.Equals(a.group ?? "", b.group ?? "", StringComparison.Ordinal)
                   && FingersInterlock(a, b);

        // A clip holds a bar (a male cylinder) of its own radius.
        if (a.kind == Connector.Kind.Clip || b.kind == Connector.Kind.Clip)
        {
            Connector clip = a.kind == Connector.Kind.Clip ? a : b;
            Connector bar = a.kind == Connector.Kind.Clip ? b : a;
            if (bar.kind != Connector.Kind.Cylinder || !bar.male)
                return false;
            (char shape, float radius) clipSize = ParseTokens(clip.profile)[0];
            foreach ((char shape, float radius) in ParseTokens(bar.profile))
            {
                if (shape == 'R' && Mathf.Abs(radius - clipSize.radius) < 0.05f)
                    return true;
            }
            return false;
        }

        // Cylinders: a peg into a hole of the same size.
        if (a.male == b.male || !string.Equals(a.group ?? "", b.group ?? "", StringComparison.Ordinal))
            return false;
        Connector peg = a.male ? a : b;
        Connector hole = a.male ? b : a;
        foreach ((char pegShape, float pegRadius) in ParseTokens(peg.profile))
        {
            foreach ((char holeShape, float holeRadius) in ParseTokens(hole.profile))
            {
                // A bar (round, radius 4) also fits into the middle of an axle hole.
                if (pegShape == 'R' && Mathf.Abs(pegRadius - 4f) < 0.05f && holeShape == 'A' && Mathf.Abs(holeRadius - 6f) < 0.05f)
                    return true;
                if (Mathf.Abs(pegRadius - holeRadius) > 0.05f)
                    continue;
                // Same shape; a round peg in a square socket; an axle spinning in a round hole.
                if (pegShape == holeShape || (pegShape == 'R' && holeShape == 'S') || (pegShape == 'A' && holeShape == 'R'))
                    return true;
            }
        }
        return false;
    }

    static bool FingersInterlock(in Connector a, in Connector b) =>
        FingerPattern.Interlock(a.profile, a.male, b.profile, b.male);

    static (char, float)[] ParseTokens(string profile)
    {
        profile ??= "";
        if (Tokens.TryGetValue(profile, out (char, float)[] parsed))
            return parsed;

        var list = new List<(char, float)>();
        foreach (string token in profile.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 2 && float.TryParse(token.Substring(1), System.Globalization.NumberStyles.Float,
                                                    System.Globalization.CultureInfo.InvariantCulture, out float radius))
                list.Add((token[0], radius));
        }
        if (list.Count == 0)
            list.Add(('?', -1f));
        Tokens[profile] = list.ToArray();
        return Tokens[profile];
    }

    /// <summary>
    /// Studs and their sockets lock parts at quarter turns to each other, and so does an axle in a
    /// cross-shaped hole (the cross sets the angle). Pins, bars, clips, and hinges leave the angle free.
    /// </summary>
    public static bool QuarterTurns(in Connector a, in Connector b)
    {
        if (a.kind != Connector.Kind.Cylinder || b.kind != Connector.Kind.Cylinder)
            return false;
        if (HasShape(a.profile, 'A') && HasShape(b.profile, 'A'))
            return true;
        return !a.slide && !b.slide && IsStudLike(a.profile) && IsStudLike(b.profile);
    }

    static bool HasShape(string profile, char shape)
    {
        foreach ((char s, float _) in ParseTokens(profile))
        {
            if (s == shape)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The stretch of connector c along host's axis, measured from host's start (meters). Used to tell
    /// whether two parts on the same axle, bar, or stud would occupy the same place.
    /// </summary>
    public static (float from, float to) SpanAlong(in Connector host, in Connector c)
    {
        Vector3 u = host.axis;
        float s = Vector3.Dot(c.Start - host.Start, u);
        float e = s + Vector3.Dot(c.axis, u) * c.length;
        return (Mathf.Min(s, e), Mathf.Max(s, e));
    }

    static bool IsStudLike(string profile)
    {
        foreach ((char shape, float radius) in ParseTokens(profile))
        {
            if (!((shape == 'R' || shape == 'S') && Mathf.Abs(radius - 6f) < 0.05f))
                return false;
        }
        return true;
    }

    /// <summary>Whether two connectors (both in world space) are currently plugged together.</summary>
    public static bool AreMated(in Connector a, in Connector b)
    {
        // Cheap distance check first: mated shapes overlap along a shared line.
        float reach = a.length + b.length + Tolerance;
        if ((a.position - b.position).sqrMagnitude > reach * reach)
            return false;
        if (!Fit(a, b))
            return false;
        if (a.kind == Connector.Kind.Generic)
            return (a.position - b.position).sqrMagnitude < Tolerance * Tolerance;
        if (Mathf.Abs(Vector3.Dot(a.axis, b.axis)) < 0.996f)
            return false;

        // On the same line...
        Vector3 u = b.axis;
        Vector3 offset = a.Start - b.Start;
        if ((offset - Vector3.Dot(offset, u) * u).sqrMagnitude > Tolerance * Tolerance)
            return false;

        // ...and overlapping along it.
        float a0 = Vector3.Dot(offset, u);
        float a1 = a0 + Vector3.Dot(a.axis, u) * a.length;
        float overlap = Mathf.Min(Mathf.Max(a0, a1), b.length) - Mathf.Max(Mathf.Min(a0, a1), 0f);
        return overlap > 0.0001f;
    }

    /// <summary>
    /// Where a part must go so its connector (given in the part's local space) plugs into a fixed
    /// connector (world space): turned the least amount needed, then moved onto the connection.
    /// </summary>
    /// <param name="slideAnchors">For sliding connections: where parts already on the fixed shape begin and end
    /// (meters along it from its start), so the moving part can rest against them.</param>
    public static void Align(in Connector movingLocal, Vector3 partPosition, Quaternion partRotation,
                             in Connector fixedWorld, bool quarterTurns,
                             out Vector3 newPosition, out Quaternion newRotation,
                             IReadOnlyList<float> slideAnchors = null)
    {
        Connector moving = movingLocal.Moved(partPosition, partRotation);

        // Line the axes up, whichever way round is closer. Parts describe the same hole either from its
        // opening or from inside (e.g. the tap 4599's socket points down from within), so both are valid;
        // a part turned upside down is ruled out by the caller's tilt limit.
        Vector3 target = Vector3.Dot(moving.axis, fixedWorld.axis) >= 0f ? fixedWorld.axis : -fixedWorld.axis;

        Quaternion turn = Quaternion.FromToRotation(moving.axis, target);
        Quaternion rotation = turn * partRotation;

        if (quarterTurns)
        {
            Vector3 mine = Vector3.ProjectOnPlane(turn * moving.reference, target);
            Vector3 theirs = Vector3.ProjectOnPlane(fixedWorld.reference, target);
            if (mine.sqrMagnitude > 1e-6f && theirs.sqrMagnitude > 1e-6f)
            {
                float angle = Vector3.SignedAngle(mine, theirs, target);
                float snapped = Mathf.Round(angle / 90f) * 90f;
                rotation = Quaternion.AngleAxis(angle - snapped, target) * rotation;
            }
        }

        Connector turned = movingLocal.Moved(partPosition, rotation);
        Vector3 delta;
        if (turned.kind == Connector.Kind.Generic)
        {
            delta = fixedWorld.position - turned.position;
        }
        else
        {
            Vector3 u = fixedWorld.axis;
            Vector3 offset = turned.Start - fixedWorld.Start;
            float direction = Vector3.Dot(turned.axis, u) >= 0f ? 1f : -1f;
            float a0 = Vector3.Dot(offset, u);
            float a1 = a0 + direction * turned.length;
            float middle = (a0 + a1) * 0.5f;

            float shift;
            bool sliding = turned.slide || fixedWorld.slide
                           || turned.kind == Connector.Kind.Clip || fixedWorld.kind == Connector.Kind.Clip;
            bool clip = turned.kind == Connector.Kind.Clip || fixedWorld.kind == Connector.Kind.Clip;
            if (sliding && !clip)
            {
                // Pins, axles, and holes that may slide still click into place: at plate-height or half-stud
                // steps from the fixed shape's ends and from the parts already on it, so a pin goes fully in,
                // a gear sits at a set spot on an axle, and a part can rest against the one below it.
                // The steps line up each shape's reference face (see FaceOffset), which is its start unless it's
                // a hole of an odd length, like the 18 LDU holes of the 6 x 8 Technic frame.
                float lowEnd = Mathf.Min(a0, a1);
                float movingFace = a0 + direction * FaceOffset(turned);
                float phase = FaceOffset(fixedWorld) - (movingFace - lowEnd);
                var shapes = new SlideShapes
                {
                    fixedSections = ParseSections(fixedWorld.sections),
                    movingSections = ParseSections(turned.sections),
                    movingStart = a0,
                    direction = direction,
                    movingMale = turned.male,
                };
                shift = SlideShift(lowEnd, Mathf.Max(a0, a1), fixedWorld.length, slideAnchors, phase, shapes);
            }
            else if (sliding)
            {
                // Clips: stay where it is along the bar, just enough to keep the shorter one within the longer.
                float lo = Mathf.Min(a0, a1), hi = Mathf.Max(a0, a1);
                if (hi - lo > fixedWorld.length)
                {
                    float fixedMiddle = fixedWorld.length * 0.5f;
                    shift = Mathf.Clamp(0f, fixedMiddle - hi, fixedMiddle - lo); // e.g. a long bar through a clip
                }
                else
                {
                    shift = Mathf.Clamp(middle, 0f, fixedWorld.length) - middle; // e.g. a clip along a long bar
                }
            }
            else if ((turned.center && fixedWorld.center) || turned.kind == Connector.Kind.Finger)
            {
                // (Hinge fingers interlock centre to centre; see FingerPattern.)
                shift = fixedWorld.length * 0.5f - middle;                       // middles meet
            }
            else
            {
                // A peg goes in through the hole's end it points toward: pointing the same way as the hole,
                // its base meets the hole's opening (stud into socket); pointing against it, its base meets
                // the hole's far end (e.g. a plate's underside pin into an open stud from above).
                Connector peg = turned.male ? turned : fixedWorld;
                Connector hole = turned.male ? fixedWorld : turned;
                bool sameWay = Vector3.Dot(peg.axis, hole.axis) > 0f;
                Vector3 entry = sameWay ? hole.Start : hole.Start + hole.axis * hole.length;
                float needed = Vector3.Dot(entry - peg.Start, u);   // how far the peg must move along the axis
                shift = turned.male ? needed : -needed;              // ...or the hole the opposite way
            }

            Vector3 sideways = offset - Vector3.Dot(offset, u) * u;
            delta = -sideways + u * shift;
        }

        newPosition = partPosition + delta;
        newRotation = rotation;
    }

    const float HalfStudStep = Brick.StudPitch * 0.5f; // half a stud (10 LDU)
    const float RestMagnet = 0.003f;                    // within 3 mm, rest flush against a neighbor

    // How far along a shape (from its start) the face that sliding steps are counted from is. Normally the
    // start. A hole whose length isn't a whole number of half studs is described from inside the part, and
    // its open face is the end with the wider section (the recess a pin's collar sits in), e.g. the
    // 6 x 8 Technic frame's holes: 16 LDU of bore, then a 2 LDU recess at the outer face.
    static float FaceOffset(in Connector c)
    {
        if (c.kind != Connector.Kind.Cylinder || c.male)
            return 0f;
        float steps = c.length / HalfStudStep;
        if (Mathf.Abs(steps - Mathf.Round(steps)) < 0.02f)
            return 0f;
        (char shape, float radius)[] tokens = ParseTokens(c.profile);
        return tokens.Length >= 2 && tokens[tokens.Length - 1].radius > tokens[0].radius + 0.05f ? c.length : 0f;
    }

    // One stretch of a pin, axle or hole: where it runs (meters from the shape's start) and how wide it is.
    struct Section
    {
        public float from, to;
        public float radius;   // LDU
        public bool flexible;  // the springy lips at a pin's ends, which squeeze through anything
    }

    static readonly Dictionary<string, Section[]> SectionCache = new Dictionary<string, Section[]>();
    static readonly Section[] NoSections = new Section[0];

    // "R8:2 R6:16 R8:2" -> sections in order from the shape's start.
    static Section[] ParseSections(string text)
    {
        if (string.IsNullOrEmpty(text))
            return NoSections;
        if (SectionCache.TryGetValue(text, out Section[] parsed))
            return parsed;

        var list = new List<Section>();
        float at = 0f;
        foreach (string token in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = token.IndexOf(':');
            if (colon < 2
                || !float.TryParse(token.Substring(1, colon - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float radius)
                || !float.TryParse(token.Substring(colon + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float width))
                continue;
            float length = width * 0.0004f; // LDU -> meters
            list.Add(new Section { from = at, to = at + length, radius = radius, flexible = token[0] == 'L' });
            at += length;
        }
        parsed = list.ToArray();
        SectionCache[text] = parsed;
        return parsed;
    }

    // What SlideShift needs to know about the two shapes to keep wide sections out of narrow ones.
    struct SlideShapes
    {
        public Section[] fixedSections, movingSections;
        public float movingStart;  // the moving shape's start, along the fixed shape from its start
        public float direction;    // +1 if the moving shape points the same way as the fixed one, else -1
        public bool movingMale;
    }

    // Whether, with the moving shape shifted by `shift`, a wide section of the peg would sit inside a narrower
    // section of the hole: a pin's collar in the bore instead of the recess made for it, or a part's body
    // pushed into a stud socket. Real parts stop there.
    static bool Fouls(in SlideShapes shapes, float shift)
    {
        if (shapes.fixedSections == null || shapes.movingSections == null)
            return false;
        foreach (Section moving in shapes.movingSections)
        {
            if (moving.flexible)
                continue;
            float m0 = shapes.movingStart + shift + shapes.direction * moving.from;
            float m1 = shapes.movingStart + shift + shapes.direction * moving.to;
            float low = Mathf.Min(m0, m1), high = Mathf.Max(m0, m1);
            foreach (Section still in shapes.fixedSections)
            {
                if (still.flexible || Mathf.Min(high, still.to) - Mathf.Max(low, still.from) < 0.0002f)
                    continue;
                float peg = shapes.movingMale ? moving.radius : still.radius;
                float hole = shapes.movingMale ? still.radius : moving.radius;
                if (peg > hole + 0.3f)
                    return true;
            }
        }
        return false;
    }

    // Along a sliding connection, the moving shape (lo..hi, measured from the fixed shape's start) clicks to
    // half-stud steps: its low end may sit at phase + whole steps (phase is 0 when both shapes are measured
    // from their starts). If it's within a few mm of resting flush against a part already on the same shaft
    // (anchors = pairs of those parts' ends), it rests there instead, so stacked parts sit snugly. A spot is
    // no good if another part is already there or a wide section would be forced into a narrow one (Fouls);
    // then the nearest good spot is used: a step, resting against a neighbor, or seated section to section
    // (a collar in its recess). Returns how far to move it.
    static float SlideShift(float lo, float hi, float fixedLength, IReadOnlyList<float> anchors, float phase, in SlideShapes shapes)
    {
        float length = hi - lo;
        SlideShapes s = shapes; // (local functions can't capture an `in` parameter)
        bool Overlaps(float newLo) => newLo < fixedLength - 0.0001f && newLo + length > 0.0001f;
        bool Good(float newLo) => Overlaps(newLo) && (anchors == null || !Blocked(newLo, length, anchors)) && !Fouls(s, newLo - lo);

        float restShift = 0f, restDistance = float.MaxValue;
        if (anchors != null)
        {
            foreach (float end in anchors)
            {
                // Our low end against its end, or our high end against it.
                for (int side = 0; side < 2; side++)
                {
                    float newLo = side == 0 ? end : end - length;
                    float distance = Mathf.Abs(newLo - lo);
                    if (distance < restDistance && distance <= RestMagnet && Overlaps(newLo))
                    {
                        restDistance = distance;
                        restShift = newLo - lo;
                    }
                }
            }
        }

        float shift;
        if (restDistance < float.MaxValue)
        {
            shift = restShift;
        }
        else
        {
            float stepped = Mathf.Round((lo - phase) / HalfStudStep) * HalfStudStep + phase;
            float home = phase - Mathf.Round(phase / HalfStudStep) * HalfStudStep; // the step nearest the fixed shape's start
            shift = (Overlaps(stepped) ? stepped : home) - lo;
        }
        if (Good(lo + shift))
            return shift;

        // That spot is taken or impossible: take the nearest good one instead.
        float best = shift, bestDistance = float.MaxValue;
        void Try(float newLo)
        {
            float distance = Mathf.Abs(newLo - lo);
            if (distance < bestDistance && Good(newLo))
            {
                bestDistance = distance;
                best = newLo - lo;
            }
        }
        if (anchors != null)
        {
            foreach (float end in anchors)
            {
                Try(end);
                Try(end - length);
            }
        }
        float near = Mathf.Round((lo - phase) / HalfStudStep);
        for (int k = -6; k <= 6; k++)
            Try((near + k) * HalfStudStep + phase);

        // Seated: an edge of one of our sections meeting an edge of one of the fixed shape's.
        if (s.fixedSections != null && s.movingSections != null)
        {
            foreach (Section moving in s.movingSections)
            {
                foreach (Section still in s.fixedSections)
                {
                    for (int corner = 0; corner < 4; corner++)
                    {
                        float mine = s.movingStart + s.direction * ((corner & 1) == 0 ? moving.from : moving.to);
                        float theirs = (corner & 2) == 0 ? still.from : still.to;
                        Try(lo + (theirs - mine));
                    }
                }
            }
        }
        return best;
    }

    // Whether lo..lo+length overlaps a part already on the shaft (anchors = pairs of its from/to).
    static bool Blocked(float lo, float length, IReadOnlyList<float> anchors)
    {
        for (int i = 0; i + 1 < anchors.Count; i += 2)
        {
            if (Mathf.Min(anchors[i + 1], lo + length) - Mathf.Max(anchors[i], lo) > 0.0005f)
                return true;
        }
        return false;
    }
}
