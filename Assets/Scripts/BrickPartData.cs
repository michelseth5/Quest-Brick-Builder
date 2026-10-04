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

    [Tooltip("Base of each stud on top of the part.")]
    public Vector3[] studs;

    [Tooltip("Where a stud from a part below plugs into the underside.")]
    public Vector3[] antiStuds;

    [Tooltip("Bounds of the part without its studs. Used for the collider and overlap checks.")]
    public Bounds bodyBounds;

    public string Category => !string.IsNullOrEmpty(category) ? category : CategoryFromDescription(description);

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
