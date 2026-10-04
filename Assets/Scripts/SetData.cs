using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A set's building instructions: every part's final position, grouped into steps.
/// Created from an LDraw set file by Tools > LDraw Part Importer > Import Set Parts,
/// and stored in Resources/Sets so the game can list every set.
/// Positions are in meters relative to the model's own origin (Unity axes).
/// </summary>
public class SetData : ScriptableObject
{
    public const string ResourceFolder = "Sets";

    [System.Serializable]
    public class Placement
    {
        public string partId;
        public int colorCode;
        public Vector3 position;
        public Quaternion rotation;
    }

    [System.Serializable]
    public class Step
    {
        public List<Placement> parts = new List<Placement>();
    }

    /// <summary>One line of the set's parts list: a part in a color, and how many.</summary>
    [System.Serializable]
    public class InventoryItem
    {
        public string partId;   // LDraw part number
        public int colorCode;   // LDraw color code
        public int quantity;
    }

    public string setNumber;
    public string title;
    public string author;
    public int year;

    [Tooltip("Step-by-step building instructions (from an LDraw model file). Empty if none are available.")]
    public List<Step> steps = new List<Step>();

    [Tooltip("The set's parts list (from Rebrickable), for building it yourself. Empty if it wasn't fetched.")]
    public List<InventoryItem> inventory = new List<InventoryItem>();

    public bool HasSteps => steps.Count > 0;
    public bool HasInventory => inventory.Count > 0;

    /// <summary>Number of parts: from the building steps if there are any, otherwise from the parts list.</summary>
    public int PartCount
    {
        get
        {
            int count = 0;
            if (HasSteps)
            {
                foreach (Step step in steps)
                    count += step.parts.Count;
            }
            else
            {
                foreach (InventoryItem item in inventory)
                    count += item.quantity;
            }
            return count;
        }
    }

    public static SetData[] LoadAll() => Resources.LoadAll<SetData>(ResourceFolder);
}
