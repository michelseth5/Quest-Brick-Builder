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

    static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    static string SetsFolder => Path.Combine(ProjectRoot, "Sets");

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
                    List<string> ids = LDrawParser.ListModelParts(setFiles[_setIndex]);
                    Import(libraryRoot, ids, $"Set {Path.GetFileName(setFiles[_setIndex])}: {ids.Count} different parts");
                }
            }

            // Specific part numbers.
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Part numbers (separate with spaces, commas, or new lines)", EditorStyles.boldLabel);
            _partList = EditorGUILayout.TextArea(_partList, GUILayout.MinHeight(80));
            if (GUILayout.Button("Import Parts", GUILayout.Height(30)))
            {
                string[] ids = _partList.Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                Import(libraryRoot, ids, null);
            }
        }

        EditorGUILayout.Space();
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.LabelField(_log, EditorStyles.wordWrappedLabel);
        EditorGUILayout.EndScrollView();
    }

    void Import(string libraryRoot, IList<string> partIds, string heading)
    {
        var parser = new LDrawParser(libraryRoot);
        var log = new StringBuilder();
        if (heading != null)
            log.AppendLine(heading).AppendLine();

        EnsureFolder(OutputFolder);
        EnsureFolder(MeshFolder);
        BrickCatalog catalog = LoadOrCreateAsset<BrickCatalog>(CatalogPath);
        int colorCount = SaveColorTable(parser);

        try
        {
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
                var bounds = new Bounds();
                bounds.SetMinMax(ToUnity(data.BodyMin), ToUnity(data.BodyMax));
                part.bodyBounds = bounds;
                EditorUtility.SetDirty(part);

                if (!catalog.parts.Contains(part))
                    catalog.parts.Add(part);

                log.Append($"✓ {id}: {data.Description} — {data.TriangleCount} triangles, {part.studs.Length} studs");
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
        _log = log.ToString();
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
}
