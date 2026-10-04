using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;

/// <summary>
/// Wrench-style wrist inventory. Let go of a brick (or a whole model) next to your other wrist and it
/// shrinks onto that forearm. Identical items stack in one slot with a count (x3); different items
/// line up along the arm. Grab an item to take one back out at full size.
/// The real bricks are stored (shrunk), so groups stay intact.
/// </summary>
public class WristInventory : MonoBehaviour
{
    [Tooltip("Where the wrist is, relative to the hand/controller. Let go within the radius to store.")]
    public Vector3 depositOffset = new Vector3(0f, 0.02f, -0.08f);
    public float depositRadius = 0.08f;

    [Tooltip("First slot, relative to the hand/controller; later slots go further up the arm.")]
    public Vector3 firstSlotOffset = new Vector3(0f, 0.04f, -0.10f);
    public Vector3 slotStep = new Vector3(0f, 0f, -0.05f);

    [Tooltip("Stored items are shrunk to fit this size (meters). Small parts aren't enlarged.")]
    public float slotSize = 0.04f;
    [Min(1)] public int maxSlotsPerWrist = 6;

    public Material ghostMaterial;

    static WristInventory _instance;

    /// <summary>One stored copy: the brick that was held, plus the bricks attached to it.</summary>
    class Item
    {
        public Brick root;
        public List<Brick> members;
    }

    class Slot
    {
        public string signature;
        public Transform anchor;
        public TextMeshPro countLabel;
        public readonly List<Item> items = new List<Item>();
    }

    class Wrist
    {
        public Transform hand;
        public Transform root;
        public readonly List<Slot> slots = new List<Slot>();
    }

    /// <summary>A stored stack, for saving: which wrist, how many, and the bricks of one copy.</summary>
    public struct StackRecord
    {
        public int hand;
        public int count;
        public List<Brick> members; // root first
    }

    readonly Wrist[] _wrists = new Wrist[2];
    GameObject _ghost;

    void Awake()
    {
        _instance = this;
    }

    void Start()
    {
        OVRCameraRig rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig == null)
        {
            Debug.LogWarning("WristInventory: no OVRCameraRig found.", this);
            enabled = false;
            return;
        }

        _wrists[0] = CreateWrist(rig.leftHandAnchor, "Left");
        _wrists[1] = CreateWrist(rig.rightHandAnchor, "Right");

        _ghost = new GameObject("Wrist Ghost");
        _ghost.AddComponent<MeshFilter>().sharedMesh = MeshBuilder.Box(Vector3.one * slotSize * 1.2f);
        var renderer = _ghost.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = ghostMaterial != null ? ghostMaterial : MenuStyle.Material;
        MenuStyle.Tint(renderer, new Color(0.6f, 0.9f, 1f, 0.35f));
        _ghost.SetActive(false);
    }

    static Wrist CreateWrist(Transform hand, string side)
    {
        var root = new GameObject($"Wrist Inventory ({side})").transform;
        root.SetParent(hand, false);
        return new Wrist { hand = hand, root = root };
    }

    // ---------- Hands ----------

    /// <summary>Which hand is nearest the point: 0 = left, 1 = right, -1 if unknown.</summary>
    public static int ClosestHand(Vector3 point)
    {
        if (_instance == null || _instance._wrists[0] == null)
            return -1;
        float left = (_instance._wrists[0].hand.position - point).sqrMagnitude;
        float right = (_instance._wrists[1].hand.position - point).sqrMagnitude;
        return left <= right ? 0 : 1;
    }

    // The wrist a held brick would be stored on if let go now: the one opposite the holding hand, if close enough.
    Wrist TargetWrist(Brick held)
    {
        if (held.HeldHand < 0 || _wrists[0] == null)
            return null;

        Wrist wrist = _wrists[1 - held.HeldHand];
        Vector3 center = held.transform.TransformPoint(held.BodyBounds.center);
        Vector3 deposit = wrist.hand.TransformPoint(depositOffset);
        return (center - deposit).sqrMagnitude < depositRadius * depositRadius ? wrist : null;
    }

    public static bool IsOverWrist(Brick held) => _instance != null && _instance.enabled && _instance.TargetWrist(held) != null;

    // ---------- Storing ----------

    /// <summary>Stores the released brick (and its group) on the nearby wrist. False if not near a wrist.</summary>
    public static bool TryStore(Brick released)
    {
        if (_instance == null || !_instance.enabled)
            return false;

        Wrist wrist = _instance.TargetWrist(released);
        if (wrist == null)
            return false;

        var members = new List<Brick> { released };
        members.AddRange(released.CarriedBricks);
        string signature = Signature(released, members);

        Slot slot = wrist.slots.Find(s => s.signature == signature);
        if (slot == null && wrist.slots.Count >= _instance.maxSlotsPerWrist)
        {
            Sandbox.Message("That wrist is full");
            return false;
        }

        _instance.Store(wrist, released, released.DetachGroupForStorage(), signature);
        return true;
    }

    void Store(Wrist wrist, Brick root, List<Brick> members, string signature)
    {
        Slot slot = wrist.slots.Find(s => s.signature == signature) ?? CreateSlot(wrist, signature);

        Bounds bounds = GroupBounds(root, members);
        foreach (Brick member in members)
        {
            member.SetStored(true);
            if (member != root)
                SetCollidersEnabled(member, false);
        }

        // The root's collider covers the whole group, so you can grab the stored item anywhere.
        BoxCollider box = root.GetComponent<BoxCollider>();
        if (box != null)
        {
            box.center = bounds.center;
            box.size = bounds.size;
        }

        float largest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.001f);
        float scale = Mathf.Min(1f, slotSize / largest);
        Transform t = root.transform;
        t.SetParent(slot.anchor, false);
        t.localScale = Vector3.one * scale;
        t.localRotation = Quaternion.identity;
        t.localPosition = -bounds.center * scale;

        slot.items.Add(new Item { root = root, members = members });
        RefreshSlot(slot);
    }

    Slot CreateSlot(Wrist wrist, string signature)
    {
        var anchor = new GameObject("Slot").transform;
        anchor.SetParent(wrist.root, false);

        TextMeshPro label = MenuStyle.CreateText(anchor, "", new Vector3(0f, -slotSize * 0.75f, 0f), new Vector2(0.03f, 0.012f));

        var slot = new Slot { signature = signature, anchor = anchor, countLabel = label };
        wrist.slots.Add(slot);
        LayOutSlots(wrist);
        return slot;
    }

    void LayOutSlots(Wrist wrist)
    {
        for (int i = 0; i < wrist.slots.Count; i++)
            wrist.slots[i].anchor.localPosition = firstSlotOffset + slotStep * i;
    }

    // Only the top copy in a stack is shown (and grabbable); the count shows how many there are.
    static void RefreshSlot(Slot slot)
    {
        for (int i = 0; i < slot.items.Count; i++)
            slot.items[i].root.gameObject.SetActive(i == slot.items.Count - 1);
        slot.countLabel.text = slot.items.Count > 1 ? $"x{slot.items.Count}" : "";
    }

    // ---------- Taking out ----------

    /// <summary>Called when a stored item is grabbed: restores it to full size in the hand.</summary>
    public static bool TakeOut(Brick root)
    {
        if (_instance == null)
            return false;

        foreach (Wrist wrist in _instance._wrists)
        {
            if (wrist == null)
                continue;
            foreach (Slot slot in wrist.slots)
            {
                Item item = slot.items.Find(i => i.root == root);
                if (item == null)
                    continue;

                slot.items.Remove(item);
                Restore(item);

                if (slot.items.Count == 0)
                {
                    wrist.slots.Remove(slot);
                    Destroy(slot.anchor.gameObject);
                    _instance.LayOutSlots(wrist);
                }
                else
                {
                    RefreshSlot(slot);
                }
                return true;
            }
        }
        return false;
    }

    static void Restore(Item item)
    {
        Transform t = item.root.transform;
        t.SetParent(null, worldPositionStays: true);
        t.localScale = Vector3.one; // the attached bricks are children, so they grow back with it

        foreach (Brick member in item.members)
        {
            if (member != item.root)
            {
                member.transform.SetParent(null, worldPositionStays: true);
                SetCollidersEnabled(member, true);
            }
            member.SetStored(false);
        }
        item.root.Rebuild(); // puts the root's collider back to just its own body
    }

    static void SetCollidersEnabled(Brick brick, bool enabled)
    {
        foreach (Collider collider in brick.GetComponents<Collider>())
            collider.enabled = enabled;
    }

    // ---------- Ghost and labels ----------

    void Update()
    {
        Vector3? ghostPosition = null;
        Quaternion ghostRotation = Quaternion.identity;

        foreach (Brick held in Brick.Held)
        {
            Wrist wrist = TargetWrist(held);
            if (wrist == null)
                continue;

            // Show where it would go: its matching stack, or the next empty slot.
            var members = new List<Brick> { held };
            members.AddRange(held.CarriedBricks);
            string signature = Signature(held, members);
            int index = wrist.slots.FindIndex(s => s.signature == signature);
            if (index < 0)
                index = wrist.slots.Count;

            ghostPosition = wrist.root.TransformPoint(firstSlotOffset + slotStep * index);
            ghostRotation = wrist.root.rotation;
            break;
        }

        if (_ghost != null)
        {
            _ghost.SetActive(ghostPosition.HasValue);
            if (ghostPosition.HasValue)
                _ghost.transform.SetPositionAndRotation(ghostPosition.Value, ghostRotation);
        }
    }

    // Keep the "x3" labels facing you.
    void LateUpdate()
    {
        Camera head = Camera.main;
        if (head == null)
            return;
        foreach (Wrist wrist in _wrists)
        {
            if (wrist == null)
                continue;
            foreach (Slot slot in wrist.slots)
            {
                Transform label = slot.countLabel.transform;
                label.rotation = Quaternion.LookRotation(label.position - head.transform.position, Vector3.up);
            }
        }
    }

    // ---------- Helpers ----------

    // Identifies identical items: same parts, colors, and arrangement relative to the held brick.
    static string Signature(Brick root, List<Brick> members)
    {
        var lines = new List<string>();
        foreach (Brick member in members)
        {
            Vector3 p = root.transform.InverseTransformPoint(member.transform.position) / 0.0004f; // in LDraw units
            Quaternion r = Quaternion.Inverse(root.transform.rotation) * member.transform.rotation;
            Vector3 e = r.eulerAngles;
            lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2:0},{3:0},{4:0}:{5:0},{6:0},{7:0}",
                member.part != null ? member.part.partId : $"{member.studsX}x{member.studsZ}x{member.heightInPlates}",
                member.colorCode, p.x, p.y, p.z, Mathf.Round(e.x) % 360f, Mathf.Round(e.y) % 360f, Mathf.Round(e.z) % 360f));
        }
        lines.Sort();
        return string.Join("|", lines);
    }

    // Bounds of the whole group (bodies only), in the root brick's local space.
    static Bounds GroupBounds(Brick root, List<Brick> members)
    {
        var bounds = new Bounds();
        bool first = true;
        foreach (Brick member in members)
        {
            Bounds b = member.BodyBounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = new Vector3(
                    (corner & 1) == 0 ? b.min.x : b.max.x,
                    (corner & 2) == 0 ? b.min.y : b.max.y,
                    (corner & 4) == 0 ? b.min.z : b.max.z);
                Vector3 p = root.transform.InverseTransformPoint(member.transform.TransformPoint(local));
                if (first)
                {
                    bounds = new Bounds(p, Vector3.zero);
                    first = false;
                }
                else
                {
                    bounds.Encapsulate(p);
                }
            }
        }
        return bounds;
    }

    // ---------- Saving and loading ----------

    /// <summary>Every stack on both wrists, for saving.</summary>
    public static List<StackRecord> Export()
    {
        var records = new List<StackRecord>();
        if (_instance == null)
            return records;

        for (int hand = 0; hand < 2; hand++)
        {
            Wrist wrist = _instance._wrists[hand];
            if (wrist == null)
                continue;
            foreach (Slot slot in wrist.slots)
            {
                if (slot.items.Count == 0)
                    continue;
                records.Add(new StackRecord
                {
                    hand = hand,
                    count = slot.items.Count,
                    members = slot.items[slot.items.Count - 1].members,
                });
            }
        }
        return records;
    }

    /// <summary>
    /// Adds loaded bricks to a wrist. They're given a couple of frames at full size first: the grab system
    /// remembers an object's size when it starts up, and it must remember full size, not the shrunk size.
    /// </summary>
    public static void AddLoaded(int hand, List<Brick> members)
    {
        if (_instance == null || members.Count == 0)
            return;
        _instance.StartCoroutine(_instance.AddLoadedAfterStartup(hand, members));
    }

    IEnumerator AddLoadedAfterStartup(int hand, List<Brick> members)
    {
        yield return null;
        yield return null;

        Wrist wrist = _wrists[Mathf.Clamp(hand, 0, 1)];
        if (wrist == null)
            yield break;

        Brick root = members[0];
        foreach (Brick member in members.Skip(1))
            member.transform.SetParent(root.transform, worldPositionStays: true);
        if (members.Count > 1)
            Brick.BondTogether(members); // a stored item always comes back out in one piece

        string signature = Signature(root, members);
        if (wrist.slots.Find(s => s.signature == signature) == null && wrist.slots.Count >= maxSlotsPerWrist)
            wrist = _wrists[1 - Mathf.Clamp(hand, 0, 1)]; // overflow onto the other wrist

        Store(wrist, root, members, signature);
    }
}
