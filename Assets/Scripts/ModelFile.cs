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
        public int bond;            // parts with the same non-zero bond are bonded together
        public bool fromSet;        // taken out of the Build It Yourself bin (see SetProgress)
        public int lockSection;     // 0 = loose; otherwise the numbered section it was locked into
    }

    /// <summary>A saved wrist inventory stack: which wrist, how many copies, and one copy's parts.</summary>
    public class InventoryStack
    {
        public int hand; // 0 = left, 1 = right
        public int count;
        public bool fromSet; // these came out of the Build It Yourself bin
        public List<Placement> parts = new List<Placement>(); // relative to the first part
    }

    // Inventory stacks are written as comment lines, which other LDraw programs ignore.
    const string InventoryPrefix = "0 !QUESTBRICK";

    /// <summary>A Build It Yourself set in progress: which set, and where its instructions were open.</summary>
    public class SetProgress
    {
        public string setNumber;
        public int booklet;
        public int page;
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

    /// <summary>
    /// Saves the bricks as a new model. The first brick decides which way the model faces.
    /// With includeInventory, everything on both wrists is saved too. With a set in progress, the set, its
    /// instruction page, and which parts came out of its bin (fromSet) are saved so the build can be resumed.
    /// </summary>
    public static Info Save(string title, IList<Brick> bricks, bool includeInventory = false,
                            SetProgress progress = null, Func<Brick, bool> fromSet = null)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"model-{DateTime.Now:yyyyMMdd-HHmmss-fff}.ldr");
        File.WriteAllText(path, Write(title, bricks, includeInventory, progress, fromSet));
        return new Info { path = path, title = title, saved = DateTime.Now };
    }

    public static List<Placement> Load(string path, out string title)
    {
        return Read(File.ReadAllLines(path), out title, out _, out _);
    }

    public static List<Placement> Load(string path, out string title, out List<InventoryStack> inventory)
    {
        return Read(File.ReadAllLines(path), out title, out inventory, out _);
    }

    /// <summary>Loads a model, its wrist inventory, and the Build It Yourself set it was saved during (or null).</summary>
    public static List<Placement> Load(string path, out string title, out List<InventoryStack> inventory, out SetProgress progress)
    {
        return Read(File.ReadAllLines(path), out title, out inventory, out progress);
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

    static string Write(string title, IList<Brick> bricks, bool includeInventory, SetProgress progress, Func<Brick, bool> fromSet)
    {
        var text = new StringBuilder();
        text.AppendLine("0 " + title);
        text.AppendLine("0 Name: " + title + ".ldr");
        text.AppendLine("0 Author: Quest Brick Builder");
        text.AppendLine("0 BFC CERTIFY CCW");
        if (progress != null)
        {
            text.Append(InventoryPrefix).Append(" SET ").Append(progress.setNumber)
                .Append(" BOOK ").Append(progress.booklet).Append(' ').Append(progress.page).AppendLine();
        }
        bool FromSet(Brick brick) => progress != null && fromSet != null && fromSet(brick);

        if (bricks.Count > 0)
        {
            // Frame: bottom center of everything, facing the way the first brick faces (turned to upright).
            Vector3 origin = BottomCenter(bricks);
            Quaternion toModel = Quaternion.Inverse(Quaternion.Euler(0f, bricks[0].transform.eulerAngles.y, 0f));

            var bondIds = new Dictionary<Brick.Bond, int>();
            foreach (Brick brick in bricks)
            {
                int bondId = 0;
                if (brick.CurrentBond != null && !bondIds.TryGetValue(brick.CurrentBond, out bondId))
                    bondIds[brick.CurrentBond] = bondId = bondIds.Count + 1;

                if (brick.IsLockedBody)
                {
                    // A locked body is saved as the pieces it's made of, each with the lock it belongs to.
                    foreach (LockedBody.Piece piece in brick.Locked.Explode(brick.transform))
                    {
                        if (bondId != 0)
                            text.Append(InventoryPrefix).Append(" BOND ").Append(bondId).AppendLine();
                        text.Append(InventoryPrefix).Append(" LOCK ").Append(piece.section).AppendLine();
                        if (progress != null && piece.fromSet)
                            text.Append(InventoryPrefix).AppendLine(" FROMSET");
                        AppendPartLine(text, piece.part.partId, piece.colorCode, toModel * (piece.position - origin), toModel * piece.rotation);
                        text.AppendLine();
                    }
                    continue;
                }
                if (brick.part == null)
                    continue; // simple test bricks have no LDraw part

                if (bondId != 0)
                    text.Append(InventoryPrefix).Append(" BOND ").Append(bondId).AppendLine();
                if (brick.IsSectionLocked)
                    text.Append(InventoryPrefix).Append(" LOCK ").Append(brick.LockedSection).AppendLine(); // applies to the next part line
                if (FromSet(brick))
                    text.Append(InventoryPrefix).AppendLine(" FROMSET"); // applies to the next part line

                AppendPartLine(text, brick, toModel * (brick.transform.position - origin), toModel * brick.transform.rotation);
                text.AppendLine();
            }
        }

        if (includeInventory)
        {
            foreach (WristInventory.StackRecord stack in WristInventory.Export())
            {
                text.Append(InventoryPrefix).Append(" WRIST ").Append(stack.hand == 0 ? "L" : "R").Append(' ').Append(stack.count);
                if (stack.members.Count > 0 && stack.members.TrueForAll(FromSet))
                    text.Append(" FROMSET");
                text.AppendLine();

                // Positions relative to the stack's first brick. (InverseTransformPoint undoes the shrinking.)
                Transform root = stack.members[0].transform;
                foreach (Brick member in stack.members)
                {
                    if (member.part == null)
                        continue;
                    text.Append(InventoryPrefix).Append(' ');
                    AppendPartLine(text, member, root.InverseTransformPoint(member.transform.position),
                                   Quaternion.Inverse(root.rotation) * member.transform.rotation);
                    text.AppendLine();
                }
                text.Append(InventoryPrefix).AppendLine(" END");
            }
        }

        return text.ToString();
    }

    // "1 <color> x y z a b c d e f g h i <part>.dat" in LDraw units and axes.
    static void AppendPartLine(StringBuilder text, Brick brick, Vector3 position, Quaternion rotation) =>
        AppendPartLine(text, brick.part.partId, brick.colorCode, position, rotation);

    static void AppendPartLine(StringBuilder text, string partId, int colorCode, Vector3 position, Quaternion rotation)
    {
        Vector3 ld = new Vector3(position.x, -position.y, position.z) / MetersPerLdu;
        Matrix4x4 m = FlipY * Matrix4x4.Rotate(rotation) * FlipY;

        text.Append("1 ").Append(colorCode).Append(' ');
        text.Append(F(ld.x)).Append(' ').Append(F(ld.y)).Append(' ').Append(F(ld.z)).Append(' ');
        text.Append(F(m.m00)).Append(' ').Append(F(m.m01)).Append(' ').Append(F(m.m02)).Append(' ');
        text.Append(F(m.m10)).Append(' ').Append(F(m.m11)).Append(' ').Append(F(m.m12)).Append(' ');
        text.Append(F(m.m20)).Append(' ').Append(F(m.m21)).Append(' ').Append(F(m.m22)).Append(' ');
        text.Append(partId).Append(".dat");
    }

    static List<Placement> Read(string[] lines, out string title, out List<InventoryStack> inventory, out SetProgress progress)
    {
        title = lines.Length > 0 && lines[0].StartsWith("0 ") ? lines[0].Substring(2).Trim() : "Model";
        var result = new List<Placement>();
        inventory = new List<InventoryStack>();
        progress = null;
        InventoryStack stack = null;
        int pendingBond = 0;
        bool pendingFromSet = false;
        int pendingLock = 0;

        foreach (string line in lines)
        {
            string[] t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            if (t.Length >= 2 && t[0] == "0" && t[1] == "!QUESTBRICK")
            {
                if (t.Length >= 4 && t[2] == "SET")
                {
                    // "SET <set number> BOOK <booklet> <page>"
                    progress = new SetProgress { setNumber = t[3] };
                    if (t.Length >= 7 && t[4] == "BOOK")
                    {
                        int.TryParse(t[5], out progress.booklet);
                        int.TryParse(t[6], out progress.page);
                    }
                }
                else if (t.Length >= 4 && t[2] == "LOCK")
                {
                    int.TryParse(t[3], out pendingLock); // applies to the next part line
                }
                else if (t.Length >= 3 && t[2] == "FROMSET")
                {
                    pendingFromSet = true; // applies to the next part line
                }
                else if (t.Length >= 4 && t[2] == "BOND")
                {
                    int.TryParse(t[3], out pendingBond); // applies to the next part line
                }
                else if (t.Length >= 5 && t[2] == "WRIST")
                {
                    int.TryParse(t[4], out int count);
                    stack = new InventoryStack { hand = t[3] == "L" ? 0 : 1, count = Math.Max(1, count), fromSet = t.Length >= 6 && t[5] == "FROMSET" };
                    inventory.Add(stack);
                }
                else if (t.Length >= 3 && t[2] == "END")
                {
                    stack = null;
                }
                else if (stack != null && TryParsePartLine(t, 2, out Placement part))
                {
                    stack.parts.Add(part);
                }
                continue;
            }

            if (TryParsePartLine(t, 0, out Placement placement))
            {
                placement.bond = pendingBond;
                placement.fromSet = pendingFromSet;
                placement.lockSection = pendingLock;
                result.Add(placement);
                pendingBond = 0;
                pendingFromSet = false;
                pendingLock = 0;
            }
        }

        inventory.RemoveAll(s => s.parts.Count == 0);
        return result;
    }

    // Parses "1 <color> x y z a..i <file>" starting at token index `start`.
    static bool TryParsePartLine(string[] t, int start, out Placement placement)
    {
        placement = default;
        if (t.Length < start + 15 || t[start] != "1")
            return false;

        float[] v = new float[12];
        if (!int.TryParse(t[start + 1], out int colorCode))
            return false;
        for (int i = 0; i < 12; i++)
        {
            if (!float.TryParse(t[start + 2 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                return false;
        }

        var m = Matrix4x4.identity;
        m.m00 = v[3]; m.m01 = v[4]; m.m02 = v[5];
        m.m10 = v[6]; m.m11 = v[7]; m.m12 = v[8];
        m.m20 = v[9]; m.m21 = v[10]; m.m22 = v[11];
        Matrix4x4 unity = FlipY * m * FlipY;

        string file = string.Join(" ", t, start + 14, t.Length - start - 14);
        placement = new Placement
        {
            partId = Path.GetFileNameWithoutExtension(file.Replace('\\', '/')),
            colorCode = colorCode,
            position = new Vector3(v[0], -v[1], v[2]) * MetersPerLdu,
            rotation = unity.rotation,
        };
        return true;
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
