using System.Collections.Generic;
using System.Linq;
using LDrawImport;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Turns parsed LDraw data into the game's parts and sets. Shared by the editor's importer (which saves the
/// results as assets) and the headset's set download (which caches them in app storage, see PartStore), so a
/// part comes out the same either way.
/// </summary>
public static class PartBuilder
{
    /// <summary>The part's mesh: one submesh per color (see BrickPartData.submeshColors). Fills `into` if given.</summary>
    public static Mesh BuildMesh(LDrawMeshData data, string name, Mesh into = null)
    {
        Mesh mesh = into != null ? into : new Mesh();
        mesh.Clear();
        mesh.name = name;
        mesh.indexFormat = data.Vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(data.Vertices.Select(ToUnity).ToList());
        mesh.SetNormals(data.Normals.Select(ToUnity).ToList());
        mesh.subMeshCount = Mathf.Max(1, data.Submeshes.Count);
        for (int i = 0; i < data.Submeshes.Count; i++)
            mesh.SetTriangles(data.Submeshes[i], i);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Fills in a part from parsed data and its mesh.</summary>
    public static void Fill(BrickPartData part, string partId, LDrawMeshData data, Mesh mesh)
    {
        part.partId = partId;
        part.description = data.Description;
        part.category = BrickPartData.CategoryFromDescription(data.Description);
        part.mesh = mesh;
        part.submeshColors = data.SubmeshColors.ToArray();
        part.studs = data.Studs.Select(ToUnity).ToArray();
        part.antiStuds = data.AntiStuds.Select(ToUnity).ToArray();
        part.connectors = data.Connectors.Select(ToConnector).ToArray();
        part.components = data.Components.Select(ToComponent).ToArray();
        part.details = data.Details.SelectMany(d => d.Ranges.Select(r => new BrickPartData.Detail
        {
            position = ToUnity(d.Position), up = ToUnity(d.Up), submesh = r.Submesh, start = r.Start, count = r.Count,
        })).ToArray();
        var bounds = new Bounds();
        bounds.SetMinMax(ToUnity(data.BodyMin), ToUnity(data.BodyMax));
        part.bodyBounds = bounds;
    }

    public static Vector3 ToUnity(System.Numerics.Vector3 v) => new Vector3(v.X, v.Y, v.Z);

    public static Connector ToConnector(ConnectorData c) => new Connector
    {
        kind = c.Kind switch
        {
            "clip" => Connector.Kind.Clip,
            "finger" => Connector.Kind.Finger,
            "generic" => Connector.Kind.Generic,
            "ball" => Connector.Kind.Ball,
            _ => Connector.Kind.Cylinder,
        },
        male = c.Male,
        position = ToUnity(c.Position),
        axis = ToUnity(c.Axis),
        reference = ToUnity(c.Reference),
        length = c.Length,
        center = c.Center,
        slide = c.Slide,
        openEnded = c.OpenEnded,
        profile = c.Profile ?? "",
        sections = c.Sections ?? "",
        group = c.Group ?? "",
    };

    // An assembly's part line (LDraw: x y z, then a 3 x 3 matrix, Y down) as a Unity position and rotation.
    public static BrickPartData.Component ToComponent((string partId, float[] placement) component)
    {
        float[] v = component.placement;
        var flip = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));
        var m = Matrix4x4.identity;
        m.m00 = v[3]; m.m01 = v[4]; m.m02 = v[5];
        m.m10 = v[6]; m.m11 = v[7]; m.m12 = v[8];
        m.m20 = v[9]; m.m21 = v[10]; m.m22 = v[11];
        return new BrickPartData.Component
        {
            partId = component.partId,
            position = new Vector3(v[0], -v[1], v[2]) * LDrawParser.MetersPerLdu,
            rotation = (flip * m * flip).rotation,
        };
    }

    /// <summary>A set's building steps, read from its LDraw model file (e.g. from the OMR).</summary>
    public static List<SetData.Step> ReadSteps(string modelFile, out string author)
    {
        List<List<LDrawParser.ModelPart>> steps = LDrawParser.ReadModelSteps(modelFile, out author);
        return steps.Select(step => new SetData.Step { parts = step.Select(ToPlacement).ToList() }).ToList();
    }

    // LDraw placement -> Unity: meters, Y flipped (the same conversion the part meshes use).
    public static SetData.Placement ToPlacement(LDrawParser.ModelPart part)
    {
        System.Numerics.Matrix4x4 m = part.Transform;
        // m stores LDraw's rotation transposed (row vectors), so R[i][j] = m[j][i].
        var unity = Matrix4x4.identity;
        unity.m00 = m.M11;  unity.m01 = -m.M21; unity.m02 = m.M31;
        unity.m10 = -m.M12; unity.m11 = m.M22;  unity.m12 = -m.M32;
        unity.m20 = m.M13;  unity.m21 = -m.M23; unity.m22 = m.M33;

        return new SetData.Placement
        {
            partId = part.PartId,
            colorCode = part.Color,
            position = new Vector3(m.M41, -m.M42, m.M43) * LDrawParser.MetersPerLdu,
            rotation = unity.rotation,
        };
    }
}
