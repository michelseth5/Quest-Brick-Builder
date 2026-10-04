using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns rows of bricks as children of this object when the app starts.
/// If a part catalog is assigned, spawns one of each imported part; otherwise uses the simple Bricks list.
/// Put PlaceInFrontOfUser on the same object to bring the whole tray in front of the player.
/// </summary>
public class BrickTray : MonoBehaviour
{
    [System.Serializable]
    public struct Entry
    {
        [Min(1)] public int studsX;
        [Min(1)] public int studsZ;
        [Min(1)] public int heightInPlates;
        public Color color;
    }

    public Brick brickPrefab;

    [Tooltip("Imported LDraw parts (Assets/LDrawParts/PartCatalog). When set, one of each part is spawned.")]
    public BrickCatalog catalog;

    [Tooltip("Colors handed out to catalog parts in turn.")]
    public Color[] palette =
    {
        new Color(0.80f, 0.10f, 0.10f), // red
        new Color(0.10f, 0.35f, 0.80f), // blue
        new Color(0.95f, 0.80f, 0.10f), // yellow
        new Color(0.15f, 0.60f, 0.20f), // green
        new Color(0.95f, 0.95f, 0.95f), // white
        new Color(0.20f, 0.20f, 0.20f), // dark gray
    };

    [Tooltip("Gap between bricks, in meters.")]
    public float spacing = 0.02f;

    [Tooltip("Start a new row once a row gets this wide, in meters.")]
    public float maxRowWidth = 0.35f;

    [Tooltip("Used when no catalog is assigned.")]
    public Entry[] bricks =
    {
        new Entry { studsX = 2, studsZ = 4, heightInPlates = 3, color = new Color(0.80f, 0.10f, 0.10f) },
        new Entry { studsX = 2, studsZ = 4, heightInPlates = 3, color = new Color(0.10f, 0.35f, 0.80f) },
        new Entry { studsX = 2, studsZ = 2, heightInPlates = 3, color = new Color(0.95f, 0.80f, 0.10f) },
        new Entry { studsX = 1, studsZ = 4, heightInPlates = 3, color = new Color(0.15f, 0.60f, 0.20f) },
        new Entry { studsX = 2, studsZ = 6, heightInPlates = 1, color = new Color(0.95f, 0.95f, 0.95f) },
        new Entry { studsX = 4, studsZ = 4, heightInPlates = 1, color = new Color(0.20f, 0.20f, 0.20f) },
    };

    void Start()
    {
        if (brickPrefab == null)
        {
            Debug.LogWarning("BrickTray: no brick prefab assigned.", this);
            return;
        }

        var spawned = new List<Brick>();
        if (catalog != null && catalog.parts.Count > 0)
        {
            for (int i = 0; i < catalog.parts.Count; i++)
            {
                if (catalog.parts[i] == null)
                    continue;
                Brick brick = Spawn(palette.Length > 0 ? palette[i % palette.Length] : Color.white);
                brick.part = catalog.parts[i];
                brick.Rebuild();
                spawned.Add(brick);
            }
        }
        else
        {
            foreach (Entry entry in bricks)
            {
                Brick brick = Spawn(entry.color);
                brick.part = null;
                brick.studsX = entry.studsX;
                brick.studsZ = entry.studsZ;
                brick.heightInPlates = entry.heightInPlates;
                brick.Rebuild();
                spawned.Add(brick);
            }
        }

        LayOut(spawned);
    }

    Brick Spawn(Color color)
    {
        Brick brick = Instantiate(brickPrefab, transform);
        brick.color = color;
        brick.transform.localRotation = Quaternion.identity;
        return brick;
    }

    // Arranges bricks in centered rows, each brick resting on the tray's plane, rows going away from the player.
    void LayOut(List<Brick> spawned)
    {
        var rows = new List<List<Brick>>();
        var current = new List<Brick>();
        float rowWidth = 0f;
        foreach (Brick brick in spawned)
        {
            float width = brick.BodyBounds.size.x;
            if (current.Count > 0 && rowWidth + spacing + width > maxRowWidth)
            {
                rows.Add(current);
                current = new List<Brick>();
                rowWidth = 0f;
            }
            rowWidth += (current.Count > 0 ? spacing : 0f) + width;
            current.Add(brick);
        }
        if (current.Count > 0)
            rows.Add(current);

        float z = 0f;
        foreach (List<Brick> row in rows)
        {
            float totalWidth = spacing * (row.Count - 1);
            float depth = 0f;
            foreach (Brick brick in row)
            {
                totalWidth += brick.BodyBounds.size.x;
                depth = Mathf.Max(depth, brick.BodyBounds.size.z);
            }

            float x = -totalWidth * 0.5f;
            foreach (Brick brick in row)
            {
                Bounds b = brick.BodyBounds;
                brick.transform.localPosition = new Vector3(x - b.min.x, -b.min.y, z + depth * 0.5f - b.center.z);
                x += b.size.x + spacing;
            }
            z += depth + spacing;
        }
    }
}
