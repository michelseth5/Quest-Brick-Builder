using System.Collections.Generic;
using UnityEngine;

/// <summary>Small helper for building flat-sided procedural meshes (boxes and cylinders).</summary>
public class MeshBuilder
{
    readonly List<Vector3> _vertices = new List<Vector3>();
    readonly List<Vector3> _normals = new List<Vector3>();
    readonly List<int> _triangles = new List<int>();

    /// <summary>A box mesh of the given size, centered on the origin.</summary>
    public static Mesh Box(Vector3 size)
    {
        var builder = new MeshBuilder();
        builder.AddBox(-size * 0.5f, size * 0.5f);
        return builder.ToMesh("Box");
    }

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

    public Mesh ToMesh(string name)
    {
        var mesh = new Mesh { name = name };
        ApplyTo(mesh);
        return mesh;
    }
}
