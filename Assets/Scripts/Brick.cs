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

    // Shape and connection points, in local space. Filled by Rebuild().
    Mesh _activeMesh;
    Mesh _generatedMesh;
    readonly List<Vector3> _studs = new List<Vector3>();
    readonly List<Vector3> _antiStuds = new List<Vector3>();
    Bounds _body;

    bool _dirty = true;
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
            PutDownStack();
            if (BrickGrabInput.IsWholeGroupGrab(evt))
                PickUpGroup();
        }
        else if ((evt.Type == PointerEventType.Unselect || evt.Type == PointerEventType.Cancel)
                 && _grabbable.SelectingPointsCount == 0)
        {
            // Only snap once every hand has let go.
            HideGhosts();
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
                if (other.isActiveAndEnabled && !group.Contains(other)
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
            if (other == this || !other.isActiveAndEnabled || IsCarrying(other))
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
        Vector3 halfExtents = body.extents - Vector3.one * 0.001f;
        Vector3 center = position + rotation * body.center;

        int count = Physics.OverlapBoxNonAlloc(center, halfExtents, OverlapResults, rotation,
                                               Physics.AllLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Brick hit = OverlapResults[i].GetComponentInParent<Brick>();
            if (hit != null && hit != this && !IsCarrying(hit))
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

        if (!FindSnap(out Vector3 position, out Quaternion rotation))
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

    /// <summary>Minimal helper for flat-sided procedural meshes.</summary>
    class MeshBuilder
    {
        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Vector3> _normals = new List<Vector3>();
        readonly List<int> _triangles = new List<int>();

        // Adds a triangle facing the given outward direction, fixing the winding if needed.
        void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f)
            {
                (b, c) = (c, b);
                (nb, nc) = (nc, nb);
            }
            int start = _vertices.Count;
            _vertices.Add(a); _vertices.Add(b); _vertices.Add(c);
            _normals.Add(na); _normals.Add(nb); _normals.Add(nc);
            _triangles.Add(start); _triangles.Add(start + 1); _triangles.Add(start + 2);
        }

        void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            AddTriangle(a, b, c, normal, normal, normal, normal);
            AddTriangle(a, c, d, normal, normal, normal, normal);
        }

        public void AddBox(Vector3 min, Vector3 max)
        {
            Vector3 p000 = new Vector3(min.x, min.y, min.z), p100 = new Vector3(max.x, min.y, min.z);
            Vector3 p010 = new Vector3(min.x, max.y, min.z), p110 = new Vector3(max.x, max.y, min.z);
            Vector3 p001 = new Vector3(min.x, min.y, max.z), p101 = new Vector3(max.x, min.y, max.z);
            Vector3 p011 = new Vector3(min.x, max.y, max.z), p111 = new Vector3(max.x, max.y, max.z);

            AddQuad(p010, p110, p111, p011, Vector3.up);
            AddQuad(p000, p100, p101, p001, Vector3.down);
            AddQuad(p000, p100, p110, p010, Vector3.back);
            AddQuad(p001, p101, p111, p011, Vector3.forward);
            AddQuad(p000, p001, p011, p010, Vector3.left);
            AddQuad(p100, p101, p111, p110, Vector3.right);
        }

        public void AddCylinder(Vector3 baseCenter, float radius, float height, int segments)
        {
            Vector3 up = Vector3.up * height;
            Vector3 topCenter = baseCenter + up;

            for (int s = 0; s < segments; s++)
            {
                float a0 = s * Mathf.PI * 2f / segments;
                float a1 = (s + 1) * Mathf.PI * 2f / segments;
                Vector3 n0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
                Vector3 n1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                Vector3 b0 = baseCenter + n0 * radius;
                Vector3 b1 = baseCenter + n1 * radius;
                Vector3 outward = (n0 + n1).normalized;

                // Smooth-shaded side.
                AddTriangle(b0, b1, b1 + up, n0, n1, n1, outward);
                AddTriangle(b0, b1 + up, b0 + up, n0, n1, n0, outward);

                // Flat top cap.
                AddTriangle(topCenter, b0 + up, b1 + up, Vector3.up, Vector3.up, Vector3.up, Vector3.up);
            }
        }

        public void ApplyTo(Mesh mesh)
        {
            mesh.Clear();
            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
            mesh.SetTriangles(_triangles, 0);
            mesh.RecalculateBounds();
        }
    }
}
