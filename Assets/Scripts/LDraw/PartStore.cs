using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Parts made on the headset (for sets downloaded in the game), kept in app storage so each is only built once:
/// Parts/&lt;id&gt;.part, a small binary file with the part's mesh and everything else BrickPartData holds.
/// Parts that come with the app are found first (Sandbox.FindPart); these fill in the rest.
/// </summary>
public static class PartStore
{
    // Bump when the file layout or how parts are made changes, so old files are rebuilt instead of misread.
    const int Version = 1;

    public static string Folder => Path.Combine(Application.persistentDataPath, "Parts");

    static readonly Dictionary<string, BrickPartData> Loaded = new Dictionary<string, BrickPartData>(StringComparer.OrdinalIgnoreCase);

    static string PathFor(string partId)
    {
        var name = new System.Text.StringBuilder();
        foreach (char c in partId.ToLowerInvariant())
            name.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return Path.Combine(Folder, name + ".part");
    }

    public static bool Has(string partId) => Loaded.ContainsKey(partId) || File.Exists(PathFor(partId));

    /// <summary>A stored part, loaded on first use; null if it hasn't been made (or was made by an older version).</summary>
    public static BrickPartData Find(string partId)
    {
        if (string.IsNullOrEmpty(partId))
            return null;
        if (Loaded.TryGetValue(partId, out BrickPartData known))
            return known;
        string path = PathFor(partId);
        if (!File.Exists(path))
            return null;
        try
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                BrickPartData part = Read(reader);
                if (part != null)
                {
                    ConnectionOverrides.Apply(part); // points fixed in the game (Fix Snapping)
                    Loaded[partId] = part;
                }
                return part;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Couldn't read stored part {partId}: {e.Message}");
            return null;
        }
    }

    public static void Save(BrickPartData part)
    {
        Directory.CreateDirectory(Folder);
        string path = PathFor(part.partId), temp = path + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temp)))
            Write(writer, part);
        if (File.Exists(path))
            File.Delete(path);
        File.Move(temp, path);
        Loaded[part.partId] = part;
    }

    /// <summary>Forgets every stored part (and deletes the files).</summary>
    public static void Clear()
    {
        Loaded.Clear();
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, true);
    }

    // ---------- File layout ----------

    static void Write(BinaryWriter w, BrickPartData part)
    {
        w.Write(Version);
        w.Write(part.partId ?? "");
        w.Write(part.description ?? "");
        w.Write(part.category ?? "");

        Mesh mesh = part.mesh;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        mesh.GetVertices(vertices);
        mesh.GetNormals(normals);
        w.Write(vertices.Count);
        foreach (Vector3 v in vertices)
            WriteVector(w, v);
        w.Write(normals.Count);
        foreach (Vector3 n in normals)
            WriteVector(w, n);
        w.Write(mesh.subMeshCount);
        var indices = new List<int>();
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            mesh.GetTriangles(indices, s);
            w.Write(indices.Count);
            foreach (int i in indices)
                w.Write(i);
        }

        WriteInts(w, part.submeshColors);
        WriteVectors(w, part.studs);
        WriteVectors(w, part.antiStuds);
        Connector[] connectors = part.connectors ?? new Connector[0];
        w.Write(connectors.Length);
        foreach (Connector c in connectors)
        {
            w.Write((int)c.kind);
            w.Write(c.male);
            WriteVector(w, c.position);
            WriteVector(w, c.axis);
            WriteVector(w, c.reference);
            w.Write(c.length);
            w.Write(c.center);
            w.Write(c.slide);
            w.Write(c.openEnded);
            w.Write(c.profile ?? "");
            w.Write(c.group ?? "");
            w.Write(c.sections ?? "");
        }
        BrickPartData.Component[] components = part.components ?? new BrickPartData.Component[0];
        w.Write(components.Length);
        foreach (BrickPartData.Component c in components)
        {
            w.Write(c.partId ?? "");
            WriteVector(w, c.position);
            WriteQuaternion(w, c.rotation);
        }
        BrickPartData.Detail[] details = part.details ?? new BrickPartData.Detail[0];
        w.Write(details.Length);
        foreach (BrickPartData.Detail d in details)
        {
            WriteVector(w, d.position);
            WriteVector(w, d.up);
            w.Write(d.submesh);
            w.Write(d.start);
            w.Write(d.count);
        }
        WriteVector(w, part.bodyBounds.center);
        WriteVector(w, part.bodyBounds.size);
    }

    static BrickPartData Read(BinaryReader r)
    {
        if (r.ReadInt32() != Version)
            return null;
        BrickPartData part = ScriptableObject.CreateInstance<BrickPartData>();
        part.partId = r.ReadString();
        part.name = part.partId;
        part.description = r.ReadString();
        part.category = r.ReadString();

        var vertices = new Vector3[r.ReadInt32()];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = ReadVector(r);
        var normals = new Vector3[r.ReadInt32()];
        for (int i = 0; i < normals.Length; i++)
            normals[i] = ReadVector(r);
        var mesh = new Mesh { name = part.partId };
        mesh.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = vertices;
        if (normals.Length == vertices.Length)
            mesh.normals = normals;
        int submeshes = r.ReadInt32();
        mesh.subMeshCount = Mathf.Max(1, submeshes);
        for (int s = 0; s < submeshes; s++)
        {
            var triangles = new int[r.ReadInt32()];
            for (int i = 0; i < triangles.Length; i++)
                triangles[i] = r.ReadInt32();
            mesh.SetTriangles(triangles, s);
        }
        mesh.RecalculateBounds();
        part.mesh = mesh;

        part.submeshColors = ReadInts(r);
        part.studs = ReadVectors(r);
        part.antiStuds = ReadVectors(r);
        var connectors = new Connector[r.ReadInt32()];
        for (int i = 0; i < connectors.Length; i++)
        {
            connectors[i] = new Connector
            {
                kind = (Connector.Kind)r.ReadInt32(),
                male = r.ReadBoolean(),
                position = ReadVector(r),
                axis = ReadVector(r),
                reference = ReadVector(r),
                length = r.ReadSingle(),
                center = r.ReadBoolean(),
                slide = r.ReadBoolean(),
                openEnded = r.ReadBoolean(),
                profile = r.ReadString(),
                group = r.ReadString(),
                sections = r.ReadString(),
            };
        }
        part.connectors = connectors;
        var components = new BrickPartData.Component[r.ReadInt32()];
        for (int i = 0; i < components.Length; i++)
            components[i] = new BrickPartData.Component { partId = r.ReadString(), position = ReadVector(r), rotation = ReadQuaternion(r) };
        part.components = components;
        var details = new BrickPartData.Detail[r.ReadInt32()];
        for (int i = 0; i < details.Length; i++)
        {
            details[i] = new BrickPartData.Detail
            {
                position = ReadVector(r), up = ReadVector(r), submesh = r.ReadInt32(), start = r.ReadInt32(), count = r.ReadInt32(),
            };
        }
        part.details = details;
        part.bodyBounds = new Bounds(ReadVector(r), ReadVector(r));
        return part;
    }

    static void WriteVector(BinaryWriter w, Vector3 v)
    {
        w.Write(v.x);
        w.Write(v.y);
        w.Write(v.z);
    }

    static Vector3 ReadVector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    static void WriteQuaternion(BinaryWriter w, Quaternion q)
    {
        w.Write(q.x);
        w.Write(q.y);
        w.Write(q.z);
        w.Write(q.w);
    }

    static Quaternion ReadQuaternion(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    static void WriteVectors(BinaryWriter w, Vector3[] values)
    {
        values = values ?? new Vector3[0];
        w.Write(values.Length);
        foreach (Vector3 v in values)
            WriteVector(w, v);
    }

    static Vector3[] ReadVectors(BinaryReader r)
    {
        var values = new Vector3[r.ReadInt32()];
        for (int i = 0; i < values.Length; i++)
            values[i] = ReadVector(r);
        return values;
    }

    static void WriteInts(BinaryWriter w, int[] values)
    {
        values = values ?? new int[0];
        w.Write(values.Length);
        foreach (int v in values)
            w.Write(v);
    }

    static int[] ReadInts(BinaryReader r)
    {
        var values = new int[r.ReadInt32()];
        for (int i = 0; i < values.Length; i++)
            values[i] = r.ReadInt32();
        return values;
    }
}
