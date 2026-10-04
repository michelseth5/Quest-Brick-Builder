using UnityEngine;

/// <summary>
/// Spawns a row of bricks as children of this object when the app starts.
/// Put PlaceInFrontOfUser on the same object to bring the whole row in front of the player.
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

    [Tooltip("Gap between bricks in the row, in meters.")]
    public float spacing = 0.02f;

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

        float totalWidth = spacing * (bricks.Length - 1);
        foreach (Entry entry in bricks)
            totalWidth += entry.studsX * Brick.StudPitch;

        float x = -totalWidth * 0.5f;
        foreach (Entry entry in bricks)
        {
            float width = entry.studsX * Brick.StudPitch;

            Brick brick = Instantiate(brickPrefab, transform);
            brick.studsX = entry.studsX;
            brick.studsZ = entry.studsZ;
            brick.heightInPlates = entry.heightInPlates;
            brick.color = entry.color;
            brick.Rebuild();
            brick.transform.localPosition = new Vector3(x + width * 0.5f, 0f, 0f);
            brick.transform.localRotation = Quaternion.identity;

            x += width + spacing;
        }
    }
}
