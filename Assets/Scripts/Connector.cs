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
            return a.kind == b.kind && a.profile == b.profile && a.male != b.male;

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
                shift = SlideShift(Mathf.Min(a0, a1), Mathf.Max(a0, a1), fixedWorld.length, slideAnchors);
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
            else if (turned.center && fixedWorld.center)
            {
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

    // Along a sliding connection, the moving shape (lo..hi, measured from the fixed shape's start) clicks to
    // half-stud steps from the fixed shape's start. If it's within a few mm of resting flush against a part
    // already on the same shaft (anchors = those parts' ends), it rests there instead, so stacked parts sit
    // snugly. Returns how far to move it.
    static float SlideShift(float lo, float hi, float fixedLength, IReadOnlyList<float> anchors)
    {
        float length = hi - lo;
        bool Overlaps(float newLo) => newLo < fixedLength - 0.0001f && newLo + length > 0.0001f;

        float restShift = 0f, restDistance = float.MaxValue;
        if (anchors != null)
        {
            foreach (float end in anchors)
            {
                // Our low end against its end, or our high end against it.
                foreach (float newLo in new[] { end, end - length })
                {
                    float distance = Mathf.Abs(newLo - lo);
                    if (distance < restDistance && distance <= RestMagnet && Overlaps(newLo))
                    {
                        restDistance = distance;
                        restShift = newLo - lo;
                    }
                }
            }
        }
        if (restDistance < float.MaxValue)
            return restShift;

        float stepped = Mathf.Round(lo / HalfStudStep) * HalfStudStep;
        return (Overlaps(stepped) ? stepped : 0f) - lo;
    }
}
