using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Posing models at their joints. Connections that spin freely (a pin or axle in a round hole, a bar
/// in a clip, hinge fingers) are joints; everything else holds parts rigidly. While one hand holds a model,
/// a grip with the other hand on a part beyond a joint turns that part's side around the joint, following
/// the hand. (Two-handed only, so a model can't be turned by accident.)
/// </summary>
public class Articulation
{
    /// <summary>A connection between two bricks: rigid, or a joint turning around a line.</summary>
    struct Link
    {
        public Brick a, b;
        public bool joint;
        public Vector3 point; // a point on the joint's axis (world)
        public Vector3 axis;  // the joint's axis direction (world)
    }

    readonly Brick _grabbed;
    readonly Transform _frame;               // the holding brick in two-handed posing; null = the world
    readonly Vector3 _pivot, _axis;           // joint axis, in _frame's space
    readonly List<(Brick brick, Vector3 position, Quaternion rotation)> _start = new List<(Brick, Vector3, Quaternion)>();
    Vector3 _handStart;                       // in _frame's space
    bool _hasHand;
    readonly TextMeshPro _label;

    Articulation(Brick grabbed, Transform frame, Vector3 pivotWorld, Vector3 axisWorld, List<Brick> moving)
    {
        _grabbed = grabbed;
        _frame = frame;
        _pivot = ToFrame(pivotWorld);
        _axis = ToFrameDirection(axisWorld).normalized;
        foreach (Brick brick in moving)
            _start.Add((brick, ToFrame(brick.transform.position), ToFrameRotation(brick.transform.rotation)));

        _label = MenuStyle.CreateText(null, "0°", pivotWorld, new Vector2(0.04f, 0.012f));
        _label.color = new Color(1f, 0.9f, 0.4f);
    }

    // ---------- Deciding what turns ----------

    /// <summary>
    /// Called when a part is gripped. If the other hand is holding the model and this part is beyond a
    /// joint, returns the posing session; otherwise null (picked up as usual). Two-handed only, so a
    /// model can't be turned by accident.
    /// </summary>
    public static Articulation TryBegin(Brick grabbed, Vector3 handPosition)
    {
        Brick holder = null;
        foreach (Brick held in Brick.Held)
        {
            if (held != grabbed && held.IsCarrying(grabbed))
            {
                holder = held;
                break;
            }
        }
        if (holder == null)
            return null;

        List<Brick> model = Members(holder);
        if (model.Count < 2)
            return null;

        List<Link> links = FindLinks(model);
        HashSet<Brick> section = RigidSection(grabbed, links);

        Link? best = null;
        List<Brick> bestMoving = null;
        int bestAnchorSize = -1;
        foreach (Link link in links)
        {
            if (!link.joint || section.Contains(link.a) == section.Contains(link.b))
                continue; // not a joint leaving the grabbed part's section

            // The grabbed part's side of this joint.
            List<Brick> side = SideOf(grabbed, link, links);
            if (side.Count >= model.Count)
                continue; // a loop: cutting this joint doesn't free anything
            int anchorSize = model.Count - side.Count;

            if (side.Contains(holder))
                continue; // must turn away from the holding hand

            if (anchorSize > bestAnchorSize)
            {
                best = link;
                bestMoving = side;
                bestAnchorSize = anchorSize;
            }
        }

        if (best == null)
            return null;

        var session = new Articulation(grabbed, holder.transform, best.Value.point, best.Value.axis, bestMoving);
        session.SetHand(handPosition);
        return session;
    }

    static List<Brick> Members(Brick holder)
    {
        var members = new List<Brick> { holder };
        members.AddRange(holder.CarriedBricks);
        return members;
    }

    static List<Link> FindLinks(List<Brick> bricks)
    {
        var links = new List<Link>();
        for (int i = 0; i < bricks.Count; i++)
        {
            for (int j = i + 1; j < bricks.Count; j++)
            {
                if (Classify(bricks[i], bricks[j], out Link link))
                    links.Add(link);
            }
        }
        return links;
    }

    // Rigid if any connection between them locks the angle (studs, an axle in a cross hole, special
    // shapes), or if they connect along two different lines. A joint if everything spins on one line.
    static bool Classify(Brick a, Brick b, out Link link)
    {
        link = new Link { a = a, b = b };
        bool connected = false;
        bool rigid = false;
        foreach (Connector ca in a.Connectors)
        {
            Connector wa = ca.InWorld(a.transform);
            foreach (Connector cb in b.Connectors)
            {
                Connector wb = cb.InWorld(b.transform);
                if (!Connections.AreMated(wa, wb))
                    continue;

                if (Connections.QuarterTurns(ca, cb) || wa.kind == Connector.Kind.Generic)
                {
                    rigid = true;
                }
                else if (!connected)
                {
                    link.point = wb.Start;
                    link.axis = wb.axis;
                }
                else if (!OnSameLine(link.point, link.axis, wb.Start, wb.axis))
                {
                    rigid = true; // two different spin axes lock each other
                }
                connected = true;
            }
        }
        link.joint = connected && !rigid;
        return connected;
    }

    static bool OnSameLine(Vector3 p1, Vector3 d1, Vector3 p2, Vector3 d2)
    {
        if (Mathf.Abs(Vector3.Dot(d1, d2)) < 0.996f)
            return false;
        Vector3 offset = p2 - p1;
        return (offset - Vector3.Dot(offset, d1) * d1).sqrMagnitude < 0.0006f * 0.0006f;
    }

    // Bricks reachable from `start` through rigid links only.
    static HashSet<Brick> RigidSection(Brick start, List<Link> links)
    {
        var section = new HashSet<Brick> { start };
        var queue = new Queue<Brick>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            Brick brick = queue.Dequeue();
            foreach (Link link in links)
            {
                if (link.joint)
                    continue;
                Brick other = link.a == brick ? link.b : link.b == brick ? link.a : null;
                if (other != null && section.Add(other))
                    queue.Enqueue(other);
            }
        }
        return section;
    }

    // Bricks reachable from `start` through any link except `cut`.
    static List<Brick> SideOf(Brick start, Link cut, List<Link> links)
    {
        var side = new List<Brick> { start };
        for (int i = 0; i < side.Count; i++)
        {
            foreach (Link link in links)
            {
                if ((link.a == cut.a && link.b == cut.b) || (link.a == cut.b && link.b == cut.a))
                    continue;
                Brick other = link.a == side[i] ? link.b : link.b == side[i] ? link.a : null;
                if (other != null && !side.Contains(other))
                    side.Add(other);
            }
        }
        return side;
    }

    // ---------- Turning ----------

    public void SetHand(Vector3 handWorld)
    {
        if (!_hasHand)
        {
            _handStart = ToFrame(handWorld);
            _hasHand = true;
        }
        _handNow = ToFrame(handWorld);
    }

    Vector3 _handNow;

    /// <summary>Each frame: turn the moving side to follow the hand (after the grab system moved the part).</summary>
    public void Apply()
    {
        Vector3 from = Vector3.ProjectOnPlane(_handStart - _pivot, _axis);
        Vector3 to = Vector3.ProjectOnPlane(_handNow - _pivot, _axis);
        float angle = 0f;
        if (from.sqrMagnitude > 1e-6f && to.sqrMagnitude > 1e-6f)
            angle = Vector3.SignedAngle(from, to, _axis);

        float step = GameSettings.JointStepDegrees;
        if (step > 0f)
            angle = Mathf.Round(angle / step) * step;

        Quaternion spin = Quaternion.AngleAxis(angle, _axis);
        foreach ((Brick brick, Vector3 position, Quaternion rotation) in _start)
        {
            if (brick == null)
                continue;
            Vector3 p = _pivot + spin * (position - _pivot);
            Quaternion r = spin * rotation;
            if (_frame != null)
                brick.transform.SetPositionAndRotation(_frame.TransformPoint(p), _frame.rotation * r);
            else
                brick.transform.SetPositionAndRotation(p, r);
        }

        if (_label == null)
            return; // finished: the label is gone
        Vector3 pivotWorld = FromFrame(_pivot);
        _label.text = $"{angle:+0;-0;0}°";
        _label.transform.position = pivotWorld + Vector3.up * 0.02f;
        Camera head = Camera.main;
        if (head != null)
            _label.transform.rotation = Quaternion.LookRotation(_label.transform.position - head.transform.position, Vector3.up);
    }

    /// <summary>The bricks this session turns (for undo).</summary>
    public IEnumerable<Brick> MovingBricks
    {
        get
        {
            foreach ((Brick brick, Vector3 _, Quaternion _) in _start)
                yield return brick;
        }
    }

    public void End()
    {
        // On the release frame the grab system has already moved the part to the hand; put it back on the
        // joint one last time so it doesn't come loose.
        Apply();

        if (_label != null)
            Object.Destroy(_label.gameObject);

        // The holding hand's model changed shape: update what it can snap with.
        if (_frame != null && _frame.TryGetComponent(out Brick holder))
            holder.RefreshGroupConnections();
    }

    // ---------- Frame helpers ----------

    Vector3 ToFrame(Vector3 world) => _frame != null ? _frame.InverseTransformPoint(world) : world;
    Vector3 FromFrame(Vector3 local) => _frame != null ? _frame.TransformPoint(local) : local;
    Vector3 ToFrameDirection(Vector3 world) => _frame != null ? _frame.InverseTransformDirection(world) : world;
    Quaternion ToFrameRotation(Quaternion world) => _frame != null ? Quaternion.Inverse(_frame.rotation) * world : world;
}
