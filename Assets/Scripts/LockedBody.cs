using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A rigid group of locked pieces collapsed into one object, so a big locked model costs the headset a
/// handful of objects instead of thousands of bricks. It's carried by a Brick that has no part of its own:
/// the body supplies its shape (a few combined meshes), its grab boxes, and its connection points (only the
/// ones still exposed). A locked model becomes one body per rigid piece: it's split wherever pieces meet at
/// a hinge or other joint, so those can still be posed. Unlocking turns pieces back into real bricks.
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
            world.rotation = carrier.rotation * piece.rotation;
            result.Add(world);
        }
        return result;
    }

    // ---------- Building ----------

    struct Link
    {
        public bool rigid;
        public Vector3 point, axis; // the line a joint turns on
    }

    /// <summary>
    /// Sorts pieces (posed in the room) into rigid groups and makes a body for each. Each body comes with
    /// the pose its carrier brick should take.
    /// </summary>
    public static List<(LockedBody body, Vector3 position, Quaternion rotation)> Build(
        List<Piece> worldPieces, Material opaque, Material transparent)
    {
        var result = new List<(LockedBody, Vector3, Quaternion)>();
        int count = worldPieces.Count;
        if (count == 0)
            return result;

        // 1. Every piece's connection points, in the room, in a grid so neighbors are found quickly.
        var grid = new ConnectorGrid();
        for (int i = 0; i < count; i++)
        {
            Piece piece = worldPieces[i];
            foreach (Connector local in Brick.ConnectorsOf(piece.part))
                grid.Add(local.Moved(piece.position, piece.rotation), null, false, i);
        }

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
                }
                else if (!OnSameLine(link.point, link.axis, theirs.connector.Start, theirs.connector.axis))
                {
                    link.rigid = true;
                }
                links[key] = link;
            }
        }

        // 3. Rigid groups. Then groups joined by joints on two different lines are rigid too (a door on
        //    two hinges that don't line up doesn't swing); repeat until nothing more merges.
        int[] group = new int[count];
        for (int i = 0; i < count; i++)
            group[i] = i;
        int Find(int i)
        {
            while (group[i] != i)
                i = group[i] = group[group[i]];
            return i;
        }
        foreach (KeyValuePair<(int, int), Link> pair in links)
        {
            if (pair.Value.rigid)
                group[Find(pair.Key.Item1)] = Find(pair.Key.Item2);
        }
        bool merged = true;
        while (merged)
        {
            merged = false;
            var hinges = new Dictionary<(int, int), Link>();
            foreach (KeyValuePair<(int, int), Link> pair in links)
            {
                int a = Find(pair.Key.Item1), b = Find(pair.Key.Item2);
                if (a == b)
                    continue;
                var key = a < b ? (a, b) : (b, a);
                if (!hinges.TryGetValue(key, out Link first))
                {
                    hinges[key] = pair.Value;
                }
                else if (!OnSameLine(first.point, first.axis, pair.Value.point, pair.Value.axis))
                {
                    group[a] = b;
                    merged = true;
                    break; // groups changed: start the pass again
                }
            }
        }

        // 4. Which connection points are used up inside their own group. A point plugged end to end into
        //    another piece of the group is hidden for good (a stud under a brick). One that's only partly
        //    covered stays (an axle with room left on it), and so do the points covering it, so the game
        //    still knows which stretch of it is taken.
        int entries = grid.Count;
        var full = new bool[entries];
        var partly = new bool[entries];
        var inside = new List<(int, int)>();
        foreach ((int e, int f) in mates)
        {
            if (Find(grid[e].tag) != Find(grid[f].tag))
                continue; // a joint between two bodies: both keep their point
            inside.Add((e, f));
            Mark(grid[e].connector, grid[f].connector, e);
            Mark(grid[f].connector, grid[e].connector, f);
        }
        void Mark(in Connector mine, in Connector theirs, int index)
        {
            (float from, float to) span = Connections.SpanAlong(mine, theirs);
            if (mine.kind == Connector.Kind.Generic || (span.from <= 0.0002f && span.to >= mine.length - 0.0002f))
                full[index] = true;
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
            Quaternion facing = first.rotation;
            Quaternion toLocal = Quaternion.Inverse(facing);

            var body = new LockedBody();
            var usedFully = new List<bool>();
            var usedPartly = new List<bool>();
            var meshParts = new Dictionary<Vector3Int, List<(Mesh, Material[], Matrix4x4)>>();
            var boxes = new Dictionary<Vector3Int, Bounds>();
            bool anyBounds = false;

            foreach (int index in list)
            {
                Piece piece = worldPieces[index];
                Piece local = piece;
                local.position = toLocal * (piece.position - origin);
                local.rotation = toLocal * piece.rotation;
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

                Mesh mesh = piece.part.mesh;
                if (mesh != null)
                {
                    Vector3Int cell = Cell(local.position, MeshCell);
                    if (!meshParts.TryGetValue(cell, out List<(Mesh, Material[], Matrix4x4)> parts))
                        meshParts[cell] = parts = new List<(Mesh, Material[], Matrix4x4)>();
                    parts.Add((mesh, BrickPartData.MaterialsFor(mesh, piece.part.submeshColors, piece.color, opaque, transparent),
                               Matrix4x4.TRS(local.position, local.rotation, Vector3.one)));
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
            }

            body.fullyUsedInside = usedFully.ToArray();
            body.partlyUsedInside = usedPartly.ToArray();
            body.grabBoxes.AddRange(boxes.Values);

            var materials = new List<Material>();
            foreach (List<(Mesh, Material[], Matrix4x4)> parts in meshParts.Values)
            {
                Mesh combined = BrickMerger.Combine(parts, materials);
                if (combined == null)
                    continue;
                body.meshes.Add(combined);
                body.chunks.Add((combined, materials.ToArray()));
            }

            result.Add((body, origin, facing));
        }
        return result;
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
