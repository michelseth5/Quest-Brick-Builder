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
        public readonly List<int> Triangles = new List<int>();

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
        const float StudPitchLdu = 20f;
        const float SmoothingAngleDegrees = 35f;
        const int MaxDepth = 32;

        readonly string _root;
        readonly Dictionary<string, string[]> _fileCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        // Per-part working state, in LDraw coordinates (-Y up, LDU).
        readonly List<Vector3> _corners = new List<Vector3>();
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
            _studs.Clear();
            _missing.Clear();
            _hasBody = false;

            ProcessFile(fileName, Matrix4x4.Identity, inverted: false, allowBfc: true, lowRes: false, inStud: false, depth: 0);

            var data = new LDrawMeshData { Description = ReadDescription(lines) };
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

        void ProcessFile(string name, Matrix4x4 transform, bool inverted, bool allowBfc, bool lowRes, bool inStud, int depth)
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
                            ProcessReference(t, transform, inverted ^ invertNext, certified, lowRes, inStud, depth);
                        }
                        invertNext = false;
                        break;

                    case "3":
                        if (t.Length >= 11)
                            AddPolygon(transform, certified, ccw ^ inverted, inStud, P(t, 2), P(t, 5), P(t, 8));
                        break;

                    case "4":
                        if (t.Length >= 14)
                        {
                            Vector3 a = P(t, 2), b = P(t, 5), c = P(t, 8), d = P(t, 11);
                            bool front = ccw ^ inverted;
                            AddPolygon(transform, certified, front, inStud, a, b, c);
                            AddPolygon(transform, certified, front, inStud, a, c, d);
                        }
                        break;
                }
            }
        }

        void ProcessReference(string[] t, Matrix4x4 parent, bool inverted, bool certified, bool lowRes, bool inStud, int depth)
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

            ProcessFile(file, world, childInverted, certified, lowRes || isStud, inStud || isStud, depth + 1);
        }

        void AddPolygon(Matrix4x4 transform, bool certified, bool frontAsListed, bool inStud, Vector3 a, Vector3 b, Vector3 c)
        {
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
                data.Triangles.Add(index);
            }
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
