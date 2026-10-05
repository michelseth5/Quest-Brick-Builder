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

    static readonly Dictionary<string, bool> FitCache = new Dictionary<string, bool>();
    static readonly Dictionary<string, (char shape, float radius)[]> Tokens = new Dictionary<string, (char, float)[]>();

    /// <summary>Whether two connectors can plug into each other (ignoring where they are).</summary>
    public static bool Fit(in Connector a, in Connector b)
    {
        string key = $"{(int)a.kind}{(a.male ? 'M' : 'F')}{a.profile}|{a.group}/{(int)b.kind}{(b.male ? 'M' : 'F')}{b.profile}|{b.group}";
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
    /// Studs and their sockets lock parts at quarter turns to each other; pins, axles, bars, clips,
    /// and hinges leave the angle around the connection free.
    /// </summary>
    public static bool QuarterTurns(in Connector a, in Connector b) =>
        a.kind == Connector.Kind.Cylinder && b.kind == Connector.Kind.Cylinder
        && !a.slide && !b.slide && IsStudLike(a.profile) && IsStudLike(b.profile);

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
    public static void Align(in Connector movingLocal, Vector3 partPosition, Quaternion partRotation,
                             in Connector fixedWorld, bool quarterTurns,
                             out Vector3 newPosition, out Quaternion newRotation)
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
            if (sliding)
            {
                // Stay where it is along the bar or axle, just enough to keep the shorter one within the longer.
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
                shift = fixedWorld.length * 0.5f - middle;                       // middles meet
            else if (direction > 0f)
                shift = -a0;                                                     // starts meet (stud into socket)
            else
                shift = fixedWorld.length - a0;                                  // entered from the far end

            Vector3 sideways = offset - Vector3.Dot(offset, u) * u;
            delta = -sideways + u * shift;
        }

        newPosition = partPosition + delta;
        newRotation = rotation;
    }
}
