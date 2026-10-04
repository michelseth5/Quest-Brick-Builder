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

    public string setNumber;
    public string title;
    public string author;
    public List<Step> steps = new List<Step>();

    public int PartCount
    {
        get
        {
            int count = 0;
            foreach (Step step in steps)
                count += step.parts.Count;
            return count;
        }
    }

    public static SetData[] LoadAll() => Resources.LoadAll<SetData>(ResourceFolder);
}
