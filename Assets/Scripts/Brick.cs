using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// A real-scale brick. Builds its own mesh (body + studs) from its size, and when the
/// player lets go of it, snaps it onto the stud grid of the nearest brick above or below.
/// The pivot is at the bottom-center of the brick and the transform scale must stay at 1.
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

    public float Height => heightInPlates * PlateHeight;

    Mesh _mesh;
    bool _dirty = true;
    PointableElement _grabbable;

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
        if (_grabbable != null)
            _grabbable.WhenPointerEventRaised -= OnPointerEvent;
    }

    void OnValidate() => _dirty = true;

    void Update()
    {
        if (_dirty)
            Rebuild();
    }

    void OnDestroy()
    {
        if (_mesh == null)
            return;
        if (Application.isPlaying)
            Destroy(_mesh);
        else
            DestroyImmediate(_mesh);
    }

    void OnPointerEvent(PointerEvent evt)
    {
        // Only snap once every hand has let go.
        if (evt.Type == PointerEventType.Unselect && _grabbable.SelectingPointsCount == 0)
            TrySnap();
    }

    public bool TrySnap()
    {
        if (!FindSnap(out Vector3 position, out Quaternion rotation))
            return false;
        transform.SetPositionAndRotation(position, rotation);
        return true;
    }

    /// <summary>Finds the closest pose that sits this brick on top of, or underneath, another brick.</summary>
    public bool FindSnap(out Vector3 bestPosition, out Quaternion bestRotation)
    {
        bestPosition = default;
        bestRotation = default;
        float bestScore = float.MaxValue;

        foreach (Brick other in AllBricks)
        {
            if (other == this || !other.isActiveAndEnabled)
                continue;

            Transform ot = other.transform;

            // Work in the other brick's local space, where its studs lie on a simple grid.
            Quaternion relative = Quaternion.Inverse(ot.rotation) * transform.rotation;
            if (Vector3.Angle(relative * Vector3.up, Vector3.up) > maxTiltDegrees)
                continue;

            Vector3 forward = relative * Vector3.forward;
            int quarterTurns = Mathf.RoundToInt(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg / 90f);
            quarterTurns = ((quarterTurns % 4) + 4) % 4;
            bool turnedSideways = quarterTurns % 2 == 1;
            int footprintX = turnedSideways ? studsZ : studsX;
            int footprintZ = turnedSideways ? studsX : studsZ;

            Vector3 local = ot.InverseTransformPoint(transform.position);

            // Candidate heights: resting on top of the other brick, or hanging underneath it.
            foreach (float targetY in new[] { other.Height, -Height })
            {
                if (Mathf.Abs(local.y - targetY) > maxVerticalSnap)
                    continue;

                float x = SnapAxis(local.x, other.studsX, footprintX);
                float z = SnapAxis(local.z, other.studsZ, footprintZ);
                if (!Overlaps(x, other.studsX, footprintX) || !Overlaps(z, other.studsZ, footprintZ))
                    continue;

                Vector3 target = new Vector3(x, targetY, z);
                float score = (target - local).sqrMagnitude;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestPosition = ot.TransformPoint(target);
                    bestRotation = ot.rotation * Quaternion.Euler(0f, quarterTurns * 90f, 0f);
                }
            }
        }

        return bestScore < float.MaxValue;
    }

    // Moves a center coordinate so this brick's studs line up with the other brick's studs on one axis.
    static float SnapAxis(float center, int otherStuds, int myStuds)
    {
        float otherFirstStud = -(otherStuds - 1) * 0.5f * StudPitch;
        float myFirstStud = center - (myStuds - 1) * 0.5f * StudPitch;
        float snappedFirstStud = otherFirstStud + Mathf.Round((myFirstStud - otherFirstStud) / StudPitch) * StudPitch;
        return snappedFirstStud + (myStuds - 1) * 0.5f * StudPitch;
    }

    // True when the two footprints share at least one row of studs on this axis.
    static bool Overlaps(float center, int otherStuds, int myStuds)
    {
        return Mathf.Abs(center) < (otherStuds + myStuds) * 0.5f * StudPitch - StudPitch * 0.5f;
    }

    /// <summary>Regenerates the mesh, collider, and color from the current size settings.</summary>
    public void Rebuild()
    {
        _dirty = false;

        if (_mesh == null)
            _mesh = new Mesh { name = "Brick", hideFlags = HideFlags.DontSave };
        BuildMesh(_mesh);
        GetComponent<MeshFilter>().sharedMesh = _mesh;

        BoxCollider box = GetComponent<BoxCollider>();
        if (box != null)
        {
            box.center = new Vector3(0f, Height * 0.5f, 0f);
            box.size = new Vector3(studsX * StudPitch, Height, studsZ * StudPitch);
        }

        MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
        var block = new MaterialPropertyBlock();
        meshRenderer.GetPropertyBlock(block);
        block.SetColor(BaseColorId, color);
        meshRenderer.SetPropertyBlock(block);
    }

    void BuildMesh(Mesh mesh)
    {
        var builder = new MeshBuilder();

        float halfW = studsX * StudPitch * 0.5f - SideGap;
        float halfD = studsZ * StudPitch * 0.5f - SideGap;
        builder.AddBox(new Vector3(-halfW, 0f, -halfD), new Vector3(halfW, Height, halfD));

        for (int i = 0; i < studsX; i++)
        {
            for (int j = 0; j < studsZ; j++)
            {
                float x = (i - (studsX - 1) * 0.5f) * StudPitch;
                float z = (j - (studsZ - 1) * 0.5f) * StudPitch;
                builder.AddCylinder(new Vector3(x, Height, z), StudRadius, StudHeight, StudSegments);
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
