using UnityEngine;

/// <summary>
/// An imported LDraw part: its mesh and connection points, all in meters with the part's origin as pivot.
/// Created by Tools > LDraw Part Importer.
/// </summary>
public class BrickPartData : ScriptableObject
{
    public string partId;
    public string description;
    public Mesh mesh;

    [Tooltip("Base of each stud on top of the part.")]
    public Vector3[] studs;

    [Tooltip("Where a stud from a part below plugs into the underside.")]
    public Vector3[] antiStuds;

    [Tooltip("Bounds of the part without its studs. Used for the collider and overlap checks.")]
    public Bounds bodyBounds;
}
