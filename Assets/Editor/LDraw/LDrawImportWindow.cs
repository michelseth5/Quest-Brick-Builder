using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LDrawImport;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Tools > LDraw Part Importer. Converts LDraw parts into Unity meshes plus BrickPartData assets,
/// and adds them to the part catalog the brick tray uses.
/// </summary>
public class LDrawImportWindow : EditorWindow
{
    const string OutputFolder = "Assets/LDrawParts";
    const string MeshFolder = OutputFolder + "/Meshes";
    const string CatalogPath = OutputFolder + "/PartCatalog.asset";

    const string StarterParts =
        "3001 3002 3003 3004 3005 3010 3009 3007\n" + // bricks
        "3020 3022 3023b 3024 3710 3031\n" +          // plates
        "3039 3040b 3062b\n" +                       // slopes, round
        "3068b 3069b\n" +                            // tiles
        "3811";                                      // 32 x 32 baseplate

    const string ColorTablePath = "Assets/Resources/" + LDrawColors.ResourceName + ".asset";

    string _partList = StarterParts;
    string _log = "";
    Vector2 _scroll;
    int _setIndex;

    public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    public static string SetsFolder => Path.Combine(ProjectRoot, "Sets");

    [MenuItem("Tools/LDraw Part Importer")]
    static void Open() => GetWindow<LDrawImportWindow>("LDraw Importer");

    void OnGUI()
    {
        string libraryRoot = LDrawParser.FindLibraryRoot(ProjectRoot);

        EditorGUILayout.LabelField("LDraw library", EditorStyles.boldLabel);
        if (libraryRoot != null)
            EditorGUILayout.LabelField(libraryRoot, EditorStyles.wordWrappedMiniLabel);
        else
            EditorGUILayout.HelpBox("LDraw library not found. Unzip complete.zip into an 'LDraw' folder next to Assets.", MessageType.Error);

        using (new EditorGUI.DisabledScope(libraryRoot == null))
        {
            // Every part a set's model file uses.
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Import every part a set uses", EditorStyles.boldLabel);
            string[] setFiles = Directory.Exists(SetsFolder)
                ? Directory.GetFiles(SetsFolder).Where(f => f.EndsWith(".mpd") || f.EndsWith(".ldr")).OrderBy(f => f).ToArray()
                : new string[0];
            if (setFiles.Length == 0)
            {
                EditorGUILayout.HelpBox("Put set files (.mpd or .ldr) in a 'Sets' folder next to Assets.", MessageType.Info);
            }
            else
            {
                _setIndex = Mathf.Clamp(_setIndex, 0, setFiles.Length - 1);
                _setIndex = EditorGUILayout.Popup("Set", _setIndex, setFiles.Select(Path.GetFileName).ToArray());
                if (GUILayout.Button("Import Set Parts", GUILayout.Height(30)))
                {
                    string setFile = setFiles[_setIndex];
                    List<string> ids = LDrawParser.ListModelParts(setFile);
                    _log = ImportParts(libraryRoot, ids, $"Set {Path.GetFileName(setFile)}: {ids.Count} different parts");
                    _log += "\n" + SaveSet(Path.GetFileNameWithoutExtension(setFile), setFile, null, 0, null);
                }
            }

            // Everything already imported (e.g. after an importer update).
            EditorGUILayout.Space();
            BrickCatalog existing = AssetDatabase.LoadAssetAtPath<BrickCatalog>(CatalogPath);
            using (new EditorGUI.DisabledScope(existing == null || existing.parts.Count == 0))
            {
                if (GUILayout.Button($"Re-import All Parts ({(existing != null ? existing.parts.Count : 0)})", GUILayout.Height(30)))
                {
                    List<string> ids = existing.parts.Where(p => p != null).Select(p => p.partId).ToList();
                    _log = ImportParts(libraryRoot, ids, $"Re-importing {ids.Count} parts");
                }
            }

            // Specific part numbers.
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Part numbers (separate with spaces, commas, or new lines)", EditorStyles.boldLabel);
            _partList = EditorGUILayout.TextArea(_partList, GUILayout.MinHeight(80));
            if (GUILayout.Button("Import Parts", GUILayout.Height(30)))
            {
                string[] ids = _partList.Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                _log = ImportParts(libraryRoot, ids, null);
            }
        }

        EditorGUILayout.Space();
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.LabelField(_log, EditorStyles.wordWrappedLabel);
        EditorGUILayout.EndScrollView();
    }

    /// <summary>Imports (or re-imports) LDraw parts into the catalog. Returns a log of what happened.</summary>
    public static string ImportParts(string libraryRoot, IList<string> partIds, string heading)
    {
        // Connection data: the LDCad shadow library (LDraw/shadow) plus our own additions (Connections/).
        var parser = new LDrawParser(libraryRoot) { ExtraShadowRoot = Path.Combine(ProjectRoot, "Connections") };
        var log = new StringBuilder();
        if (heading != null)
            log.AppendLine(heading).AppendLine();

        EnsureFolder(OutputFolder);
        EnsureFolder(MeshFolder);
        BrickCatalog catalog = LoadOrCreateAsset<BrickCatalog>(CatalogPath);
        int colorCount = SaveColorTable(parser);

        try
        {
            // (A copy, so the parts that assemblies are made of can be added as they turn up.)
            partIds = new List<string>(partIds);
            for (int i = 0; i < partIds.Count; i++)
            {
                string id = partIds[i];
                EditorUtility.DisplayProgressBar("Importing LDraw parts", id, (float)i / partIds.Count);

                LDrawMeshData data;
                try
                {
                    data = parser.Parse(id);
                }
                catch (Exception e)
                {
                    log.AppendLine($"✗ {id}: {e.Message}");
                    continue;
                }

                string fileId = string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                Mesh mesh = SaveMesh(data, id, $"{MeshFolder}/{fileId}.asset");

                BrickPartData part = LoadOrCreateAsset<BrickPartData>($"{OutputFolder}/{fileId}.asset");
                part.partId = id;
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
                foreach ((string componentId, float[] _) in data.Components)
                {
                    if (!partIds.Contains(componentId))
                        partIds.Add(componentId);
                }
                var bounds = new Bounds();
                bounds.SetMinMax(ToUnity(data.BodyMin), ToUnity(data.BodyMax));
                part.bodyBounds = bounds;
                EditorUtility.SetDirty(part);

                if (!catalog.parts.Contains(part))
                    catalog.parts.Add(part);

                log.Append($"✓ {id}: {data.Description} — {data.TriangleCount} triangles, {part.connectors.Length} connections");
                if (data.SubmeshColors.Count > 1)
                    log.Append($", {data.SubmeshColors.Count} colors");
                if (data.MissingFiles.Count > 0)
                    log.Append($" (missing: {string.Join(", ", data.MissingFiles)})");
                log.AppendLine();
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        catalog.parts.RemoveAll(p => p == null);
        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();

        log.AppendLine();
        log.AppendLine($"Saved to {OutputFolder}. The catalog has {catalog.parts.Count} parts and {colorCount} colors are available.");
        return log.ToString();
    }

    public static string SetAssetPath(string setNumber) => $"Assets/Resources/{SetData.ResourceFolder}/{setNumber}.asset";

    /// <summary>
    /// Creates or updates a set in Resources/Sets so the game can list it. Pass a model file to (re)read
    /// its building steps, and/or an inventory to store its parts list; null leaves that part unchanged.
    /// </summary>
    public static string SaveSet(string setNumber, string modelFile, string title, int year, List<SetData.InventoryItem> inventory)
    {
        EnsureFolder("Assets/Resources");
        EnsureFolder("Assets/Resources/" + SetData.ResourceFolder);
        SetData set = LoadOrCreateAsset<SetData>(SetAssetPath(setNumber));
        set.setNumber = setNumber;
        set.title = title ?? ReadSetTitle(setNumber) ?? (string.IsNullOrEmpty(set.title) ? setNumber : set.title);
        if (year > 0)
            set.year = year;

        if (modelFile != null)
        {
            List<List<LDrawParser.ModelPart>> steps = LDrawParser.ReadModelSteps(modelFile, out string author);
            set.author = author;
            set.steps = steps.Select(step => new SetData.Step { parts = step.Select(ToPlacement).ToList() }).ToList();
        }
        if (inventory != null)
            set.inventory = inventory;

        EditorUtility.SetDirty(set);
        AssetDatabase.SaveAssets();

        var summary = new StringBuilder($"Set saved: \"{set.title}\"");
        if (set.HasSteps)
            summary.Append($", {set.steps.Count} building steps");
        if (set.HasInventory)
            summary.Append($", parts list of {set.inventory.Sum(i => i.quantity)} parts");
        return summary.Append('.').ToString();
    }

    static string ReadSetTitle(string setNumber)
    {
        string path = Path.Combine(SetsFolder, "titles.txt");
        if (!File.Exists(path))
            return null;
        foreach (string line in File.ReadAllLines(path))
        {
            int equals = line.IndexOf('=');
            if (line.StartsWith("#") || equals < 0)
                continue;
            if (line.Substring(0, equals).Trim().Equals(setNumber, StringComparison.OrdinalIgnoreCase))
                return line.Substring(equals + 1).Trim();
        }
        return null;
    }

    // LDraw placement -> Unity: meters, Y flipped (the same conversion the part meshes use).
    static SetData.Placement ToPlacement(LDrawParser.ModelPart part)
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

    // Every LDraw color, into Resources so the game can look up any color code a set uses.
    static int SaveColorTable(LDrawParser parser)
    {
        List<LDrawParser.ColorEntry> entries = parser.ReadColors();
        if (entries.Count == 0)
            return 0;

        EnsureFolder("Assets/Resources");
        LDrawColors table = LoadOrCreateAsset<LDrawColors>(ColorTablePath);
        table.codes = entries.Select(e => e.Code).ToArray();
        table.names = entries.Select(e => e.Name).ToArray();
        table.colors = entries.Select(e => new Color(e.R, e.G, e.B, e.A)).ToArray();
        EditorUtility.SetDirty(table);
        return entries.Count;
    }

    // An assembly's part line (LDraw: x y z, then a 3 x 3 matrix, Y down) as a Unity position and rotation.
    static BrickPartData.Component ToComponent((string partId, float[] placement) component)
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

    static Mesh SaveMesh(LDrawMeshData data, string name, string path)
    {
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        bool isNew = mesh == null;
        if (isNew)
            mesh = new Mesh();

        mesh.Clear();
        mesh.name = name;
        mesh.indexFormat = data.Vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(data.Vertices.Select(ToUnity).ToList());
        mesh.SetNormals(data.Normals.Select(ToUnity).ToList());
        mesh.subMeshCount = Mathf.Max(1, data.Submeshes.Count);
        for (int i = 0; i < data.Submeshes.Count; i++)
            mesh.SetTriangles(data.Submeshes[i], i);
        mesh.RecalculateBounds();

        if (isNew)
            AssetDatabase.CreateAsset(mesh, path);
        else
            EditorUtility.SetDirty(mesh);
        return mesh;
    }

    static T LoadOrCreateAsset<T>(string path) where T : ScriptableObject
    {
        T asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset == null)
        {
            asset = CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
        }
        return asset;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    static Vector3 ToUnity(System.Numerics.Vector3 v) => new Vector3(v.X, v.Y, v.Z);

    static Connector ToConnector(ConnectorData c) => new Connector
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
}
