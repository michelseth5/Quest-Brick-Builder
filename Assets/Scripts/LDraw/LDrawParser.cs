using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace LDrawImport
{
    /// <summary>
    /// One LDraw part converted to Unity's space: meters, +Y up, triangles wound for Unity's front faces.
    /// Pure C# (no UnityEngine) so it can be tested outside the editor.
    /// </summary>
    public sealed class LDrawMeshData
    {
        public string Description = "";
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();

        /// <summary>
        /// Triangles grouped by color, one list per submesh. SubmeshColors gives each one's LDraw color code;
        /// MainColor (16) means "the brick's color". The main-color submesh comes first when present.
        /// </summary>
        public readonly List<List<int>> Submeshes = new List<List<int>>();
        public readonly List<int> SubmeshColors = new List<int>();

        public int TriangleCount
        {
            get
            {
                int count = 0;
                foreach (List<int> submesh in Submeshes)
                    count += submesh.Count / 3;
                return count;
            }
        }

        /// <summary>Where studs sit on top of the part (the base of each stud).</summary>
        public readonly List<Vector3> Studs = new List<Vector3>();

        /// <summary>Where a stud from a part below would plug into the underside.</summary>
        public readonly List<Vector3> AntiStuds = new List<Vector3>();

        /// <summary>Bounds of the part without its studs.</summary>
        public Vector3 BodyMin, BodyMax;

        /// <summary>Connection points from the LDCad shadow library (studs, pins, holes, bars, clips, hinges...).</summary>
        public readonly List<ConnectorData> Connectors = new List<ConnectorData>();

        public readonly List<string> MissingFiles = new List<string>();

        /// <summary>
        /// Each stud, hollow stud, or underside tube: where it is, which way it points, and which stretches of
        /// the mesh's index lists draw it, so it can be left out when it's hidden inside a model.
        /// </summary>
        public readonly List<DetailData> Details = new List<DetailData>();

        /// <summary>
        /// For a part that is really an assembly of other whole parts (e.g. a "complete" universal joint):
        /// each of them and where it sits, as the 12 numbers of its LDraw line (x y z, then the 3 x 3 matrix).
        /// Empty for ordinary parts.
        /// </summary>
        public readonly List<(string partId, float[] placement)> Components = new List<(string, float[])>();
    }

    /// <summary>One connection point on a part, in Unity space (meters, +Y up).</summary>
    /// <summary>A stretch of one submesh's index list (Count indices from Start).</summary>
    public sealed class DetailRange
    {
        public int Submesh, Start, Count;
    }

    /// <summary>One stud, hollow stud, or underside tube of a part.</summary>
    public sealed class DetailData
    {
        public Vector3 Position;   // its base, in Unity space
        public Vector3 Up;         // the way it points
        public readonly List<DetailRange> Ranges = new List<DetailRange>();
    }

    public sealed class ConnectorData
    {
        public string Kind;        // "cylinder", "clip", "finger", or "generic"
        public bool Male;
        public Vector3 Position;   // where the shape starts (or its middle, if Center)
        public Vector3 Axis;       // the direction the shape extends from Position
        public Vector3 Reference;  // the shape's sideways axis, for lining up rotation
        public float Length;       // meters along Axis
        public bool Center;
        public bool Slide;
        public bool OpenEnded;     // no closed end: can be entered from either side
        public string Profile;     // what it fits, e.g. "R6" (round, radius 6), "R6 R8", "A6" (axle), clip "R4", "F6", "G:group"
        public string Sections;    // cylinders: each section from the start as <shape><radius>:<width>, in LDU, e.g. "R8:2 R6:16 R8:2"
        public string Group;
    }

    /// <summary>
    /// Reads parts from an LDraw library folder (the one containing "parts" and "p"),
    /// following sub-file references and BFC winding rules.
    /// </summary>
    public sealed class LDrawParser
    {
        public const float MetersPerLdu = 0.0004f;

        /// <summary>
        /// Where library files (parts, primitives, connection info) are read from: folders on disk by default, or
        /// the downloaded library zips on the headset (see ZipLibraryFiles). Model files are always read from disk.
        /// </summary>
        public static ILibraryFiles Files { get; set; } = DiskFiles.Instance;

        /// <summary>LDraw's "use the current color" code; at the top level, the brick's own color.</summary>
        public const int MainColor = 16;
        const int EdgeColor = 24;

        const float StudPitchLdu = 20f;
        const float SmoothingAngleDegrees = 35f;
        const int MaxDepth = 32;

        readonly string _root;
        readonly Dictionary<string, string[]> _fileCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        // Per-part working state, in LDraw coordinates (-Y up, LDU).
        readonly List<Vector3> _corners = new List<Vector3>();
        readonly List<int> _triangleColors = new List<int>();
        // Which stud or underside tube each triangle belongs to (-1 for the body), and where each of those is.
        readonly List<int> _triangleDetails = new List<int>();
        readonly List<(Vector3 origin, Vector3 up)> _details = new List<(Vector3, Vector3)>();
        int _currentDetail = -1;
        readonly List<Vector3> _studs = new List<Vector3>();
        readonly HashSet<string> _missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool _hasBody;
        Vector3 _bodyMin, _bodyMax;

        public LDrawParser(string libraryRoot)
        {
            _root = libraryRoot;
            UnofficialRoot = FindUnofficialRoot(libraryRoot);
        }

        /// <summary>The unofficial parts library (newer parts awaiting review), if it's been unzipped next to the official one.</summary>
        public string UnofficialRoot { get; }

        /// <summary>LDraw/unofficial, next to the official library folder (LDraw/ldraw).</summary>
        public static string FindUnofficialRoot(string libraryRoot)
        {
            string sibling = Path.Combine(Path.GetDirectoryName(libraryRoot.TrimEnd('/', '\\')) ?? libraryRoot, "unofficial");
            return Files.DirectoryExists(Path.Combine(sibling, "parts")) ? sibling : null;
        }

        /// <summary>The file for a part id in the official library or, failing that, the unofficial one (null if neither).</summary>
        public static string FindPartFile(string libraryRoot, string partId)
        {
            string official = Path.Combine(libraryRoot, "parts", partId + ".dat");
            if (Files.FileExists(official))
                return official;
            string unofficialRoot = FindUnofficialRoot(libraryRoot);
            string unofficial = unofficialRoot != null ? Path.Combine(unofficialRoot, "parts", partId + ".dat") : null;
            return unofficial != null && Files.FileExists(unofficial) ? unofficial : null;
        }

        /// <summary>Finds the LDraw library under the project folder, or returns null.</summary>
        public static string FindLibraryRoot(string projectRoot)
        {
            foreach (string candidate in new[] { Path.Combine(projectRoot, "LDraw", "ldraw"), Path.Combine(projectRoot, "LDraw") })
            {
                if (Files.DirectoryExists(Path.Combine(candidate, "parts")) && Files.DirectoryExists(Path.Combine(candidate, "p")))
                    return candidate;
            }
            return null;
        }

        public bool PartExists(string partId) => Load(ToFileName(partId), lowRes: false) != null;

        public LDrawMeshData Parse(string partId)
        {
            string fileName = ToFileName(partId);
            string[] lines = Load(fileName, lowRes: false);
            if (lines == null)
                throw new FileNotFoundException($"LDraw part '{partId}' was not found in {_root}.");

            _corners.Clear();
            _triangleColors.Clear();
            _triangleDetails.Clear();
            _details.Clear();
            _currentDetail = -1;
            _studs.Clear();
            _missing.Clear();
            _hasBody = false;

            List<Snap> snaps = ProcessFile(fileName, Matrix4x4.Identity, MainColor, inverted: false, allowBfc: true, lowRes: false, inStud: false, depth: 0);

            // Renumbered parts are stubs saying "~Moved to X": use X's description instead.
            string description = ReadDescription(lines);
            for (int hops = 0; hops < 4 && description.StartsWith("~Moved to ", StringComparison.OrdinalIgnoreCase); hops++)
            {
                string[] target = Load(ToFileName(description.Substring(10)), lowRes: false);
                if (target == null)
                    break;
                description = ReadDescription(target);
            }

            var data = new LDrawMeshData { Description = description };
            FindComponents(lines, data);
            data.MissingFiles.AddRange(_missing);
            BuildMesh(data);
            BuildConnections(data);
            BuildConnectors(data, snaps);
            return data;
        }

        // An assembly: a file that does nothing but place two or more other parts (no shapes of its own).
        void FindComponents(string[] lines, LDrawMeshData data)
        {
            // Only assemblies meant to move: LDraw calls them "(Complete)" (a universal joint, a hinge plate)
            // or "Poseable". Hoses, chains and the like are also drawn from segments but stay one piece.
            string description = data.Description ?? "";
            if (description.IndexOf("(Complete)", StringComparison.OrdinalIgnoreCase) < 0
                && description.IndexOf("Poseable", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            List<(string partId, Matrix4x4 world)> found = PlacedParts(lines, Matrix4x4.Identity, 0);
            if (found == null || found.Count < 2)
                return;
            foreach ((string partId, Matrix4x4 m) in found)
            {
                // Back to the numbers of an LDraw part line: x y z, then the 3 x 3 matrix row by row.
                data.Components.Add((partId, new[]
                {
                    m.M41, m.M42, m.M43,
                    m.M11, m.M21, m.M31,
                    m.M12, m.M22, m.M32,
                    m.M13, m.M23, m.M33,
                }));
            }
        }

        // The whole parts a file places, if placing parts is all it does (null if it has shapes of its own or
        // uses sub-parts or primitives, i.e. it's an ordinary part). A file that places exactly one part is an
        // alias ("=Hinge Plate 1 x 4 (Complete)" just places the real one) or a renumbered part, and is followed
        // to what it points at. Real assemblies are not opened up further (a poseable horse is head, body and
        // legs, not the half-shells those are drawn from).
        List<(string partId, Matrix4x4 world)> PlacedParts(string[] lines, Matrix4x4 parent, int depth)
        {
            var found = new List<(string, Matrix4x4)>();
            foreach (string line in lines)
            {
                string[] t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0)
                    continue;
                if (t[0] == "2" || t[0] == "3" || t[0] == "4" || t[0] == "5")
                    return null; // has geometry of its own
                if (t[0] != "1" || t.Length < 15)
                    continue;

                string file = string.Join(" ", t, 14, t.Length - 14);
                if (file.Contains("\\") || file.Contains("/"))
                    return null; // a sub-part or primitive: an ordinary part
                string id = Path.GetFileNameWithoutExtension(file);
                if (FindPartFile(_root, id) == null)
                    return null; // a primitive

                float x = F(t[2]), y = F(t[3]), z = F(t[4]);
                float a = F(t[5]), b = F(t[6]), c = F(t[7]);
                float d = F(t[8]), e = F(t[9]), f = F(t[10]);
                float g = F(t[11]), h = F(t[12]), i = F(t[13]);
                var local = new Matrix4x4(
                    a, d, g, 0f,
                    b, e, h, 0f,
                    c, f, i, 0f,
                    x, y, z, 1f);
                Matrix4x4 world = local * parent;

                found.Add((id, world));
            }

            if (depth >= 4)
                return found;
            if (found.Count == 1)
            {
                // An alias: whatever its one part places, if that is itself only placing parts.
                string[] target = Load(ToFileName(found[0].Item1), lowRes: false);
                List<(string, Matrix4x4)> placed = target != null ? PlacedParts(target, found[0].Item2, depth + 1) : null;
                return placed != null && placed.Count > 0 ? placed : found;
            }
            for (int index = 0; index < found.Count; index++)
            {
                // A piece that has been renumbered: use the part it was renamed to.
                string[] target = Load(ToFileName(found[index].Item1), lowRes: false);
                List<(string, Matrix4x4)> placed = target != null ? PlacedParts(target, found[index].Item2, depth + 1) : null;
                if (placed != null && placed.Count == 1)
                    found[index] = placed[0];
            }
            return found;
        }

        static string ToFileName(string partId)
        {
            string name = partId.Trim();
            return name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ? name : name + ".dat";
        }

        static string ReadDescription(string[] lines)
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (!line.StartsWith("0"))
                    break;
                string text = line.Substring(1).Trim();
                return string.Join(" ", text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            }
            return "";
        }

        // Top-of-part studs. Underside tubes and posts (stud3, stud4, ...) are not included.
        // Any of LDraw's stud primitives: top studs, hollow studs, and the tubes and posts underneath parts.
        static bool IsStudPrimitive(string fileName)
        {
            string name = fileName.ToLowerInvariant();
            return name.StartsWith("stud") || name.StartsWith("stu2");
        }

        static bool IsTopStud(string fileName)
        {
            string name = fileName.ToLowerInvariant();
            switch (name)
            {
                case "stud.dat":
                case "stud2.dat":
                case "stud2a.dat":
                case "stud10.dat":
                case "stud13.dat":
                case "stud15.dat":
                case "stud17.dat":
                case "stud17a.dat":
                case "stud26.dat":
                    return true;
                default:
                    return name.StartsWith("stud-logo") || name.StartsWith("stud2-logo");
            }
        }

        string[] Load(string name, bool lowRes)
        {
            name = name.Replace('\\', '/').Trim();
            string key = (lowRes ? "lowres:" : "") + name;
            if (_fileCache.TryGetValue(key, out string[] cached))
                return cached;

            var candidates = new List<string>();
            if (lowRes)
                candidates.Add(Path.Combine(_root, "p", "8", name)); // 8-segment primitives keep studs cheap on Quest
            candidates.Add(Path.Combine(_root, "parts", name));
            candidates.Add(Path.Combine(_root, "p", name));
            candidates.Add(Path.Combine(_root, "models", name));

            // Newer parts not yet in the official release (LDraw/unofficial, from ldrawunf.zip).
            if (UnofficialRoot != null)
            {
                candidates.Add(Path.Combine(UnofficialRoot, "parts", name));
                candidates.Add(Path.Combine(UnofficialRoot, "p", name));
            }

            string[] lines = null;
            foreach (string path in candidates)
            {
                if (Files.FileExists(path))
                {
                    lines = Files.ReadAllLines(path);
                    break;
                }
            }

            _fileCache[key] = lines;
            return lines;
        }

        // A line's color: 16 (and 24, edges) mean "whatever color this file is being drawn in".
        static int ResolveColor(string token, int current)
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                return current;
            return code == MainColor || code == EdgeColor ? current : code;
        }

        // Draws one file into the mesh and returns the connection points it (and its sub-files) define.
        List<Snap> ProcessFile(string name, Matrix4x4 transform, int color, bool inverted, bool allowBfc, bool lowRes, bool inStud, int depth)
        {
            var snaps = new List<Snap>();
            if (depth > MaxDepth)
                return snaps;

            string[] lines = Load(name, lowRes);
            if (lines == null)
            {
                _missing.Add(name);
                return snaps;
            }

            bool certified = false;
            bool ccw = true;
            bool invertNext = false;

            foreach (string raw in lines)
            {
                string[] t = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0)
                    continue;

                switch (t[0])
                {
                    case "0":
                        if (t.Length >= 2 && t[1] == "BFC")
                        {
                            for (int i = 2; i < t.Length; i++)
                            {
                                switch (t[i])
                                {
                                    case "CERTIFY": certified = allowBfc; break;
                                    case "NOCERTIFY": certified = false; break;
                                    case "CW": ccw = false; break;
                                    case "CCW": ccw = true; break;
                                    case "INVERTNEXT": invertNext = true; break;
                                }
                            }
                        }
                        break;

                    case "1":
                        if (t.Length >= 15)
                        {
                            snaps.AddRange(ProcessReference(t, transform, ResolveColor(t[1], color), inverted ^ invertNext, certified, lowRes, inStud, depth));
                        }
                        invertNext = false;
                        break;

                    case "3":
                        if (t.Length >= 11)
                            AddPolygon(transform, ResolveColor(t[1], color), certified, ccw ^ inverted, inStud, P(t, 2), P(t, 5), P(t, 8));
                        break;

                    case "4":
                        if (t.Length >= 14)
                        {
                            Vector3 a = P(t, 2), b = P(t, 5), c = P(t, 8), d = P(t, 11);
                            bool front = ccw ^ inverted;
                            int polygonColor = ResolveColor(t[1], color);
                            AddPolygon(transform, polygonColor, certified, front, inStud, a, b, c);
                            AddPolygon(transform, polygonColor, certified, front, inStud, a, c, d);
                        }
                        break;
                }
            }

            ApplyShadow(name, transform, snaps, includeDepth: 0);
            return snaps;
        }

        List<Snap> ProcessReference(string[] t, Matrix4x4 parent, int color, bool inverted, bool certified, bool lowRes, bool inStud, int depth)
        {
            float x = F(t[2]), y = F(t[3]), z = F(t[4]);
            float a = F(t[5]), b = F(t[6]), c = F(t[7]);
            float d = F(t[8]), e = F(t[9]), f = F(t[10]);
            float g = F(t[11]), h = F(t[12]), i = F(t[13]);

            // LDraw: p' = [a b c; d e f; g h i] p + (x y z). System.Numerics uses row vectors (p * M).
            var local = new Matrix4x4(
                a, d, g, 0f,
                b, e, h, 0f,
                c, f, i, 0f,
                x, y, z, 1f);
            Matrix4x4 world = local * parent;
            bool childInverted = inverted ^ (local.GetDeterminant() < 0f);

            string file = string.Join(" ", t, 14, t.Length - 14);
            string baseName = Path.GetFileName(file.Replace('\\', '/'));
            bool isStud = !inStud && IsTopStud(baseName);

            if (isStud)
            {
                // Only studs pointing up (LDraw up is -Y); sideways studs are ignored for now.
                Vector3 studUp = Vector3.Normalize(Vector3.TransformNormal(new Vector3(0f, -1f, 0f), world));
                if (studUp.Y < -0.9f)
                    _studs.Add(Vector3.Transform(Vector3.Zero, world));
            }

            // Each stud, tube or other stud-family primitive is one "detail": its triangles are tagged so a
            // locked model can leave it out when it's hidden (see LockedBody).
            int outerDetail = _currentDetail;
            if (_currentDetail < 0 && IsStudPrimitive(baseName))
            {
                _details.Add((Vector3.Transform(Vector3.Zero, world), Vector3.Normalize(Vector3.TransformNormal(new Vector3(0f, -1f, 0f), world))));
                _currentDetail = _details.Count - 1;
            }
            // (Stud-family shapes, tubes included, use LDraw's simplified versions: a fraction of the triangles.)
            bool simplified = lowRes || isStud || IsStudPrimitive(baseName);
            List<Snap> result = ProcessFile(file, world, color, childInverted, certified, simplified, inStud || isStud, depth + 1);
            _currentDetail = outerDetail;
            return result;
        }

        // ---------- Connection points (LDCad shadow library) ----------

        /// <summary>A raw snap meta, placed in the part's space (LDraw axes and units).</summary>
        struct Snap
        {
            public string Kind;                       // CYL, CLP, FGR, GEN
            public string Id;
            public Dictionary<string, string> Params;
            public Matrix4x4 World;                   // the shape's own frame: its axis is -Y, its sideways axis is X
        }

        string _shadowRoot;
        bool _shadowChecked;
        readonly Dictionary<string, string[]> _shadowCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        // The shadow library sits next to the parts library (LDraw/shadow) or inside it.
        string ShadowRoot
        {
            get
            {
                if (!_shadowChecked)
                {
                    _shadowChecked = true;
                    string sibling = Path.Combine(Path.GetDirectoryName(_root.TrimEnd('/', '\\')) ?? _root, "shadow");
                    string inside = Path.Combine(_root, "shadow");
                    _shadowRoot = Files.DirectoryExists(sibling) ? sibling : Files.DirectoryExists(inside) ? inside : null;
                }
                return _shadowRoot;
            }
        }

        public bool HasShadowLibrary => ShadowRoot != null;

        /// <summary>
        /// Optional folder of extra connection files in the same format (for parts the shadow library
        /// doesn't cover). Its lines are added after the library's for the same file name.
        /// </summary>
        public string ExtraShadowRoot { get; set; }

        string[] LoadShadow(string name)
        {
            name = name.Replace('\\', '/').Trim();
            if (_shadowCache.TryGetValue(name, out string[] cached))
                return cached;

            var lines = new List<string>();
            foreach (string root in new[] { ShadowRoot, ExtraShadowRoot })
            {
                if (root == null)
                    continue;
                foreach (string folder in new[] { "parts", "p" })
                {
                    string path = Path.Combine(root, folder, name);
                    if (Files.FileExists(path))
                    {
                        lines.AddRange(Files.ReadAllLines(path));
                        break;
                    }
                }
            }

            string[] result = lines.Count > 0 ? lines.ToArray() : null;
            _shadowCache[name] = result;
            return result;
        }

        static readonly System.Text.RegularExpressions.Regex SnapParam =
            new System.Text.RegularExpressions.Regex(@"\[(\w+)=([^\]]*)\]");

        // Adds a file's snap metas to the list (already holding what its sub-files defined).
        void ApplyShadow(string name, Matrix4x4 transform, List<Snap> snaps, int includeDepth)
        {
            string[] lines = LoadShadow(name);
            if (lines == null || includeDepth > 4)
                return;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (!line.StartsWith("0 !LDCAD SNAP_"))
                    continue;
                int space = line.IndexOf(' ', 9);
                string kind = (space > 0 ? line.Substring(9, space - 9) : line.Substring(9)).Substring(5);

                var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in SnapParam.Matches(line))
                    p[m.Groups[1].Value] = m.Groups[2].Value.Trim();
                p.TryGetValue("id", out string id);

                Matrix4x4 local = LocalFrame(p);
                switch (kind)
                {
                    case "CLEAR":
                        // Drops what sub-files defined (all of it, or just one ID), so this file can redefine it.
                        if (includeDepth == 0)
                            snaps.RemoveAll(s => string.IsNullOrEmpty(id) || string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
                        break;

                    case "INCL":
                        if (p.TryGetValue("ref", out string reference))
                        {
                            foreach (Matrix4x4 placed in GridPlacements(local, p))
                                ApplyShadow(reference, placed * transform, snaps, includeDepth + 1);
                        }
                        break;

                    case "CYL":
                    case "CLP":
                    case "FGR":
                    case "GEN":
                        foreach (Matrix4x4 placed in GridPlacements(local, p))
                            snaps.Add(new Snap { Kind = kind, Id = id, Params = p, World = placed * transform });
                        break;
                }
            }
        }

        // [pos=x y z] [ori=a b c d e f g h i], in the same row-vector form as type 1 lines.
        static Matrix4x4 LocalFrame(Dictionary<string, string> p)
        {
            float[] pos = Floats(p, "pos", 3) ?? new float[3];
            float[] o = Floats(p, "ori", 9) ?? new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            return new Matrix4x4(
                o[0], o[3], o[6], 0f,
                o[1], o[4], o[7], 0f,
                o[2], o[5], o[8], 0f,
                pos[0], pos[1], pos[2], 1f);
        }

        // [grid=Xcnt Zcnt Xstep Zstep] or [grid=C Xcnt C Zcnt Xstep Zstep] (C = centered), along the shape's X and Z.
        static IEnumerable<Matrix4x4> GridPlacements(Matrix4x4 local, Dictionary<string, string> p)
        {
            if (!p.TryGetValue("grid", out string grid))
            {
                yield return local;
                yield break;
            }

            string[] t = grid.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            bool centerX = IsCenterMark(t, i);
            if (centerX)
                i++;
            int countX = t.Length > i ? (int)F(t[i++]) : 1;
            bool centerZ = IsCenterMark(t, i);
            if (centerZ)
                i++;
            int countZ = t.Length > i ? (int)F(t[i++]) : 1;
            float stepX = t.Length > i ? F(t[i++]) : 0f;
            float stepZ = t.Length > i ? F(t[i]) : 0f;

            for (int x = 0; x < Math.Max(1, countX); x++)
            {
                for (int z = 0; z < Math.Max(1, countZ); z++)
                {
                    float ox = centerX ? (x - (countX - 1) * 0.5f) * stepX : x * stepX;
                    float oz = centerZ ? (z - (countZ - 1) * 0.5f) * stepZ : z * stepZ;
                    yield return Matrix4x4.CreateTranslation(ox, 0f, oz) * local;
                }
            }
        }

        static bool IsCenterMark(string[] tokens, int index) =>
            index < tokens.Length && tokens[index].Equals("C", StringComparison.OrdinalIgnoreCase);

        static float[] Floats(Dictionary<string, string> p, string key, int count)
        {
            if (!p.TryGetValue(key, out string text))
                return null;
            string[] t = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < count)
                return null;
            var values = new float[count];
            for (int i = 0; i < count; i++)
                values[i] = F(t[i]);
            return values;
        }

        static bool Flag(Dictionary<string, string> p, string key) =>
            p.TryGetValue(key, out string value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);

        // Some parts aren't covered by the shadow library, so their sub-parts give studs on top but nothing
        // underneath (e.g. 1745, 5847), and stud-less ones (newer tiles, slopes, wedges, curved bricks) get
        // nothing at all. If a part has no stud sockets facing down, add the estimated ones across its
        // footprint (BuildConnections' AntiStuds, each checked against the part's real underside).
        // Parts that don't build by studs (Technic, minifig gear, wheels, tyres, plants, animals) are left
        // alone unless they have studs on top.
        void AddMissingBottomSockets(LDrawMeshData data)
        {
            // Any upward stud socket counts, wherever it is: parts like 1750 have their real underside above
            // the lowest point of a curved skirt, and guessed sockets at the very bottom would be wrong.
            bool hasTopStud = false, hasBottomSocket = false;
            foreach (ConnectorData c in data.Connectors)
            {
                if (c.Kind != "cylinder" || c.Axis.Y < 0.99f)
                    continue;
                if (c.Male && c.Profile.Contains("R6"))
                    hasTopStud = true;
                if (!c.Male && (c.Profile.Contains("R6") || c.Profile.Contains("S6")))
                    hasBottomSocket = true;
            }
            if (hasBottomSocket || (!hasTopStud && !BuildsByStuds(data.Description)))
                return;

            var sockets = new List<Vector3>(data.AntiStuds);
            if (sockets.Count == 0 && _hasBody)
            {
                // Nothing on the footprint grid: try the spots half a stud off it, e.g. the single centre
                // socket under a 2 x 2 dish or a part that sits on one stud.
                float[] xs = GridAlongAxis(_bodyMin.X, _bodyMax.X, null), zs = GridAlongAxis(_bodyMin.Z, _bodyMax.Z, null);
                float half = StudPitchLdu * 0.5f;
                foreach ((float dx, float dz) in new[] { (half, half), (half, 0f), (0f, half) })
                {
                    foreach (float x in xs)
                    {
                        foreach (float z in zs)
                        {
                            float px = x + dx, pz = z + dz;
                            if (px > _bodyMax.X - half * 0.5f || pz > _bodyMax.Z - half * 0.5f)
                                continue; // off the edge of the part
                            float? y = UndersideAt(px, pz);
                            if (y.HasValue)
                                sockets.Add(ToUnity(new Vector3(px, y.Value, pz)));
                        }
                    }
                    if (sockets.Count > 0)
                        break;
                }
            }

            foreach (Vector3 socket in sockets)
            {
                data.Connectors.Add(new ConnectorData
                {
                    Kind = "cylinder",
                    Male = false,
                    Position = socket,
                    Axis = Vector3.UnitY,
                    Reference = Vector3.UnitX,
                    Length = 4 * MetersPerLdu,
                    Profile = "R6",
                    Group = "",
                });
            }
        }

        static readonly string[] NotStudBuilt =
        {
            "Minifig", "Technic", "Tyre", "Wheel", "Animal", "Plant", "Constraction", "Train", "Bar", "Bone",
            "Claw", "Spike", "Flag", "Rock", "Figure", "Sticker", "Hose", "String", "Electric",
        };

        // Whether a part with no studs of its own is still the kind that presses onto studs (tiles, slopes,
        // wedges, curved bricks, dishes, windscreens...), going by the first word of its description.
        static bool BuildsByStuds(string description)
        {
            string text = (description ?? "").TrimStart('~', '=', '_', ' ');
            foreach (string word in NotStudBuilt)
            {
                if (text.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return text.Length > 0;
        }

        static readonly System.Text.RegularExpressions.Regex TyreName =
            new System.Text.RegularExpressions.Regex(@"^[~=_\s]*Tyre\s+([\d.]+)\s*/\s*[\d.]+\s*x\s*([\d.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex RimName =
            new System.Text.RegularExpressions.Regex(@"^[~=_\s]*Wheel Rim\s+([\d.]+)\s*x\s*([\d.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Tyres go on rims of the same size, and LDraw names both by size: "Tyre 20/ 48 x 30" fits
        // "Wheel Rim 20 x 30" (width x rim diameter). Gives each a matching point at its middle on its
        // axle line, unless a connection file already says where the tyre goes.
        void AddWheelConnection(LDrawMeshData data)
        {
            if (!_hasBody)
                return;
            foreach (ConnectorData c in data.Connectors)
            {
                if (c.Kind == "generic")
                    return;
            }

            var tyre = TyreName.Match(data.Description ?? "");
            var rim = tyre.Success ? tyre : RimName.Match(data.Description ?? "");
            if (!rim.Success)
                return;

            // The axle line: the direction the part is thinnest or longest in, i.e. the odd one out of its
            // three sizes (a wheel is as tall as it is deep).
            Vector3 size = _bodyMax - _bodyMin;
            float xy = Math.Abs(size.X - size.Y), xz = Math.Abs(size.X - size.Z), yz = Math.Abs(size.Y - size.Z);
            Vector3 axis = yz <= xy && yz <= xz ? Vector3.UnitX : xz <= xy ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 side = axis == Vector3.UnitY ? Vector3.UnitX : Vector3.UnitY;

            string group = "rim" + rim.Groups[1].Value + "x" + rim.Groups[2].Value;
            Vector3 centre = (_bodyMin + _bodyMax) * 0.5f;
            data.Connectors.Add(new ConnectorData
            {
                Kind = "generic",
                Male = !tyre.Success,
                Position = ToUnity(centre),
                Axis = new Vector3(axis.X, -axis.Y, axis.Z),
                Reference = new Vector3(side.X, -side.Y, side.Z),
                Group = group,
                Profile = "G:" + group,
            });
        }

        // Turns raw snap metas into connectors in Unity space.
        void BuildConnectors(LDrawMeshData data, List<Snap> snaps)
        {
            foreach (Snap snap in snaps)
            {
                Vector3 origin = Vector3.Transform(Vector3.Zero, snap.World);
                Vector3 down = Vector3.TransformNormal(new Vector3(0f, -1f, 0f), snap.World); // shapes extend along -Y
                Vector3 side = Vector3.TransformNormal(new Vector3(1f, 0f, 0f), snap.World);
                float scale = down.Length();
                if (scale < 1e-6f)
                    continue;

                var connector = new ConnectorData
                {
                    Position = ToUnity(origin),
                    Axis = Vector3.Normalize(new Vector3(down.X, -down.Y, down.Z)),
                    Reference = Vector3.Normalize(new Vector3(side.X, -side.Y, side.Z)),
                    Center = Flag(snap.Params, "center"),
                    Slide = Flag(snap.Params, "slide"),
                };
                snap.Params.TryGetValue("group", out connector.Group);

                switch (snap.Kind)
                {
                    case "CYL":
                    {
                        connector.Kind = "cylinder";
                        connector.Male = !(snap.Params.TryGetValue("gender", out string gender) && gender.StartsWith("F", StringComparison.OrdinalIgnoreCase));
                        connector.OpenEnded = snap.Params.TryGetValue("caps", out string caps) && caps.Equals("none", StringComparison.OrdinalIgnoreCase);
                        var profile = new List<string>();
                        var sections = new List<string>(); // every section in order from the start, with its width
                        float length = 0f;
                        if (snap.Params.TryGetValue("secs", out string secs))
                        {
                            string[] t = secs.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                            for (int i = 0; i + 2 < t.Length; i += 3)
                            {
                                length += F(t[i + 2]);
                                sections.Add(t[i].Trim('_').ToUpperInvariant() + F(t[i + 1]).ToString("0.##", CultureInfo.InvariantCulture)
                                             + ":" + (F(t[i + 2]) * scale).ToString("0.##", CultureInfo.InvariantCulture));
                                string shape = t[i].Trim('_').ToUpperInvariant(); // "_L"/"L_" are flexible extensions
                                if (shape == "L")
                                    continue;
                                string token = shape + F(t[i + 1]).ToString("0.#", CultureInfo.InvariantCulture);
                                if (!profile.Contains(token))
                                    profile.Add(token);
                            }
                        }
                        connector.Length = length * scale * MetersPerLdu;
                        connector.Profile = string.Join(" ", profile);
                        connector.Sections = string.Join(" ", sections);
                        // A bar (round, bar-thick, open at the ends, long enough to hold) slides in a hole or clip even
                        // where the library doesn't say so, e.g. the handle of minifig cutlery.
                        if (connector.Male && connector.OpenEnded && connector.Profile == "R4" && length * scale >= 8f)
                            connector.Slide = true;
                        break;
                    }
                    case "CLP":
                        connector.Kind = "clip";
                        connector.Male = false;
                        connector.OpenEnded = true;
                        connector.Length = (Floats(snap.Params, "length", 1)?[0] ?? 8f) * scale * MetersPerLdu;
                        connector.Profile = "R" + (Floats(snap.Params, "radius", 1)?[0] ?? 4f).ToString("0.#", CultureInfo.InvariantCulture);
                        break;
                    case "FGR":
                    {
                        connector.Kind = "finger";
                        connector.Male = !(snap.Params.TryGetValue("genderOfs", out string gender) && gender.StartsWith("F", StringComparison.OrdinalIgnoreCase));
                        connector.OpenEnded = true;
                        // LDCad centres a finger pattern on its position unless told otherwise (not like cylinders).
                        connector.Center = !(snap.Params.TryGetValue("center", out string fingerCenter) && fingerCenter.Equals("false", StringComparison.OrdinalIgnoreCase));
                        float total = 0f;
                        var widths = new List<string>();
                        if (snap.Params.TryGetValue("seq", out string seq))
                        {
                            foreach (string width in seq.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                            {
                                total += F(width);
                                widths.Add(F(width).ToString("0.###", CultureInfo.InvariantCulture));
                            }
                        }
                        connector.Length = total * scale * MetersPerLdu;
                        // "F<radius>|<first segment's gender>|<segment widths>": segments alternate finger and gap,
                        // so the runtime can check that two hinges' fingers interlock (Connections.FingersInterlock).
                        connector.Profile = "F" + (Floats(snap.Params, "radius", 1)?[0] ?? 6f).ToString("0.#", CultureInfo.InvariantCulture)
                                            + "|" + (connector.Male ? "M" : "F") + "|" + string.Join(" ", widths);
                        break;
                    }
                    case "GEN":
                        connector.Kind = "generic";
                        connector.Male = !(snap.Params.TryGetValue("gender", out string genGender) && genGender.StartsWith("F", StringComparison.OrdinalIgnoreCase));
                        connector.Profile = "G:" + (connector.Group ?? "");
                        // A ball or its socket: a sphere that fits any sphere of its size, at any angle.
                        if (snap.Params.TryGetValue("bounding", out string bounding) && bounding.TrimStart().StartsWith("sph", StringComparison.OrdinalIgnoreCase)
                            && ((snap.Params.TryGetValue("match", out string match) && match.Equals("size", StringComparison.OrdinalIgnoreCase))
                                || (snap.Params.TryGetValue("placement", out string placement) && placement.Equals("free", StringComparison.OrdinalIgnoreCase))))
                        {
                            string[] size = bounding.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                            connector.Kind = "ball";
                            connector.Profile = "B" + (size.Length > 1 ? F(size[1]) : 8f).ToString("0.#", CultureInfo.InvariantCulture);
                        }
                        break;
                }

                // A part and one of its sub-parts sometimes describe the same connection; keep one.
                bool duplicate = false;
                foreach (ConnectorData existing in data.Connectors)
                {
                    if (existing.Kind == connector.Kind && existing.Male == connector.Male && existing.Profile == connector.Profile
                        && Vector3.DistanceSquared(existing.Position, connector.Position) < 1e-10f
                        && Vector3.Dot(existing.Axis, connector.Axis) > 0.999f)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                    data.Connectors.Add(connector);
            }

            AddMissingBottomSockets(data);
            AddWheelConnection(data);
        }

        void AddPolygon(Matrix4x4 transform, int color, bool certified, bool frontAsListed, bool inStud, Vector3 a, Vector3 b, Vector3 c)
        {
            int before = _corners.Count;
            a = Vector3.Transform(a, transform);
            b = Vector3.Transform(b, transform);
            c = Vector3.Transform(c, transform);

            // Unity's front faces wind the opposite way to LDraw's once Y is flipped,
            // so front-facing LDraw polygons are reversed (verified: parts come out with positive volume).
            if (!certified)
            {
                AddTriangle(a, b, c);
                AddTriangle(a, c, b);
            }
            else if (frontAsListed)
            {
                AddTriangle(a, c, b);
            }
            else
            {
                AddTriangle(a, b, c);
            }

            for (int added = (_corners.Count - before) / 3; added > 0; added--)
            {
                _triangleColors.Add(color);
                _triangleDetails.Add(_currentDetail);
            }

            if (!inStud)
            {
                IncludeInBody(a);
                IncludeInBody(b);
                IncludeInBody(c);
            }
        }

        void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            _corners.Add(a);
            _corners.Add(b);
            _corners.Add(c);
        }

        void IncludeInBody(Vector3 p)
        {
            if (!_hasBody)
            {
                _bodyMin = _bodyMax = p;
                _hasBody = true;
                return;
            }
            _bodyMin = Vector3.Min(_bodyMin, p);
            _bodyMax = Vector3.Max(_bodyMax, p);
        }

        static Vector3 ToUnity(Vector3 ldraw) => new Vector3(ldraw.X, -ldraw.Y, ldraw.Z) * MetersPerLdu;

        // Welds shared corners and smooths normals across edges flatter than the smoothing angle.
        void BuildMesh(LDrawMeshData data)
        {
            int triangleCount = _corners.Count / 3;
            var positions = new Vector3[_corners.Count];
            var faceNormals = new Vector3[triangleCount];
            var valid = new bool[triangleCount];

            for (int tri = 0; tri < triangleCount; tri++)
            {
                Vector3 a = ToUnity(_corners[tri * 3]);
                Vector3 b = ToUnity(_corners[tri * 3 + 1]);
                Vector3 c = ToUnity(_corners[tri * 3 + 2]);
                positions[tri * 3] = a;
                positions[tri * 3 + 1] = b;
                positions[tri * 3 + 2] = c;

                Vector3 cross = Vector3.Cross(b - a, c - a);
                float length = cross.Length();
                valid[tri] = length > 1e-12f;
                faceNormals[tri] = valid[tri] ? cross / length : Vector3.Zero;
            }

            var cornersAtPosition = new Dictionary<(long, long, long), List<int>>();
            for (int corner = 0; corner < positions.Length; corner++)
            {
                if (!valid[corner / 3])
                    continue;
                var key = Quantize(positions[corner], 1e-6f);
                if (!cornersAtPosition.TryGetValue(key, out List<int> list))
                    cornersAtPosition[key] = list = new List<int>();
                list.Add(corner);
            }

            float minDot = (float)Math.Cos(SmoothingAngleDegrees * Math.PI / 180.0);
            var vertexIndex = new Dictionary<(long, long, long, long, long, long), int>();

            // One submesh per color, with the brick's own color first.
            var submeshForColor = new Dictionary<int, List<int>>();
            foreach ((Vector3 origin, Vector3 up) in _details)
                data.Details.Add(new DetailData { Position = ToUnity(origin), Up = new Vector3(up.X, -up.Y, up.Z) });
            if (_triangleColors.Contains(MainColor))
                AddSubmesh(data, submeshForColor, MainColor);

            for (int corner = 0; corner < positions.Length; corner++)
            {
                if (!valid[corner / 3])
                    continue;

                Vector3 faceNormal = faceNormals[corner / 3];
                Vector3 normal = Vector3.Zero;
                foreach (int other in cornersAtPosition[Quantize(positions[corner], 1e-6f)])
                {
                    Vector3 otherNormal = faceNormals[other / 3];
                    if (Vector3.Dot(otherNormal, faceNormal) >= minDot)
                        normal += otherNormal;
                }
                normal = normal.LengthSquared() > 0f ? Vector3.Normalize(normal) : faceNormal;

                var p = Quantize(positions[corner], 1e-6f);
                var n = Quantize(normal, 1e-3f);
                var key = (p.Item1, p.Item2, p.Item3, n.Item1, n.Item2, n.Item3);
                if (!vertexIndex.TryGetValue(key, out int index))
                {
                    index = data.Vertices.Count;
                    data.Vertices.Add(positions[corner]);
                    data.Normals.Add(normal);
                    vertexIndex[key] = index;
                }

                int color = _triangleColors[corner / 3];
                if (!submeshForColor.TryGetValue(color, out List<int> submesh))
                    submesh = AddSubmesh(data, submeshForColor, color);
                if (corner % 3 == 0)
                {
                    int detail = _triangleDetails[corner / 3];
                    if (detail >= 0)
                    {
                        // Extend this detail's last stretch if this triangle follows straight on, else start one.
                        List<DetailRange> ranges = data.Details[detail].Ranges;
                        int submeshIndex = data.Submeshes.IndexOf(submesh);
                        if (ranges.Count > 0 && ranges[ranges.Count - 1].Submesh == submeshIndex
                            && ranges[ranges.Count - 1].Start + ranges[ranges.Count - 1].Count == submesh.Count)
                            ranges[ranges.Count - 1].Count += 3;
                        else
                            ranges.Add(new DetailRange { Submesh = submeshIndex, Start = submesh.Count, Count = 3 });
                    }
                }
                submesh.Add(index);
            }
        }

        static List<int> AddSubmesh(LDrawMeshData data, Dictionary<int, List<int>> submeshForColor, int color)
        {
            var submesh = new List<int>();
            data.Submeshes.Add(submesh);
            data.SubmeshColors.Add(color);
            submeshForColor[color] = submesh;
            return submesh;
        }

        /// <summary>
        /// The part ids a model file (.ldr or .mpd) uses, following its submodels. Each part is listed once.
        /// </summary>
        public static List<string> ListModelParts(string modelPath)
        {
            // An .mpd holds several "0 FILE name" sections; a plain .ldr is one section.
            var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string mainSection = null;
            List<string> current = null;
            foreach (string line in File.ReadAllLines(modelPath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("0 FILE ", StringComparison.OrdinalIgnoreCase))
                {
                    string name = NormalizeName(trimmed.Substring(7));
                    current = new List<string>();
                    sections[name] = current;
                    if (mainSection == null)
                        mainSection = name;
                    continue;
                }
                if (current == null)
                {
                    mainSection = NormalizeName(Path.GetFileName(modelPath));
                    current = new List<string>();
                    sections[mainSection] = current;
                }
                current.Add(trimmed);
            }

            var parts = new List<string>();
            var seenParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (mainSection != null)
                CollectParts(mainSection, sections, visited, seenParts, parts);
            return parts;
        }

        static void CollectParts(string section, Dictionary<string, List<string>> sections,
                                 HashSet<string> visited, HashSet<string> seenParts, List<string> parts)
        {
            if (!visited.Add(section))
                return;

            foreach (string line in sections[section])
            {
                string[] t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 15 || t[0] != "1")
                    continue;

                string reference = NormalizeName(string.Join(" ", t, 14, t.Length - 14));
                if (sections.ContainsKey(reference))
                {
                    CollectParts(reference, sections, visited, seenParts, parts);
                }
                else
                {
                    string id = reference.EndsWith(".dat") ? reference.Substring(0, reference.Length - 4) : reference;
                    if (seenParts.Add(id))
                        parts.Add(id);
                }
            }
        }

        static string NormalizeName(string name) => name.Trim().Replace('\\', '/').ToLowerInvariant();

        /// <summary>A part placed in a model: its id, LDraw color, and transform (LDraw axes and units).</summary>
        public struct ModelPart
        {
            public string PartId;
            public int Color;
            public Matrix4x4 Transform; // System.Numerics row-vector form; translation in M41..M43
        }

        /// <summary>
        /// Reads a model's building steps. Submodels are built in place: their steps are inserted where the
        /// parent model uses them. Returns the steps (each a list of parts) and the main model's author.
        /// </summary>
        public static List<List<ModelPart>> ReadModelSteps(string modelPath, out string author)
        {
            var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string mainSection = null;
            List<string> current = null;
            foreach (string line in File.ReadAllLines(modelPath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("0 FILE ", StringComparison.OrdinalIgnoreCase))
                {
                    string name = NormalizeName(trimmed.Substring(7));
                    current = new List<string>();
                    sections[name] = current;
                    if (mainSection == null)
                        mainSection = name;
                    continue;
                }
                if (current == null)
                {
                    mainSection = NormalizeName(Path.GetFileName(modelPath));
                    current = new List<string>();
                    sections[mainSection] = current;
                }
                current.Add(trimmed);
            }

            author = "";
            var steps = new List<List<ModelPart>> { new List<ModelPart>() };
            if (mainSection == null)
                return steps;

            foreach (string line in sections[mainSection])
            {
                if (line.StartsWith("0 Author:", StringComparison.OrdinalIgnoreCase))
                {
                    author = line.Substring(9).Trim();
                    break;
                }
            }

            AddSectionSteps(mainSection, Matrix4x4.Identity, MainColor, sections, steps, 0);
            steps.RemoveAll(s => s.Count == 0);
            return steps;
        }

        static void AddSectionSteps(string section, Matrix4x4 transform, int color,
                                    Dictionary<string, List<string>> sections, List<List<ModelPart>> steps, int depth)
        {
            if (depth > MaxDepth)
                return;

            foreach (string line in sections[section])
            {
                string[] t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0)
                    continue;

                if (t[0] == "0")
                {
                    if (t.Length >= 2 && (t[1] == "STEP" || t[1] == "ROTSTEP") && steps[steps.Count - 1].Count > 0)
                        steps.Add(new List<ModelPart>());
                    continue;
                }

                if (t[0] != "1" || t.Length < 15)
                    continue;

                float x = F(t[2]), y = F(t[3]), z = F(t[4]);
                var local = new Matrix4x4(
                    F(t[5]), F(t[8]), F(t[11]), 0f,
                    F(t[6]), F(t[9]), F(t[12]), 0f,
                    F(t[7]), F(t[10]), F(t[13]), 0f,
                    x, y, z, 1f);
                Matrix4x4 world = local * transform;
                int partColor = ResolveColor(t[1], color);
                string reference = NormalizeName(string.Join(" ", t, 14, t.Length - 14));

                if (sections.ContainsKey(reference))
                {
                    // Build the submodel's steps here, then carry on with this model in a fresh step.
                    if (steps[steps.Count - 1].Count > 0)
                        steps.Add(new List<ModelPart>());
                    AddSectionSteps(reference, world, partColor, sections, steps, depth + 1);
                    if (steps[steps.Count - 1].Count > 0)
                        steps.Add(new List<ModelPart>());
                }
                else
                {
                    string id = reference.EndsWith(".dat") ? reference.Substring(0, reference.Length - 4) : reference;
                    steps[steps.Count - 1].Add(new ModelPart { PartId = id, Color = partColor, Transform = world });
                }
            }
        }

        /// <summary>One entry from LDConfig.ldr, LDraw's color list.</summary>
        public struct ColorEntry
        {
            public int Code;
            public string Name;
            public float R, G, B, A; // 0..1, sRGB
        }

        /// <summary>Reads every color defined in the library's LDConfig.ldr.</summary>
        public List<ColorEntry> ReadColors()
        {
            var colors = new List<ColorEntry>();
            string path = Path.Combine(_root, "LDConfig.ldr");
            if (!Files.FileExists(path))
                return colors;

            foreach (string line in Files.ReadAllLines(path))
            {
                string[] t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 4 || t[0] != "0" || t[1] != "!COLOUR")
                    continue;

                var entry = new ColorEntry { Name = t[2].Replace('_', ' '), A = 1f };
                bool hasCode = false, hasValue = false;
                for (int i = 3; i < t.Length - 1; i++)
                {
                    switch (t[i])
                    {
                        case "CODE":
                            hasCode = int.TryParse(t[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out entry.Code);
                            break;
                        case "VALUE":
                            string hex = t[i + 1].TrimStart('#');
                            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                            {
                                entry.R = ((rgb >> 16) & 0xFF) / 255f;
                                entry.G = ((rgb >> 8) & 0xFF) / 255f;
                                entry.B = (rgb & 0xFF) / 255f;
                                hasValue = true;
                            }
                            break;
                        case "ALPHA":
                            if (int.TryParse(t[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int alpha))
                                entry.A = alpha / 255f;
                            break;
                    }
                }

                if (hasCode && hasValue)
                    colors.Add(entry);
            }
            return colors;
        }

        void BuildConnections(LDrawMeshData data)
        {
            if (!_hasBody)
                return;

            // Unity Y is flipped, so LDraw's max Y (the bottom) becomes Unity's min Y.
            data.BodyMin = ToUnity(new Vector3(_bodyMin.X, _bodyMax.Y, _bodyMin.Z));
            data.BodyMax = ToUnity(new Vector3(_bodyMax.X, _bodyMin.Y, _bodyMax.Z));

            var seen = new HashSet<(long, long, long)>();
            foreach (Vector3 stud in _studs)
            {
                if (seen.Add(Quantize(stud, 0.5f)))
                    data.Studs.Add(ToUnity(stud));
            }

            // LDraw doesn't mark underside connections, so assume a full stud grid across the footprint,
            // lined up with the top studs. Right for bricks, plates, tiles, and most slopes.
            float[] xs = GridAlongAxis(_bodyMin.X, _bodyMax.X, _studs.Count > 0 ? _studs[0].X : (float?)null);
            float[] zs = GridAlongAxis(_bodyMin.Z, _bodyMax.Z, _studs.Count > 0 ? _studs[0].Z : (float?)null);
            foreach (float x in xs)
            {
                foreach (float z in zs)
                {
                    float? y = UndersideAt(x, z);
                    if (y.HasValue)
                        data.AntiStuds.Add(ToUnity(new Vector3(x, y.Value, z)));
                }
            }
        }

        // Where a stud pushed up from below at (x, z) would seat: the lowest flat face around that stud
        // position (the rim a stud plugs into), with room above it for the stud. Car bases are thin at
        // the ends but solid and deep in the middle, and mudguards stand on legs over a wheel arch,
        // so the part's very bottom isn't always right. Null where nothing can plug in.
        float? UndersideAt(float x, float z)
        {
            const float StudHeight = 4f;
            const float HalfCell = StudPitchLdu * 0.45f;
            float rim = float.NegativeInfinity; // LDraw Y points down, so lowest = largest
            float ceiling = float.NegativeInfinity;
            for (int i = 0; i + 2 < _corners.Count; i += 3)
            {
                Vector3 a = _corners[i], b = _corners[i + 1], c = _corners[i + 2];
                Vector3 normal = Vector3.Cross(b - a, c - a);
                float length = normal.Length();
                if (length < 1e-6f)
                    continue;
                if (Math.Abs(normal.Y) / length > 0.99f && a.Y > rim && OverlapsSquare(a, b, c, x, z, HalfCell))
                    rim = a.Y;
                if (VerticalHit(a, b, c, x, z, out float y) && y > ceiling)
                    ceiling = y;
            }
            if (float.IsNegativeInfinity(rim))
                return float.IsNegativeInfinity(ceiling) ? (float?)null : _bodyMax.Y; // nothing there at all: a gap in the footprint
            // The first thing above the stud position's centre must leave room for a stud.
            if (!float.IsNegativeInfinity(ceiling) && ceiling > rim - StudHeight + 0.5f)
                return null;
            return rim;
        }

        // Height at which a vertical line through (x, z) crosses triangle abc, if it does.
        static bool VerticalHit(Vector3 a, Vector3 b, Vector3 c, float x, float z, out float y)
        {
            y = 0f;
            float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (Math.Abs(d) < 1e-6f)
                return false;
            float u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
            float v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
            float w = 1f - u - v;
            const float Edge = -1e-4f;
            if (u < Edge || v < Edge || w < Edge)
                return false;
            y = u * a.Y + v * b.Y + w * c.Y;
            return true;
        }

        // Whether triangle abc, seen from above, overlaps the square of half-size h around (x, z).
        static bool OverlapsSquare(Vector3 a, Vector3 b, Vector3 c, float x, float z, float h)
        {
            if (Math.Max(a.X, Math.Max(b.X, c.X)) < x - h || Math.Min(a.X, Math.Min(b.X, c.X)) > x + h
                || Math.Max(a.Z, Math.Max(b.Z, c.Z)) < z - h || Math.Min(a.Z, Math.Min(b.Z, c.Z)) > z + h)
                return false;
            Vector3[] tri = { a, b, c };
            for (int i = 0; i < 3; i++)
            {
                Vector3 p = tri[i], q = tri[(i + 1) % 3], r = tri[(i + 2) % 3];
                float nx = q.Z - p.Z, nz = p.X - q.X; // edge normal in the XZ plane
                float side = nx * (r.X - p.X) + nz * (r.Z - p.Z);
                float centre = nx * (x - p.X) + nz * (z - p.Z);
                float reach = (Math.Abs(nx) + Math.Abs(nz)) * h;
                if (side > 0f ? centre < -reach : centre > reach)
                    return false;
            }
            return true;
        }

        static float[] GridAlongAxis(float min, float max, float? alignTo)
        {
            int count = Math.Max(1, (int)Math.Round((max - min) / StudPitchLdu));
            float start = (min + max) * 0.5f - (count - 1) * StudPitchLdu * 0.5f;

            if (alignTo.HasValue)
            {
                float offset = alignTo.Value - start;
                offset -= (float)Math.Round(offset / StudPitchLdu) * StudPitchLdu;
                // A stud exactly half a step off the footprint grid is a centred "jumper" stud (e.g. 1745,
                // 15573); the underside still follows the footprint, so don't shift for it.
                if (Math.Abs(Math.Abs(offset) - StudPitchLdu * 0.5f) > 0.5f)
                    start += offset;
            }

            var result = new float[count];
            for (int i = 0; i < count; i++)
                result[i] = start + i * StudPitchLdu;
            return result;
        }

        static (long, long, long) Quantize(Vector3 v, float step) =>
            ((long)Math.Round(v.X / step), (long)Math.Round(v.Y / step), (long)Math.Round(v.Z / step));

        static Vector3 P(string[] t, int index) => new Vector3(F(t[index]), F(t[index + 1]), F(t[index + 2]));

        static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
