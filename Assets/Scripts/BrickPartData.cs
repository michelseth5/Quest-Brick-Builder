using UnityEngine;

/// <summary>
/// An imported LDraw part: its mesh and connection points, all in meters with the part's origin as pivot.
/// Created by Tools > LDraw Part Importer.
/// </summary>
public class BrickPartData : ScriptableObject
{
    public string partId;
    public string description;

    [Tooltip("Menu group, e.g. Brick, Plate, Slope, Tile. Taken from the first word of the LDraw description.")]
    public string category;

    public Mesh mesh;

    [Tooltip("LDraw color code of each submesh. 16 means \"the brick's color\"; others are printed or fixed colors.")]
    public int[] submeshColors;

    [Tooltip("Base of each stud on top of the part.")]
    public Vector3[] studs;

    [Tooltip("Where a stud from a part below plugs into the underside.")]
    public Vector3[] antiStuds;

    [Tooltip("Every connection point (studs, sockets, pins, holes, bars, clips, hinges) from the LDCad shadow library. " +
             "When empty, the studs and antiStuds above are used.")]
    public Connector[] connectors;

    /// <summary>One of the whole parts an assembly is made of, and where it sits in the assembly.</summary>
    [System.Serializable]
    public struct Component
    {
        public string partId;
        public Vector3 position;
        public Quaternion rotation;
    }

    [Tooltip("For a part that LDraw supplies as a ready-made assembly of other parts (e.g. a complete universal joint): " +
             "those parts. Once placed, the assembly is swapped for them so its joints can turn.")]
    public Component[] components;

    public bool IsAssembly => components != null && components.Length > 1;

    [Tooltip("Bounds of the part without its studs. Used for the collider and overlap checks.")]
    public Bounds bodyBounds;

    public string Category => !string.IsNullOrEmpty(category) ? category : CategoryFromDescription(description);

    public const int MainColorCode = 16;

    /// <summary>
    /// Colors a renderer showing this part: the brick's color on its main-color submesh, and each printed
    /// or fixed-color submesh in its own color. See-through colors use the transparent material.
    /// </summary>
    public static void ApplyColors(MeshRenderer renderer, Mesh mesh, int[] submeshColors, Color mainColor,
                                   Material opaque, Material transparent)
    {
        int count = mesh != null ? Mathf.Max(1, mesh.subMeshCount) : 1;
        var materials = new Material[count];
        var colors = new Color[count];
        for (int i = 0; i < count; i++)
        {
            int code = submeshColors != null && i < submeshColors.Length ? submeshColors[i] : MainColorCode;
            colors[i] = code == MainColorCode ? mainColor : LDrawColors.Get(code);
            materials[i] = colors[i].a < 1f && transparent != null ? transparent : opaque;
        }
        for (int i = 0; i < count; i++)
            materials[i] = SharedMaterial(materials[i], colors[i]);
        renderer.sharedMaterials = materials;

        // Colors live in the shared materials, not per-renderer overrides, so URP's SRP Batcher can draw
        // many bricks together. (Clears overrides left from older builds of a brick.)
        renderer.SetPropertyBlock(null);
        for (int i = 0; i < count; i++)
            renderer.SetPropertyBlock(null, i);
    }

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    // One material per (base material, color), shared by every brick of that color.
    static readonly System.Collections.Generic.Dictionary<(Material, Color), Material> SharedMaterials =
        new System.Collections.Generic.Dictionary<(Material, Color), Material>();
    static readonly System.Collections.Generic.Dictionary<Material, Material> BaseOf =
        new System.Collections.Generic.Dictionary<Material, Material>();

    /// <summary>
    /// The shared materials for a part's submeshes: the given color on its main-color submesh, and each
    /// printed or fixed-color submesh in its own color (see-through ones on the transparent material).
    /// </summary>
    public static Material[] MaterialsFor(Mesh mesh, int[] submeshColors, Color mainColor, Material opaque, Material transparent)
    {
        int count = mesh != null ? Mathf.Max(1, mesh.subMeshCount) : 1;
        var materials = new Material[count];
        for (int i = 0; i < count; i++)
        {
            int code = submeshColors != null && i < submeshColors.Length ? submeshColors[i] : MainColorCode;
            Color color = code == MainColorCode ? mainColor : LDrawColors.Get(code);
            materials[i] = SharedMaterial(color.a < 1f && transparent != null ? transparent : opaque, color);
        }
        return materials;
    }

    /// <summary>The base material in the given color, shared with everything else using that color.</summary>
    public static Material SharedMaterial(Material baseMaterial, Color color)
    {
        if (baseMaterial == null)
            return null;
        // Bricks copied from other bricks start out with an already-colored material; use its original.
        if (BaseOf.TryGetValue(baseMaterial, out Material original))
            baseMaterial = original;

        if (!SharedMaterials.TryGetValue((baseMaterial, color), out Material material) || material == null)
        {
            material = new Material(baseMaterial) { name = $"{baseMaterial.name} #{ColorUtility.ToHtmlStringRGBA(color)}" };
            material.SetColor(BaseColorId, color);
            SharedMaterials[(baseMaterial, color)] = material;
            BaseOf[material] = baseMaterial;
        }
        return material;
    }

    /// <summary>Description without the category word, e.g. "2 x 4" for "Brick 2 x 4".</summary>
    public string ShortName
    {
        get
        {
            string text = description ?? partId;
            string first = CategoryFromDescription(text);
            return text.Length > first.Length ? text.Substring(first.Length).Trim() : text;
        }
    }

    // LDraw names start with the part type ("Brick 2 x 4", "Slope Brick 45 2 x 2"); "~" or "_" mark special parts.
    public static string CategoryFromDescription(string description)
    {
        if (string.IsNullOrEmpty(description))
            return "Other";
        string text = description.TrimStart('~', '_', '=', '|');
        int space = text.IndexOf(' ');
        return space > 0 ? text.Substring(0, space) : text;
    }
}
