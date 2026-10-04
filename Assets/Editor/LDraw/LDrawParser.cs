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

        public readonly List<string> MissingFiles = new List<string>();
    }

    /// <summary>
    /// Reads parts from an LDraw library folder (the one containing "parts" and "p"),
    /// following sub-file references and BFC winding rules.
    /// </summary>
    public sealed class LDrawParser
    {
        public const float MetersPerLdu = 0.0004f;

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
        readonly List<Vector3> _studs = new List<Vector3>();
        readonly HashSet<string> _missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool _hasBody;
        Vector3 _bodyMin, _bodyMax;

        public LDrawParser(string libraryRoot)
        {
            _root = libraryRoot;
        }

        /// <summary>Finds the LDraw library under the project folder, or returns null.</summary>
        public static string FindLibraryRoot(string projectRoot)
        {
            foreach (string candidate in new[] { Path.Combine(projectRoot, "LDraw", "ldraw"), Path.Combine(projectRoot, "LDraw") })
            {
                if (Directory.Exists(Path.Combine(candidate, "parts")) && Directory.Exists(Path.Combine(candidate, "p")))
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
            _studs.Clear();
            _missing.Clear();
            _hasBody = false;

            ProcessFile(fileName, Matrix4x4.Identity, MainColor, inverted: false, allowBfc: true, lowRes: false, inStud: false, depth: 0);

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
            data.MissingFiles.AddRange(_missing);
            BuildMesh(data);
            BuildConnections(data);
            return data;
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

            string[] lines = null;
            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    lines = File.ReadAllLines(path);
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

        void ProcessFile(string name, Matrix4x4 transform, int color, bool inverted, bool allowBfc, bool lowRes, bool inStud, int depth)
        {
            if (depth > MaxDepth)
                return;

            string[] lines = Load(name, lowRes);
            if (lines == null)
            {
                _missing.Add(name);
                return;
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
                            ProcessReference(t, transform, ResolveColor(t[1], color), inverted ^ invertNext, certified, lowRes, inStud, depth);
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
        }

        void ProcessReference(string[] t, Matrix4x4 parent, int color, bool inverted, bool certified, bool lowRes, bool inStud, int depth)
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

            ProcessFile(file, world, color, childInverted, certified, lowRes || isStud, inStud || isStud, depth + 1);
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
                _triangleColors.Add(color);

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
            if (!File.Exists(path))
                return colors;

            foreach (string line in File.ReadAllLines(path))
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
                    data.AntiStuds.Add(ToUnity(new Vector3(x, _bodyMax.Y, z)));
            }
        }

        static float[] GridAlongAxis(float min, float max, float? alignTo)
        {
            int count = Math.Max(1, (int)Math.Round((max - min) / StudPitchLdu));
            float start = (min + max) * 0.5f - (count - 1) * StudPitchLdu * 0.5f;

            if (alignTo.HasValue)
            {
                float offset = alignTo.Value - start;
                offset -= (float)Math.Round(offset / StudPitchLdu) * StudPitchLdu;
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
