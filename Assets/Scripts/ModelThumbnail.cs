using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The 3D thumbnail of a saved model (the load screen's spinning previews): one mesh with a submesh per color,
/// made from the model's biggest pieces up to a triangle budget, with studs and underside tubes left out (they
/// can't be seen at that size, and are a fifth to half of all triangles). Saved beside the model as
/// &lt;model&gt;.thumb when the model is saved, so the load screen only reads a small file.
/// </summary>
public static class ModelThumbnail
{
    const int Version = 2; // 2: outside pieces first (older thumbnails are remade)
    const int TriangleBudget = 120000;

    public class Data
    {
        public readonly List<Color> colors = new List<Color>();
        public readonly List<List<Vector3>> vertices = new List<List<Vector3>>();
        public readonly List<List<Vector3>> normals = new List<List<Vector3>>();
        public readonly List<List<int>> indices = new List<List<int>>();
    }

    public static string PathFor(string modelPath) => Path.ChangeExtension(modelPath, ".thumb");

    /// <summary>The model's saved thumbnail, if there is one at least as new as the model file.</summary>
    public static Data Load(string modelPath)
    {
        string path = PathFor(modelPath);
        if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < File.GetLastWriteTimeUtc(modelPath))
            return null;
        try
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (r.ReadInt32() != Version)
                    return null;
                var data = new Data();
                int submeshes = r.ReadInt32();
                for (int s = 0; s < submeshes; s++)
                {
                    data.colors.Add(new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                    int count = r.ReadInt32();
                    var vertices = new List<Vector3>(count);
                    var normals = new List<Vector3>(count);
                    for (int i = 0; i < count; i++)
                        vertices.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                    for (int i = 0; i < count; i++)
                        normals.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                    int indexCount = r.ReadInt32();
                    var indices = new List<int>(indexCount);
                    for (int i = 0; i < indexCount; i++)
                        indices.Add(r.ReadInt32());
                    data.vertices.Add(vertices);
                    data.normals.Add(normals);
                    data.indices.Add(indices);
                }
                return data;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"Couldn't read the thumbnail {path}: {e.Message}");
            return null;
        }
    }

    public static void Save(string modelPath, Data data)
    {
        try
        {
            using (var w = new BinaryWriter(File.Create(PathFor(modelPath))))
            {
                w.Write(Version);
                w.Write(data.colors.Count);
                for (int s = 0; s < data.colors.Count; s++)
                {
                    Color c = data.colors[s];
                    w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a);
                    w.Write(data.vertices[s].Count);
                    foreach (Vector3 v in data.vertices[s]) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
                    foreach (Vector3 n in data.normals[s]) { w.Write(n.x); w.Write(n.y); w.Write(n.z); }
                    w.Write(data.indices[s].Count);
                    foreach (int i in data.indices[s])
                        w.Write(i);
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"Couldn't save the thumbnail for {modelPath}: {e.Message}");
        }
    }

    /// <summary>Makes the thumbnail of a saved model file (and saves it beside the file).</summary>
    public static Data Make(string modelPath, System.Func<string, BrickPartData> findPart, System.Func<int, Color> colorOf)
    {
        var pieces = new List<(BrickPartData part, Color color, Matrix4x4 matrix, float size)>();
        var boxes = new List<Bounds>();
        foreach (ModelFile.Placement placement in ModelFile.Load(modelPath, out _))
        {
            BrickPartData part = findPart(placement.partId);
            if (part == null || part.mesh == null || !part.mesh.isReadable)
                continue;
            Vector3 e = part.mesh.bounds.size;
            var matrix = Matrix4x4.TRS(placement.position, placement.rotation, Vector3.one);
            pieces.Add((part, colorOf(placement.colorCode), matrix, e.x * e.y + e.y * e.z + e.x * e.z));
            boxes.Add(WorldBox(part.mesh.bounds, matrix));
        }

        // Pieces you can see from outside first (the triangles go where they show; a big model's insides, like
        // the Technic frames in a castle's walls, would otherwise use up the budget), the biggest first in each.
        bool[] outside = OnTheOutside(boxes);
        var order = new List<int>();
        for (int i = 0; i < pieces.Count; i++)
            order.Add(i);
        order.Sort((a, b) => outside[a] != outside[b] ? (outside[a] ? -1 : 1) : pieces[b].size.CompareTo(pieces[a].size));
        pieces = order.ConvertAll(i => pieces[i]);

        var data = new Data();
        var submeshOf = new Dictionary<Color, int>();
        var cache = new Dictionary<BrickPartData, (List<Vector3> v, List<Vector3> n, List<int>[] tris)>();
        int budget = TriangleBudget;
        foreach ((BrickPartData part, Color mainColor, Matrix4x4 matrix, float _) in pieces)
        {
            if (!cache.TryGetValue(part, out var mesh))
                cache[part] = mesh = Geometry(part);
            int triangles = 0;
            foreach (List<int> t in mesh.tris)
                triangles += t.Count / 3;
            if (triangles > budget)
                continue; // a smaller piece may still fit
            budget -= triangles;

            for (int s = 0; s < mesh.tris.Length; s++)
            {
                if (mesh.tris[s].Count == 0)
                    continue;
                int code = part.submeshColors != null && s < part.submeshColors.Length ? part.submeshColors[s] : BrickPartData.MainColorCode;
                Color color = code == BrickPartData.MainColorCode ? mainColor : LDrawColors.Get(code);
                if (!submeshOf.TryGetValue(color, out int target))
                {
                    submeshOf[color] = target = data.colors.Count;
                    data.colors.Add(color);
                    data.vertices.Add(new List<Vector3>());
                    data.normals.Add(new List<Vector3>());
                    data.indices.Add(new List<int>());
                }
                // This submesh's triangles, with its own copy of the vertices they use.
                var remap = new Dictionary<int, int>();
                List<Vector3> vertices = data.vertices[target], normals = data.normals[target];
                List<int> indices = data.indices[target];
                foreach (int index in mesh.tris[s])
                {
                    if (!remap.TryGetValue(index, out int mapped))
                    {
                        remap[index] = mapped = vertices.Count;
                        vertices.Add(matrix.MultiplyPoint3x4(mesh.v[index]));
                        normals.Add(matrix.MultiplyVector(index < mesh.n.Count ? mesh.n[index] : Vector3.up));
                    }
                    indices.Add(mapped);
                }
            }
            if (budget <= 0)
                break;
        }

        Save(modelPath, data);
        return data;
    }

    static Bounds WorldBox(Bounds local, Matrix4x4 matrix)
    {
        var box = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.zero);
        for (int corner = 0; corner < 8; corner++)
        {
            box.Encapsulate(matrix.MultiplyPoint3x4(new Vector3(
                (corner & 1) == 0 ? local.min.x : local.max.x,
                (corner & 2) == 0 ? local.min.y : local.max.y,
                (corner & 4) == 0 ? local.min.z : local.max.z)));
        }
        return box;
    }

    // Which pieces touch the open air around the model: a coarse grid of the model's space, each piece's box
    // (a little shrunk, as boxes overstate shapes) filling cells, then the air flooded in from the edges.
    // A piece is on the outside if a cell next to its box is reached by the air.
    static bool[] OnTheOutside(List<Bounds> boxes)
    {
        var result = new bool[boxes.Count];
        if (boxes.Count == 0)
            return result;
        Bounds all = boxes[0];
        foreach (Bounds b in boxes)
            all.Encapsulate(b);
        const int Resolution = 48;
        float cell = Mathf.Max(all.size.x, all.size.y, all.size.z) / Resolution;
        if (cell <= 0f)
        {
            for (int i = 0; i < result.Length; i++)
                result[i] = true;
            return result;
        }
        Vector3 origin = all.min - Vector3.one * cell; // a one-cell border of air all round
        int nx = Mathf.CeilToInt(all.size.x / cell) + 3, ny = Mathf.CeilToInt(all.size.y / cell) + 3,
            nz = Mathf.CeilToInt(all.size.z / cell) + 3;
        var solid = new bool[nx, ny, nz];
        Vector3Int Cell(Vector3 p) => new Vector3Int(
            Mathf.Clamp(Mathf.FloorToInt((p.x - origin.x) / cell), 0, nx - 1),
            Mathf.Clamp(Mathf.FloorToInt((p.y - origin.y) / cell), 0, ny - 1),
            Mathf.Clamp(Mathf.FloorToInt((p.z - origin.z) / cell), 0, nz - 1));

        foreach (Bounds b in boxes)
        {
            var shrunk = new Bounds(b.center, b.size * 0.7f);
            Vector3Int lo = Cell(shrunk.min), hi = Cell(shrunk.max);
            for (int x = lo.x; x <= hi.x; x++)
                for (int y = lo.y; y <= hi.y; y++)
                    for (int z = lo.z; z <= hi.z; z++)
                        solid[x, y, z] = true;
        }

        // Flood the air in from the border.
        var air = new bool[nx, ny, nz];
        var queue = new Queue<Vector3Int>();
        void Visit(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz || air[x, y, z] || solid[x, y, z])
                return;
            air[x, y, z] = true;
            queue.Enqueue(new Vector3Int(x, y, z));
        }
        Visit(0, 0, 0);
        while (queue.Count > 0)
        {
            Vector3Int c = queue.Dequeue();
            Visit(c.x + 1, c.y, c.z); Visit(c.x - 1, c.y, c.z);
            Visit(c.x, c.y + 1, c.z); Visit(c.x, c.y - 1, c.z);
            Visit(c.x, c.y, c.z + 1); Visit(c.x, c.y, c.z - 1);
        }

        for (int i = 0; i < boxes.Count; i++)
        {
            Vector3Int lo = Cell(boxes[i].min) - Vector3Int.one, hi = Cell(boxes[i].max) + Vector3Int.one;
            for (int x = Mathf.Max(0, lo.x); x <= Mathf.Min(nx - 1, hi.x) && !result[i]; x++)
                for (int y = Mathf.Max(0, lo.y); y <= Mathf.Min(ny - 1, hi.y) && !result[i]; y++)
                    for (int z = Mathf.Max(0, lo.z); z <= Mathf.Min(nz - 1, hi.z); z++)
                    {
                        if (air[x, y, z])
                        {
                            result[i] = true;
                            break;
                        }
                    }
        }
        return result;
    }

    // A part's vertices, normals and each submesh's triangles without its studs and tubes (BrickPartData.details).
    static (List<Vector3>, List<Vector3>, List<int>[]) Geometry(BrickPartData part)
    {
        Mesh mesh = part.mesh;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        mesh.GetVertices(vertices);
        mesh.GetNormals(normals);
        var tris = new List<int>[mesh.subMeshCount];
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            var all = new List<int>();
            mesh.GetTriangles(all, s);
            var skip = new bool[all.Count];
            foreach (BrickPartData.Detail detail in part.details ?? new BrickPartData.Detail[0])
            {
                if (detail.submesh != s)
                    continue;
                for (int i = detail.start; i < detail.start + detail.count && i < skip.Length; i++)
                    skip[i] = true;
            }
            var kept = new List<int>(all.Count);
            for (int i = 0; i + 2 < all.Count; i += 3)
            {
                if (!skip[i])
                {
                    kept.Add(all[i]);
                    kept.Add(all[i + 1]);
                    kept.Add(all[i + 2]);
                }
            }
            tris[s] = kept;
        }
        return (vertices, normals, tris);
    }

    /// <summary>A mesh and materials to show a thumbnail.</summary>
    public static (Mesh mesh, Material[] materials) ToMesh(Data data, Material opaque, Material transparent)
    {
        if (data == null || data.colors.Count == 0)
            return (null, null);
        var mesh = new Mesh { name = "Thumbnail", indexFormat = IndexFormat.UInt32 };
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var offsets = new int[data.colors.Count];
        for (int s = 0; s < data.colors.Count; s++)
        {
            offsets[s] = vertices.Count;
            vertices.AddRange(data.vertices[s]);
            normals.AddRange(data.normals[s]);
        }
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.subMeshCount = data.colors.Count;
        var materials = new Material[data.colors.Count];
        for (int s = 0; s < data.colors.Count; s++)
        {
            List<int> indices = data.indices[s];
            var shifted = new int[indices.Count];
            for (int i = 0; i < shifted.Length; i++)
                shifted[i] = indices[i] + offsets[s];
            mesh.SetTriangles(shifted, s, false);
            Color color = data.colors[s];
            materials[s] = BrickPartData.SharedMaterial(color.a < 1f && transparent != null ? transparent : opaque, color);
        }
        mesh.RecalculateBounds();
        mesh.UploadMeshData(true);
        return (mesh, materials);
    }
}
