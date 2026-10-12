using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// World-space connectors bucketed into small cubes, so snapping compares a connector only with the ones
/// near it instead of every connector on every nearby part. A connector is filed under every cube its
/// length passes through, so looking around a point or along another connector stays a small search however
/// long some shapes are (axles, bars, a 26-stud hose). Reused from frame to frame without allocating.
/// </summary>
public class ConnectorGrid
{
    const float CellSize = 0.016f; // two studs
    const float SampleStep = CellSize * 0.5f;

    public struct Entry
    {
        public Connector connector; // world space
        public Brick owner;
        public bool partlyUsed;     // free, but something already sits on part of it (e.g. an axle with a gear on it)
        public int tag;             // whatever the caller wants to remember (e.g. which of the owner's connectors this is)
    }

    readonly List<Entry> _items = new List<Entry>();
    readonly List<int> _seen = new List<int>(); // per item: the last query that returned it
    readonly Dictionary<Vector3Int, List<int>> _cells = new Dictionary<Vector3Int, List<int>>();
    readonly Stack<List<int>> _spare = new Stack<List<int>>();
    int _query;

    public int Count => _items.Count;
    public Entry this[int index] => _items[index];

    public void Clear()
    {
        foreach (List<int> list in _cells.Values)
        {
            list.Clear();
            _spare.Push(list);
        }
        _cells.Clear();
        _items.Clear();
        _seen.Clear();
    }

    public void Add(in Connector connector, Brick owner, bool partlyUsed = false, int tag = 0)
    {
        int index = _items.Count;
        _items.Add(new Entry { connector = connector, owner = owner, partlyUsed = partlyUsed, tag = tag });
        _seen.Add(0);

        // Every cube from one end of the shape to the other (plus where its position is, if that's its middle).
        Vector3 start = connector.Start;
        int steps = Mathf.Max(1, Mathf.CeilToInt(connector.length / SampleStep));
        Vector3Int last = default;
        for (int i = 0; i <= steps; i++)
        {
            Vector3Int cell = CellOf(start + connector.axis * (connector.length * i / steps));
            if (i > 0 && cell == last)
                continue;
            last = cell;
            File(cell, index);
        }
    }

    void File(Vector3Int cell, int index)
    {
        if (!_cells.TryGetValue(cell, out List<int> list))
        {
            list = _spare.Count > 0 ? _spare.Pop() : new List<int>();
            _cells[cell] = list;
        }
        if (list.Count == 0 || list[list.Count - 1] != index)
            list.Add(index);
    }

    /// <summary>Indexes of items that pass within radius of the point (fills results; may include a few farther ones).</summary>
    public void Query(Vector3 center, float radius, List<int> results)
    {
        results.Clear();
        if (_items.Count == 0)
            return;
        _query++;
        Collect(center, radius, results);
    }

    /// <summary>Indexes of items that pass within margin of any part of the given connector.</summary>
    public void Query(in Connector along, float margin, List<int> results)
    {
        results.Clear();
        if (_items.Count == 0)
            return;
        _query++;

        Vector3 start = along.Start;
        int steps = Mathf.Max(1, Mathf.CeilToInt(along.length / SampleStep));
        for (int i = 0; i <= steps; i++)
            Collect(start + along.axis * (along.length * i / steps), margin, results);
    }

    void Collect(Vector3 center, float radius, List<int> results)
    {
        Vector3Int min = CellOf(center - Vector3.one * radius);
        Vector3Int max = CellOf(center + Vector3.one * radius);
        for (int x = min.x; x <= max.x; x++)
        {
            for (int y = min.y; y <= max.y; y++)
            {
                for (int z = min.z; z <= max.z; z++)
                {
                    if (!_cells.TryGetValue(new Vector3Int(x, y, z), out List<int> list))
                        continue;
                    foreach (int index in list)
                    {
                        if (_seen[index] == _query)
                            continue; // already found through another cube
                        _seen[index] = _query;
                        results.Add(index);
                    }
                }
            }
        }
    }

    static Vector3Int CellOf(Vector3 p) =>
        new Vector3Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize), Mathf.FloorToInt(p.z / CellSize));
}
