using System.Linq;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A locked model collapsed into one solid object, so it costs the headset one object instead of thousands
/// of bricks and can't be shifted or pulled apart. It's carried by a Brick that has no part of its own:
/// the body supplies its shape (a few combined meshes), its grab boxes, and its connection points (only the
/// ones still exposed). Joints inside it don't move; to pose something, do it before locking or unlock it.
/// Unlocking turns pieces back into real bricks.
/// </summary>
public class LockedBody
{
    public struct Piece
    {
        public BrickPartData part;
        public int colorCode;
        public Color color;
        public Vector3 position;    // in the room when passed to Build or returned by Explode; inside a body, in its space
        public Quaternion rotation;
        public int section;         // which lock it belongs to (see Brick.LockedSection)
        public bool fromSet;        // taken out of the Build It Yourself bin
        public object bond;         // a Brick.Bond, or a saved bond number while loading; null for none
        public int unit;            // which locked group it belongs to (0 = none yet: grouped by what it's joined to)
    }

    const float MeshCell = 0.128f; // one combined mesh per 16-stud cube
    const float GrabCell = 0.032f; // one grab box per 4-stud cube

    public readonly List<Piece> pieces = new List<Piece>();
    public readonly List<Connector> connectors = new List<Connector>();
    /// <summary>Per connector: plugged into another piece of this same body, wholly or along part of it.</summary>
    public bool[] fullyUsedInside, partlyUsedInside;
    public Bounds bounds;
    public readonly List<Bounds> grabBoxes = new List<Bounds>();
    public readonly List<Mesh> meshes = new List<Mesh>();
    public readonly List<(Mesh mesh, Material[] materials)> chunks = new List<(Mesh, Material[])>();
    public readonly HashSet<object> bonds = new HashSet<object>();
    public int triangles;
    public int newestSection;

    /// <summary>This body's locked group: pieces locked together stay one group until unlocked.</summary>
    public int unit;

    static int s_lastUnit;

    /// <summary>A group number not used yet.</summary>
    public static int NewUnit() => ++s_lastUnit;

    /// <summary>Keeps new group numbers clear of one that's in use (e.g. from a loaded save).</summary>
    public static void NoteUnit(int unit) => s_lastUnit = Mathf.Max(s_lastUnit, unit);

    public void Dispose()
    {
        foreach (Mesh mesh in meshes)
        {
            if (mesh != null)
                Object.Destroy(mesh);
        }
        meshes.Clear();
        chunks.Clear();
    }

    /// <summary>The body's pieces as they sit in the room, given the pose of the brick carrying it.</summary>
    public List<Piece> Explode(Transform carrier)
    {
        var result = new List<Piece>(pieces.Count);
        foreach (Piece piece in pieces)
        {
            Piece world = piece;
            world.position = carrier.TransformPoint(piece.position);
            // (Normalized every time: see Unit.)
            world.rotation = Unit(carrier.rotation * piece.rotation);
            result.Add(world);
        }
        return result;
    }

    // ---------- Building ----------

    struct Link
    {
        public bool rigid;
        public Vector3 point, axis; // the line a joint turns on
        public bool ball;           // a ball joint: turns around the point instead
    }

    /// <summary>
    /// Sorts pieces (posed in the room) into rigid groups and makes a body for each. Each body comes with
    /// the pose its carrier brick should take.
    /// </summary>
    public static List<(LockedBody body, Vector3 position, Quaternion rotation)> Build(
        List<Piece> worldPieces, Material opaque, Material transparent)
    {
        var result = new List<(LockedBody, Vector3, Quaternion)>();
        foreach (float _ in BuildSteps(worldPieces, opaque, transparent, result)) { }
        return result;
    }

    /// <summary>How long each stage of the last build took, for the load-time log.</summary>
    public static string LastTimes = "";

    /// <summary>
    /// Build, a bit at a time: yields how far along it is (0 to 1) after each stage and each body, so a
    /// caller can spread a big model over several frames and show progress. Bodies are added to `result`.
    /// </summary>
    public static IEnumerable<float> BuildSteps(List<Piece> worldPieces, Material opaque, Material transparent,
                                                List<(LockedBody body, Vector3 position, Quaternion rotation)> result)
    {
        var variants = new Dictionary<string, Mesh>();
        int count = worldPieces.Count;
        if (count == 0)
            yield break;

        // How long each stage takes (paused while the caller waits for the next frame): see LastTimes.
        var times = new Dictionary<string, double>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var partClock = new System.Diagnostics.Stopwatch();
        void Lap(string stage)
        {
            times[stage] = (times.TryGetValue(stage, out double soFar) ? soFar : 0) + clock.Elapsed.TotalMilliseconds;
            clock.Restart();
        }
        void AddPart(string stage)
        {
            partClock.Stop();
            times[stage] = (times.TryGetValue(stage, out double soFar) ? soFar : 0) + partClock.Elapsed.TotalMilliseconds;
            partClock.Reset();
        }

        // 1. Every piece's connection points, in the room, in a grid so neighbors are found quickly.
        var firstEntry = new int[count]; // where each piece's connectors start in the grid
        var grid = new ConnectorGrid();
        for (int i = 0; i < count; i++)
        {
            firstEntry[i] = grid.Count;
            Piece piece = worldPieces[i];
            foreach (Connector local in Brick.ConnectorsOf(piece.part))
                grid.Add(local.Moved(piece.position, piece.rotation), null, false, i);
        }

        Lap("connection grid");
        yield return 0.05f;
        clock.Restart();

        // 2. Which pieces are plugged into which, and whether each pairing is rigid or a joint (the same
        //    rules as posing: studs and axles-in-cross-holes are rigid; so are two connections on
        //    different lines; everything spinning on one line is a joint).
        var links = new Dictionary<(int, int), Link>();
        var mates = new List<(int, int)>(); // pairs of grid entries that are plugged together
        var found = new List<int>();
        for (int e = 0; e < grid.Count; e++)
        {
            ConnectorGrid.Entry mine = grid[e];
            grid.Query(mine.connector, 0.002f, found);
            foreach (int f in found)
            {
                if (f <= e)
                    continue;
                ConnectorGrid.Entry theirs = grid[f];
                if (theirs.tag == mine.tag || !Connections.AreMated(mine.connector, theirs.connector))
                    continue;
                mates.Add((e, f));

                var key = mine.tag < theirs.tag ? (mine.tag, theirs.tag) : (theirs.tag, mine.tag);
                bool known = links.TryGetValue(key, out Link link);
                if (Connections.QuarterTurns(mine.connector, theirs.connector) || mine.connector.kind == Connector.Kind.Generic)
                {
                    link.rigid = true;
                }
                else if (!known)
                {
                    link.point = theirs.connector.Start;
                    link.axis = theirs.connector.axis;
                    link.ball = mine.connector.kind == Connector.Kind.Ball;
                }
                else if (link.ball || mine.connector.kind == Connector.Kind.Ball
                         || !OnSameLine(link.point, link.axis, theirs.connector.Start, theirs.connector.axis))
                {
                    link.rigid = true;
                }
                links[key] = link;
            }
        }

        // 3. One body per locked group. A piece that already belongs to a group (`unit`) stays in it: groups
        //    are what was locked together, so two locked groups snapped onto each other stay two and can be
        //    taken apart again. Pieces with no group yet (old saves) are grouped by what they're joined to,
        //    by any connection, rigid or a joint, or by a bond. (Splitting models at hinges and pins so they
        //    could still be posed made two-handed grabs shift or tear them: Technic pins count as joints.)
        int[] group = new int[count];
        for (int i = 0; i < count; i++)
            group[i] = i;
        int Find(int i)
        {
            while (group[i] != i)
                i = group[i] = group[group[i]];
            return i;
        }
        var firstOfUnit = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            int unit = worldPieces[i].unit;
            if (unit == 0)
                continue;
            if (firstOfUnit.TryGetValue(unit, out int first))
                group[Find(i)] = Find(first);
            else
                firstOfUnit[unit] = i;
        }
        foreach (KeyValuePair<(int, int), Link> pair in links)
        {
            if (worldPieces[pair.Key.Item1].unit == 0 && worldPieces[pair.Key.Item2].unit == 0)
                group[Find(pair.Key.Item1)] = Find(pair.Key.Item2);
        }
        var bonded = new Dictionary<object, int>();
        for (int i = 0; i < count; i++)
        {
            object bond = worldPieces[i].bond;
            if (bond == null || worldPieces[i].unit != 0)
                continue;
            if (bonded.TryGetValue(bond, out int other))
                group[Find(i)] = Find(other);
            else
                bonded[bond] = i;
        }
        // 4. Which connection points are used up inside their own group. A point plugged end to end into
        //    another piece of the group is hidden for good (a stud under a brick). One that's only partly
        //    covered stays (an axle with room left on it), and so do the points covering it, so the game
        //    still knows which stretch of it is taken.
        int entries = grid.Count;
        var full = new bool[entries];
        var partly = new bool[entries];
        var inside = new List<(int, int)>();
        var hidden = new bool[entries]; // covered end to end by a piece you can't see through
        foreach ((int e, int f) in mates)
        {
            if (Find(grid[e].tag) != Find(grid[f].tag))
                continue; // a joint between two bodies: both keep their point
            inside.Add((e, f));
            Mark(grid[e].connector, grid[f].connector, e, f);
            Mark(grid[f].connector, grid[e].connector, f, e);
        }
        void Mark(in Connector mine, in Connector theirs, int index, int partner)
        {
            (float from, float to) span = Connections.SpanAlong(mine, theirs);
            if (mine.IsPoint || (span.from <= 0.0002f && span.to >= mine.length - 0.0002f))
            {
                full[index] = true;
                // Out of sight too, unless what covers it is see-through or open right through (an open stud).
                if (worldPieces[grid[partner].tag].color.a >= 1f && !(theirs.profile ?? "").Contains(" "))
                    hidden[index] = true;
            }
            else
                partly[index] = true;
        }
        var keep = new bool[entries];
        for (int e = 0; e < entries; e++)
            keep[e] = !full[e];
        foreach ((int e, int f) in inside)
        {
            if (!full[e] && partly[e])
                keep[f] = true;
            if (!full[f] && partly[f])
                keep[e] = true;
        }

        Lap("plugged-in pairs + groups");
        yield return 0.25f;
        clock.Restart();

        // 4b. Drift check: places where pieces aren't quite where they should be (see Drift).
        Drift.Clear();
        FindDrift(grid, mates, full, partly);

        // 5. A body per group.
        var members = new Dictionary<int, List<int>>();
        for (int i = 0; i < count; i++)
        {
            int root = Find(i);
            if (!members.TryGetValue(root, out List<int> list))
                members[root] = list = new List<int>();
            list.Add(i);
        }
        var entriesOf = new Dictionary<int, List<int>>(); // piece -> its grid entries
        for (int e = 0; e < entries; e++)
        {
            if (!keep[e])
                continue;
            int piece = grid[e].tag;
            if (!entriesOf.TryGetValue(piece, out List<int> list))
                entriesOf[piece] = list = new List<int>();
            list.Add(e);
        }

        Lap("drift check");
        yield return 0.3f;
        clock.Restart();
        int piecesDone = 0, piecesCombined = 0;
        foreach (List<int> list in members.Values)
        {
            // The body takes the pose of one of its pieces: one that stands the way most of them do, so the
            // body's "up" is the model's (it's used when setting a model down on a table).
            var ups = new Dictionary<Vector3Int, (int count, int first)>();
            foreach (int index in list)
            {
                Vector3 up = worldPieces[index].rotation * Vector3.up;
                var key = new Vector3Int(Mathf.RoundToInt(up.x * 8f), Mathf.RoundToInt(up.y * 8f), Mathf.RoundToInt(up.z * 8f));
                ups[key] = ups.TryGetValue(key, out (int count, int first) seen) ? (seen.count + 1, seen.first) : (1, index);
            }
            int chosen = list[0], most = 0;
            foreach ((int count, int first) entry in ups.Values)
            {
                if (entry.count > most)
                {
                    most = entry.count;
                    chosen = entry.first;
                }
            }
            Piece first = worldPieces[chosen];
            Vector3 origin = first.position;
            Quaternion facing = Unit(first.rotation);
            Quaternion toLocal = Quaternion.Inverse(facing);

            var body = new LockedBody();
            foreach (int index in list)
            {
                if (worldPieces[index].unit != 0)
                {
                    body.unit = worldPieces[index].unit;
                    break;
                }
            }
            if (body.unit == 0)
                body.unit = NewUnit();
            NoteUnit(body.unit);
            var usedFully = new List<bool>();
            var usedPartly = new List<bool>();
            var meshParts = new Dictionary<Vector3Int, List<(Mesh, Material[], Matrix4x4)>>();
            var boxes = new Dictionary<Vector3Int, Bounds>();
            bool anyBounds = false;

            foreach (int index in list)
            {
                Piece piece = worldPieces[index];
                Piece local = piece;
                local.unit = body.unit;
                local.position = toLocal * (piece.position - origin);
                local.rotation = Unit(toLocal * piece.rotation);
                body.pieces.Add(local);
                body.newestSection = Mathf.Max(body.newestSection, piece.section);
                if (piece.bond != null)
                    body.bonds.Add(piece.bond);

                if (entriesOf.TryGetValue(index, out List<int> kept))
                {
                    foreach (int e in kept)
                    {
                        body.connectors.Add(grid[e].connector.Moved(-(toLocal * origin), toLocal));
                        usedFully.Add(full[e]);
                        usedPartly.Add(partly[e] && !full[e]);
                    }
                }

                // Its mesh, without the studs and underside tubes that are covered up (see Stripped).
                Mesh mesh = piece.part.mesh;
                if (mesh != null && GameSettings.HideCoveredStuds && piece.color.a >= 1f)
                {
                    partClock.Start();
                    mesh = Stripped(piece.part, firstEntry[index], hidden, variants);
                    AddPart("hidden studs");
                }
                if (mesh != null)
                {
                    Vector3Int cell = Cell(local.position, MeshCell);
                    if (!meshParts.TryGetValue(cell, out List<(Mesh, Material[], Matrix4x4)> parts))
                        meshParts[cell] = parts = new List<(Mesh, Material[], Matrix4x4)>();
                    partClock.Start();
                    parts.Add((mesh, BrickPartData.MaterialsFor(mesh, piece.part.submeshColors, piece.color, opaque, transparent),
                               Matrix4x4.TRS(local.position, local.rotation, Vector3.one)));
                    AddPart("materials");
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        body.triangles += (int)(mesh.GetIndexCount(s) / 3);
                }

                // The piece's body box, as an upright box in the group's space.
                Bounds box = Reoriented(piece.part.bodyBounds, local.position, local.rotation);
                if (!anyBounds)
                {
                    body.bounds = box;
                    anyBounds = true;
                }
                else
                {
                    body.bounds.Encapsulate(box);
                }
                Vector3Int grabCell = Cell(box.center, GrabCell);
                if (boxes.TryGetValue(grabCell, out Bounds soFar))
                {
                    soFar.Encapsulate(box);
                    boxes[grabCell] = soFar;
                }
                else
                {
                    boxes[grabCell] = box;
                }

                // Every few pieces, a chance to show progress.
                if (++piecesDone % 32 == 0)
                {
                    Lap("pieces");
                    yield return 0.3f + 0.35f * piecesDone / count;
                    clock.Restart();
                }
            }

            body.fullyUsedInside = usedFully.ToArray();
            body.partlyUsedInside = usedPartly.ToArray();
            body.grabBoxes.AddRange(boxes.Values);
            Lap("pieces");

            var materials = new List<Material>();
            foreach (List<(Mesh, Material[], Matrix4x4)> parts in meshParts.Values)
            {
                Mesh combined = BrickMerger.Combine(parts, materials);
                piecesCombined += parts.Count;
                if (combined != null)
                {
                    body.meshes.Add(combined);
                    body.chunks.Add((combined, materials.ToArray()));
                }
                Lap("combining meshes");
                yield return 0.65f + 0.35f * piecesCombined / count;
                clock.Restart();
            }

            result.Add((body, origin, facing));
        }
        Lap("pieces");
        LastTimes = string.Join(", ", times.Select(t => $"{t.Key} {t.Value / 1000.0:0.0} s"));
        foreach (Mesh variant in variants.Values)
        {
            if (variant != null)
                Object.Destroy(variant); // only needed while the combined meshes were being made
        }
    }

    // ---------- Leaving out what can't be seen ----------

    // A part's mesh data, read once.
    class Source
    {
        public Vector3[] vertices, normals;
        public int[][] indices; // per submesh
    }

    static readonly Dictionary<Mesh, Source> Sources = new Dictionary<Mesh, Source>();

    /// <summary>
    /// The part's mesh without the studs and underside tubes that are hidden inside the model: a stud is left
    /// out when it's plugged, end to end, into a piece you can't see through; the tubes and posts underneath
    /// when every socket on that side is plugged that way (the underside is sealed). Most of a brick's
    /// triangles are in these. Returns the part's own mesh when nothing is hidden. Variants are shared between
    /// pieces hidden the same way (the cache is the caller's, to destroy when done).
    /// </summary>
    static Mesh Stripped(BrickPartData part, int firstEntry, bool[] hidden, Dictionary<string, Mesh> variants)
    {
        Mesh mesh = part.mesh;
        BrickPartData.Detail[] details = part.details;
        if (details == null || details.Length == 0 || !mesh.isReadable)
            return mesh;

        IReadOnlyList<Connector> connectors = Brick.ConnectorsOf(part);
        bool any = false;
        var key = new System.Text.StringBuilder(part.partId).Append(':');
        var drop = new bool[details.Length];
        for (int d = 0; d < details.Length; d++)
        {
            drop[d] = IsHidden(details[d], connectors, firstEntry, hidden);
            any |= drop[d];
            key.Append(drop[d] ? '1' : '0');
        }
        if (!any)
            return mesh;
        if (variants.TryGetValue(key.ToString(), out Mesh known))
            return known;

        if (!Sources.TryGetValue(mesh, out Source source))
        {
            source = new Source { vertices = mesh.vertices, normals = mesh.normals, indices = new int[mesh.subMeshCount][] };
            for (int s = 0; s < mesh.subMeshCount; s++)
                source.indices[s] = mesh.GetIndices(s);
            Sources[mesh] = source;
        }

        // Each submesh's indices minus the dropped stretches, keeping only the vertices still used.
        var remap = new int[source.vertices.Length];
        for (int i = 0; i < remap.Length; i++)
            remap[i] = -1;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var kept = new List<int>[source.indices.Length];
        for (int s = 0; s < source.indices.Length; s++)
        {
            int[] indices = source.indices[s];
            var skip = new bool[indices.Length];
            for (int d = 0; d < details.Length; d++)
            {
                if (!drop[d] || details[d].submesh != s)
                    continue;
                int end = Mathf.Min(indices.Length, details[d].start + details[d].count);
                for (int i = Mathf.Max(0, details[d].start); i < end; i++)
                    skip[i] = true;
            }

            kept[s] = new List<int>(indices.Length);
            for (int i = 0; i < indices.Length; i++)
            {
                if (skip[i])
                    continue;
                int vertex = indices[i];
                if (remap[vertex] < 0)
                {
                    remap[vertex] = vertices.Count;
                    vertices.Add(source.vertices[vertex]);
                    if (source.normals.Length == source.vertices.Length)
                        normals.Add(source.normals[vertex]);
                }
                kept[s].Add(remap[vertex]);
            }
        }

        var variant = new Mesh { name = mesh.name + " (covered)" };
        if (vertices.Count > 65535)
            variant.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        variant.SetVertices(vertices);
        if (normals.Count == vertices.Count)
            variant.SetNormals(normals);
        variant.subMeshCount = kept.Length;
        for (int s = 0; s < kept.Length; s++)
            variant.SetTriangles(kept[s], s);
        variant.RecalculateBounds();
        variants[key.ToString()] = variant;
        return variant;
    }

    // A stud (the detail sits where one of the part's studs starts): hidden if that stud is.
    // Anything else is a tube or post on the underside: hidden if every stud socket facing that way is.
    static bool IsHidden(in BrickPartData.Detail detail, IReadOnlyList<Connector> connectors, int firstEntry, bool[] hidden)
    {
        const float Near = 0.0006f;
        for (int k = 0; k < connectors.Count; k++)
        {
            Connector c = connectors[k];
            if (c.kind == Connector.Kind.Cylinder && c.male && IsStudSized(c.profile)
                && (c.Start - detail.position).sqrMagnitude < Near * Near && Vector3.Dot(c.axis, detail.up) > 0.9f)
                return hidden[firstEntry + k];
        }

        bool anySocket = false;
        for (int k = 0; k < connectors.Count; k++)
        {
            Connector c = connectors[k];
            if (c.kind != Connector.Kind.Cylinder || c.male || !IsStudSized(c.profile) || Mathf.Abs(Vector3.Dot(c.axis, detail.up)) < 0.9f)
                continue;
            if (!hidden[firstEntry + k])
                return false;
            anySocket = true;
        }
        return anySocket;
    }

    static bool IsStudSized(string profile) => profile != null && (profile.Contains("R6") || profile.Contains("S6"));

    // ---------- Drift check ----------

    /// <summary>
    /// Where the last Build found pieces not quite where they should be, in the room: a stud only partly
    /// pushed into its socket, or a stud or pin sitting near a matching hole without being in it. Exact
    /// snapping never leaves these; when they show up, something went in slightly wrong at that spot.
    /// </summary>
    public static readonly List<Vector3> Drift = new List<Vector3>();

    const float ShortOfHome = 0.0002f;   // a stud more than 0.5 LDU short of fully in
    const float NearMiss = 0.002f;       // within 2 mm of a hole it fits, but not in it

    static void FindDrift(ConnectorGrid grid, List<(int, int)> mates, bool[] full, bool[] partly)
    {
        // Studs plugged in but not all the way.
        var counted = new HashSet<int>();
        foreach ((int e, int f) in mates)
        {
            int stud = grid[e].connector.male ? e : f, socket = stud == e ? f : e;
            Connector s = grid[stud].connector, h = grid[socket].connector;
            if (!IsStud(s) || !IsStudSized(h.profile) || h.male || counted.Contains(stud))
                continue;
            (float from, float to) span = Connections.SpanAlong(s, h);
            float inside = Mathf.Min(span.to, s.length) - Mathf.Max(span.from, 0f);
            if (inside < s.length - ShortOfHome)
            {
                counted.Add(stud);
                Drift.Add(s.Start + s.axis * (s.length * 0.5f));
            }
        }

        // Studs and pins that aren't in anything, but nearly are.
        var found = new List<int>();
        for (int e = 0; e < grid.Count; e++)
        {
            Connector peg = grid[e].connector;
            if (full[e] || partly[e] || !peg.male || peg.kind != Connector.Kind.Cylinder || !(IsStud(peg) || IsPin(peg)))
                continue;
            grid.Query(peg, NearMiss, found);
            foreach (int f in found)
            {
                ConnectorGrid.Entry other = grid[f];
                if (other.tag == grid[e].tag || other.connector.male || full[f] || !Connections.Fit(peg, other.connector))
                    continue;
                if (NearlyIn(peg, other.connector))
                {
                    Drift.Add(peg.Start + peg.axis * (peg.length * 0.5f));
                    break;
                }
            }
        }
    }

    static bool IsStud(in Connector c) => c.kind == Connector.Kind.Cylinder && c.profile == "R6" && c.length < 0.0025f;
    // A Technic pin: both the pin and collar sizes, and at least a stud long (a round plate body is R10 R8, shorter).
    static bool IsPin(in Connector c) =>
        c.kind == Connector.Kind.Cylinder && c.profile != null && c.profile.Contains("R8") && c.profile.Contains("R6") && c.length >= 0.0064f;

    // Parallel and within NearMiss of plugging in: off to the side, or short of reaching, by up to that much.
    static bool NearlyIn(in Connector peg, in Connector hole)
    {
        if (Mathf.Abs(Vector3.Dot(peg.axis, hole.axis)) < 0.996f)
            return false;
        Vector3 u = hole.axis;
        Vector3 offset = peg.Start - hole.Start;
        if ((offset - Vector3.Dot(offset, u) * u).sqrMagnitude > NearMiss * NearMiss)
            return false;
        float a0 = Vector3.Dot(offset, u);
        float a1 = a0 + Vector3.Dot(peg.axis, u) * peg.length;
        float overlap = Mathf.Min(Mathf.Max(a0, a1), hole.length) - Mathf.Max(Mathf.Min(a0, a1), 0f);
        return overlap > -NearMiss;
    }

    /// <summary>
    /// A rotation scaled back to exactly unit length. Quaternion.Inverse is only right for unit rotations, and
    /// every lock and unlock converts each piece's rotation into the body's space and back, so a tiny rounding
    /// error roughly doubled each time: after a dozen or so cycles pieces came out visibly stretched and
    /// pushed out of place (Oct 14 save: rotations 2-16% oversized after repeated lock/unlock).
    /// </summary>
    public static Quaternion Unit(Quaternion q)
    {
        float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return length > 1e-6f ? new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length) : Quaternion.identity;
    }

    static Vector3Int Cell(Vector3 p, float size) =>
        new Vector3Int(Mathf.FloorToInt(p.x / size), Mathf.FloorToInt(p.y / size), Mathf.FloorToInt(p.z / size));

    // The upright box around a box that's been moved and turned.
    static Bounds Reoriented(Bounds box, Vector3 position, Quaternion rotation)
    {
        var result = new Bounds(position + rotation * box.center, Vector3.zero);
        Vector3 e = box.extents;
        for (int corner = 0; corner < 8; corner++)
        {
            var offset = new Vector3((corner & 1) == 0 ? -e.x : e.x, (corner & 2) == 0 ? -e.y : e.y, (corner & 4) == 0 ? -e.z : e.z);
            result.Encapsulate(position + rotation * (box.center + offset));
        }
        return result;
    }

    static bool OnSameLine(Vector3 p1, Vector3 d1, Vector3 p2, Vector3 d2)
    {
        if (Mathf.Abs(Vector3.Dot(d1, d2)) < 0.996f)
            return false;
        Vector3 offset = p2 - p1;
        return (offset - Vector3.Dot(offset, d1) * d1).sqrMagnitude < 0.0006f * 0.0006f;
    }
}
