using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

/// <summary>
/// "Build a Set" mode. Shows each step's parts as see-through ghosts on the model, puts the exact parts
/// for the step on a tray beside it, and locks a part into place when it's let go near its ghost
/// (any connection type, any starting angle). When every ghost in a step is filled, the next step starts.
/// The whole build can be moved with the handle in front of it.
/// </summary>
public class SetBuilder : MonoBehaviour
{
    const float PlaceDistance = 0.025f;   // how close a part's center must be to its ghost's
    const float AdvanceDelay = 0.6f;
    const float TrayGap = 0.015f;

    // Ghost opacity (from Settings): faint while waiting, stronger when the part in your hand would drop in.
    static float GhostAlpha => GameSettings.GhostAlpha;
    static float GhostReadyAlpha => GameSettings.GhostReadyAlpha;

    static SetBuilder _instance;

    class Target
    {
        public SetData.Placement placement;
        public BrickPartData part;
        public GameObject ghost;
        public MeshRenderer ghostRenderer;
        public bool filled;
    }

    Sandbox _sandbox;
    Material _ghostMaterial;

    SetData _set;
    int _step;
    Transform _root;
    Transform _handle;
    Vector3 _handleToRoot;
    Bounds _modelBounds; // in the root's space

    Transform _tray;
    MeshFilter _traySlab;
    Transform _trayHandle;
    Vector3 _trayHandleToTray;

    readonly List<Target> _targets = new List<Target>();
    readonly List<Brick> _placed = new List<Brick>();
    readonly List<Brick> _trayParts = new List<Brick>();
    float _advanceAt = -1f;

    MenuPanel _panel;
    TextMeshPro _stepLabel;
    TextMeshPro _detailLabel;

    public bool IsBuilding => _set != null;

    public SetData CurrentSet => _set;

    public void Init(Sandbox sandbox, Material ghostMaterial)
    {
        _instance = this;
        _sandbox = sandbox;
        _ghostMaterial = ghostMaterial;
        BuildPanel();
        Brick.Grabbed += OnBrickGrabbed;
    }

    void OnDestroy()
    {
        Brick.Grabbed -= OnBrickGrabbed;
    }

    // ---------- Starting and stopping ----------

    public void StartSet(SetData set)
    {
        ExitSet(keepBuild: false);
        _set = set;

        _modelBounds = ModelBounds(set);
        Vector3 bottom = new Vector3(_modelBounds.center.x, _modelBounds.min.y, _modelBounds.center.z);

        // In front of you; on your table if there's one nearby.
        (Vector3 point, Quaternion facing) = _sandbox.InFrontOfUser(0f, 0.35f, 0.5f);
        if (RoomSurfaces.TryFindSurface(point, 0.5f, out Vector3 surface))
            point = surface;

        _root = new GameObject($"Set {set.setNumber}").transform;
        _root.SetPositionAndRotation(point - facing * bottom, facing);

        // A handle at the front edge moves the whole build.
        _handle = _sandbox.CreateHandle();
        _handle.SetPositionAndRotation(
            _root.TransformPoint(bottom + new Vector3(0f, 0.006f, -_modelBounds.extents.z - 0.03f)), _root.rotation);
        _handleToRoot = _handle.InverseTransformPoint(_root.position);
        CreateTray();

        _panel.SetTitle(set.title);
        (Vector3 panelPosition, Quaternion panelRotation) = _sandbox.InFrontOfUser(-0.3f, 0.05f);
        _panel.Open(panelPosition, panelRotation);

        ShowStep(0);
    }

    /// <summary>Stops building. With keepBuild, the parts placed so far stay as a normal model.</summary>
    public void ExitSet(bool keepBuild = true)
    {
        if (_set == null)
            return;

        ClearTargets();
        DestroyTray();

        if (keepBuild)
            ReleasePlacedParts();
        _placed.Clear();

        if (_root != null)
            Destroy(_root.gameObject);
        if (_handle != null)
            Destroy(_handle.gameObject);
        _root = null;
        _handle = null;
        _set = null;
        _advanceAt = -1f;
        _panel.Close();
    }

    void Restart()
    {
        SetData set = _set;
        ExitSet(keepBuild: false);
        StartSet(set);
    }

    // ---------- Steps ----------

    void ShowStep(int index)
    {
        ClearTargets();
        _step = index;

        if (_step >= _set.steps.Count)
        {
            Complete();
            return;
        }

        foreach (SetData.Placement placement in _set.steps[_step].parts)
        {
            BrickPartData part = _sandbox.FindPart(placement.partId);
            if (part == null)
            {
                Debug.LogWarning($"SetBuilder: part {placement.partId} isn't imported; skipping it.");
                continue;
            }
            _targets.Add(new Target { placement = placement, part = part, ghost = CreateGhost(placement, part) });
        }
        foreach (Target target in _targets)
            target.ghostRenderer = target.ghost.GetComponent<MeshRenderer>();

        SupplyParts();
        RefreshPanel();

        if (_targets.Count == 0)
            _advanceAt = Time.time; // nothing usable in this step
    }

    GameObject CreateGhost(SetData.Placement placement, BrickPartData part)
    {
        var ghost = new GameObject($"Ghost {part.partId}");
        ghost.transform.SetParent(_root, false);
        ghost.transform.localPosition = placement.position;
        ghost.transform.localRotation = placement.rotation;
        ghost.AddComponent<MeshFilter>().sharedMesh = part.mesh;

        var renderer = ghost.AddComponent<MeshRenderer>();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.sharedMaterials = Enumerable.Repeat(_ghostMaterial, Mathf.Max(1, part.mesh.subMeshCount)).ToArray();
        TintGhost(renderer, placement.colorCode, GhostAlpha);
        return ghost;
    }

    static void TintGhost(MeshRenderer renderer, int colorCode, float alpha)
    {
        Color color = LDrawColors.Get(colorCode);
        color.a = alpha;
        MenuStyle.Tint(renderer, color);
    }

    void Complete()
    {
        DestroyTray();
        ReleasePlacedParts();
        _placed.Clear();
        if (_handle != null)
            Destroy(_handle.gameObject);
        if (_root != null)
            Destroy(_root.gameObject); // empty now: the finished parts were released
        _handle = null;
        _root = null;

        _stepLabel.text = "Complete!";
        _detailLabel.text = $"{_set.PartCount} parts built";
        Sandbox.Message($"{_set.title} complete!");
        _set = null;
    }

    // Finished (or exited) builds become ordinary bricks you can grab, save, or store, bonded into one
    // model so wheels, clips, and sideways-stud parts stay attached.
    void ReleasePlacedParts()
    {
        foreach (Brick brick in _placed)
        {
            if (brick == null)
                continue;
            brick.transform.SetParent(null, worldPositionStays: true);
            brick.SetLocked(false);
        }
        Brick.BondTogether(_placed.Where(b => b != null));
    }

    // ---------- Parts tray ----------

    const float TrayWidth = 0.2f, TrayMargin = 0.012f;

    // A flat tray to the right of the build, with its own handle at the front edge.
    void CreateTray()
    {
        _tray = new GameObject("Parts Tray").transform;
        _tray.SetPositionAndRotation(
            _root.TransformPoint(new Vector3(_modelBounds.max.x + 0.06f, _modelBounds.min.y, _modelBounds.min.z)), _root.rotation);
        _traySlab = MenuStyle.CreateBox(_tray, "Tray", Vector3.zero, Vector3.one * 0.01f, MenuStyle.Panel).GetComponent<MeshFilter>();

        _trayHandle = _sandbox.CreateHandle();
        _trayHandle.SetPositionAndRotation(_tray.TransformPoint(new Vector3(TrayWidth * 0.5f, 0.006f, -0.03f)), _tray.rotation);
        _trayHandleToTray = _trayHandle.InverseTransformPoint(_tray.position);
    }

    // The parts still needed for this step, laid out in rows on the tray.
    void SupplyParts()
    {
        ClearTray();
        float x = 0f, z = 0f, rowDepth = 0f;

        foreach (Target target in _targets)
        {
            if (target.filled)
                continue;

            Brick brick = _sandbox.SpawnBrickWithColorCode(target.part, target.placement.colorCode);
            Bounds b = brick.BodyBounds;
            if (x > 0f && x + b.size.x > TrayWidth - TrayMargin * 2f)
            {
                x = 0f;
                z += rowDepth + TrayGap;
                rowDepth = 0f;
            }

            brick.transform.SetParent(_tray, worldPositionStays: false);
            brick.transform.localRotation = Quaternion.identity;
            brick.transform.localPosition = new Vector3(TrayMargin + x - b.min.x, -b.min.y, TrayMargin + z - b.min.z);
            x += b.size.x + TrayGap;
            rowDepth = Mathf.Max(rowDepth, b.size.z);
            _trayParts.Add(brick);
        }

        // Size the tray's base to fit what's on it.
        float depth = z + rowDepth + TrayMargin * 2f;
        _traySlab.sharedMesh = MeshBuilder.Box(new Vector3(TrayWidth, 0.004f, depth));
        _traySlab.transform.localPosition = new Vector3(TrayWidth * 0.5f, -0.002f, depth * 0.5f);
    }

    // Parts taken off the tray belong to the room, not the tray, so moving the tray leaves them behind.
    void OnBrickGrabbed(Brick brick)
    {
        if (_tray != null && brick.transform.parent == _tray)
            brick.transform.SetParent(null, worldPositionStays: true);
    }

    // Removes unused parts still sitting on the tray. Parts you've moved elsewhere are left alone.
    void ClearTray()
    {
        foreach (Brick brick in _trayParts)
        {
            if (brick != null && _tray != null && brick.transform.parent == _tray)
                Destroy(brick.gameObject);
        }
        _trayParts.Clear();
    }

    void DestroyTray()
    {
        ClearTray();
        if (_tray != null)
            Destroy(_tray.gameObject);
        if (_trayHandle != null)
            Destroy(_trayHandle.gameObject);
        _tray = null;
        _trayHandle = null;
    }

    void ClearTargets()
    {
        foreach (Target target in _targets)
        {
            if (target.ghost != null)
                Destroy(target.ghost);
        }
        _targets.Clear();
    }

    // ---------- Placing ----------

    /// <summary>If the released brick is near an empty ghost of the same part and color, locks it there.</summary>
    public static bool TryPlace(Brick brick)
    {
        if (_instance == null || !_instance.IsBuilding)
            return false;
        Target target = _instance.FindTarget(brick);
        if (target == null)
            return false;
        _instance.Place(brick, target);
        return true;
    }

    /// <summary>True while a held brick is close enough to its ghost to be placed.</summary>
    public static bool HasTargetFor(Brick brick) =>
        _instance != null && _instance.IsBuilding && _instance.FindTarget(brick) != null;

    Target FindTarget(Brick brick)
    {
        if (brick.part == null || brick.CarriedBricks.Any())
            return null; // one part at a time

        Vector3 center = brick.transform.TransformPoint(brick.BodyBounds.center);
        Target best = null;
        float bestDistance = PlaceDistance * PlaceDistance;
        foreach (Target target in _targets)
        {
            if (target.filled || target.part != brick.part || target.placement.colorCode != brick.colorCode)
                continue;

            Vector3 targetCenter = _root.TransformPoint(target.placement.position + target.placement.rotation * brick.BodyBounds.center);
            float distance = (targetCenter - center).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = target;
            }
        }
        return best;
    }

    void Place(Brick brick, Target target)
    {
        brick.transform.SetParent(_root, worldPositionStays: false);
        brick.transform.localPosition = target.placement.position;
        brick.transform.localRotation = target.placement.rotation;
        brick.SetLocked(true);

        target.filled = true;
        if (target.ghost != null)
            Destroy(target.ghost);
        _placed.Add(brick);
        _trayParts.Remove(brick);
        RefreshPanel();

        if (_targets.All(t => t.filled))
            _advanceAt = Time.time + AdvanceDelay;
    }

    void AutoPlace()
    {
        if (!IsBuilding)
            return;
        foreach (Target target in _targets.Where(t => !t.filled).ToList())
            Place(_sandbox.SpawnBrickWithColorCode(target.part, target.placement.colorCode), target);
        ClearTray();
    }

    // ---------- Every frame ----------

    void Update()
    {
        if (!IsBuilding)
            return;

        if (_advanceAt >= 0f && Time.time >= _advanceAt)
        {
            _advanceAt = -1f;
            ShowStep(_step + 1);
            return;
        }

        // Ghosts gently pulse; the one a held part would drop into glows.
        Target hot = null;
        foreach (Brick held in Brick.Held)
        {
            hot = FindTarget(held);
            if (hot != null)
                break;
        }
        float pulse = GhostAlpha * (1f + 0.375f * Mathf.Sin(Time.time * 4f)); // pulse scales with the setting
        foreach (Target target in _targets)
        {
            if (target.filled || target.ghostRenderer == null)
                continue;
            TintGhost(target.ghostRenderer, target.placement.colorCode, target == hot ? GhostReadyAlpha : pulse);
        }
    }

    // The build and the tray each follow their handle.
    void LateUpdate()
    {
        if (_root != null && _handle != null)
            _root.SetPositionAndRotation(_handle.TransformPoint(_handleToRoot), _handle.rotation);
        if (_tray != null && _trayHandle != null)
            _tray.SetPositionAndRotation(_trayHandle.TransformPoint(_trayHandleToTray), _trayHandle.rotation);
    }

    // ---------- Instruction panel ----------

    void BuildPanel()
    {
        const float width = 0.16f, buttonHeight = 0.024f, gap = 0.006f;
        var size = new Vector2(width, 0.045f + 0.05f + 4 * (buttonHeight + gap) + 0.01f);
        _panel = MenuPanel.Create("Instructions", "", size, _sandbox.CreateHandle());

        float y = _panel.ContentTop - 0.012f;
        _stepLabel = MenuStyle.CreateText(_panel.Content, "", new Vector3(0f, y, -0.001f), new Vector2(width - 0.02f, 0.02f));
        y -= 0.022f;
        _detailLabel = MenuStyle.CreateText(_panel.Content, "", new Vector3(0f, y, -0.001f), new Vector2(width - 0.02f, 0.013f));
        y -= 0.026f;

        var buttonSize = new Vector2(width - 0.02f, buttonHeight);
        AddButton("Auto-place Step", ref y, buttonSize, AutoPlace);
        AddButton("Parts Again", ref y, buttonSize, () => { if (IsBuilding) SupplyParts(); });
        AddButton("Restart", ref y, buttonSize, () => { if (IsBuilding) Restart(); });
        AddButton("Exit Set", ref y, buttonSize, () => { if (IsBuilding) ExitSet(); else _panel.Close(); });
        _panel.Close();

        void AddButton(string label, ref float yy, Vector2 sizeOfButton, System.Action onClick)
        {
            MenuButton button = MenuButton.Create(_panel.Content, label, new Vector3(0f, yy, 0f), sizeOfButton, MenuStyle.Button, label);
            button.Clicked += onClick;
            yy -= sizeOfButton.y + gap;
        }
    }

    void RefreshPanel()
    {
        if (!IsBuilding)
            return;
        int remaining = _targets.Count(t => !t.filled);
        _stepLabel.text = $"Step {_step + 1} of {_set.steps.Count}";
        _detailLabel.text = remaining == 0 ? "Step done!" : $"{remaining} {(remaining == 1 ? "part" : "parts")} to place";
    }

    // ---------- Helpers ----------

    // Bounds of the finished model, from every part's mesh, in model space.
    Bounds ModelBounds(SetData set)
    {
        var bounds = new Bounds();
        bool first = true;
        foreach (SetData.Step step in set.steps)
        {
            foreach (SetData.Placement placement in step.parts)
            {
                BrickPartData part = _sandbox.FindPart(placement.partId);
                if (part == null || part.mesh == null)
                    continue;
                Bounds m = part.mesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = new Vector3(
                        (corner & 1) == 0 ? m.min.x : m.max.x,
                        (corner & 2) == 0 ? m.min.y : m.max.y,
                        (corner & 4) == 0 ? m.min.z : m.max.z);
                    Vector3 p = placement.position + placement.rotation * local;
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
        }
        return bounds;
    }
}
