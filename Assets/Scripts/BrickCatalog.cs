using System.Collections.Generic;
using UnityEngine;

/// <summary>The list of imported parts. The LDraw importer adds every part it imports here.</summary>
public class BrickCatalog : ScriptableObject
{
    public List<BrickPartData> parts = new List<BrickPartData>();
}
