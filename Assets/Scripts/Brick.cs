using System.Collections.Generic;
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
    const float ConnectedTolerance = 0.0005f;

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
    [Tooltip("How close (in meters) the brick must be to another brick's top or bottom to snap.")]
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
    public static event System.Action<Brick> PreviewTaken;

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

    // Bricks that can be built on: real, active, and not sitting in a menu or an inventory.
    bool IsBuildable => isActiveAndEnabled && !IsPreview && !IsStored;

    // Shape and connection points, in local space. Filled by Rebuild().
    Mesh _activeMesh;
    Mesh _generatedMesh;
    readonly List<Vector3> _studs = new List<Vector3>();
    readonly List<Vector3> _antiStuds = new List<Vector3>();
    Bounds _body;

    bool _dirty = true;
    Material _opaqueMaterial;
    PointableElement _grabbable;
    GameObject _ghost;
    readonly List<(Brick brick, Transform originalParent)> _carried = new List<(Brick, Transform)>();

    // While carrying a group: the group's unused connection points, in this brick's local space.
    readonly List<Vector3> _groupStuds = new List<Vector3>();
    readonly List<Vector3> _groupAntiStuds = new List<Vector3>();
    List<Vector3> SnapStuds => _carried.Count > 0 ? _groupStuds : _studs;
    List<Vector3> SnapAntiStuds => _carried.Count > 0 ? _groupAntiStuds : _antiStuds;

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

    void OnDestroy()
    {
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
            if (IsPreview)
            {
                // Taken out of a menu: it's a real brick from now on.
                IsPreview = false;
                transform.SetParent(null, worldPositionStays: true);
                PreviewTaken?.Invoke(this);
            }

            if (!Held.Contains(this))
                Held.Add(this);
            HeldHand = WristInventory.ClosestHand(evt.Pose.position);

            // Taken off a wrist: grows back to full size, and always comes out as the whole stored group.
            bool fromInventory = IsStored && WristInventory.TakeOut(this);

            PutDownStack();
            if (fromInventory || BrickGrabInput.IsWholeGroupGrab(evt))
                PickUpGroup();
        }
        else if ((evt.Type == PointerEventType.Unselect || evt.Type == PointerEventType.Cancel)
                 && _grabbable.SelectingPointsCount == 0)
        {
            // Only snap once every hand has let go.
            Held.Remove(this);
            HideGhosts();

            // Let go next to the other wrist: goes into that wrist's inventory.
            if (WristInventory.TryStore(this))
                return;

            // Dropped into a trash can: delete it, along with everything it's carrying (they're its children).
            if (TrashCan.Contains(transform.TransformPoint(_body.center)))
            {
                _carried.Clear();
                Destroy(gameObject);
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
            foreach (Brick other in AllBricks)
            {
                if (other.IsBuildable && !other.IsBaseplate && !group.Contains(other)
                    && (other.RestsOn(group[i]) || group[i].RestsOn(other)))
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

    // Collects every member's connection points into this brick's space, leaving out the ones
    // already plugged into another member, so only the group's exposed studs and sockets can snap.
    void BuildGroupConnections(List<Brick> group)
    {
        var studs = new List<Vector3>();
        var antiStuds = new List<Vector3>();
        foreach (Brick member in group)
        {
            foreach (Vector3 stud in member._studs)
                studs.Add(transform.InverseTransformPoint(member.transform.TransformPoint(stud)));
            foreach (Vector3 antiStud in member._antiStuds)
                antiStuds.Add(transform.InverseTransformPoint(member.transform.TransformPoint(antiStud)));
        }

        _groupStuds.Clear();
        _groupAntiStuds.Clear();
        foreach (Vector3 stud in studs)
        {
            if (!ContainsPoint(antiStuds, stud))
                _groupStuds.Add(stud);
        }
        foreach (Vector3 antiStud in antiStuds)
        {
            if (!ContainsPoint(studs, antiStud))
                _groupAntiStuds.Add(antiStud);
        }
    }

    static bool ContainsPoint(List<Vector3> points, Vector3 point)
    {
        foreach (Vector3 p in points)
        {
            if ((p - point).sqrMagnitude < ConnectedTolerance * ConnectedTolerance)
                return true;
        }
        return false;
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
        _groupStuds.Clear();
        _groupAntiStuds.Clear();
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
        _groupStuds.Clear();
        _groupAntiStuds.Clear();
    }

    bool IsCarrying(Brick brick)
    {
        foreach ((Brick carried, Transform _) in _carried)
        {
            if (carried == brick)
                return true;
        }
        return false;
    }

    /// <summary>True when one of the lower brick's studs is plugged into this brick's underside.</summary>
    public bool RestsOn(Brick lower)
    {
        Quaternion relative = Quaternion.Inverse(lower.transform.rotation) * transform.rotation;
        if (Vector3.Angle(relative * Vector3.up, Vector3.up) > 2f)
            return false;

        foreach (Vector3 antiStud in _antiStuds)
        {
            Vector3 socket = transform.TransformPoint(antiStud);
            foreach (Vector3 stud in lower._studs)
            {
                if ((lower.transform.TransformPoint(stud) - socket).sqrMagnitude < ConnectedTolerance * ConnectedTolerance)
                    return true;
            }
        }
        return false;
    }

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

    /// <summary>Finds the closest pose that plugs this brick onto, or under, another brick.</summary>
    public bool FindSnap(out Vector3 bestPosition, out Quaternion bestRotation)
    {
        bestPosition = default;
        bestRotation = default;
        float bestScore = float.MaxValue;

        foreach (Brick other in AllBricks)
        {
            if (other == this || !other.IsBuildable || IsCarrying(other))
                continue;

            // Work in the other brick's local space. Bricks only connect upright to each other,
            // turned in quarter turns.
            Transform ot = other.transform;
            Quaternion relative = Quaternion.Inverse(ot.rotation) * transform.rotation;
            if (Vector3.Angle(relative * Vector3.up, Vector3.up) > maxTiltDegrees)
                continue;

            Vector3 forward = relative * Vector3.forward;
            float quarterTurns = Mathf.Round(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg / 90f);
            Quaternion turn = Quaternion.Euler(0f, quarterTurns * 90f, 0f);
            Vector3 current = ot.InverseTransformPoint(transform.position);

            // On top: our underside onto their studs. Underneath: our studs into their underside.
            float score = float.MaxValue;
            Vector3 target = default;
            FindClosestPairing(SnapAntiStuds, other._studs, turn, current, ref score, ref target);
            FindClosestPairing(SnapStuds, other._antiStuds, turn, current, ref score, ref target);
            if (score >= bestScore)
                continue;

            Vector3 worldPosition = ot.TransformPoint(target);
            Quaternion worldRotation = ot.rotation * turn;
            if (WouldOverlapOthers(worldPosition, worldRotation))
                continue;

            bestScore = score;
            bestPosition = worldPosition;
            bestRotation = worldRotation;
        }

        if (bestScore < float.MaxValue)
            return true;

        // No brick to connect to: try setting it down on a real table or the floor instead.
        return FindSurfaceSnap(out bestPosition, out bestRotation);
    }

    // Tries every way of lining up one of our connection points with one of theirs, and keeps the
    // position that needs the smallest move from where the brick is now.
    void FindClosestPairing(List<Vector3> ours, List<Vector3> theirs, Quaternion turn, Vector3 current,
                            ref float bestScore, ref Vector3 bestTarget)
    {
        foreach (Vector3 mine in ours)
        {
            Vector3 turned = turn * mine;
            foreach (Vector3 point in theirs)
            {
                Vector3 target = point - turned;
                Vector3 move = target - current;
                if (Mathf.Abs(move.y) > maxVerticalSnap)
                    continue;
                if (move.x * move.x + move.z * move.z > StudPitch * StudPitch)
                    continue;

                float score = move.sqrMagnitude;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestTarget = target;
                }
            }
        }
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

        if (WristInventory.IsOverWrist(this) || TrashCan.Contains(transform.TransformPoint(_body.center)) || !FindSnap(out Vector3 position, out Quaternion rotation))
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
        ghostRenderer.sharedMaterial = ghostMaterial;
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
        _studs.Clear();
        _antiStuds.Clear();

        if (part != null && part.mesh != null)
        {
            _activeMesh = part.mesh;
            _studs.AddRange(part.studs);
            _antiStuds.AddRange(part.antiStuds);
            _body = part.bodyBounds;
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
            _ghost.GetComponent<MeshFilter>().sharedMesh = _activeMesh;

        BoxCollider box = GetComponent<BoxCollider>();
        if (box != null)
        {
            box.center = _body.center;
            box.size = _body.size;
        }

        MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
        if (_opaqueMaterial == null)
            _opaqueMaterial = meshRenderer.sharedMaterial;
        bool seeThrough = color.a < 1f && transparentMaterial != null;
        meshRenderer.sharedMaterial = seeThrough ? transparentMaterial : _opaqueMaterial;

        var block = new MaterialPropertyBlock();
        meshRenderer.GetPropertyBlock(block);
        block.SetColor(BaseColorId, color);
        meshRenderer.SetPropertyBlock(block);
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
                _studs.Add(new Vector3(x, height, z));
                _antiStuds.Add(new Vector3(x, 0f, z));
            }
        }

        builder.ApplyTo(mesh);
    }
}
