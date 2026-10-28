using System.Linq;
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

    /// <summary>The set's parts list; worked out from the building steps if no list was fetched.</summary>
    public List<InventoryItem> PartsList()
    {
        if (HasInventory)
            return inventory;

        var totals = new Dictionary<(string, int), int>();
        foreach (Step step in steps)
        {
            foreach (Placement placement in step.parts)
            {
                var key = (placement.partId, placement.colorCode);
                totals[key] = totals.TryGetValue(key, out int quantity) ? quantity + 1 : 1;
            }
        }
        var list = new List<InventoryItem>();
        foreach (KeyValuePair<(string, int), int> total in totals)
            list.Add(new InventoryItem { partId = total.Key.Item1, colorCode = total.Key.Item2, quantity = total.Value });
        return list;
    }

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

    /// <summary>
    /// Every set in the game, except ones the player removed: those that come with the app plus those
    /// downloaded in the game (a download of the same set number takes the place of the built-in one).
    /// </summary>
    public static SetData[] LoadAll()
    {
        List<SetData> downloaded = Downloaded();
        var numbers = new HashSet<string>(downloaded.Select(s => s.setNumber));
        return Resources.LoadAll<SetData>(ResourceFolder)
                        .Where(s => !IsRemoved(s.setNumber) && !numbers.Contains(s.setNumber))
                        .Concat(downloaded)
                        .ToArray();
    }

    // ---------- Sets downloaded in the game: Sets/<number>.json in app storage ----------

    public static string DownloadFolder => System.IO.Path.Combine(Application.persistentDataPath, "Sets");

    static string DownloadPath(string setNumber) => System.IO.Path.Combine(DownloadFolder, setNumber + ".json");

    static List<SetData> s_downloaded; // read once, then kept up to date by SaveDownloaded/Remove

    static List<SetData> Downloaded()
    {
        if (s_downloaded != null)
            return s_downloaded;
        s_downloaded = new List<SetData>();
        if (!System.IO.Directory.Exists(DownloadFolder))
            return s_downloaded;
        foreach (string path in System.IO.Directory.GetFiles(DownloadFolder, "*.json"))
        {
            try
            {
                SetData set = CreateInstance<SetData>();
                JsonUtility.FromJsonOverwrite(System.IO.File.ReadAllText(path), set);
                set.name = set.setNumber;
                s_downloaded.Add(set);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Couldn't read downloaded set {path}: {e.Message}");
            }
        }
        return s_downloaded;
    }

    public bool IsDownloaded => Downloaded().Contains(this);

    /// <summary>Stores a set downloaded in the game (replacing an earlier download of it) and shows it again if removed.</summary>
    public static void SaveDownloaded(SetData set)
    {
        System.IO.Directory.CreateDirectory(DownloadFolder);
        System.IO.File.WriteAllText(DownloadPath(set.setNumber), JsonUtility.ToJson(set));
        List<SetData> downloaded = Downloaded();
        downloaded.RemoveAll(s => s.setNumber == set.setNumber);
        downloaded.Add(set);
        List<string> removed = RemovedList();
        if (removed.Remove(set.setNumber))
            SaveRemoved(removed);
    }

    // Sets that come with the app can't be deleted from it, so removing one hides it (kept on the headset).
    const string RemovedKey = "RemovedSets";

    static List<string> RemovedList() =>
        PlayerPrefs.GetString(RemovedKey, "").Split('|').Where(s => s.Length > 0).ToList();

    static void SaveRemoved(List<string> removed)
    {
        PlayerPrefs.SetString(RemovedKey, string.Join("|", removed));
        PlayerPrefs.Save();
    }

    public static bool IsRemoved(string setNumber) => RemovedList().Contains(setNumber);

    public static int RemovedCount => RemovedList().Count;

    /// <summary>
    /// Takes a set out of the game: a downloaded one is deleted (its parts stay, other sets may use them);
    /// one that came with the app is hidden.
    /// </summary>
    public static void Remove(string setNumber)
    {
        // The set, and the instructions file it was read from.
        foreach (string extension in new[] { ".json", ".mpd", ".ldr" })
        {
            string path = System.IO.Path.Combine(DownloadFolder, setNumber + extension);
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        Downloaded().RemoveAll(s => s.setNumber == setNumber);

        if (Resources.LoadAll<SetData>(ResourceFolder).Any(s => s.setNumber == setNumber))
        {
            List<string> removed = RemovedList();
            if (!removed.Contains(setNumber))
                removed.Add(setNumber);
            SaveRemoved(removed);
        }
    }

    /// <summary>Brings back every removed set (their instruction PDFs are gone and need adding again).</summary>
    public static void RestoreRemoved()
    {
        PlayerPrefs.DeleteKey(RemovedKey);
        PlayerPrefs.Save();
    }
}
