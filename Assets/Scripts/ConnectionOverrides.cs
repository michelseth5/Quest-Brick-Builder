using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Connection points fixed in the game (Y menu > Fix Snapping): each fixed part's whole list of points, kept in
/// app storage as Connections/&lt;part&gt;.json and used instead of the points it was made with, for every copy of that
/// part in every set. Restoring a part deletes its file and puts the original points back.
/// (Pull the files off the headset to make a fix part of the project: Connections/ in LDCad shadow format.)
/// </summary>
public static class ConnectionOverrides
{
    [Serializable]
    class Saved
    {
        public string partId;
        public Connector[] connectors;
    }

    public static string Folder => Path.Combine(Application.persistentDataPath, "Connections");

    // Each changed part's points as it was made, for Restore.
    static readonly Dictionary<BrickPartData, Connector[]> Originals = new Dictionary<BrickPartData, Connector[]>();

    static string PathFor(string partId)
    {
        var name = new System.Text.StringBuilder();
        foreach (char c in partId.ToLowerInvariant())
            name.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return Path.Combine(Folder, name + ".json");
    }

    public static bool IsChanged(BrickPartData part) => part != null && File.Exists(PathFor(part.partId));

    static HashSet<string> s_files; // the fix files there are, read once (startup checks every part)

    /// <summary>Uses a part's fixed points, if it has any (call once when a part is first looked up).</summary>
    public static void Apply(BrickPartData part)
    {
        if (part == null || string.IsNullOrEmpty(part.partId))
            return;
        if (s_files == null)
        {
            s_files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(Folder))
                s_files.UnionWith(Directory.GetFiles(Folder, "*.json"));
        }
        string path = PathFor(part.partId);
        if (!s_files.Contains(path))
            return;
        try
        {
            Saved saved = JsonUtility.FromJson<Saved>(File.ReadAllText(path));
            if (saved?.connectors == null)
                return;
            if (!Originals.ContainsKey(part))
                Originals[part] = part.connectors;
            part.connectors = saved.connectors;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Couldn't read the fixed connection points of {part.partId}: {e.Message}");
        }
    }

    /// <summary>Saves a part's points and uses them at once, everywhere.</summary>
    public static void Save(BrickPartData part, Connector[] connectors)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathFor(part.partId), JsonUtility.ToJson(new Saved { partId = part.partId, connectors = connectors }, true));
        s_files?.Add(PathFor(part.partId));
        if (!Originals.ContainsKey(part))
            Originals[part] = part.connectors;
        part.connectors = connectors;
        Brick.ConnectionsChanged(part);
    }

    /// <summary>Forgets a part's fixes and goes back to the points it was made with.</summary>
    public static void Restore(BrickPartData part)
    {
        string path = PathFor(part.partId);
        if (File.Exists(path))
            File.Delete(path);
        s_files?.Remove(path);
        if (Originals.TryGetValue(part, out Connector[] original))
        {
            part.connectors = original;
            Originals.Remove(part);
        }
        Brick.ConnectionsChanged(part);
    }
}
