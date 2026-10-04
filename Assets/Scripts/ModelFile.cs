using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Saves and loads models as LDraw .ldr files (the standard format other LEGO building programs open).
/// Files live in the app's storage under "Models". Positions are stored relative to the model's
/// bottom center, so a loaded model can be placed anywhere.
/// </summary>
public static class ModelFile
{
    const float MetersPerLdu = 0.0004f;

    public struct Placement
    {
        public string partId;
        public int colorCode;
        public Vector3 position;    // Unity meters, relative to the model's bottom center
        public Quaternion rotation; // relative to the model's facing
    }

    public class Info
    {
        public string path;
        public string title;
        public DateTime saved;
    }

    public static string Folder => Path.Combine(Application.persistentDataPath, "Models");

    // ---------- Library ----------

    /// <summary>All saved models, newest first.</summary>
    public static List<Info> List()
    {
        var result = new List<Info>();
        if (!Directory.Exists(Folder))
            return result;

        foreach (string path in Directory.GetFiles(Folder, "*.ldr"))
        {
            result.Add(new Info
            {
                path = path,
                title = ReadTitle(path),
                saved = File.GetLastWriteTime(path),
            });
        }
        result.Sort((a, b) => b.saved.CompareTo(a.saved));
        return result;
    }

    /// <summary>"Model N" with the first number not already used.</summary>
    public static string NextAutoName()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Info info in List())
            used.Add(info.title);
        for (int n = 1; ; n++)
        {
            string name = $"Model {n}";
            if (!used.Contains(name))
                return name;
        }
    }

    /// <summary>Saves the bricks as a new model. The first brick decides which way the model faces.</summary>
    public static Info Save(string title, IList<Brick> bricks)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"model-{DateTime.Now:yyyyMMdd-HHmmss-fff}.ldr");
        File.WriteAllText(path, Write(title, bricks));
        return new Info { path = path, title = title, saved = DateTime.Now };
    }

    public static List<Placement> Load(string path, out string title)
    {
        return Read(File.ReadAllLines(path), out title);
    }

    public static void Rename(string path, string newTitle)
    {
        string[] lines = File.ReadAllLines(path);
        if (lines.Length > 0 && lines[0].StartsWith("0 "))
            lines[0] = "0 " + newTitle;
        File.WriteAllLines(path, lines);
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    static string ReadTitle(string path)
    {
        using (var reader = new StreamReader(path))
        {
            string first = reader.ReadLine();
            return first != null && first.StartsWith("0 ") ? first.Substring(2).Trim() : Path.GetFileNameWithoutExtension(path);
        }
    }

    // ---------- Format ----------

    // LDraw is Y-down in LDU; Unity is Y-up in meters. Flipping Y both ways converts positions and rotations.
    static readonly Matrix4x4 FlipY = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));

    static string Write(string title, IList<Brick> bricks)
    {
        // Frame: bottom center of everything, facing the way the first brick faces (turned to upright).
        Vector3 origin = BottomCenter(bricks);
        float yaw = bricks.Count > 0 ? bricks[0].transform.eulerAngles.y : 0f;
        Quaternion toModel = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f));

        var text = new StringBuilder();
        text.AppendLine("0 " + title);
        text.AppendLine("0 Name: " + title + ".ldr");
        text.AppendLine("0 Author: Quest Brick Builder");
        text.AppendLine("0 BFC CERTIFY CCW");

        foreach (Brick brick in bricks)
        {
            if (brick.part == null)
                continue; // simple test bricks have no LDraw part

            Vector3 position = toModel * (brick.transform.position - origin);
            Quaternion rotation = toModel * brick.transform.rotation;

            Vector3 ld = new Vector3(position.x, -position.y, position.z) / MetersPerLdu;
            Matrix4x4 m = FlipY * Matrix4x4.Rotate(rotation) * FlipY;

            text.Append("1 ").Append(brick.colorCode).Append(' ');
            text.Append(F(ld.x)).Append(' ').Append(F(ld.y)).Append(' ').Append(F(ld.z)).Append(' ');
            text.Append(F(m.m00)).Append(' ').Append(F(m.m01)).Append(' ').Append(F(m.m02)).Append(' ');
            text.Append(F(m.m10)).Append(' ').Append(F(m.m11)).Append(' ').Append(F(m.m12)).Append(' ');
            text.Append(F(m.m20)).Append(' ').Append(F(m.m21)).Append(' ').Append(F(m.m22)).Append(' ');
            text.Append(brick.part.partId).AppendLine(".dat");
        }
        return text.ToString();
    }

    static List<Placement> Read(string[] lines, out string title)
    {
        title = lines.Length > 0 && lines[0].StartsWith("0 ") ? lines[0].Substring(2).Trim() : "Model";
        var result = new List<Placement>();

        foreach (string line in lines)
        {
            string[] t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 15 || t[0] != "1")
                continue;

            float[] v = new float[12];
            bool ok = int.TryParse(t[1], out int colorCode);
            for (int i = 0; i < 12 && ok; i++)
                ok = float.TryParse(t[2 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
            if (!ok)
                continue;

            var m = Matrix4x4.identity;
            m.m00 = v[3]; m.m01 = v[4]; m.m02 = v[5];
            m.m10 = v[6]; m.m11 = v[7]; m.m12 = v[8];
            m.m20 = v[9]; m.m21 = v[10]; m.m22 = v[11];
            Matrix4x4 unity = FlipY * m * FlipY;

            string file = string.Join(" ", t, 14, t.Length - 14);
            result.Add(new Placement
            {
                partId = Path.GetFileNameWithoutExtension(file.Replace('\\', '/')),
                colorCode = colorCode,
                position = new Vector3(v[0], -v[1], v[2]) * MetersPerLdu,
                rotation = unity.rotation,
            });
        }
        return result;
    }

    static Vector3 BottomCenter(IList<Brick> bricks)
    {
        var bounds = new Bounds();
        bool first = true;
        foreach (Brick brick in bricks)
        {
            Bounds b = brick.BodyBounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = new Vector3(
                    (corner & 1) == 0 ? b.min.x : b.max.x,
                    (corner & 2) == 0 ? b.min.y : b.max.y,
                    (corner & 4) == 0 ? b.min.z : b.max.z);
                Vector3 world = brick.transform.TransformPoint(local);
                if (first)
                {
                    bounds = new Bounds(world, Vector3.zero);
                    first = false;
                }
                else
                {
                    bounds.Encapsulate(world);
                }
            }
        }
        return new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
    }

    static string F(float value)
    {
        // Snap tiny float noise so files stay clean (e.g. 0.9999999 -> 1, -0 -> 0).
        float rounded = Mathf.Round(value * 1000f) / 1000f;
        if (Mathf.Abs(rounded) < 0.0005f)
            rounded = 0f;
        return rounded.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
