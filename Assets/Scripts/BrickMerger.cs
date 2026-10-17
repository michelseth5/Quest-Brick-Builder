using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws bricks that are sitting still as a few combined meshes instead of one by one, so big models cost
/// far fewer draw calls. Every brick stays a real object (grabbing, snapping, saving and undo don't change);
/// only its own renderer is switched off while a combined mesh is drawing it.
///
/// Bricks are grouped into chunks: cubes of space within whatever they're parented to (nothing for bricks
/// resting in the room, the held brick for a model being carried, the build root for a set). A chunk's mesh
/// lives in that parent's space, so a carried model's chunks move with it. When a brick moves, changes, or
/// is grabbed, its chunk goes straight back to separate bricks, and is merged again once things settle.
/// </summary>
[DefaultExecutionOrder(30000)] // after the grab system and posing have moved bricks this frame
public class BrickMerger : MonoBehaviour
{
    const float CellSize = 0.128f;        // 16 studs
    const float QuietSeconds = 0.4f;      // a brick must sit still this long before it's merged
    const float RebuildDelay = 0.15f;     // a chunk waits this long after its last change
    const int VerticesPerFrame = 150000;  // merging budget, to avoid hitches

    class Tracked
    {
        public Brick brick;
        public MeshRenderer renderer;
        public MeshFilter filter;
        public Transform parent;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public float quietSince;
        public bool changed = true; // looks different (part, color) or just appeared
        public bool opaque;
        public Chunk chunk;
        public int index;           // in All
    }

    class Chunk
    {
        public (int, Vector3Int) key;
        public Transform parent;
        public bool hadParent;
        public GameObject gameObject;
        public MeshFilter filter;
        public MeshRenderer renderer;
        public Mesh mesh;
        public readonly List<Tracked> members = new List<Tracked>();
        public readonly List<Tracked> merged = new List<Tracked>(); // the members the current mesh draws
        public bool dirty;
        public float dirtySince;
    }

    static readonly List<Tracked> All = new List<Tracked>();
    static readonly Dictionary<Brick, Tracked> ByBrick = new Dictionary<Brick, Tracked>();
    static readonly Dictionary<(int, Vector3Int), Chunk> Chunks = new Dictionary<(int, Vector3Int), Chunk>();
    static readonly List<Chunk> ChunkList = new List<Chunk>();
    static BrickMerger s_runner;
    static bool s_wasEnabled = true;

    /// <summary>How many combined meshes are being drawn, and how many bricks they stand in for.</summary>
    public static (int chunks, int bricks) Stats
    {
        get
        {
            int chunks = 0, bricks = 0;
            foreach (Chunk chunk in ChunkList)
            {
                if (chunk.merged.Count == 0)
                    continue;
                chunks++;
                bricks += chunk.merged.Count;
            }
            return (chunks, bricks);
        }
    }

    // ---------- Called by Brick ----------

    public static void Register(Brick brick)
    {
        if (!Application.isPlaying || ByBrick.ContainsKey(brick))
            return;
        EnsureRunner();

        var tracked = new Tracked
        {
            brick = brick,
            renderer = brick.GetComponent<MeshRenderer>(),
            filter = brick.GetComponent<MeshFilter>(),
            quietSince = Time.unscaledTime,
            index = All.Count,
        };
        // A brick copied from a merged one starts with its renderer off.
        if (tracked.renderer != null)
            tracked.renderer.enabled = true;
        All.Add(tracked);
        ByBrick[brick] = tracked;
    }

    public static void Unregister(Brick brick)
    {
        if (!ByBrick.TryGetValue(brick, out Tracked tracked))
            return;
        Leave(tracked);
        if (tracked.renderer != null)
            tracked.renderer.enabled = true;

        ByBrick.Remove(brick);
        Tracked last = All[All.Count - 1];
        All[tracked.index] = last;
        last.index = tracked.index;
        All.RemoveAt(All.Count - 1);
    }

    /// <summary>The brick's shape or color changed, so any combined mesh showing it is out of date.</summary>
    public static void MarkChanged(Brick brick)
    {
        if (ByBrick.TryGetValue(brick, out Tracked tracked))
            tracked.changed = true;
    }

    // ---------- Each frame ----------

    static void EnsureRunner()
    {
        if (s_runner != null)
            return;
        var holder = new GameObject("Brick Merger");
        s_runner = holder.AddComponent<BrickMerger>();
    }

    void OnDestroy()
    {
        // Scene reload (Start Over): the chunk objects go with the scene.
        if (s_runner == this)
            s_runner = null;
        foreach (Chunk chunk in ChunkList)
        {
            foreach (Tracked member in chunk.members)
                member.chunk = null;
            if (chunk.mesh != null)
                Destroy(chunk.mesh);
        }
        ChunkList.Clear();
        Chunks.Clear();
    }

    void LateUpdate()
    {
        bool enabledNow = GameSettings.MergeModels;
        if (!enabledNow)
        {
            if (s_wasEnabled)
                UnmergeEverything();
            s_wasEnabled = false;
            return;
        }
        s_wasEnabled = true;

        float now = Time.unscaledTime;

        // 1. Which bricks moved or changed, and which have been still long enough to merge.
        for (int i = 0; i < All.Count; i++)
        {
            Tracked t = All[i];
            Brick brick = t.brick;
            Transform transform = brick.transform;

            Transform parent = transform.parent;
            Vector3 localPosition = transform.localPosition;
            Quaternion localRotation = transform.localRotation;
            bool moved = t.changed || parent != t.parent || localPosition != t.localPosition || localRotation != t.localRotation;
            if (moved)
            {
                if (t.changed)
                    t.opaque = IsOpaque(t.renderer);
                t.changed = false;
                t.parent = parent;
                t.localPosition = localPosition;
                t.localRotation = localRotation;
                t.quietSince = now;
            }

            if (moved || !CanMerge(t))
            {
                if (t.chunk != null)
                    Leave(t);
                continue;
            }
            if (t.chunk == null && now - t.quietSince >= QuietSeconds)
                Join(t, now);
        }

        // 2. Chunks whose parent was destroyed lose their object; forget them.
        for (int i = ChunkList.Count - 1; i >= 0; i--)
        {
            Chunk chunk = ChunkList[i];
            if (chunk.hadParent && chunk.parent == null)
                Drop(chunk);
        }

        // 3. Merge chunks that have settled, a limited amount per frame.
        int budget = VerticesPerFrame;
        foreach (Chunk chunk in ChunkList)
        {
            if (!chunk.dirty || now - chunk.dirtySince < RebuildDelay)
                continue;
            budget -= Rebuild(chunk);
            if (budget <= 0)
                break;
        }
    }

    static bool CanMerge(Tracked t)
    {
        Brick brick = t.brick;
        if (!brick.isActiveAndEnabled || brick.IsPreview || brick.IsStored || !t.opaque || Brick.Held.Contains(brick))
            return false;
        Mesh mesh = t.filter != null ? t.filter.sharedMesh : null;
        return t.renderer != null && mesh != null && mesh.isReadable;
    }

    // See-through bricks stay separate: merged into one mesh they couldn't be sorted against each other.
    static bool IsOpaque(MeshRenderer renderer)
    {
        if (renderer == null)
            return false;
        foreach (Material material in renderer.sharedMaterials)
        {
            if (material == null || material.color.a < 1f)
                return false;
        }
        return true;
    }

    // ---------- Chunks ----------

    static void Join(Tracked t, float now)
    {
        Vector3 p = t.localPosition; // within its parent (the room, for bricks with none)
        var cell = new Vector3Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize), Mathf.FloorToInt(p.z / CellSize));
        var key = (t.parent != null ? t.parent.GetHashCode() : 0, cell);

        if (!Chunks.TryGetValue(key, out Chunk chunk))
        {
            chunk = new Chunk { key = key, parent = t.parent, hadParent = t.parent != null };
            Chunks[key] = chunk;
            ChunkList.Add(chunk);
        }
        chunk.members.Add(t);
        t.chunk = chunk;
        // The newcomer keeps drawing itself until the chunk's mesh is rebuilt with it included.
        chunk.dirty = true;
        chunk.dirtySince = now;
    }

    static void Leave(Tracked t)
    {
        Chunk chunk = t.chunk;
        if (chunk == null)
            return;
        t.chunk = null;
        chunk.members.Remove(t);
        if (t.renderer != null)
            t.renderer.enabled = true;

        // The combined mesh still shows this brick where it was: go back to separate bricks right away.
        if (chunk.merged.Contains(t))
            Unmerge(chunk);

        if (chunk.members.Count == 0)
        {
            Drop(chunk);
        }
        else
        {
            chunk.dirty = true;
            chunk.dirtySince = Time.unscaledTime;
        }
    }

    static void Unmerge(Chunk chunk)
    {
        foreach (Tracked member in chunk.merged)
        {
            if (member.renderer != null)
                member.renderer.enabled = true;
        }
        chunk.merged.Clear();
        if (chunk.renderer != null)
            chunk.renderer.enabled = false;
    }

    static void Drop(Chunk chunk)
    {
        Unmerge(chunk);
        foreach (Tracked member in chunk.members)
            member.chunk = null;
        chunk.members.Clear();
        if (chunk.gameObject != null)
            Destroy(chunk.gameObject);
        if (chunk.mesh != null)
            Destroy(chunk.mesh);
        Chunks.Remove(chunk.key);
        ChunkList.Remove(chunk);
    }

    static void UnmergeEverything()
    {
        for (int i = ChunkList.Count - 1; i >= 0; i--)
            Drop(ChunkList[i]);
    }

    static readonly Dictionary<Material, List<CombineInstance>> ByMaterial = new Dictionary<Material, List<CombineInstance>>();
    static readonly List<CombineInstance> PerMaterial = new List<CombineInstance>();
    static readonly List<Material> MaterialList = new List<Material>();

    // Builds the chunk's combined mesh (one submesh per material) and switches its bricks' own renderers off.
    // Returns how many vertices were merged.
    static int Rebuild(Chunk chunk)
    {
        chunk.dirty = false;
        Unmerge(chunk);
        if (chunk.members.Count < 2)
            return 0; // nothing to gain

        Matrix4x4 toParent = chunk.parent != null ? chunk.parent.worldToLocalMatrix : Matrix4x4.identity;
        foreach (List<CombineInstance> list in ByMaterial.Values)
            list.Clear();

        foreach (Tracked member in chunk.members)
        {
            Mesh mesh = member.filter.sharedMesh;
            Material[] materials = member.renderer.sharedMaterials;
            Matrix4x4 placed = toParent * member.brick.transform.localToWorldMatrix;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                Material material = materials[Mathf.Min(s, materials.Length - 1)];
                if (!ByMaterial.TryGetValue(material, out List<CombineInstance> pieces))
                    ByMaterial[material] = pieces = new List<CombineInstance>();
                pieces.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = placed });
            }
        }

        PerMaterial.Clear();
        MaterialList.Clear();
        int vertices = 0;
        foreach (KeyValuePair<Material, List<CombineInstance>> pair in ByMaterial)
        {
            if (pair.Value.Count == 0)
                continue;
            var part = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            part.CombineMeshes(pair.Value.ToArray(), mergeSubMeshes: true, useMatrices: true);
            vertices += part.vertexCount;
            PerMaterial.Add(new CombineInstance { mesh = part, transform = Matrix4x4.identity });
            MaterialList.Add(pair.Key);
        }

        if (chunk.mesh != null)
            Destroy(chunk.mesh);
        chunk.mesh = new Mesh { name = "Merged Bricks", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        chunk.mesh.CombineMeshes(PerMaterial.ToArray(), mergeSubMeshes: false, useMatrices: false);
        foreach (CombineInstance piece in PerMaterial)
            Destroy(piece.mesh);
        chunk.mesh.UploadMeshData(markNoLongerReadable: true); // frees the copy kept in main memory

        if (chunk.gameObject == null)
        {
            chunk.gameObject = new GameObject("Merged Bricks");
            chunk.gameObject.transform.SetParent(chunk.parent, worldPositionStays: false);
            chunk.filter = chunk.gameObject.AddComponent<MeshFilter>();
            chunk.renderer = chunk.gameObject.AddComponent<MeshRenderer>();
            chunk.renderer.shadowCastingMode = chunk.members[0].renderer.shadowCastingMode;
            chunk.renderer.receiveShadows = chunk.members[0].renderer.receiveShadows;
            chunk.gameObject.layer = chunk.members[0].brick.gameObject.layer;
        }
        chunk.filter.sharedMesh = chunk.mesh;
        chunk.renderer.sharedMaterials = MaterialList.ToArray();
        chunk.renderer.enabled = true;

        foreach (Tracked member in chunk.members)
        {
            member.renderer.enabled = false;
            chunk.merged.Add(member);
        }
        return vertices;
    }

    /// <summary>
    /// One mesh out of many placed meshes, with a submesh per material (returned in materialsOut).
    /// The result is uploaded to the graphics card and can't be read back. Null if there was nothing to combine.
    /// </summary>
    public static Mesh Combine(List<(Mesh mesh, Material[] materials, Matrix4x4 matrix)> parts, List<Material> materialsOut)
    {
        var byMaterial = new Dictionary<Material, List<CombineInstance>>();
        foreach ((Mesh mesh, Material[] materials, Matrix4x4 matrix) in parts)
        {
            if (mesh == null || !mesh.isReadable || materials == null || materials.Length == 0)
                continue;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                Material material = materials[Mathf.Min(s, materials.Length - 1)];
                if (material == null || mesh.GetIndexCount(s) == 0)
                    continue;
                if (!byMaterial.TryGetValue(material, out List<CombineInstance> pieces))
                    byMaterial[material] = pieces = new List<CombineInstance>();
                pieces.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = matrix });
            }
        }

        materialsOut.Clear();
        if (byMaterial.Count == 0)
            return null;

        var perMaterial = new List<CombineInstance>();
        foreach (KeyValuePair<Material, List<CombineInstance>> pair in byMaterial)
        {
            var part = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            part.CombineMeshes(pair.Value.ToArray(), mergeSubMeshes: true, useMatrices: true);
            perMaterial.Add(new CombineInstance { mesh = part, transform = Matrix4x4.identity });
            materialsOut.Add(pair.Key);
        }
        var combined = new Mesh { name = "Merged Bricks", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        combined.CombineMeshes(perMaterial.ToArray(), mergeSubMeshes: false, useMatrices: false);
        foreach (CombineInstance piece in perMaterial)
            Destroy(piece.mesh);
        combined.UploadMeshData(markNoLongerReadable: true);
        return combined;
    }
}
