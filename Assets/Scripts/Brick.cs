using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// A real-scale brick. Its shape comes from an imported LDraw part, or is generated from a simple size.
/// When the player lets go of it, it snaps so one of its studs plugs into another brick (or one of the
/// other brick's studs plugs into it), or sets down on a real table or floor.
/// A trigger/pinch grab takes just this brick; a grip/whole-hand grab takes every brick connected to it.
/// The transform scale must stay at 1.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class Brick : MonoBehaviour
{
    // Real-world brick dimensions in meters (same proportions LDraw uses).
    public const float StudPitch = 0.008f;
    public const float PlateHeight = 0.0032f;
    const float StudRadius = 0.0024f;
    const float StudHeight = 0.0017f;
    const float SideGap = 0.0001f; // real bricks are slightly undersized so neighbors don't touch
    const int StudSegments = 16;

    static readonly List<Brick> AllBricks = new List<Brick>();
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly Collider[] OverlapResults = new Collider[32];

    [Tooltip("An imported LDraw part. When set, its shape and studs are used instead of the simple size below.")]
    public BrickPartData part;

    [Header("Simple brick (used when no part is set)")]
    [Min(1)] public int studsX = 2;
    [Min(1)] public int studsZ = 4;
    [Tooltip("3 for a brick, 1 for a plate.")]
    [Min(1)] public int heightInPlates = 3;

    public Color color = new Color(0.8f, 0.1f, 0.1f);
    [Tooltip("LDraw color code for the color (used when saving or matching sets).")]
    public int colorCode = 4;

    [Tooltip("Used instead of the normal material when the color is see-through.")]
    public Material transparentMaterial;

    [Header("Snapping")]
    [Tooltip("How far (in meters) a brick may be moved to plug it into a nearby connection.")]
    public float maxVerticalSnap = 0.008f;
    [Tooltip("How far the brick can be tilted relative to the other brick and still snap.")]
    [Range(0f, 90f)] public float maxTiltDegrees = 35f;
    [Tooltip("How close (in meters) the brick must be to a real table or floor to set down on it.")]
    public float maxSurfaceSnap = 0.03f;

    [Header("Preview")]
    [Tooltip("A transparent material that shows where the brick will land while it's held. Leave empty for no preview.")]
    public Material ghostMaterial;

    /// <summary>Bounds of the brick without its studs, in local space.</summary>
    public Bounds BodyBounds => _body;

    /// <summary>
    /// A display copy sitting in a menu. Previews are ignored by snapping until someone grabs one,
    /// which turns it into a normal brick.
    /// </summary>
    public bool IsPreview { get; set; }

    /// <summary>Raised when a menu preview is grabbed and becomes a real brick, so the menu can replace it.</summary>
    /// The bool is true for a grip (whole-group) grab, false for a trigger (single) grab.
    public static event System.Action<Brick, bool> PreviewTaken;

    /// <summary>Raised whenever a brick is picked up.</summary>
    public static event System.Action<Brick> Grabbed;

    /// <summary>Raised for each brick dropped into a trash can, just before it's deleted.</summary>
    public static event System.Action<Brick> Trashed;

    /// <summary>
    /// Bricks bonded into one model, even where they don't connect by studs (wheels on pins, parts on
    /// sideways studs, tools in clips). A grip grab takes the whole bond; a trigger grab pulls one brick out.
    /// </summary>
    public class Bond
    {
        public readonly HashSet<Brick> members = new HashSet<Brick>();
    }

    Bond _bond;

    /// <summary>The brick's bond, or null. Bricks sharing a bond move together.</summary>
    public Bond CurrentBond => _bond;

    public static void BondTogether(IEnumerable<Brick> bricks)
    {
        var bond = new Bond();
        foreach (Brick brick in bricks)
        {
            if (brick == null)
                continue;
            brick.LeaveBond();
            brick._bond = bond;
            bond.members.Add(brick);
        }
    }

    public void LeaveBond()
    {
        if (_bond == null)
            return;
        _bond.members.Remove(this);
        _bond = null;
    }

    /// <summary>Bricks currently held in a hand (only the brick actually grabbed, not what it carries).</summary>
    public static readonly List<Brick> Held = new List<Brick>();

    /// <summary>Every real brick placed in the world (not menu previews).</summary>
    public static List<Brick> AllPlaced()
    {
        var placed = new List<Brick>();
        foreach (Brick brick in AllBricks)
        {
            if (brick.IsBuildable)
                placed.Add(brick);
        }
        return placed;
    }

    /// <summary>Bricks this brick is carrying as part of a grabbed group.</summary>
    UndoHistory.Step _undoStep; // what this brick's current grab changed, for Undo

    bool CameFromMenu(UndoHistory.Step step) => UndoHistory.IsSpawnOf(step, this);

    // The held brick plus everything it's carrying, each relative to where it should go back to.
    UndoHistory.Step RecordGroupForUndo()
    {
        UndoHistory.Step step = UndoHistory.RecordMove(new[] { this });
        foreach ((Brick brick, Transform originalParent) in _carried)
            UndoHistory.AddPose(step, brick, originalParent);
        return step;
    }

    /// <summary>Tells listeners (like the Parts Bin) this brick is being thrown away.</summary>
    public void NotifyTrashed() => Trashed?.Invoke(this);

    /// <summary>Puts the brick back into a bond it used to belong to (or none).</summary>
    public void RejoinBond(Bond bond)
    {
        if (_bond == bond)
            return;
        LeaveBond();
        if (bond == null)
            return;
        _bond = bond;
        bond.members.Add(this);
    }

    /// <summary>Re-reads the held group's free connectors (e.g. after part of it was turned at a joint).</summary>
    public void RefreshGroupConnections()
    {
        if (_carried.Count == 0)
            return;
        var group = new List<Brick> { this };
        group.AddRange(CarriedBricks);
        BuildGroupConnections(group);
    }

    /// <summary>This brick's connection points, in its local space.</summary>
    public IReadOnlyList<Connector> Connectors => _connectors;

    /// <summary>Every brick connected to this one, directly or through others (this one first).</summary>
    public List<Brick> ConnectedModel()
    {
        var model = new List<Brick> { this };
        for (int i = 0; i < model.Count; i++)
        {
            foreach (Brick other in AllBricks)
            {
                if (other.IsBuildable && !model.Contains(other) && model[i].IsConnectedTo(other))
                    model.Add(other);
            }
        }
        return model;
    }

    public IEnumerable<Brick> CarriedBricks
    {
        get
        {
            foreach ((Brick brick, Transform _) in _carried)
                yield return brick;
        }
    }

    // Baseplates anchor a model: grabbing a model by its grip doesn't pull the baseplate along.
    bool IsBaseplate => part != null && part.Category == "Baseplate";

    /// <summary>Shrunk down and sitting in a wrist inventory slot.</summary>
    public bool IsStored { get; private set; }

    /// <summary>Which hand last grabbed this brick: 0 = left, 1 = right, -1 = unknown.</summary>
    public int HeldHand { get; private set; } = -1;

    /// <summary>Placed into a set being built; can't be grabbed until the set is finished or exited.</summary>
    public bool IsLocked { get; private set; }

    public void SetLocked(bool locked)
    {
        IsLocked = locked;
        foreach (Collider collider in GetComponents<Collider>())
            collider.enabled = !locked;
    }

    // Bricks that can be built on: real, active, and not sitting in a menu, an inventory, or a set build.
    bool IsBuildable => isActiveAndEnabled && !IsPreview && !IsStored && !IsLocked;

    // Shape and connection points, in local space. Filled by Rebuild().
    Mesh _activeMesh;
    Mesh _generatedMesh;
    readonly List<Connector> _connectors = new List<Connector>();
    Bounds _body;

    bool _dirty = true;
    Articulation _articulation; // set while this brick is being turned around a joint
    Articulation _articulationTail; // just finished: applied once more the next frame
    Material _opaqueMaterial;
    PointableElement _grabbable;
    GameObject _ghost;
    readonly List<(Brick brick, Transform originalParent)> _carried = new List<(Brick, Transform)>();

    // While carrying a group: the group's unused connection points, in this brick's local space.
    readonly List<Connector> _groupConnectors = new List<Connector>();
    List<Connector> SnapConnectors => _carried.Count > 0 ? _groupConnectors : _connectors;

    void OnEnable()
    {
        AllBricks.Add(this);
        _dirty = true;

        if (Application.isPlaying)
        {
            _grabbable = GetComponent<PointableElement>();
            if (_grabbable != null)
                _grabbable.WhenPointerEventRaised += OnPointerEvent;
        }
    }

    void OnDisable()
    {
        AllBricks.Remove(this);
        Held.Remove(this);
        HideGhosts();
        PutDownStack();
        _articulation?.End();
        _articulation = null;
        if (_grabbable != null)
            _grabbable.WhenPointerEventRaised -= OnPointerEvent;
    }

    void OnValidate() => _dirty = true;

    void Update()
    {
        if (_dirty)
            Rebuild();
        if (Application.isPlaying)
            UpdateGhost();
    }

    // Posing runs after the grab system has moved the grabbed part this frame, and overrides it.
    void LateUpdate()
    {
        _articulation?.Apply();
        if (_articulationTail != null)
        {
            _articulationTail.Apply();
            _articulationTail = null;
        }
    }

    void OnDestroy()
    {
        LeaveBond();
        if (_ghost != null)
            Destroy(_ghost);

        // Only the generated mesh belongs to this brick; imported part meshes are shared assets.
        if (_generatedMesh == null)
            return;
        if (Application.isPlaying)
            Destroy(_generatedMesh);
        else
            DestroyImmediate(_generatedMesh);
    }

    // ---------- Grabbing ----------

    void OnPointerEvent(PointerEvent evt)
    {
        if (evt.Type == PointerEventType.Select && _grabbable.SelectingPointsCount == 1)
        {
            bool wholeGroupGrab = BrickGrabInput.IsWholeGroupGrab(evt);
            bool fromMenu = IsPreview;
            if (IsPreview)
            {
                // Taken out of a menu: it's a real brick from now on.
                IsPreview = false;
                transform.SetParent(null, worldPositionStays: true);
                PreviewTaken?.Invoke(this, wholeGroupGrab);
            }

            if (!Held.Contains(this))
                Held.Add(this);
            HeldHand = WristInventory.ClosestHand(evt.Pose.position);

            // Taken off a wrist: grows back to full size, and always comes out as the whole stored group.
            bool fromInventory = IsStored && WristInventory.TakeOut(this);

            PutDownStack();

            // Gripping a part beyond a joint poses it (turns it around the joint) instead of lifting the model.
            if (wholeGroupGrab && !fromInventory && !fromMenu)
                _articulation = Articulation.TryBegin(this, evt.Pose.position);

            // Remember where everything was, for Undo.
            _undoStep = null;
            if (fromMenu)
            {
                _undoStep = UndoHistory.RecordSpawn(new[] { this }); // undo puts it back in the menu
            }
            else if (_articulation != null)
            {
                _undoStep = UndoHistory.RecordMove(_articulation.MovingBricks);
            }
            else if (fromInventory || wholeGroupGrab)
            {
                PickUpGroup();
                if (!fromInventory)
                    _undoStep = RecordGroupForUndo();
            }
            else
            {
                _undoStep = UndoHistory.RecordMove(new[] { this });
                LeaveBond(); // pulled off on its own
            }

            Grabbed?.Invoke(this);
        }
        else if (evt.Type == PointerEventType.Move && _articulation != null)
        {
            _articulation.SetHand(evt.Pose.position);
        }
        else if ((evt.Type == PointerEventType.Unselect || evt.Type == PointerEventType.Cancel)
                 && _grabbable.SelectingPointsCount == 0)
        {
            // Only snap once every hand has let go.
            Held.Remove(this);
            HideGhosts();

            // Finished posing a joint: it stays at the angle it was turned to. (Re-applied once more next
            // frame too, in case the grab system moves the part again after letting go.)
            if (_articulation != null)
            {
                _articulation.End();
                _articulationTail = _articulation;
                _articulation = null;
                return;
            }

            // Let go near its spot in a set being built: locks into place.
            if (SetBuilder.TryPlace(this))
            {
                UndoHistory.Discard(_undoStep);
                return;
            }

            // Let go next to the other wrist: goes into that wrist's inventory.
            if (WristInventory.TryStore(this))
            {
                UndoHistory.Discard(_undoStep);
                return;
            }

            // Dropped into a trash can: it goes, along with everything it's carrying (they're its children).
            if (TrashCan.Contains(transform.TransformPoint(_body.center)))
            {
                var trashed = new List<Brick> { this };
                foreach ((Brick carried, Transform _) in _carried)
                    trashed.Add(carried);
                foreach (Brick brick in trashed)
                    Trashed?.Invoke(brick);
                _carried.Clear();

                if (CameFromMenu(_undoStep))
                {
                    // Straight from a menu into the trash: nothing worth undoing.
                    UndoHistory.Discard(_undoStep);
                    Destroy(gameObject);
                }
                else
                {
                    // Hidden rather than deleted, so Undo can bring it back.
                    UndoHistory.MarkTrashed(_undoStep, this, trashed);
                    gameObject.SetActive(false);
                }
                return;
            }

            TrySnap();
            PutDownStack();
        }
    }

    // Temporarily parents every brick connected to this one (above or below) to it,
    // so the whole model moves as a unit.
    void PickUpGroup()
    {
        var group = new List<Brick> { this };
        for (int i = 0; i < group.Count; i++)
        {
            // Bonded bricks come along whether or not they connect by studs.
            if (group[i]._bond != null)
            {
                foreach (Brick bonded in group[i]._bond.members)
                {
                    if (bonded != null && bonded.IsBuildable && !bonded.IsBaseplate && !group.Contains(bonded))
                        group.Add(bonded);
                }
            }

            foreach (Brick other in AllBricks)
            {
                if (other.IsBuildable && !other.IsBaseplate && !group.Contains(other) && group[i].IsConnectedTo(other))
                {
                    group.Add(other);
                }
            }
        }

        for (int i = 1; i < group.Count; i++)
        {
            Transform t = group[i].transform;
            _carried.Add((group[i], t.parent));
            t.SetParent(transform, worldPositionStays: true);
        }

        BuildGroupConnections(group);
    }

    // Collects every member's connection points into this brick's space, leaving out the ones already
    // plugged into another member, so only the group's exposed studs, sockets, pins, etc. can snap.
    void BuildGroupConnections(List<Brick> group)
    {
        var world = new List<(Brick owner, Connector connector)>();
        foreach (Brick member in group)
        {
            foreach (Connector connector in member._connectors)
                world.Add((member, connector.InWorld(member.transform)));
        }

        _groupConnectors.Clear();
        Quaternion toLocal = Quaternion.Inverse(transform.rotation);
        foreach ((Brick owner, Connector connector) in world)
        {
            bool used = false;
            foreach ((Brick otherOwner, Connector other) in world)
            {
                if (otherOwner != owner && Connections.AreMated(connector, other))
                {
                    used = true;
                    break;
                }
            }
            if (!used) // world -> this brick's local space
                _groupConnectors.Add(connector.Moved(-(toLocal * transform.position), toLocal));
        }
    }

    /// <summary>
    /// For the wrist inventory: ends carrying, but leaves the carried bricks attached as children so the
    /// group stays together while stored. Returns this brick followed by the bricks it was carrying.
    /// </summary>
    public List<Brick> DetachGroupForStorage()
    {
        var members = new List<Brick> { this };
        foreach ((Brick brick, Transform _) in _carried)
            members.Add(brick);
        _carried.Clear();
        _groupConnectors.Clear();
        return members;
    }

    public void SetStored(bool stored) => IsStored = stored;

    void PutDownStack()
    {
        foreach ((Brick brick, Transform originalParent) in _carried)
        {
            if (brick != null)
                brick.transform.SetParent(originalParent, worldPositionStays: true);
        }
        _carried.Clear();
        _groupConnectors.Clear();
    }

    public bool IsCarrying(Brick brick)
    {
        foreach ((Brick carried, Transform _) in _carried)
        {
            if (carried == brick)
                return true;
        }
        return false;
    }

    /// <summary>True when any of this brick's connectors is plugged into one of the other brick's.</summary>
    public bool IsConnectedTo(Brick other)
    {
        // Quick rejection: too far apart to touch.
        float reach = WorldRadius + other.WorldRadius + 0.01f;
        if ((WorldCenter - other.WorldCenter).sqrMagnitude > reach * reach)
            return false;

        foreach (Connector mine in _connectors)
        {
            Connector a = mine.InWorld(transform);
            foreach (Connector theirs in other._connectors)
            {
                if (Connections.AreMated(a, theirs.InWorld(other.transform)))
                    return true;
            }
        }
        return false;
    }

    Vector3 WorldCenter => transform.TransformPoint(_body.center);
    float WorldRadius => _body.extents.magnitude + 0.004f; // a little extra for studs and pins

    // ---------- Snapping ----------

    public bool TrySnap()
    {
        // Make sure overlap checks see where every brick is right now.
        Physics.SyncTransforms();
        if (!FindSnap(out Vector3 position, out Quaternion rotation))
            return false;
        transform.SetPositionAndRotation(position, rotation);
        return true;
    }

    /// <summary>
    /// Finds the closest pose that plugs one of this brick's (or its group's) free connectors into a
    /// matching connector on another brick: studs into sockets, pins into holes, bars into clips, and so on.
    /// </summary>
    public bool FindSnap(out Vector3 bestPosition, out Quaternion bestRotation)
    {
        Vector3 position = transform.position;
        Quaternion rotation = transform.rotation;
        List<Connector> mine = SnapConnectors;

        // 1. Connectors on nearby parts, in world space.
        List<Connector> nearby = NearbyScratch;
        nearby.Clear();
        float myReach = GroupRadius();
        float gather = myReach + maxVerticalSnap + 0.02f;
        foreach (Brick other in AllBricks)
        {
            if (other == this || !other.IsBuildable || IsCarrying(other))
                continue;
            float reach = myReach + other.WorldRadius + maxVerticalSnap + 0.01f;
            if ((WorldCenter - other.WorldCenter).sqrMagnitude > reach * reach)
                continue;
            foreach (Connector theirsLocal in other._connectors)
            {
                Connector theirs = theirsLocal.InWorld(other.transform);
                if ((theirs.position - WorldCenter).sqrMagnitude < gather * gather)
                    nearby.Add(theirs);
            }
        }

        // 2. Every single connection we could make, lined up the simple way. Keep the closest few.
        List<SnapCandidate> candidates = CandidateScratch;
        candidates.Clear();
        foreach (Connector theirs in nearby)
        {
            foreach (Connector local in mine)
            {
                Connector current = local.Moved(position, rotation);
                float near = maxVerticalSnap + 0.012f + current.length + theirs.length;
                if ((current.position - theirs.position).sqrMagnitude > near * near || !Connections.Fit(local, theirs))
                    continue;

                Connections.Align(local, position, rotation, theirs, Connections.QuarterTurns(local, theirs),
                                  out Vector3 snappedPosition, out Quaternion snappedRotation,
                                  local.slide || theirs.slide ? PartnerEnds(theirs, nearby) : null);
                float move = (snappedPosition - position).magnitude;
                float turn = Quaternion.Angle(snappedRotation, rotation);
                if (move > maxVerticalSnap || turn > maxTiltDegrees)
                    continue;
                candidates.Add(new SnapCandidate
                {
                    theirs = theirs,
                    position = snappedPosition,
                    rotation = snappedRotation,
                    score = move + turn * 0.0003f,
                });
            }
        }
        candidates.Sort((a, b) => a.score.CompareTo(b.score));
        if (candidates.Count > RefinedCandidates)
            candidates.RemoveRange(RefinedCandidates, candidates.Count - RefinedCandidates);

        // 3. For those, also try turning around the connection so a second connector lines up too
        //    (needed when the first stud's angle means nothing, e.g. a stud on a spinning pin), and prefer
        //    whichever pose plugs in the most connections.
        float bestScore = float.MaxValue;
        Vector3 foundPosition = default;
        Quaternion foundRotation = default;
        foreach (SnapCandidate candidate in candidates)
        {
            Consider(candidate.position, candidate.rotation, candidate.theirs);
            foreach (float roll in SecondConnectionTurns(candidate, mine, nearby))
            {
                Quaternion spin = Quaternion.AngleAxis(roll, candidate.theirs.axis);
                Vector3 pivot = candidate.theirs.Start;
                Consider(pivot + spin * (candidate.position - pivot), spin * candidate.rotation, candidate.theirs);
            }
        }

        bestPosition = foundPosition;
        bestRotation = foundRotation;
        if (bestScore < float.MaxValue)
            return true;

        // No brick to connect to: try setting it down on a real table or the floor instead.
        return FindSurfaceSnap(out bestPosition, out bestRotation);

        void Consider(Vector3 p, Quaternion r, Connector theirs)
        {
            float move = (p - position).magnitude;
            float turn = Quaternion.Angle(r, rotation);
            if (move > maxVerticalSnap || turn > maxTiltDegrees)
                return;
            int plugged = CountConnections(p, r, mine, nearby);
            float score = move + turn * 0.0003f - (plugged - 1) * ConnectionBonus;

            // Hanging on a single connection (e.g. a round plate on one center stud), prefer the placement
            // that centers the part over it, since that's usually what's meant.
            if (plugged <= 1)
            {
                Vector3 offset = p + r * _body.center - theirs.Start;
                score += CenteringWeight * Vector3.ProjectOnPlane(offset, theirs.axis).magnitude;
            }
            if (score >= bestScore || UsesTakenConnection(p, r)) // don't plug into a spot that's already used
                return;
            bestScore = score;
            foundPosition = p;
            foundRotation = r;
        }
    }

    // One way of plugging in: the fixed connector used, and where that puts this brick.
    struct SnapCandidate
    {
        public Connector theirs;
        public Vector3 position;
        public Quaternion rotation;
        public float score; // distance moved, plus a little for turning
    }

    const int RefinedCandidates = 6;
    const float ConnectionBonus = 0.002f; // each extra connection plugged in counts like 2 mm closer
    const float CenteringWeight = 0.25f;  // single-connection snaps: 4 mm off-center counts like 1 mm farther
    static readonly List<float> AnchorScratch = new List<float>();

    // Where the parts already on a connector (e.g. an axle) begin and end along it, from its start.
    static List<float> PartnerEnds(in Connector host, List<Connector> nearby)
    {
        List<float> ends = AnchorScratch;
        ends.Clear();
        foreach (Connector other in nearby)
        {
            if (other.position == host.position && other.axis == host.axis)
                continue; // the connector itself
            if (!Connections.AreMated(other, host))
                continue;
            (float from, float to) span = Connections.SpanAlong(host, other);
            ends.Add(span.from);
            ends.Add(span.to);
        }
        return ends;
    }
    static readonly List<Connector> NearbyScratch = new List<Connector>();
    static readonly List<SnapCandidate> CandidateScratch = new List<SnapCandidate>();
    static readonly List<float> RollScratch = new List<float>();

    // Angles (degrees) to turn around the candidate's connection so that another of our connectors lands
    // on another nearby connector it fits, the same distance from the turning axis.
    static List<float> SecondConnectionTurns(in SnapCandidate candidate, List<Connector> mine, List<Connector> nearby)
    {
        List<float> rolls = RollScratch;
        rolls.Clear();
        Vector3 axis = candidate.theirs.axis;
        Vector3 pivot = candidate.theirs.Start;

        foreach (Connector local in mine)
        {
            Connector moved = local.Moved(candidate.position, candidate.rotation);
            if (Mathf.Abs(Vector3.Dot(moved.axis, axis)) < 0.996f)
                continue;
            Vector3 from = Vector3.ProjectOnPlane(moved.position - pivot, axis);
            if (from.sqrMagnitude < 1e-8f)
                continue; // on the axis itself: turning doesn't move it

            foreach (Connector theirs in nearby)
            {
                if (Mathf.Abs(Vector3.Dot(theirs.axis, axis)) < 0.996f)
                    continue;
                Vector3 to = Vector3.ProjectOnPlane(theirs.position - pivot, axis);
                if (Mathf.Abs(to.magnitude - from.magnitude) > 0.0008f || !Connections.Fit(local, theirs))
                    continue;

                float roll = Vector3.SignedAngle(from, to, axis);
                if (Mathf.Abs(roll) < 0.5f)
                    continue;
                bool seen = false;
                foreach (float r in rolls)
                {
                    if (Mathf.Abs(Mathf.DeltaAngle(r, roll)) < 1f)
                    {
                        seen = true;
                        break;
                    }
                }
                if (!seen)
                    rolls.Add(roll);
            }
        }
        return rolls;
    }

    // How many of our connectors would be plugged into nearby connectors at this pose.
    static int CountConnections(Vector3 position, Quaternion rotation, List<Connector> mine, List<Connector> nearby)
    {
        int count = 0;
        foreach (Connector local in mine)
        {
            Connector moved = local.Moved(position, rotation);
            foreach (Connector theirs in nearby)
            {
                if (Connections.AreMated(moved, theirs))
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    // True if, at this pose, any of our free connectors would plug into a connector that another part is
    // already plugged into (e.g. a stud that already has a brick on it). This is exact for any part shape,
    // unlike checking boxes, which fails for clips, brackets, and other irregular parts.
    bool UsesTakenConnection(Vector3 position, Quaternion rotation)
    {
        Vector3 center = position + rotation * _body.center;
        float myReach = GroupRadius();

        foreach (Brick other in AllBricks)
        {
            if (other == this || !other.IsBuildable || IsCarrying(other))
                continue;
            float reach = myReach + other.WorldRadius;
            if ((center - other.WorldCenter).sqrMagnitude > reach * reach)
                continue;

            foreach (Connector theirsLocal in other._connectors)
            {
                Connector theirs = theirsLocal.InWorld(other.transform);
                foreach (Connector mine in SnapConnectors)
                {
                    Connector moved = mine.Moved(position, rotation);
                    if (Connections.AreMated(moved, theirs) && IsTaken(theirs, other, moved))
                        return true;
                }
            }
        }
        return false;
    }

    // Whether `incoming` (world space) would plug into `connector` (on `owner`) where some other part we
    // aren't holding is already plugged in. Long shapes (axles, bars, pins through two holes) can hold
    // several parts, so only an overlap along the shape counts; a stud with a brick on it is fully taken.
    bool IsTaken(in Connector connector, Brick owner, in Connector incoming)
    {
        (float from, float to) wanted = Connections.SpanAlong(connector, incoming);
        foreach (Brick brick in AllBricks)
        {
            if (brick == owner || brick == this || !brick.IsBuildable || IsCarrying(brick))
                continue;
            float reach = brick.WorldRadius + connector.length + 0.01f;
            if ((brick.WorldCenter - connector.position).sqrMagnitude > reach * reach)
                continue;

            foreach (Connector local in brick._connectors)
            {
                Connector other = local.InWorld(brick.transform);
                if (!Connections.AreMated(other, connector))
                    continue;
                if (connector.kind == Connector.Kind.Generic)
                    return true;
                (float from, float to) used = Connections.SpanAlong(connector, other);
                if (Mathf.Min(used.to, wanted.to) - Mathf.Max(used.from, wanted.from) > 0.0005f)
                    return true;
            }
        }
        return false;
    }

    // How far the held brick and everything it's carrying reach from this brick's center.
    float GroupRadius()
    {
        float radius = WorldRadius;
        foreach ((Brick member, Transform _) in _carried)
            radius = Mathf.Max(radius, (member.WorldCenter - WorldCenter).magnitude + member.WorldRadius);
        return radius;
    }

    // Stands the brick upright on the nearest real surface below or slightly above it, keeping its heading.
    bool FindSurfaceSnap(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;

        if (Vector3.Angle(transform.up, Vector3.up) > maxTiltDegrees)
            return false;

        Vector3 bottom = LowestBottomPoint();
        if (!RoomSurfaces.TryFindSurface(transform.TransformPoint(bottom), maxSurfaceSnap, out Vector3 surfacePoint))
            return false;

        Vector3 heading = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (heading.sqrMagnitude < 1e-6f)
            heading = Vector3.ProjectOnPlane(transform.up, Vector3.up);

        rotation = Quaternion.LookRotation(heading, Vector3.up);
        position = surfacePoint - rotation * bottom;
        return !WouldOverlapOthers(position, rotation);
    }

    // Bottom-center of the lowest brick in the group being held, in this brick's local space.
    Vector3 LowestBottomPoint()
    {
        Vector3 lowest = new Vector3(_body.center.x, _body.min.y, _body.center.z);
        foreach ((Brick member, Transform _) in _carried)
        {
            Transform t = member.transform;
            Bounds b = member._body;
            Vector3 bottom = t.localPosition + t.localRotation * new Vector3(b.center.x, b.min.y, b.center.z);
            if (bottom.y < lowest.y)
                lowest = bottom;
        }
        return lowest;
    }

    // True if this brick (and everything it's carrying) placed at the given pose would cut into another brick.
    bool WouldOverlapOthers(Vector3 position, Quaternion rotation)
    {
        if (BodyOverlapsOthers(_body, position, rotation))
            return true;

        foreach ((Brick member, Transform _) in _carried)
        {
            Transform t = member.transform;
            if (BodyOverlapsOthers(member._body, position + rotation * t.localPosition, rotation * t.localRotation))
                return true;
        }
        return false;
    }

    bool BodyOverlapsOthers(Bounds body, Vector3 position, Quaternion rotation)
    {
        // Shrink slightly so bricks that merely touch (stacked or side by side) don't count.
        // (Kept above zero for very thin parts like baseplates.)
        Vector3 halfExtents = Vector3.Max(body.extents - Vector3.one * 0.001f, Vector3.one * 0.0002f);
        Vector3 center = position + rotation * body.center;

        int count = Physics.OverlapBoxNonAlloc(center, halfExtents, OverlapResults, rotation,
                                               Physics.AllLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Brick hit = OverlapResults[i].GetComponentInParent<Brick>();
            if (hit != null && hit != this && !hit.IsPreview && !IsCarrying(hit))
                return true;
        }
        return false;
    }

    // ---------- Preview ----------

    // While held, shows see-through copies of the brick (and the group it's carrying)
    // where they would snap if released now.
    void UpdateGhost()
    {
        bool held = _grabbable != null && _grabbable.SelectingPointsCount > 0;
        if (!held)
            return;

        if (_articulation != null || SetBuilder.HasTargetFor(this) || WristInventory.IsOverWrist(this) || TrashCan.Contains(transform.TransformPoint(_body.center)) || !FindSnap(out Vector3 position, out Quaternion rotation))
        {
            HideGhosts();
            return;
        }

        ShowGhost(position, rotation);
        foreach ((Brick member, Transform _) in _carried)
        {
            Transform t = member.transform;
            member.ShowGhost(position + rotation * t.localPosition, rotation * t.localRotation);
        }
    }

    void ShowGhost(Vector3 position, Quaternion rotation)
    {
        if (ghostMaterial == null)
            return;
        if (_ghost == null)
            CreateGhost();
        _ghost.transform.SetPositionAndRotation(position, rotation);
        _ghost.SetActive(true);
    }

    void HideGhosts()
    {
        if (_ghost != null)
            _ghost.SetActive(false);
        foreach ((Brick member, Transform _) in _carried)
        {
            if (member != null && member._ghost != null)
                member._ghost.SetActive(false);
        }
    }

    void CreateGhost()
    {
        _ghost = new GameObject(name + " Ghost");
        _ghost.AddComponent<MeshFilter>().sharedMesh = _activeMesh;

        MeshRenderer ghostRenderer = _ghost.AddComponent<MeshRenderer>();
        ghostRenderer.sharedMaterials = Enumerable.Repeat(ghostMaterial, Mathf.Max(1, _activeMesh.subMeshCount)).ToArray();
        ghostRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        // Brick's color, with the ghost material's transparency.
        Color tint = color;
        tint.a = ghostMaterial.color.a;
        var block = new MaterialPropertyBlock();
        block.SetColor(BaseColorId, tint);
        ghostRenderer.SetPropertyBlock(block);
    }

    // ---------- Shape ----------

    /// <summary>Applies the part (or simple size) and color: mesh, connection points, and collider.</summary>
    public void Rebuild()
    {
        _dirty = false;
        _connectors.Clear();

        if (part != null && part.mesh != null)
        {
            _activeMesh = part.mesh;
            _body = part.bodyBounds;
            if (part.connectors != null && part.connectors.Length > 0)
            {
                _connectors.AddRange(part.connectors);
            }
            else
            {
                // Imported before connection data (or not covered by it): plain studs and sockets.
                foreach (Vector3 stud in part.studs ?? new Vector3[0])
                    _connectors.Add(Connector.Stud(stud, male: true));
                foreach (Vector3 socket in part.antiStuds ?? new Vector3[0])
                    _connectors.Add(Connector.Stud(socket, male: false));
            }
        }
        else
        {
            if (_generatedMesh == null)
                _generatedMesh = new Mesh { name = "Brick", hideFlags = HideFlags.DontSave };
            BuildSimpleBrick(_generatedMesh);
            _activeMesh = _generatedMesh;
        }

        GetComponent<MeshFilter>().sharedMesh = _activeMesh;
        if (_ghost != null)
        {
            // Rebuilt on next use with the new shape and colors.
            Destroy(_ghost);
            _ghost = null;
        }

        BoxCollider box = GetComponent<BoxCollider>();
        if (box != null)
        {
            box.center = _body.center;
            box.size = _body.size;
        }

        MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
        if (_opaqueMaterial == null)
            _opaqueMaterial = meshRenderer.sharedMaterial;
        BrickPartData.ApplyColors(meshRenderer, _activeMesh, part != null && part.mesh != null ? part.submeshColors : null,
                                  color, _opaqueMaterial, transparentMaterial);
    }

    // A plain rectangular brick with its pivot at the bottom center.
    void BuildSimpleBrick(Mesh mesh)
    {
        float height = heightInPlates * PlateHeight;
        _body = new Bounds(new Vector3(0f, height * 0.5f, 0f), new Vector3(studsX * StudPitch, height, studsZ * StudPitch));

        var builder = new MeshBuilder();
        float halfW = studsX * StudPitch * 0.5f - SideGap;
        float halfD = studsZ * StudPitch * 0.5f - SideGap;
        builder.AddBox(new Vector3(-halfW, 0f, -halfD), new Vector3(halfW, height, halfD));

        for (int i = 0; i < studsX; i++)
        {
            for (int j = 0; j < studsZ; j++)
            {
                float x = (i - (studsX - 1) * 0.5f) * StudPitch;
                float z = (j - (studsZ - 1) * 0.5f) * StudPitch;
                builder.AddCylinder(new Vector3(x, height, z), StudRadius, StudHeight, StudSegments);
                _connectors.Add(Connector.Stud(new Vector3(x, height, z), male: true));
                _connectors.Add(Connector.Stud(new Vector3(x, 0f, z), male: false));
            }
        }

        builder.ApplyTo(mesh);
    }
}
