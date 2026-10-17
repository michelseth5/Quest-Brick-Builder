using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// A real-scale brick. Its shape comes from an imported LDraw part, or is generated from a simple size.
/// When the player lets go of it, it snaps so one of its studs plugs into another brick (or one of the
/// other brick's studs plugs into it), or sets down on a real table or floor.
/// A trigger/pinch grab takes just this brick; a grip/whole-hand grab takes every brick connected to it.
/// The transform scale must stay at 1.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class Brick : MonoBehaviour
{
    // Real-world brick dimensions in meters (same proportions LDraw uses).
    public const float StudPitch = 0.008f;
    public const float PlateHeight = 0.0032f;
    const float StudRadius = 0.0024f;
    const float StudHeight = 0.0017f;
    const float SideGap = 0.0001f; // real bricks are slightly undersized so neighbors don't touch
    const int StudSegments = 16;

    static readonly List<Brick> AllBricks = new List<Brick>();
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly Collider[] OverlapResults = new Collider[32];

    [Tooltip("An imported LDraw part. When set, its shape and studs are used instead of the simple size below.")]
    public BrickPartData part;

    [Header("Simple brick (used when no part is set)")]
    [Min(1)] public int studsX = 2;
    [Min(1)] public int studsZ = 4;
    [Tooltip("3 for a brick, 1 for a plate.")]
    [Min(1)] public int heightInPlates = 3;

    public Color color = new Color(0.8f, 0.1f, 0.1f);
    [Tooltip("LDraw color code for the color (used when saving or matching sets).")]
    public int colorCode = 4;

    [Tooltip("Used instead of the normal material when the color is see-through.")]
    public Material transparentMaterial;

    [Header("Snapping")]
    [Tooltip("How far (in meters) a brick may be moved to plug it into a nearby connection.")]
    public float maxVerticalSnap = 0.008f;
    [Tooltip("How far the brick can be tilted relative to the other brick and still snap.")]
    [Range(0f, 90f)] public float maxTiltDegrees = 35f;
    [Tooltip("How close (in meters) the brick must be to a real table or floor to set down on it.")]
    public float maxSurfaceSnap = 0.03f;

    [Header("Preview")]
    [Tooltip("A transparent material that shows where the brick will land while it's held. Leave empty for no preview.")]
    public Material ghostMaterial;

    /// <summary>Bounds of the brick without its studs, in local space.</summary>
    public Bounds BodyBounds => _body;

    /// <summary>
    /// A display copy sitting in a menu. Previews are ignored by snapping until someone grabs one,
    /// which turns it into a normal brick.
    /// </summary>
    public bool IsPreview { get; set; }

    /// <summary>Raised when a menu preview is grabbed and becomes a real brick, so the menu can replace it.</summary>
    /// The bool is true for a grip (whole-group) grab, false for a trigger (single) grab.
    public static event System.Action<Brick, bool> PreviewTaken;

    /// <summary>Raised whenever a brick is picked up.</summary>
    public static event System.Action<Brick> Grabbed;

    /// <summary>Raised for each brick dropped into a trash can, just before it's deleted.</summary>
    public static event System.Action<Brick> Trashed;

    /// <summary>
    /// Bricks bonded into one model, even where they don't connect by studs (wheels on pins, parts on
    /// sideways studs, tools in clips). A grip grab takes the whole bond; a trigger grab pulls one brick out.
    /// </summary>
    public class Bond
    {
        public readonly HashSet<Brick> members = new HashSet<Brick>();
    }

    Bond _bond;

    /// <summary>The brick's bond, or null. Bricks sharing a bond move together.</summary>
    public Bond CurrentBond => _bond;

    public static void BondTogether(IEnumerable<Brick> bricks)
    {
        var bond = new Bond();
        foreach (Brick brick in bricks)
        {
            if (brick == null)
                continue;
            brick.LeaveBond();
            brick._bond = bond;
            bond.members.Add(brick);
        }
    }

    public void LeaveBond()
    {
        if (_bond == null)
            return;
        _bond.members.Remove(this);
        _bond = null;
    }

    /// <summary>Bricks currently held in a hand (only the brick actually grabbed, not what it carries).</summary>
    public static readonly List<Brick> Held = new List<Brick>();

    /// <summary>Every real brick placed in the world (not menu previews).</summary>
    public static List<Brick> AllPlaced()
    {
        var placed = new List<Brick>();
        foreach (Brick brick in AllBricks)
        {
            if (brick.IsBuildable)
                placed.Add(brick);
        }
        return placed;
    }

    /// <summary>Bricks this brick is carrying as part of a grabbed group.</summary>
    UndoHistory.Step _undoStep; // what this brick's current grab changed, for Undo

    bool CameFromMenu(UndoHistory.Step step) => UndoHistory.IsSpawnOf(step, this);

    // The held brick plus everything it's carrying, each relative to where it should go back to.
    UndoHistory.Step RecordGroupForUndo()
    {
        UndoHistory.Step step = UndoHistory.RecordMove(new[] { this });
        foreach ((Brick brick, Transform originalParent) in _carried)
            UndoHistory.AddPose(step, brick, originalParent);
        return step;
    }

    /// <summary>Tells listeners (like the Parts Bin) this brick is being thrown away.</summary>
    public void NotifyTrashed() => Trashed?.Invoke(this);

    /// <summary>Puts the brick back into a bond it used to belong to (or none).</summary>
    public void RejoinBond(Bond bond)
    {
        if (_bond == bond)
            return;
        LeaveBond();
        if (bond == null)
            return;
        _bond = bond;
        bond.members.Add(this);
    }

    /// <summary>Re-reads the held group's free connectors (e.g. after part of it was turned at a joint).</summary>
    public void RefreshGroupConnections()
    {
        if (_carried.Count == 0)
        {
            // Nothing left to carry: back to this brick's own connection points and preview.
            _groupConnectors.Clear();
            DestroyGroupGhost();
            return;
        }
        var group = new List<Brick> { this };
        group.AddRange(CarriedBricks);
        BuildGroupConnections(group);
    }

    /// <summary>This brick's connection points, in its local space.</summary>
    public IReadOnlyList<Connector> Connectors => _connectors;

    /// <summary>Every brick connected to this one, directly or through others (this one first).</summary>
    public List<Brick> ConnectedModel()
    {
        var model = new List<Brick> { this };
        for (int i = 0; i < model.Count; i++)
        {
            foreach (Brick other in AllBricks)
            {
                if (other.IsBuildable && !model.Contains(other) && model[i].IsConnectedTo(other))
                    model.Add(other);
            }
        }
        return model;
    }

    public IEnumerable<Brick> CarriedBricks
    {
        get
        {
            foreach ((Brick brick, Transform _) in _carried)
                yield return brick;
        }
    }

    // Baseplates anchor a model: grabbing a model by its grip doesn't pull the baseplate along.
    bool IsBaseplate => part != null && part.Category == "Baseplate";

    /// <summary>Shrunk down and sitting in a wrist inventory slot.</summary>
    public bool IsStored { get; private set; }

    /// <summary>Which hand last grabbed this brick: 0 = left, 1 = right, -1 = unknown.</summary>
    public int HeldHand { get; private set; } = -1;

    /// <summary>Placed into a set being built; can't be grabbed until the set is finished or exited.</summary>
    public bool IsLocked { get; private set; }

    public void SetLocked(bool locked)
    {
        IsLocked = locked;
        foreach (Collider collider in GetComponents<Collider>())
            collider.enabled = !locked;
    }

    // Bricks that can be built on: real, active, and not sitting in a menu, an inventory, or a set build.
    bool IsBuildable => isActiveAndEnabled && !IsPreview && !IsStored && !IsLocked;

    // Shape and connection points, in local space. Filled by Rebuild().
    Mesh _activeMesh;
    Mesh _generatedMesh;
    readonly List<Connector> _connectors = new List<Connector>();
    Bounds _body;

    bool _dirty = true;
    Articulation _articulation; // set while this brick is being turned around a joint
    Articulation _articulationTail; // just finished: applied once more the next frame
    Material _opaqueMaterial;
    PointableElement _grabbable;
    GameObject _ghost;
    readonly List<(Brick brick, Transform originalParent)> _carried = new List<(Brick, Transform)>();
    readonly HashSet<Brick> _carriedSet = new HashSet<Brick>();
    GameObject _groupGhost; // one combined preview for a whole carried group
    Mesh _groupGhostMesh;

    // While carrying a group: the group's unused connection points, in this brick's local space.
    readonly List<Connector> _groupConnectors = new List<Connector>();
    List<Connector> SnapConnectors => _carried.Count > 0 ? _groupConnectors : _locked != null ? _lockedFree : _connectors;

    // A locked body's connection points that aren't used up inside it (the rest only mark what's taken).
    readonly List<Connector> _lockedFree = new List<Connector>();

    void OnEnable()
    {
        AllBricks.Add(this);
        _dirty = true;

        if (Application.isPlaying)
        {
            _grabbable = GetComponent<PointableElement>();
            if (_grabbable != null)
                _grabbable.WhenPointerEventRaised += OnPointerEvent;
        }
        BrickMerger.Register(this);
    }

    void OnDisable()
    {
        AllBricks.Remove(this);
        StopFlash();
        BrickMerger.Unregister(this);
        Held.Remove(this);
        HideGhosts();
        PutDownStack();
        _articulation?.End();
        _articulation = null;
        if (_grabbable != null)
            _grabbable.WhenPointerEventRaised -= OnPointerEvent;
    }

    void OnValidate() => _dirty = true;

    void Update()
    {
        if (_dirty)
            Rebuild();
        if (Application.isPlaying)
            UpdateGhost();
    }

    // Posing runs after the grab system has moved the grabbed part this frame, and overrides it.
    void LateUpdate()
    {
        ApplyPin();
        _articulation?.Apply();
        if (_articulationTail != null)
        {
            _articulationTail.Apply();
            _articulationTail = null;
        }
    }

    void OnDestroy()
    {
        LeaveBond();
        if (_ghost != null)
            Destroy(_ghost);
        _locked?.Dispose();

        // Only the generated mesh belongs to this brick; imported part meshes are shared assets.
        if (_generatedMesh == null)
            return;
        if (Application.isPlaying)
            Destroy(_generatedMesh);
        else
            DestroyImmediate(_generatedMesh);
    }

    // ---------- Grabbing ----------

    void OnPointerEvent(PointerEvent evt)
    {
        if (evt.Type == PointerEventType.Select && _grabbable.SelectingPointsCount == 1)
        {
            // A locked brick can't be pulled off on its own: any grab takes its whole model. A locked group (a
            // locked body) is different: trigger takes that group alone, off whatever it's attached to.
            bool wholeGroupGrab = BrickGrabInput.IsWholeGroupGrab(evt) || (IsSectionLocked && !IsPreview && !IsLockedBody);
            bool fromMenu = IsPreview;
            if (IsPreview)
            {
                // Taken out of a menu: it's a real brick from now on.
                IsPreview = false;
                transform.SetParent(null, worldPositionStays: true);
                PreviewTaken?.Invoke(this, wholeGroupGrab);
            }

            // Is this brick part of a model that's in the other hand right now?
            Brick otherHand = fromMenu ? null : OtherHandHolding();

            // A locked piece of the model in the other hand doesn't come off or move: the grab does nothing. (Except
            // a trigger grab on a locked group, which takes that group off the model.)
            if (otherHand != null && IsSectionLocked && (wholeGroupGrab || !IsLockedBody))
            {
                _pinnedTo = otherHand;
                _pinnedPosition = transform.localPosition;
                _pinnedRotation = transform.localRotation;
                _pinFrames = 0;
                _undoStep = null;
                return;
            }

            if (!Held.Contains(this))
                Held.Add(this);
            HeldHand = WristInventory.ClosestHand(evt.Pose.position);

            // Taken off a wrist: grows back to full size, and always comes out as the whole stored group.
            bool fromInventory = IsStored && WristInventory.TakeOut(this);

            PutDownStack();

            // Gripping a part beyond a joint poses it (turns it around the joint) instead of lifting the model.
            if (wholeGroupGrab && !fromInventory && !fromMenu)
                _articulation = Articulation.TryBegin(this, evt.Pose.position);

            // Otherwise it's coming off the model in the other hand, which stops carrying it.
            if (otherHand != null && _articulation == null)
            {
                otherHand.GiveUp(this);
                if (wholeGroupGrab)
                    PickUpGroup(otherHand);
                otherHand.RefreshGroupConnections();
            }

            // Remember where everything was, for Undo.
            _undoStep = null;
            if (fromMenu)
            {
                _undoStep = UndoHistory.RecordSpawn(new[] { this }); // undo puts it back in the menu
            }
            else if (_articulation != null)
            {
                _undoStep = UndoHistory.RecordMove(_articulation.MovingBricks);
            }
            else if (fromInventory || wholeGroupGrab)
            {
                if (otherHand == null)
                    PickUpGroup(); // (coming off the other hand's model, its group was gathered above)
                if (!fromInventory)
                    _undoStep = RecordGroupForUndo();
            }
            else
            {
                _undoStep = UndoHistory.RecordMove(new[] { this });
                LeaveBond(); // pulled off on its own
            }

            Grabbed?.Invoke(this);
        }
        else if (evt.Type == PointerEventType.Move && _articulation != null)
        {
            _articulation.SetHand(evt.Pose.position);
        }
        else if ((evt.Type == PointerEventType.Unselect || evt.Type == PointerEventType.Cancel)
                 && _grabbable.SelectingPointsCount == 0)
        {
            // Only snap once every hand has let go.
            Held.Remove(this);
            HideGhosts();

            // It was a locked piece of the model in the other hand: it never moved. Hold it there for a
            // frame or two more, in case the grab system moves it once more after letting go.
            if (_pinnedTo != null)
            {
                _pinFrames = 2;
                return;
            }

            // Finished posing a joint: it stays at the angle it was turned to. (Re-applied once more next
            // frame too, in case the grab system moves the part again after letting go.)
            if (_articulation != null)
            {
                _articulation.End();
                _articulationTail = _articulation;
                _articulation = null;
                return;
            }

            // Let go near its spot in a set being built: locks into place.
            if (SetBuilder.TryPlace(this))
            {
                UndoHistory.Discard(_undoStep);
                return;
            }

            // Let go next to the other wrist: goes into that wrist's inventory.
            if (WristInventory.TryStore(this))
            {
                UndoHistory.Discard(_undoStep);
                return;
            }

            // Dropped into a trash can: it goes, along with everything it's carrying (they're its children).
            if (TrashCan.Contains(transform.TransformPoint(_body.center)) && HoldsLockedBricks())
            {
                // Locked pieces can't be thrown away by accident; it's put down as usual instead.
                Sandbox.Message("This model has locked pieces. Unlock them (X) before throwing it away");
            }
            else if (TrashCan.Contains(transform.TransformPoint(_body.center)))
            {
                var trashed = new List<Brick> { this };
                foreach ((Brick carried, Transform _) in _carried)
                    trashed.Add(carried);
                foreach (Brick brick in trashed)
                    Trashed?.Invoke(brick);
                ClearCarried();

                if (CameFromMenu(_undoStep))
                {
                    // Straight from a menu into the trash: nothing worth undoing.
                    UndoHistory.Discard(_undoStep);
                    Destroy(gameObject);
                }
                else
                {
                    // Hidden rather than deleted, so Undo can bring it back.
                    UndoHistory.MarkTrashed(_undoStep, this, trashed);
                    gameObject.SetActive(false);
                }
                return;
            }

            TrySnap();

            // Ready-made assemblies (a complete universal joint) become their separate parts once placed, so
            // their joints can turn. (Listed first: putting the group down forgets what was carried.)
            List<Brick> assemblies = null;
            if (AssemblyPlaced != null)
            {
                if (part != null && part.IsAssembly)
                    (assemblies ??= new List<Brick>()).Add(this);
                foreach ((Brick carried, Transform _) in _carried)
                {
                    if (carried != null && carried.part != null && carried.part.IsAssembly)
                        (assemblies ??= new List<Brick>()).Add(carried);
                }
            }
            PutDownStack();
            if (assemblies != null)
            {
                UndoHistory.Discard(_undoStep); // it refers to a brick that's about to be replaced
                foreach (Brick assembly in assemblies)
                    AssemblyPlaced.Invoke(assembly);
            }
        }
    }

    /// <summary>Raised when a brick whose part is an assembly of other parts has been put down.</summary>
    public static event System.Action<Brick> AssemblyPlaced;

    // ---------- Locked sections ----------

    static int s_newestSection;

    /// <summary>
    /// 0 for a loose brick. Otherwise the brick is locked into its model as part of that numbered section:
    /// it can't be pulled off on its own (grabbing it takes the whole model), though new pieces can still be
    /// added to it. Sections are numbered in the order they were locked, so the newest can be unlocked first.
    /// </summary>
    public int LockedSection { get; private set; }

    public bool IsSectionLocked => LockedSection > 0;

    public void SetLockedSection(int section)
    {
        LockedSection = Mathf.Max(0, section);
        s_newestSection = Mathf.Max(s_newestSection, LockedSection);
    }

    /// <summary>How many of the bricks in hand (this one plus what it's carrying) are locked, out of how many.</summary>
    public (int locked, int total) LockedCount()
    {
        int locked = IsSectionLocked ? PieceCount : 0;
        int total = PieceCount;
        foreach ((Brick carried, Transform _) in _carried)
        {
            if (carried == null)
                continue;
            total += carried.PieceCount;
            if (carried.IsSectionLocked)
                locked += carried.PieceCount;
        }
        return (locked, total);
    }

    // ---------- Locked bodies ----------

    LockedBody _locked;

    /// <summary>
    /// Set when this brick isn't a single part but the carrier of a locked body: a rigid group of locked
    /// pieces collapsed into one object (see LockedBody). It has no part; the body gives it its shape,
    /// grab boxes, and exposed connection points. It can't be recolored, duplicated, stored, or thrown away.
    /// </summary>
    public LockedBody Locked => _locked;
    public bool IsLockedBody => _locked != null;

    /// <summary>How many pieces this brick stands for: 1, or every piece of the locked body it carries.</summary>
    public int PieceCount => _locked != null ? _locked.pieces.Count : 1;

    const int GhostTriangleLimit = 150000; // bigger locked bodies get no see-through preview (too costly to draw twice)

    /// <summary>Turns a newly created brick into the carrier of a locked body. Call right after creating it.</summary>
    public void BecomeLockedBody(LockedBody body)
    {
        _locked = body;
        part = null;
        SetLockedSection(body.newestSection);

        // What it looks like: the body's combined meshes, as children.
        MeshRenderer own = GetComponent<MeshRenderer>();
        foreach ((Mesh mesh, Material[] materials) in body.chunks)
        {
            var chunk = new GameObject("Locked Pieces");
            chunk.layer = gameObject.layer;
            chunk.transform.SetParent(transform, worldPositionStays: false);
            chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = chunk.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            if (own != null)
            {
                renderer.shadowCastingMode = own.shadowCastingMode;
                renderer.receiveShadows = own.receiveShadows;
            }
        }

        // What can be grabbed: a box around each small cluster of pieces. (The grab system collects a
        // brick's colliders when it starts up, so they must all be in place before then.)
        BoxCollider first = GetComponent<BoxCollider>();
        for (int i = 0; i < body.grabBoxes.Count; i++)
        {
            BoxCollider box = i == 0 && first != null ? first : gameObject.AddComponent<BoxCollider>();
            box.center = body.grabBoxes[i].center;
            box.size = body.grabBoxes[i].size;
        }

        Rebuild();
    }

    static readonly Dictionary<BrickPartData, List<Connector>> PartConnectors = new Dictionary<BrickPartData, List<Connector>>();

    /// <summary>A part's connection points, in its own space (plain studs and sockets for parts without connection data).</summary>
    public static IReadOnlyList<Connector> ConnectorsOf(BrickPartData part)
    {
        if (part == null)
            return System.Array.Empty<Connector>();
        if (PartConnectors.TryGetValue(part, out List<Connector> known))
            return known;

        var list = new List<Connector>();
        if (part.connectors != null && part.connectors.Length > 0)
        {
            list.AddRange(part.connectors);
        }
        else
        {
            foreach (Vector3 stud in part.studs ?? new Vector3[0])
                list.Add(Connector.Stud(stud, male: true));
            foreach (Vector3 socket in part.antiStuds ?? new Vector3[0])
                list.Add(Connector.Stud(socket, male: false));
        }
        PartConnectors[part] = list;
        return list;
    }

    // Bricks with many connection points (locked bodies, baseplates) keep them in a grid, in their own
    // space, so finding which of them another brick is plugged into doesn't mean checking every one.
    const int IndexedFrom = 48;
    ConnectorGrid _localIndex;
    static readonly List<int> PairScratch = new List<int>();

    ConnectorGrid LocalIndex()
    {
        if (_localIndex == null)
        {
            _localIndex = new ConnectorGrid();
            for (int i = 0; i < _connectors.Count; i++)
                _localIndex.Add(_connectors[i], this, false, i);
        }
        return _localIndex;
    }

    /// <summary>
    /// Calls visit(index of a's connector, index of b's connector) for every pair of their connection points
    /// that are plugged together, until visit returns true. Returns whether it did.
    /// </summary>
    public static bool ForEachMatedPair(Brick a, Brick b, System.Func<int, int, bool> visit)
    {
        // Quick rejection: too far apart to touch.
        float reach = a.WorldRadius + b.WorldRadius + 0.01f;
        if ((a.WorldCenter - b.WorldCenter).sqrMagnitude > reach * reach)
            return false;

        a.UpdateWorldConnectors();
        b.UpdateWorldConnectors();
        bool swapped = a._connectors.Count < b._connectors.Count;
        Brick big = swapped ? b : a, small = swapped ? a : b;

        if (big._connectors.Count < IndexedFrom)
        {
            for (int i = 0; i < small._worldConnectors.Count; i++)
            {
                Connector mine = small._worldConnectors[i];
                for (int j = 0; j < big._worldConnectors.Count; j++)
                {
                    if (Connections.AreMated(mine, big._worldConnectors[j]) && (swapped ? visit(i, j) : visit(j, i)))
                        return true;
                }
            }
            return false;
        }

        // Each of the small brick's points, taken into the big brick's space and looked up in its grid.
        ConnectorGrid index = big.LocalIndex();
        Quaternion toBig = Quaternion.Inverse(big.transform.rotation);
        Vector3 offset = -(toBig * big.transform.position);
        for (int i = 0; i < small._worldConnectors.Count; i++)
        {
            Connector mine = small._worldConnectors[i].Moved(offset, toBig);
            index.Query(mine, 0.002f, PairScratch);
            for (int k = 0; k < PairScratch.Count; k++)
            {
                ConnectorGrid.Entry entry = index[PairScratch[k]];
                if (Connections.AreMated(mine, entry.connector) && (swapped ? visit(i, entry.tag) : visit(entry.tag, i)))
                    return true;
            }
        }
        return false;
    }

    // Whether this brick or anything it's carrying is locked.
    bool HoldsLockedBricks()
    {
        if (IsSectionLocked)
            return true;
        foreach ((Brick carried, Transform _) in _carried)
        {
            if (carried != null && carried.IsSectionLocked)
                return true;
        }
        return false;
    }

    /// <summary>The highest section number in use (new ones count up from here).</summary>
    public static int NewestSection => s_newestSection;

    /// <summary>
    /// This brick's whole model: every brick connected or bonded to it, directly or through others
    /// (baseplates stay out, as they do when a model is picked up). This brick comes first.
    /// </summary>
    public List<Brick> WholeModel()
    {
        // Being carried: the group is already worked out (and its bricks are in the holder's hand).
        foreach (Brick held in Held)
        {
            if (held != null && (held == this || held.IsCarrying(this)) && held._carried.Count > 0)
            {
                var carriedModel = new List<Brick> { held };
                carriedModel.AddRange(held.CarriedBricks);
                return carriedModel;
            }
        }
        return CollectGroup();
    }

    /// <summary>The buildable brick closest to a point, if one is within the given distance.</summary>
    public static Brick Nearest(Vector3 point, float within)
    {
        Brick best = null;
        float bestDistance = within;
        foreach (Brick brick in AllBricks)
        {
            if (!brick.IsBuildable || brick.IsBaseplate)
                continue;
            float reach = brick.WorldRadius + within;
            if ((brick.WorldCenter - point).sqrMagnitude > reach * reach)
                continue;
            // Distance to the brick's body box.
            Vector3 local = brick.transform.InverseTransformPoint(point);
            float distance = Vector3.Distance(brick.transform.TransformPoint(brick._body.ClosestPoint(local)), point);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = brick;
            }
        }
        return best;
    }

    // Every brick connected to this one (above or below), plus bonded bricks, this one first.
    //
    // What's in another hand stays there. Normally that means nothing held or carried by another hand joins
    // the group. When this brick is itself part of the model in the other hand (`otherHand` is the brick
    // that hand holds), the group is only the loose pieces that come away with it: it never takes locked
    // pieces, or the held brick, out of that hand.
    List<Brick> CollectGroup(Brick otherHand = null)
    {
        var group = new List<Brick> { this };
        var inGroup = new HashSet<Brick> { this };
        bool MayJoin(Brick other)
        {
            if (other == null || !other.IsBuildable || other.IsBaseplate || inGroup.Contains(other))
                return false;
            if (otherHand != null)
                return other != otherHand && otherHand.IsCarrying(other) && !other.IsSectionLocked;
            foreach (Brick held in Held)
            {
                if (held != null && held != this && (held == other || held.IsCarrying(other)))
                    return false;
            }
            return true;
        }

        for (int i = 0; i < group.Count; i++)
        {
            // Bonded bricks come along whether or not they connect by studs.
            if (group[i]._bond != null)
            {
                foreach (Brick bonded in group[i]._bond.members)
                {
                    if (MayJoin(bonded))
                    {
                        inGroup.Add(bonded);
                        group.Add(bonded);
                    }
                }
            }

            foreach (Brick other in AllBricks)
            {
                if (MayJoin(other) && group[i].IsConnectedTo(other))
                {
                    inGroup.Add(other);
                    group.Add(other);
                }
            }
        }

        return group;
    }

    // Temporarily parents every brick connected to this one to it, so the whole model moves as a unit.
    // (`otherHand`: the brick in the other hand that was carrying this one, if any; see CollectGroup.)
    void PickUpGroup(Brick otherHand = null)
    {
        List<Brick> group = CollectGroup(otherHand);
        for (int i = 1; i < group.Count; i++)
        {
            // Pieces that were riding in the other hand's model go back to where they belong first.
            Transform t = group[i].transform;
            if (otherHand != null)
                otherHand.GiveUp(group[i]);
            _carried.Add((group[i], t.parent));
            _carriedSet.Add(group[i]);
            t.SetParent(transform, worldPositionStays: true);
        }

        BuildGroupConnections(group);
    }

    // Stops carrying one brick (it's being taken by the other hand): it goes back under its own parent.
    void GiveUp(Brick brick)
    {
        for (int i = 0; i < _carried.Count; i++)
        {
            if (_carried[i].brick != brick)
                continue;
            brick.transform.SetParent(_carried[i].originalParent, worldPositionStays: true);
            _carried.RemoveAt(i);
            _carriedSet.Remove(brick);
            return;
        }
    }

    // The brick in another hand whose model this brick is riding in, if any.
    Brick OtherHandHolding()
    {
        foreach (Brick held in Held)
        {
            if (held != null && held != this && held.IsCarrying(this))
                return held;
        }
        return null;
    }

    // A locked piece gripped while its model is in the other hand: it stays exactly where it is on the
    // model. (The grab system moves whatever is gripped, so it's put back after it each frame.)
    Brick _pinnedTo;
    Vector3 _pinnedPosition;
    Quaternion _pinnedRotation;
    int _pinFrames;

    void ApplyPin()
    {
        if (_pinnedTo == null)
            return;
        if (transform.parent == _pinnedTo.transform)
        {
            transform.localPosition = _pinnedPosition;
            transform.localRotation = _pinnedRotation;
        }
        // After letting go: once more on the next frame, then done.
        if (_pinFrames > 0 && --_pinFrames == 0)
            _pinnedTo = null;
    }

    // While carrying a group: every member's connection points in this brick's space, used ones included,
    // so a pin's occupied stretch can be found when its free end is being plugged in.
    ConnectorGrid _groupIndex;
    static readonly List<int> OwnQueryScratch = new List<int>();
    static readonly List<float> OwnScratch = new List<float>();

    // Collects every member's connection points into this brick's space, leaving out the ones used up by
    // another member, so only the group's exposed studs, sockets, pins, etc. can snap. A point that's only
    // partly covered stays: a pin pushed halfway into the group still has its other half to plug in.
    // Also builds the group's preview, since its shape just changed.
    void BuildGroupConnections(List<Brick> group)
    {
        _groupIndex ??= new ConnectorGrid();
        _groupIndex.Clear();
        Quaternion toLocal = Quaternion.Inverse(transform.rotation);
        Vector3 offset = -(toLocal * transform.position);
        foreach (Brick member in group)
        {
            for (int i = 0; i < member._connectors.Count; i++)
            {
                // (A locked body's points that are used up inside it are only kept to mark what's taken: tag 1.)
                bool marker = member._locked != null && member._locked.fullyUsedInside[i];
                _groupIndex.Add(member._connectors[i].InWorld(member.transform).Moved(offset, toLocal), member, false, marker ? 1 : 0);
            }
        }

        _groupConnectors.Clear();
        List<int> found = QueryScratch;
        for (int i = 0; i < _groupIndex.Count; i++)
        {
            ConnectorGrid.Entry item = _groupIndex[i];
            if (item.tag == 1)
                continue;
            bool used = false;
            _groupIndex.Query(item.connector, 0.002f, found);
            foreach (int index in found)
            {
                ConnectorGrid.Entry other = _groupIndex[index];
                if (other.owner == item.owner || !Connections.AreMated(item.connector, other.connector))
                    continue;
                // Covered end to end (a stud in a socket) is used up; partly (a pin half in a hole) isn't.
                (float from, float to) span = Connections.SpanAlong(item.connector, other.connector);
                if (item.connector.IsPoint || (span.from <= 0.0002f && span.to >= item.connector.length - 0.0002f))
                {
                    used = true;
                    break;
                }
            }
            if (!used)
                _groupConnectors.Add(item.connector);
        }

        BuildGroupGhost(group);
    }

    // The stretches of one of our connectors (in this brick's space) that our own pieces already occupy,
    // as from/to pairs measured along it from its start: the half of a pin that's inside the door it's
    // carried in. Null if there are none. Whatever it's plugged into next mustn't be put there.
    IReadOnlyList<float> OwnSpans(in Connector local)
    {
        ConnectorGrid index = _carried.Count > 0 ? _groupIndex : _locked != null ? LocalIndex() : null;
        if (index == null)
            return null;

        OwnScratch.Clear();
        index.Query(local, 0.002f, OwnQueryScratch);
        foreach (int i in OwnQueryScratch)
        {
            Connector other = index[i].connector;
            if (other.position == local.position && other.axis == local.axis && other.male == local.male)
                continue; // the connector itself
            if (!Connections.AreMated(other, local))
                continue;
            (float from, float to) span = Connections.SpanAlong(local, other);
            OwnScratch.Add(span.from);
            OwnScratch.Add(span.to);
        }
        return OwnScratch.Count > 0 ? OwnScratch : null;
    }

    /// <summary>
    /// For the wrist inventory: ends carrying, but leaves the carried bricks attached as children so the
    /// group stays together while stored. Returns this brick followed by the bricks it was carrying.
    /// </summary>
    public List<Brick> DetachGroupForStorage()
    {
        var members = new List<Brick> { this };
        foreach ((Brick brick, Transform _) in _carried)
            members.Add(brick);
        ClearCarried();
        return members;
    }

    public void SetStored(bool stored) => IsStored = stored;

    void PutDownStack()
    {
        foreach ((Brick brick, Transform originalParent) in _carried)
        {
            if (brick != null)
                brick.transform.SetParent(originalParent, worldPositionStays: true);
        }
        ClearCarried();
    }

    public bool IsCarrying(Brick brick) => _carriedSet.Contains(brick);

    // Ends carrying a group: forgets its members, free connection points, and preview.
    void ClearCarried()
    {
        _carried.Clear();
        _carriedSet.Clear();
        _groupConnectors.Clear();
        DestroyGroupGhost();
    }

    /// <summary>True when any of this brick's connectors is plugged into one of the other brick's.</summary>
    public bool IsConnectedTo(Brick other)
    {
        return ForEachMatedPair(this, other, AnyPair);
    }

    static readonly System.Func<int, int, bool> AnyPair = (mine, theirs) => true;

    Vector3 WorldCenter => transform.TransformPoint(_body.center);
    float WorldRadius => _body.extents.magnitude + 0.004f; // a little extra for studs and pins

    // ---------- Snapping ----------

    public bool TrySnap()
    {
        // Make sure overlap checks see where every brick is right now.
        Physics.SyncTransforms();
        if (!FindSnap(out Vector3 position, out Quaternion rotation))
            return false;
        transform.SetPositionAndRotation(position, rotation);
        return true;
    }

    /// <summary>
    /// Finds the closest pose that plugs one of this brick's (or its group's) free connectors into a
    /// matching connector on another brick: studs into sockets, pins into holes, bars into clips, and so on.
    /// </summary>
    public bool FindSnap(out Vector3 bestPosition, out Quaternion bestRotation)
    {
        Vector3 position = transform.position;
        Quaternion rotation = transform.rotation;
        List<Connector> mine = SnapConnectors;

        // 1. Connectors on nearby parts, in world space: free ones to plug into, and ones already in use
        //    (only to stop us plugging into a spot that's taken). Points hidden inside a model are in use,
        //    so a big model only offers its exposed studs, sockets, pins and so on.
        CollectNearby();
        List<int> found = QueryScratch;

        // 2. Every single connection we could make, lined up the simple way. Keep the closest few.
        List<SnapCandidate> candidates = CandidateScratch;
        candidates.Clear();
        foreach (Connector local in mine)
        {
            Connector current = local.Moved(position, rotation);
            float reach = maxVerticalSnap + 0.012f;
            FreeGrid.Query(current, reach, found);
            foreach (int index in found)
            {
                ConnectorGrid.Entry item = FreeGrid[index];
                Connector theirs = item.connector;
                float near = reach + current.length + theirs.length;
                if ((current.position - theirs.position).sqrMagnitude > near * near || !Connections.Fit(local, theirs))
                    continue;

                Connections.Align(local, position, rotation, theirs, Connections.QuarterTurns(local, theirs),
                                  out Vector3 snappedPosition, out Quaternion snappedRotation,
                                  local.slide || theirs.slide ? PartnerEnds(theirs) : null,
                                  local.slide || theirs.slide ? OwnSpans(local) : null);
                // How far the connection point itself would move (not this brick's origin, which on a big group
                // can swing a long way for a tiny turn).
                Vector3 anchor = local.position;
                float move = (snappedPosition + snappedRotation * anchor - (position + rotation * anchor)).magnitude;
                float turn = Quaternion.Angle(snappedRotation, rotation);
                if (move > maxVerticalSnap || turn > maxTiltDegrees)
                    continue;
                candidates.Add(new SnapCandidate
                {
                    theirs = theirs,
                    position = snappedPosition,
                    rotation = snappedRotation,
                    anchor = anchor,
                    theirsRotation = item.owner != null ? item.owner.transform.rotation : Quaternion.identity,
                    spins = !Connections.QuarterTurns(local, theirs) && !theirs.IsPoint,
                    score = move + turn * 0.0003f,
                });
            }
        }
        candidates.Sort((a, b) => a.score.CompareTo(b.score));
        if (candidates.Count > RefinedCandidates)
            candidates.RemoveRange(RefinedCandidates, candidates.Count - RefinedCandidates);

        // 3. For those, also try turning around the connection so a second connector lines up too
        //    (needed when the first stud's angle means nothing, e.g. a stud on a spinning pin), and prefer
        //    whichever pose plugs in the most connections.
        float bestScore = float.MaxValue;
        int foundPlugged = 0;
        SnapCandidate foundFrom = default;
        Vector3 foundPosition = default;
        Quaternion foundRotation = default;
        foreach (SnapCandidate candidate in candidates)
        {
            Consider(candidate.position, candidate.rotation, candidate);
            foreach (float roll in SecondConnectionTurns(candidate, mine))
            {
                Quaternion spin = Quaternion.AngleAxis(roll, candidate.theirs.axis);
                Vector3 pivot = candidate.theirs.Start;
                Consider(pivot + spin * (candidate.position - pivot), spin * candidate.rotation, candidate);
            }
        }

        // Held by one connection that spins (a single pin): square it up with the part it plugs into, the way
        // real parts end up once they press against each other, if the hand has it within SquareWithin degrees.
        if (bestScore < float.MaxValue && foundPlugged == 1 && foundFrom.spins)
        {
            SnapCandidate settled = foundFrom;
            settled.position = foundPosition;
            settled.rotation = foundRotation;
            if (SquareTurn(settled) is float square)
            {
                Quaternion spin = Quaternion.AngleAxis(square, settled.theirs.axis);
                Vector3 pivot = settled.theirs.Start;
                Vector3 squaredPosition = pivot + spin * (foundPosition - pivot);
                Quaternion squaredRotation = spin * foundRotation;
                if (CountConnections(squaredPosition, squaredRotation, mine) >= 1 && !UsesTakenConnection(squaredPosition, squaredRotation, mine))
                {
                    foundPosition = squaredPosition;
                    foundRotation = squaredRotation;
                }
            }
        }

        bestPosition = foundPosition;
        bestRotation = foundRotation;
        if (bestScore < float.MaxValue)
            return true;

        // No brick to connect to: try setting it down on a real table or the floor instead.
        return FindSurfaceSnap(out bestPosition, out bestRotation);

        void Consider(Vector3 p, Quaternion r, SnapCandidate from)
        {
            Connector theirs = from.theirs;
            Vector3 anchor = from.anchor;
            float move = (p + r * anchor - (position + rotation * anchor)).magnitude;
            float turn = Quaternion.Angle(r, rotation);
            if (move > maxVerticalSnap || turn > maxTiltDegrees)
                return;
            int plugged = CountConnections(p, r, mine, out float misfit);
            float score = move + turn * 0.0003f - (plugged - 1) * ConnectionBonus + misfit * MisfitCost;

            // Hanging on a single connection (e.g. a round plate on one center stud), prefer the placement
            // that centers the part over it, since that's usually what's meant.
            if (plugged <= 1)
            {
                Vector3 offset = p + r * _body.center - theirs.Start;
                score += CenteringWeight * Vector3.ProjectOnPlane(offset, theirs.axis).magnitude;
            }
            if (score >= bestScore || UsesTakenConnection(p, r, mine)) // don't plug into a spot that's already used
                return;
            bestScore = score;
            foundPosition = p;
            foundRotation = r;
            foundPlugged = plugged;
            foundFrom = from;
        }
    }

    const float SquareWithin = 20f;      // degrees: further than this from square, the hand's angle is kept
    const float MisfitCost = 0.001f;     // each degree (or mm) a plugged-in connection is out of line counts like 1 mm farther

    // The turn about the candidate's connection that lines this part's sides up with those of the part it
    // plugs into (any quarter turn), if that's within SquareWithin degrees and not already there.
    static float? SquareTurn(in SnapCandidate candidate)
    {
        Vector3 axis = candidate.theirs.axis;
        float best = float.MaxValue;
        Vector3[] sides = { Vector3.right, Vector3.up, Vector3.forward };
        foreach (Vector3 mineSide in sides)
        {
            Vector3 mine = Vector3.ProjectOnPlane(candidate.rotation * mineSide, axis);
            if (mine.sqrMagnitude < 0.25f)
                continue; // (nearly along the axis: turning doesn't line it up)
            foreach (Vector3 theirSide in sides)
            {
                Vector3 theirs = Vector3.ProjectOnPlane(candidate.theirsRotation * theirSide, axis);
                if (theirs.sqrMagnitude < 0.25f)
                    continue;
                foreach (float sign in new[] { 1f, -1f })
                {
                    float angle = Vector3.SignedAngle(mine, theirs * sign, axis);
                    if (Mathf.Abs(angle) < Mathf.Abs(best))
                        best = angle;
                }
            }
        }
        return Mathf.Abs(best) <= SquareWithin && Mathf.Abs(best) > 0.05f ? best : (float?)null;
    }

    // One way of plugging in: the fixed connector used, and where that puts this brick.
    struct SnapCandidate
    {
        public Connector theirs;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 anchor; // our connection point, in this brick's space
        public Quaternion theirsRotation; // the part it plugs into
        public bool spins;                // the connection turns freely (a pin in a hole), so the angle is open
        public float score; // distance moved, plus a little for turning
    }

    const int RefinedCandidates = 6;
    const float ConnectionBonus = 0.002f; // each extra connection plugged in counts like 2 mm closer
    const float CenteringWeight = 0.25f;  // single-connection snaps: 4 mm off-center counts like 1 mm farther
    static readonly List<float> AnchorScratch = new List<float>();
    static readonly List<int> QueryScratch = new List<int>();
    static readonly List<int> InnerQueryScratch = new List<int>();
    static readonly List<SnapCandidate> CandidateScratch = new List<SnapCandidate>();
    static readonly List<float> RollScratch = new List<float>();
    static readonly List<(Connector theirs, Vector3 to, float radius)> AroundScratch = new List<(Connector, Vector3, float)>();
    static readonly ConnectorGrid FreeGrid = new ConnectorGrid();
    static readonly ConnectorGrid UsedGrid = new ConnectorGrid();

    // Fills FreeGrid and UsedGrid with the world-space connectors of every part near this brick (or its group).
    void CollectNearby()
    {
        RefreshLayout();
        FreeGrid.Clear();
        UsedGrid.Clear();
        float myReach = GroupRadius();
        float gather = myReach + maxVerticalSnap + 0.02f;
        Vector3 center = WorldCenter;
        foreach (Brick other in AllBricks)
        {
            if (other == this || !other.IsBuildable || IsCarrying(other))
                continue;
            float reach = myReach + other.WorldRadius + maxVerticalSnap + 0.01f;
            if ((center - other.WorldCenter).sqrMagnitude > reach * reach)
                continue;

            other.UpdateConnectorUse();
            for (int i = 0; i < other._worldConnectors.Count; i++)
            {
                Connector theirs = other._worldConnectors[i];
                if ((theirs.position - center).sqrMagnitude > gather * gather)
                    continue;
                if (other._fullyUsed[i])
                    UsedGrid.Add(theirs, other);
                else
                    FreeGrid.Add(theirs, other, other._partlyUsed[i]);
            }
        }
    }

    // Where the parts already on a connector (e.g. an axle) begin and end along it, from its start.
    static List<float> PartnerEnds(in Connector host)
    {
        List<float> ends = AnchorScratch;
        ends.Clear();
        AddPartnerEnds(FreeGrid, host, ends);
        AddPartnerEnds(UsedGrid, host, ends);
        return ends;
    }

    static void AddPartnerEnds(ConnectorGrid grid, in Connector host, List<float> ends)
    {
        grid.Query(host, 0.002f, InnerQueryScratch);
        foreach (int index in InnerQueryScratch)
        {
            Connector other = grid[index].connector;
            if (other.position == host.position && other.axis == host.axis)
                continue; // the connector itself
            if (!Connections.AreMated(other, host))
                continue;
            (float from, float to) span = Connections.SpanAlong(host, other);
            ends.Add(span.from);
            ends.Add(span.to);
        }
    }

    // Angles (degrees) to turn around the candidate's connection so that another of our connectors lands
    // on another free connector it fits, the same distance from the turning axis. Looks across the whole
    // of what's nearby, not just close to the first connection: a group held by pins several studs apart
    // (a steering assembly on four pins) has to line all of them up, not just the one that snapped first.
    // The smallest few turns are returned.
    static List<float> SecondConnectionTurns(in SnapCandidate candidate, List<Connector> mine)
    {
        List<float> rolls = RollScratch;
        rolls.Clear();
        Vector3 axis = candidate.theirs.axis;
        Vector3 pivot = candidate.theirs.Start;

        // Free connectors that run along the same direction, with how far each is from the axis, sorted by
        // that distance so the ones matching one of ours can be found without comparing every pair.
        List<(Connector theirs, Vector3 to, float radius)> around = AroundScratch;
        around.Clear();
        for (int i = 0; i < FreeGrid.Count; i++)
        {
            Connector theirs = FreeGrid[i].connector;
            if (Mathf.Abs(Vector3.Dot(theirs.axis, axis)) < 0.996f)
                continue;
            Vector3 to = Vector3.ProjectOnPlane(theirs.position - pivot, axis);
            float radius = to.magnitude;
            if (radius > 1e-4f)
                around.Add((theirs, to, radius));
        }

        around.Sort(ByRadius); // (empty if nothing runs the same way: then only the crosswise check below applies)

        const float RadiusTolerance = 0.0008f;
        foreach (Connector local in mine)
        {
            Connector moved = local.Moved(candidate.position, candidate.rotation);
            if (Mathf.Abs(Vector3.Dot(moved.axis, axis)) < 0.996f)
                continue;
            Vector3 from = Vector3.ProjectOnPlane(moved.position - pivot, axis);
            float radius = from.magnitude;
            if (radius < 1e-4f)
                continue; // on the axis itself: turning doesn't move it

            // First entry at least radius - tolerance from the axis.
            int low = 0, high = around.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (around[middle].radius < radius - RadiusTolerance)
                    low = middle + 1;
                else
                    high = middle;
            }
            for (int i = low; i < around.Count && around[i].radius <= radius + RadiusTolerance; i++)
            {
                if (!Connections.Fit(local, around[i].theirs))
                    continue;
                float roll = Vector3.SignedAngle(from, around[i].to, axis);
                // Only a small correction to where it's being held (it also keeps this quick), but a real one.
                if (Mathf.Abs(roll) < 0.02f || Mathf.Abs(roll) > MaxSecondTurn)
                    continue;
                bool seen = false;
                foreach (float r in rolls)
                {
                    if (Mathf.Abs(Mathf.DeltaAngle(r, roll)) < 0.25f)
                    {
                        seen = true;
                        break;
                    }
                }
                if (!seen)
                    rolls.Add(roll);
            }
        }

        // Connectors at right angles to the axis (studs under a part that hangs on a sideways pin): the turn
        // about the axis that stands each one exactly in line with a hole it fits nearby. Without this, a part
        // on a pin with studs as well settled a few degrees off, since studs are accepted a little out of line.
        foreach (Connector local in mine)
        {
            Connector moved = local.Moved(candidate.position, candidate.rotation);
            if (Mathf.Abs(Vector3.Dot(moved.axis, axis)) > 0.2f)
                continue;
            Vector3 mineDirection = Vector3.ProjectOnPlane(moved.axis, axis);
            if (mineDirection.sqrMagnitude < 0.5f)
                continue;
            FreeGrid.Query(moved, PerpendicularReach, InnerQueryScratch);
            foreach (int index in InnerQueryScratch)
            {
                Connector theirs = FreeGrid[index].connector;
                if (Mathf.Abs(Vector3.Dot(theirs.axis, moved.axis)) < 0.95f || !Connections.Fit(local, theirs))
                    continue;
                // (Either way along the line, whichever is nearer to how it's held.)
                Vector3 target = Vector3.ProjectOnPlane(Vector3.Dot(theirs.axis, moved.axis) >= 0f ? theirs.axis : -theirs.axis, axis);
                if (target.sqrMagnitude < 0.5f)
                    continue;
                float roll = Vector3.SignedAngle(mineDirection, target, axis);
                if (Mathf.Abs(roll) < 0.02f || Mathf.Abs(roll) > MaxSecondTurn)
                    continue;
                bool seen = false;
                foreach (float r in rolls)
                {
                    if (Mathf.Abs(Mathf.DeltaAngle(r, roll)) < 0.05f)
                    {
                        seen = true;
                        break;
                    }
                }
                if (!seen)
                    rolls.Add(roll);
            }
        }

        if (rolls.Count > MaxSecondTurns)
        {
            rolls.Sort((a, b) => Mathf.Abs(a).CompareTo(Mathf.Abs(b)));
            rolls.RemoveRange(MaxSecondTurns, rolls.Count - MaxSecondTurns);
        }
        return rolls;
    }

    const float MaxSecondTurn = 36f; // degrees (anything more is past the tilt limit anyway)
    const int MaxSecondTurns = 6;
    const float PerpendicularReach = 0.004f; // how far from a hole a crosswise connector is looked at (about half a stud)
    static readonly System.Comparison<(Connector theirs, Vector3 to, float radius)> ByRadius =
        (a, b) => a.radius.CompareTo(b.radius);

    // How many of our connectors would be plugged into nearby free connectors at this pose.
    static int CountConnections(Vector3 position, Quaternion rotation, List<Connector> mine) =>
        CountConnections(position, rotation, mine, out _);

    // Also how far out of line the plugged-in connections are (`misfit`, in degrees plus millimetres): a
    // connection is accepted a little out of line, but a pose with everything exactly in line is better.
    static int CountConnections(Vector3 position, Quaternion rotation, List<Connector> mine, out float misfit)
    {
        int count = 0;
        misfit = 0f;
        foreach (Connector local in mine)
        {
            Connector moved = local.Moved(position, rotation);
            FreeGrid.Query(moved, 0.002f, InnerQueryScratch);
            foreach (int index in InnerQueryScratch)
            {
                Connector theirs = FreeGrid[index].connector;
                if (!Connections.AreMated(moved, theirs))
                    continue;
                count++;
                if (!moved.IsPoint)
                {
                    misfit += Vector3.Angle(moved.axis, Vector3.Dot(moved.axis, theirs.axis) >= 0f ? theirs.axis : -theirs.axis);
                    Vector3 offset = moved.Start - theirs.Start;
                    misfit += (offset - Vector3.Dot(offset, theirs.axis) * theirs.axis).magnitude * 1000f;
                }
                break;
            }
        }
        return count;
    }

    // True if, at this pose, any of our free connectors would plug into a connector that another part is
    // already plugged into (e.g. a stud that already has a brick on it). This is exact for any part shape,
    // unlike checking boxes, which fails for clips, brackets, and other irregular parts.
    static bool UsesTakenConnection(Vector3 position, Quaternion rotation, List<Connector> mine)
    {
        foreach (Connector local in mine)
        {
            Connector moved = local.Moved(position, rotation);

            // Fully used (a stud with a brick on it, a hole with a pin in it).
            UsedGrid.Query(moved, 0.002f, InnerQueryScratch);
            foreach (int index in InnerQueryScratch)
            {
                if (Connections.AreMated(moved, UsedGrid[index].connector))
                    return true;
            }

            // Partly used (an axle or bar with something on it): only taken where that something already is.
            FreeGrid.Query(moved, 0.002f, InnerQueryScratch);
            for (int i = 0; i < InnerQueryScratch.Count; i++)
            {
                ConnectorGrid.Entry item = FreeGrid[InnerQueryScratch[i]];
                if (item.partlyUsed && Connections.AreMated(moved, item.connector) && OverlapsPartner(item, moved))
                    return true;
            }
        }
        return false;
    }

    // Whether `incoming` would sit where a part already plugged into the item's connector is.
    static bool OverlapsPartner(in ConnectorGrid.Entry item, in Connector incoming)
    {
        (float from, float to) wanted = Connections.SpanAlong(item.connector, incoming);
        foreach (ConnectorGrid grid in BothGrids)
        {
            // (A separate list: the caller is still walking InnerQueryScratch.)
            grid.Query(item.connector, 0.002f, PartnerQueryScratch);
            foreach (int index in PartnerQueryScratch)
            {
                ConnectorGrid.Entry other = grid[index];
                // Another part's connector, or, on a locked body, another piece of the same body.
                bool sameOwner = other.owner == item.owner;
                if (sameOwner && (item.owner == null || !item.owner.IsLockedBody
                                  || (other.connector.position == item.connector.position && other.connector.axis == item.connector.axis)))
                    continue;
                if (!Connections.AreMated(other.connector, item.connector))
                    continue;
                if (item.connector.IsPoint)
                    return true;
                (float from, float to) used = Connections.SpanAlong(item.connector, other.connector);
                if (Mathf.Min(used.to, wanted.to) - Mathf.Max(used.from, wanted.from) > 0.0005f)
                    return true;
            }
        }
        return false;
    }
    static readonly List<int> PartnerQueryScratch = new List<int>();
    static readonly ConnectorGrid[] BothGrids = { FreeGrid, UsedGrid };

    // ---------- Which connection points are already in use ----------

    // Bumped whenever a resting brick moves, appears, or goes, or a different set of bricks is being held,
    // so each brick's cached "already plugged in" flags get worked out again (only when next needed).
    static int s_layoutVersion;
    static int s_layoutFrame = -1;
    static int s_layoutSignature;
    static readonly Dictionary<Brick, Brick> MovingWith = new Dictionary<Brick, Brick>(); // brick -> the held brick carrying it

    // This brick's connectors in world space, and which are plugged into another part that moves with it.
    readonly List<Connector> _worldConnectors = new List<Connector>();
    Vector3 _worldPosition;
    Quaternion _worldRotation;
    bool _worldValid;
    int _usedVersion = -1;
    bool[] _fullyUsed = System.Array.Empty<bool>();
    bool[] _partlyUsed = System.Array.Empty<bool>();

    static void RefreshLayout()
    {
        if (s_layoutFrame == Time.frameCount)
            return;
        s_layoutFrame = Time.frameCount;

        MovingWith.Clear();
        foreach (Brick held in Held)
        {
            if (held == null)
                continue;
            MovingWith[held] = held;
            foreach ((Brick carried, Transform _) in held._carried)
            {
                if (carried != null)
                    MovingWith[carried] = held;
            }
        }

        bool moved = false;
        int signature = 17;
        unchecked
        {
            foreach (Brick brick in AllBricks)
            {
                if (!brick.IsBuildable)
                    continue;
                signature = signature * 31 + brick.GetHashCode();
                if (MovingWith.TryGetValue(brick, out Brick holder))
                {
                    signature = signature * 31 + holder.GetHashCode();
                    continue; // moves every frame while held; its group is checked as one rigid piece
                }
                if (brick.transform.hasChanged)
                {
                    brick.transform.hasChanged = false;
                    moved = true;
                }
            }
        }
        if (moved || signature != s_layoutSignature)
        {
            s_layoutSignature = signature;
            s_layoutVersion++;
        }
    }

    // Bricks are only "plugged into" parts that move with them: resting parts with resting parts, and the
    // parts of one held group with each other.
    static Brick MovingUnit(Brick brick) => MovingWith.TryGetValue(brick, out Brick holder) ? holder : null;

    void UpdateWorldConnectors()
    {
        if (_worldValid && transform.position == _worldPosition && transform.rotation == _worldRotation)
            return;
        _worldValid = true;
        _worldPosition = transform.position;
        _worldRotation = transform.rotation;
        _worldConnectors.Clear();
        foreach (Connector connector in _connectors)
            _worldConnectors.Add(connector.Moved(_worldPosition, _worldRotation));
    }

    void UpdateConnectorUse()
    {
        UpdateWorldConnectors();
        if (_usedVersion == s_layoutVersion && _fullyUsed.Length == _connectors.Count)
            return;
        _usedVersion = s_layoutVersion;

        int count = _connectors.Count;
        if (_fullyUsed.Length != count)
        {
            _fullyUsed = new bool[count];
            _partlyUsed = new bool[count];
        }
        if (_locked != null)
        {
            // A locked body starts from what its own pieces use of each other.
            System.Array.Copy(_locked.fullyUsedInside, _fullyUsed, count);
            System.Array.Copy(_locked.partlyUsedInside, _partlyUsed, count);
        }
        else
        {
            System.Array.Clear(_fullyUsed, 0, count);
            System.Array.Clear(_partlyUsed, 0, count);
        }

        Brick unit = MovingUnit(this);
        foreach (Brick other in AllBricks)
        {
            if (other == this || !other.IsBuildable || MovingUnit(other) != unit)
                continue;
            s_useMine = this;
            s_useTheirs = other;
            ForEachMatedPair(this, other, MarkUse);
        }
        s_useMine = s_useTheirs = null;
    }

    // (Static, rather than a closure made for every pair of bricks.)
    static Brick s_useMine, s_useTheirs;
    static readonly System.Func<int, int, bool> MarkUse = (i, j) =>
    {
        Brick me = s_useMine;
        if (me._fullyUsed[i])
            return false;
        Connector mine = me._worldConnectors[i];
        // Covered end to end (a stud in a socket) is used up; partly (an axle through one gear) isn't.
        (float from, float to) span = Connections.SpanAlong(mine, s_useTheirs._worldConnectors[j]);
        if (mine.IsPoint || (span.from <= 0.0002f && span.to >= mine.length - 0.0002f))
            me._fullyUsed[i] = true;
        else
            me._partlyUsed[i] = true;
        return false;
    };

    // How far the held brick and everything it's carrying reach from this brick's center.
    float GroupRadius()
    {
        float radius = WorldRadius;
        foreach ((Brick member, Transform _) in _carried)
            radius = Mathf.Max(radius, (member.WorldCenter - WorldCenter).magnitude + member.WorldRadius);
        return radius;
    }

    // Stands the brick upright on the nearest real surface below or slightly above it, keeping its heading.
    bool FindSurfaceSnap(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;

        if (Vector3.Angle(transform.up, Vector3.up) > maxTiltDegrees)
            return false;

        Vector3 bottom = LowestBottomPoint();
        if (!RoomSurfaces.TryFindSurface(transform.TransformPoint(bottom), maxSurfaceSnap, out Vector3 surfacePoint))
            return false;

        Vector3 heading = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (heading.sqrMagnitude < 1e-6f)
            heading = Vector3.ProjectOnPlane(transform.up, Vector3.up);

        rotation = Quaternion.LookRotation(heading, Vector3.up);
        position = surfacePoint - rotation * bottom;
        return !WouldOverlapOthers(position, rotation);
    }

    // Bottom-center of the lowest brick in the group being held, in this brick's local space.
    Vector3 LowestBottomPoint()
    {
        Vector3 lowest = new Vector3(_body.center.x, _body.min.y, _body.center.z);
        foreach ((Brick member, Transform _) in _carried)
        {
            Transform t = member.transform;
            Bounds b = member._body;
            Vector3 bottom = t.localPosition + t.localRotation * new Vector3(b.center.x, b.min.y, b.center.z);
            if (bottom.y < lowest.y)
                lowest = bottom;
        }
        return lowest;
    }

    // True if this brick (and everything it's carrying) placed at the given pose would cut into another brick.
    bool WouldOverlapOthers(Vector3 position, Quaternion rotation)
    {
        // (A locked body's box is the box around a whole model, mostly air, so it isn't checked.)
        if (!IsLockedBody && BodyOverlapsOthers(_body, position, rotation))
            return true;

        foreach ((Brick member, Transform _) in _carried)
        {
            Transform t = member.transform;
            if (!member.IsLockedBody && BodyOverlapsOthers(member._body, position + rotation * t.localPosition, rotation * t.localRotation))
                return true;
        }
        return false;
    }

    bool BodyOverlapsOthers(Bounds body, Vector3 position, Quaternion rotation)
    {
        // Shrink slightly so bricks that merely touch (stacked or side by side) don't count.
        // (Kept above zero for very thin parts like baseplates.)
        Vector3 halfExtents = Vector3.Max(body.extents - Vector3.one * 0.001f, Vector3.one * 0.0002f);
        Vector3 center = position + rotation * body.center;

        int count = Physics.OverlapBoxNonAlloc(center, halfExtents, OverlapResults, rotation,
                                               Physics.AllLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Brick hit = OverlapResults[i].GetComponentInParent<Brick>();
            if (hit != null && hit != this && !hit.IsPreview && !IsCarrying(hit))
                return true;
        }
        return false;
    }

    // ---------- Preview ----------

    /// <summary>The slowest snap search since this was last set to 0 (for the performance readout).</summary>
    public static float SnapSearchMilliseconds;
    static readonly System.Diagnostics.Stopwatch SnapTimer = new System.Diagnostics.Stopwatch();

    // The last snap search while held, reused while the hand is (almost) still and nothing else moved.
    Vector3 _searchedPosition;
    Quaternion _searchedRotation;
    int _searchedVersion = -1;
    bool _searchFound;
    Vector3 _foundPosition;
    Quaternion _foundRotation;

    // While held, shows a see-through copy of the brick (or the whole group it's carrying, as one
    // combined shape) where it would snap if released now.
    void UpdateGhost()
    {
        bool held = _grabbable != null && _grabbable.SelectingPointsCount > 0;
        if (!held)
        {
            _searchedVersion = -1;
            return;
        }

        if (_pinnedTo != null || _articulation != null || SetBuilder.HasTargetFor(this) || WristInventory.IsOverWrist(this) || TrashCan.Contains(transform.TransformPoint(_body.center)))
        {
            HideGhosts();
            return;
        }

        RefreshLayout();
        bool still = _searchedVersion == s_layoutVersion
                     && (transform.position - _searchedPosition).sqrMagnitude < 0.0003f * 0.0003f
                     && Quaternion.Angle(transform.rotation, _searchedRotation) < 0.3f;
        if (!still)
        {
            SnapTimer.Restart();
            _searchFound = FindSnap(out _foundPosition, out _foundRotation);
            SnapTimer.Stop();
            SnapSearchMilliseconds = Mathf.Max(SnapSearchMilliseconds, (float)SnapTimer.Elapsed.TotalMilliseconds);
            _searchedPosition = transform.position;
            _searchedRotation = transform.rotation;
            _searchedVersion = s_layoutVersion;
        }

        if (!_searchFound)
        {
            HideGhosts();
            return;
        }

        if (_carried.Count > 0 && _groupGhost != null)
        {
            _groupGhost.transform.SetPositionAndRotation(_foundPosition, _foundRotation);
            _groupGhost.SetActive(true);
        }
        else
        {
            ShowGhost(_foundPosition, _foundRotation);
        }
    }

    void ShowGhost(Vector3 position, Quaternion rotation)
    {
        if (ghostMaterial == null)
            return;
        if (_ghost == null)
            CreateGhost();
        _ghost.transform.SetPositionAndRotation(position, rotation);
        _ghost.SetActive(true);
    }

    void HideGhosts()
    {
        if (_ghost != null)
            _ghost.SetActive(false);
        if (_groupGhost != null)
            _groupGhost.SetActive(false);
    }

    // ---------- Flashing ----------

    const int FlashTimes = 3;
    const float FlashOn = 0.25f, FlashOff = 0.18f;
    GameObject _flash;

    /// <summary>Blinks the brick in a color a few times (e.g. to show what an unlock just turned loose).</summary>
    public void Flash(Color color)
    {
        if (ghostMaterial == null || !isActiveAndEnabled)
            return;
        StopFlash();
        StartCoroutine(Flashing(color));
    }

    // The flash is a see-through copy in the flash color, a touch bigger so it shows over the brick. It's a
    // child of the brick, so it moves with it and can never be left behind: if the brick is locked again,
    // thrown away or switched off mid-flash, the copy goes with it (see StopFlash in OnDisable).
    System.Collections.IEnumerator Flashing(Color color)
    {
        _flash = new GameObject("Flash");
        _flash.layer = gameObject.layer;
        _flash.transform.SetParent(transform, worldPositionStays: false);
        _flash.transform.localScale = Vector3.one * 1.02f;
        _flash.transform.localPosition = -_body.center * 0.02f; // enlarged about the brick's middle
        Material material = BrickPartData.SharedMaterial(ghostMaterial, color);
        if (_locked != null)
        {
            foreach ((Mesh mesh, Material[] materials) in _locked.chunks)
                AddFlashPart(_flash.transform, mesh, material);
        }
        else if (_activeMesh != null)
        {
            AddFlashPart(_flash.transform, _activeMesh, material);
        }

        for (int i = 0; i < FlashTimes && _flash != null; i++)
        {
            _flash.SetActive(true);
            yield return new WaitForSeconds(FlashOn);
            if (_flash == null)
                yield break;
            _flash.SetActive(false);
            yield return new WaitForSeconds(FlashOff);
        }
        StopFlash();
    }

    void StopFlash()
    {
        if (_flash != null)
            Destroy(_flash);
        _flash = null;
    }

    static void AddFlashPart(Transform parent, Mesh mesh, Material material)
    {
        var part = new GameObject("Flash Part");
        part.layer = parent.gameObject.layer;
        part.transform.SetParent(parent, worldPositionStays: false);
        part.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = part.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = Enumerable.Repeat(material, Mathf.Max(1, mesh.subMeshCount)).ToArray();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    // The brick's color, with the ghost material's transparency.
    Material GhostMaterialFor(Color brickColor)
    {
        Color tint = brickColor;
        tint.a = ghostMaterial.color.a;
        return BrickPartData.SharedMaterial(ghostMaterial, tint);
    }

    void CreateGhost()
    {
        _ghost = new GameObject(name + " Ghost");
        if (_locked != null)
        {
            AddLockedGhost(_ghost.transform, Vector3.zero, Quaternion.identity);
            return;
        }
        _ghost.AddComponent<MeshFilter>().sharedMesh = _activeMesh;

        MeshRenderer ghostRenderer = _ghost.AddComponent<MeshRenderer>();
        ghostRenderer.sharedMaterials = Enumerable.Repeat(GhostMaterialFor(color), Mathf.Max(1, _activeMesh.subMeshCount)).ToArray();
        ghostRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    // A locked body's preview: its own combined meshes again (shared, not copied) in see-through materials,
    // under the given parent. Very big bodies get none, since it would mean drawing the model twice.
    void AddLockedGhost(Transform parent, Vector3 localPosition, Quaternion localRotation)
    {
        if (_locked == null || _locked.triangles > GhostTriangleLimit)
            return;
        foreach ((Mesh mesh, Material[] materials) in _locked.chunks)
        {
            var chunk = new GameObject("Locked Ghost");
            chunk.transform.SetParent(parent, worldPositionStays: false);
            chunk.transform.localPosition = localPosition;
            chunk.transform.localRotation = localRotation;
            chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = chunk.AddComponent<MeshRenderer>();
            var ghosts = new Material[materials.Length];
            for (int i = 0; i < materials.Length; i++)
                ghosts[i] = GhostMaterialFor(materials[i].color);
            renderer.sharedMaterials = ghosts;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }

    // A carried group's preview as one mesh (one piece per color) instead of a see-through copy of every
    // brick, which doubled what the headset had to draw while holding a big model.
    void BuildGroupGhost(List<Brick> group)
    {
        DestroyGroupGhost();
        if (ghostMaterial == null || group.Count < 2)
            return;

        // This brick's space, so the preview can be placed wherever the group would snap.
        Matrix4x4 toLocal = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one).inverse;
        var byColor = new Dictionary<Color, List<CombineInstance>>();
        foreach (Brick member in group)
        {
            Mesh mesh = member._activeMesh;
            if (mesh == null || !mesh.isReadable)
                continue;
            if (!byColor.TryGetValue(member.color, out List<CombineInstance> pieces))
                byColor[member.color] = pieces = new List<CombineInstance>();
            Matrix4x4 placed = toLocal * member.transform.localToWorldMatrix;
            for (int s = 0; s < mesh.subMeshCount; s++)
                pieces.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = placed });
        }
        _groupGhost = new GameObject(name + " Group Ghost");
        if (byColor.Count > 0)
        {
            var perColor = new List<CombineInstance>();
            var materials = new List<Material>();
            foreach (KeyValuePair<Color, List<CombineInstance>> pair in byColor)
            {
                var merged = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                merged.CombineMeshes(pair.Value.ToArray(), mergeSubMeshes: true, useMatrices: true);
                perColor.Add(new CombineInstance { mesh = merged, transform = Matrix4x4.identity });
                materials.Add(GhostMaterialFor(pair.Key));
            }
            _groupGhostMesh = new Mesh { name = name + " Group Ghost", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _groupGhostMesh.CombineMeshes(perColor.ToArray(), mergeSubMeshes: false, useMatrices: false);
            foreach (CombineInstance piece in perColor)
                Destroy(piece.mesh);

            _groupGhost.AddComponent<MeshFilter>().sharedMesh = _groupGhostMesh;
            MeshRenderer ghostRenderer = _groupGhost.AddComponent<MeshRenderer>();
            ghostRenderer.sharedMaterials = materials.ToArray();
            ghostRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // Locked bodies in the group bring their own meshes.
        Quaternion toLocalRotation = Quaternion.Inverse(transform.rotation);
        foreach (Brick member in group)
        {
            if (member._locked != null)
                member.AddLockedGhost(_groupGhost.transform, transform.InverseTransformPoint(member.transform.position),
                                      toLocalRotation * member.transform.rotation);
        }
        _groupGhost.SetActive(false);
    }

    void DestroyGroupGhost()
    {
        if (_groupGhost != null)
            Destroy(_groupGhost);
        if (_groupGhostMesh != null)
            Destroy(_groupGhostMesh);
        _groupGhost = null;
        _groupGhostMesh = null;
    }

    // ---------- Shape ----------

    /// <summary>Applies the part (or simple size) and color: mesh, connection points, and collider.</summary>
    public void Rebuild()
    {
        _dirty = false;
        _connectors.Clear();
        _worldValid = false; // connection points changed: work out where they are and which are in use again
        _usedVersion = -1;
        _localIndex = null;

        if (_locked != null)
        {
            // A locked body: its exposed connection points and overall box. Its meshes and grab boxes were
            // set up once in BecomeLockedBody; this brick's own renderer draws nothing.
            _connectors.AddRange(_locked.connectors);
            _lockedFree.Clear();
            for (int i = 0; i < _connectors.Count; i++)
            {
                if (!_locked.fullyUsedInside[i])
                    _lockedFree.Add(_connectors[i]);
            }
            _body = _locked.bounds;
            _activeMesh = null;
            MeshFilter ownFilter = GetComponent<MeshFilter>();
            if (ownFilter != null)
                ownFilter.sharedMesh = null;
            if (_ghost != null)
            {
                Destroy(_ghost);
                _ghost = null;
            }
            BrickMerger.MarkChanged(this);
            return;
        }

        if (part != null && part.mesh != null)
        {
            _activeMesh = part.mesh;
            _body = part.bodyBounds;
            if (part.connectors != null && part.connectors.Length > 0)
            {
                _connectors.AddRange(part.connectors);
            }
            else
            {
                // Imported before connection data (or not covered by it): plain studs and sockets.
                foreach (Vector3 stud in part.studs ?? new Vector3[0])
                    _connectors.Add(Connector.Stud(stud, male: true));
                foreach (Vector3 socket in part.antiStuds ?? new Vector3[0])
                    _connectors.Add(Connector.Stud(socket, male: false));
            }
        }
        else
        {
            if (_generatedMesh == null)
                _generatedMesh = new Mesh { name = "Brick", hideFlags = HideFlags.DontSave };
            BuildSimpleBrick(_generatedMesh);
            _activeMesh = _generatedMesh;
        }

        GetComponent<MeshFilter>().sharedMesh = _activeMesh;
        if (_ghost != null)
        {
            // Rebuilt on next use with the new shape and colors.
            Destroy(_ghost);
            _ghost = null;
        }

        BoxCollider box = GetComponent<BoxCollider>();
        if (box != null)
        {
            box.center = _body.center;
            box.size = _body.size;
        }

        MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
        if (_opaqueMaterial == null)
            _opaqueMaterial = meshRenderer.sharedMaterial;
        BrickPartData.ApplyColors(meshRenderer, _activeMesh, part != null && part.mesh != null ? part.submeshColors : null,
                                  color, _opaqueMaterial, transparentMaterial);
        BrickMerger.MarkChanged(this); // any combined mesh showing this brick is out of date
    }

    // A plain rectangular brick with its pivot at the bottom center.
    void BuildSimpleBrick(Mesh mesh)
    {
        float height = heightInPlates * PlateHeight;
        _body = new Bounds(new Vector3(0f, height * 0.5f, 0f), new Vector3(studsX * StudPitch, height, studsZ * StudPitch));

        var builder = new MeshBuilder();
        float halfW = studsX * StudPitch * 0.5f - SideGap;
        float halfD = studsZ * StudPitch * 0.5f - SideGap;
        builder.AddBox(new Vector3(-halfW, 0f, -halfD), new Vector3(halfW, height, halfD));

        for (int i = 0; i < studsX; i++)
        {
            for (int j = 0; j < studsZ; j++)
            {
                float x = (i - (studsX - 1) * 0.5f) * StudPitch;
                float z = (j - (studsZ - 1) * 0.5f) * StudPitch;
                builder.AddCylinder(new Vector3(x, height, z), StudRadius, StudHeight, StudSegments);
                _connectors.Add(Connector.Stud(new Vector3(x, height, z), male: true));
                _connectors.Add(Connector.Stud(new Vector3(x, 0f, z), male: false));
            }
        }

        builder.ApplyTo(mesh);
    }
}
