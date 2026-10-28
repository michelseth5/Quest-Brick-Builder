using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction;
using TMPro;
using UnityEngine;

/// <summary>
/// Y menu > Fix Snapping: shows the held piece enlarged and see-through on a stand, with every connection point
/// as a colored marker (male green, female blue, clips orange, hinges yellow, special/ball purple). Click a
/// marker to select it, then nudge, re-point, lengthen or delete it; or pick a type to add and click on the piece
/// where it goes (the spot clicks to the grid, and the point faces straight out of or into the surface).
/// Save applies the points to every copy of the part (ConnectionOverrides).
/// </summary>
public class SnapEditor : MonoBehaviour
{
    const float Ldu = 0.0004f;
    const float DisplaySize = 0.2f;   // the enlarged piece's longest side, meters
    const float PanelWidth = 0.36f, TypeColumn = 0.08f;
    const float ButtonHeight = 0.024f, Gap = 0.004f;

    enum PointType { Stud, Socket, Bar, BarHole, Clip, Axle, AxleHole, Pin, PinHole, Hinge }

    // The kinds of point, down the left of the panel; picking one shows what can be added of it on the right
    // and makes its markers pulse on the piece.
    enum Family { Stud, Bar, Axle, Pin, Hinge, Special }

    static readonly (Family family, string label, (PointType type, string label)[] adds)[] Families =
    {
        (Family.Stud, "Studs", new[] { (PointType.Stud, "Stud"), (PointType.Socket, "Socket") }),
        (Family.Bar, "Bars & Clips", new[] { (PointType.Bar, "Bar"), (PointType.BarHole, "Bar Hole"), (PointType.Clip, "Clip") }),
        (Family.Axle, "Axles", new[] { (PointType.Axle, "Axle"), (PointType.AxleHole, "Axle Hole") }),
        (Family.Pin, "Pins", new[] { (PointType.Pin, "Pin"), (PointType.PinHole, "Pin Hole") }),
        (Family.Hinge, "Hinges", new[] { (PointType.Hinge, "Hinge") }),
        (Family.Special, "Special", new (PointType, string)[0]),
    };

    static Family FamilyOf(Connector point)
    {
        switch (point.kind)
        {
            case Connector.Kind.Clip: return Family.Bar;
            case Connector.Kind.Finger: return Family.Hinge;
            case Connector.Kind.Generic:
            case Connector.Kind.Ball: return Family.Special;
        }
        string profile = point.profile ?? "";
        if (profile.Contains("A"))
            return Family.Axle;
        if (profile.StartsWith("R4"))
            return Family.Bar;
        if (point.slide || profile.Contains("R8"))
            return Family.Pin;
        return Family.Stud;
    }

    static string LabelOf(PointType type) =>
        Families.SelectMany(f => f.adds).First(a => a.type == type).label;

    static readonly float[] Grids = { 1f, 2f, 5f, 10f };

    // Hinge kinds, copied from real parts. Fingers only fit their own family (group and size) and a pattern
    // that interlocks with theirs. The pattern lists finger and gap widths in LDU along the hinge's axis.
    static readonly (string name, string button, bool male, string profile, float length, string group)[] HingeKinds =
    {
        ("locking, 1 finger (44301)", "Locking 1 Finger", true, "F6|M|8", 8f, "lckHng"),
        ("locking, 2 fingers (44302)", "Locking 2 Fingers", true, "F6|M|4 8 4", 16f, "lckHng"),
        ("classic plate, 3 fingers (4275)", "Classic 3 Fingers", true, "F4|M|4 4 4 4 4", 20f, ""),
        ("classic plate, 2 fingers (4276)", "Classic 2 Fingers", false, "F4|F|4 4 4 4 4", 20f, ""),
        ("hinge brick top (3938)", "Hinge Brick Top", true, "F4|M|18 4 18", 40f, "hgBrC"),
        ("hinge brick base (3937)", "Hinge Brick Base", false, "F4|F|18 4 18", 40f, "hgBrC"),
    };

    static int HingeKindOf(Connector point) =>
        System.Array.FindIndex(HingeKinds, k => k.profile == point.profile && k.group == (point.group ?? ""));

    static readonly Color Male = new Color(0.25f, 0.85f, 0.3f), Female = new Color(0.25f, 0.55f, 1f),
                          ClipColor = new Color(1f, 0.6f, 0.15f), FingerColor = new Color(1f, 0.9f, 0.2f),
                          PointColor = new Color(0.75f, 0.35f, 1f);

    Sandbox _sandbox;
    MenuPanel _panel;
    TextMeshPro _status, _details;

    BrickPartData _part;
    Color _color;
    readonly List<Connector> _points = new List<Connector>();
    bool _changed;
    int _selected = -1;
    PointType? _placing;
    Family? _family;    // the kind picked on the left
    int _hingeKind;     // the kind of hinge "+ Hinge" adds
    int _grid = 1;      // index into Grids
    float _discardArmedUntil;
    readonly List<Transform> _axisLabels = new List<Transform>();

    // The display: a stand (turned by the view buttons), the part's own space scaled up under it, the
    // see-through piece, and the markers.
    Transform _stand, _space;
    MeshCollider _modelCollider;
    readonly List<(GameObject go, Renderer renderer)> _markers = new List<(GameObject, Renderer)>();
    GameObject _preview;

    public bool IsOpen => _stand != null;

    public void Init(Sandbox sandbox)
    {
        _sandbox = sandbox;
        _panel = MenuPanel.Create("Fix Snapping", "Fix Snapping", new Vector2(PanelWidth, 0.3f), sandbox.CreateHandle());
        _panel.Closed += () =>
        {
            if (_stand != null)
                CloseDisplay();
        };
        _panel.Close();
    }

    /// <summary>Opens the editor for a part, showing it in the given color.</summary>
    public void Open(BrickPartData part, Color color)
    {
        if (IsOpen)
            CloseDisplay();
        _part = part;
        _color = color;
        _points.Clear();
        _points.AddRange(Brick.ConnectorsOf(part));
        _changed = false;
        _selected = -1;
        _placing = null;
        _family = null;

        (Vector3 position, Quaternion rotation) = _sandbox.InFrontOfUser(-0.12f, 0.02f, 0.45f);
        BuildDisplay(position, rotation);
        _panel.Open(position + rotation * new Vector3(0.27f, 0.02f, 0.03f), rotation);
        BuildPanel();
    }

    // ---------- Display ----------

    void BuildDisplay(Vector3 position, Quaternion rotation)
    {
        Bounds bounds = _part.mesh != null ? _part.mesh.bounds : _part.bodyBounds;
        float scale = Mathf.Clamp(DisplaySize / Mathf.Max(0.004f, bounds.size.x, bounds.size.y, bounds.size.z), 1f, 25f);

        // The stand: grab it (grip or trigger, hand on the piece) to move and turn it any way you like. An invisible
        // grab box around the enlarged piece; it can't be grabbed by laser, so it doesn't block clicks on markers.
        Vector3 grabSize = bounds.size * scale + Vector3.one * 0.01f;
        GameObject stand = _sandbox.CreateGrabbableProp("Fix Snapping Display", null, new Bounds(Vector3.zero, grabSize), MenuStyle.Handle);
        stand.GetComponent<MeshRenderer>().enabled = false;
        _stand = stand.transform;
        _stand.SetPositionAndRotation(position, rotation);
        _space = new GameObject("Part Space").transform;
        _space.SetParent(_stand, false);
        _space.localScale = Vector3.one * scale;
        _space.localPosition = -bounds.center * scale; // the piece's middle on the stand

        // The piece, see-through so points inside it (sockets, holes) show.
        var model = new GameObject("Piece");
        model.SetActive(false);
        model.transform.SetParent(_space, false);
        model.AddComponent<MeshFilter>().sharedMesh = _part.mesh;
        var renderer = model.AddComponent<MeshRenderer>();
        Color seeThrough = _color;
        seeThrough.a = 0.35f;
        renderer.sharedMaterials = BrickPartData.MaterialsFor(_part.mesh, _part.submeshColors, seeThrough,
                                                              MenuStyle.Material, _sandbox.transparentMaterial);
        _modelCollider = model.AddComponent<MeshCollider>();
        _modelCollider.sharedMesh = _part.mesh;
        Pointable pointer = Pointable.Add(model, _modelCollider);
        pointer.Pointed += OnPiecePointed;
        model.SetActive(true);
        _modelCollider.enabled = false; // only while adding a point (it would hide the markers inside from the laser)

        RebuildMarkers();
        BuildAxes(bounds, scale);
    }

    static readonly Color AxisX = new Color(1f, 0.3f, 0.3f), AxisY = new Color(0.95f, 0.95f, 0.95f), AxisZ = new Color(1f, 0.5f, 0.85f);

    // Lines through the part's origin along X, Y and Z, labelled at their + ends, so the X/Y/Z buttons make sense.
    void BuildAxes(Bounds bounds, float scale)
    {
        _axisLabels.Clear();
        float reach = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z) + 12f * Ldu; // past the piece
        float thickness = Mathf.Max(0.6f * Ldu, 0.0012f / scale);                                // ~1 mm thick
        foreach ((Vector3 axis, Color color, string name) in new[] { (Vector3.right, AxisX, "X+"), (Vector3.up, AxisY, "Y+ up"), (Vector3.forward, AxisZ, "Z+") })
        {
            // From the far - end to the + end, the + half brighter.
            AxisLine(-axis * reach, axis, reach, thickness * 0.6f, Color.Lerp(color, Color.black, 0.5f));
            AxisLine(Vector3.zero, axis, reach, thickness, color);

            TextMeshPro label = MenuStyle.CreateText(_stand, name, Vector3.zero, new Vector2(0.04f, 0.014f));
            label.color = color;
            label.transform.localPosition = _space.localPosition + axis * (reach * scale + 0.012f);
            _axisLabels.Add(label.transform);
        }
    }

    void AxisLine(Vector3 from, Vector3 axis, float length, float radius, Color color)
    {
        var go = new GameObject("Axis");
        go.transform.SetParent(_space, false);
        go.transform.localPosition = from;
        go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis);
        go.transform.localScale = new Vector3(radius, length, radius);
        go.AddComponent<MeshFilter>().sharedMesh = MeshBuilder.UnitCylinder();
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = MenuStyle.Material;
        MenuStyle.Tint(renderer, color);
    }

    void CloseDisplay()
    {
        if (_stand != null)
            Destroy(_stand.gameObject);
        _stand = null;
        _space = null;
        _markers.Clear();
        _axisLabels.Clear();
        _preview = null;
        _placing = null;
    }

    void RebuildMarkers()
    {
        foreach ((GameObject go, Renderer _) in _markers)
            Destroy(go);
        _markers.Clear();
        for (int i = 0; i < _points.Count; i++)
        {
            int index = i;
            GameObject marker = CreateMarker(_points[i], "Point " + i, pointable: true);
            marker.GetComponent<Pointable>().Pointed += evt =>
            {
                if (evt.Type == PointerEventType.Select)
                    Select(index);
            };
            _markers.Add((marker, marker.GetComponent<Renderer>()));
        }
        RefreshMarkerColors();
    }

    // A marker: a cylinder along the point's shape (a short fat one for point-like fittings), in the part's space.
    GameObject CreateMarker(Connector point, string name, bool pointable)
    {
        var go = new GameObject(name);
        go.SetActive(false);
        go.transform.SetParent(_space, false);
        float radius = RadiusOf(point) * Ldu;
        float length = Mathf.Max(point.length, 2f * Ldu);
        Vector3 axis = point.axis.sqrMagnitude > 0f ? point.axis.normalized : Vector3.up;
        Vector3 start = point.IsPoint ? point.position - axis * (3f * Ldu) : point.Start;
        if (point.IsPoint)
            length = 6f * Ldu;
        go.transform.localPosition = start;
        go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis);
        go.transform.localScale = new Vector3(radius, length, radius);
        go.AddComponent<MeshFilter>().sharedMesh = MeshBuilder.UnitCylinder();
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = MenuStyle.Material;
        MenuStyle.Tint(renderer, ColorOf(point));
        if (pointable)
        {
            var box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.5f, 0f);
            box.size = new Vector3(2.4f, 1.2f, 2.4f);
            box.isTrigger = true;
            Pointable.Add(go, box);
        }
        go.SetActive(true);
        return go;
    }

    static float RadiusOf(Connector point)
    {
        // The widest size in its profile ("R8 R6" -> 8 LDU), within reason.
        float radius = 0f;
        foreach (string token in (point.profile ?? "").Split(' '))
        {
            if (token.Length > 1 && "RSAB".IndexOf(token[0]) >= 0
                && float.TryParse(token.Substring(1), System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out float r))
                radius = Mathf.Max(radius, r);
        }
        radius = Mathf.Clamp(radius > 0f ? radius : 4f, 2f, 10f);
        return point.male ? radius * 0.9f : radius * 1.1f; // a socket shows just outside what plugs into it
    }

    static Color ColorOf(Connector point)
    {
        switch (point.kind)
        {
            case Connector.Kind.Clip: return ClipColor;
            case Connector.Kind.Finger: return FingerColor;
            case Connector.Kind.Generic:
            case Connector.Kind.Ball: return PointColor;
            default: return point.male ? Male : Female;
        }
    }

    // Selected: pale. With a kind picked on the left, its markers pulse and the others go dim.
    void RefreshMarkerColors()
    {
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 6f);
        for (int i = 0; i < _markers.Count; i++)
        {
            Color color = ColorOf(_points[i]);
            if (i == _selected)
                color = Color.Lerp(color, Color.white, 0.6f);
            else if (_family != null && FamilyOf(_points[i]) == _family)
                color = Color.Lerp(color, Color.white, 0.45f * pulse);
            else if (_family != null)
                color = Color.Lerp(color, new Color(0.15f, 0.15f, 0.15f), 0.65f);
            MenuStyle.Tint(_markers[i].renderer, color);
        }
    }

    void Update()
    {
        if (!IsOpen)
            return;
        if (_family != null)
            RefreshMarkerColors(); // the pulse

        // The axis labels face you.
        Camera eye = Camera.main;
        if (eye != null)
        {
            foreach (Transform label in _axisLabels)
            {
                if (label != null)
                    label.rotation = Quaternion.LookRotation(label.position - eye.transform.position, Vector3.up);
            }
        }
    }

    // ---------- Selecting and changing a point ----------

    void Select(int index)
    {
        if (_placing != null)
            return;
        _selected = index == _selected ? -1 : index;
        if (_selected >= 0)
        {
            _family = FamilyOf(_points[_selected]); // its kind's buttons on the right
            int kind = HingeKindOf(_points[_selected]);
            if (kind >= 0)
                _hingeKind = kind;
        }
        RefreshMarkerColors();
        BuildPanel();
    }

    void PickFamily(Family family)
    {
        _family = _family == family ? (Family?)null : family;
        if (_placing != null)
            StopPlacing();
        if (_selected >= 0 && FamilyOf(_points[_selected]) != _family)
            _selected = -1;
        RefreshMarkerColors();
        BuildPanel();
    }

    void Changed(bool rebuild = true)
    {
        _changed = true;
        if (rebuild)
            RebuildMarkers();
        BuildPanel();
    }

    void Nudge(Vector3 direction)
    {
        if (_selected < 0)
            return;
        Connector point = _points[_selected];
        point.position += direction * Grids[_grid] * Ldu;
        _points[_selected] = point;
        Changed();
    }

    static readonly Vector3[] Directions = { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };

    void PointAnotherWay()
    {
        if (_selected < 0)
            return;
        Connector point = _points[_selected];
        int current = System.Array.FindIndex(Directions, d => Vector3.Dot(d, point.axis) > 0.99f);
        Vector3 axis = Directions[(current + 1) % Directions.Length];
        // A centered shape turns about its middle; others about their start.
        point.axis = axis;
        point.reference = Perpendicular(axis);
        _points[_selected] = point;
        Changed();
    }

    void Lengthen(float ldu)
    {
        if (_selected < 0)
            return;
        Connector point = _points[_selected];
        if (point.IsPoint)
            return;
        float length = Mathf.Clamp(point.length / Ldu + ldu, 2f, 400f);
        point.length = length * Ldu;
        point.sections = SectionsFor(point.profile, length);
        _points[_selected] = point;
        Changed();
    }

    // Picks a hinge kind (finger pattern): for hinges added from now on, and for the selected hinge, which keeps
    // where it is and which way it runs.
    void SetHingeKind(int index)
    {
        _hingeKind = index;
        if (_selected < 0 || _points[_selected].kind != Connector.Kind.Finger)
        {
            BuildPanel();
            return;
        }
        Connector point = _points[_selected];
        var kind = HingeKinds[index];
        point.male = kind.male;
        point.profile = kind.profile;
        point.length = kind.length * Ldu;
        point.group = kind.group;
        _points[_selected] = point;
        Changed();
    }

    void DeleteSelected()
    {
        if (_selected < 0)
            return;
        _points.RemoveAt(_selected);
        _selected = -1;
        Changed();
    }

    // ---------- Adding a point ----------

    void StartPlacing(PointType type)
    {
        _placing = _placing == type ? (PointType?)null : type; // clicking the same type again stops
        _selected = -1;
        _modelCollider.enabled = _placing != null;
        SetMarkersPointable(_placing == null);
        HidePreview();
        RefreshMarkerColors();
        BuildPanel();
    }

    void StopPlacing()
    {
        _placing = null;
        _modelCollider.enabled = false;
        SetMarkersPointable(true);
        HidePreview();
    }

    void SetMarkersPointable(bool on)
    {
        foreach ((GameObject go, Renderer _) in _markers)
        {
            var box = go.GetComponent<BoxCollider>();
            if (box != null)
                box.enabled = on;
        }
    }

    void OnPiecePointed(PointerEvent evt)
    {
        if (_placing == null)
            return;
        switch (evt.Type)
        {
            case PointerEventType.Hover:
            case PointerEventType.Move:
                ShowPreview(Candidate(_placing.Value, evt.Pose));
                break;
            case PointerEventType.Unhover:
                HidePreview();
                break;
            case PointerEventType.Select:
                _points.Add(Candidate(_placing.Value, evt.Pose));
                _placing = null;
                _modelCollider.enabled = false;
                HidePreview();
                _selected = _points.Count - 1;
                Changed();
                SetMarkersPointable(true);
                break;
        }
    }

    // The point the laser's spot would make: on the grid across the surface, facing straight out of (male)
    // or into (female) it.
    Connector Candidate(PointType type, Pose hit)
    {
        Vector3 local = _space.InverseTransformPoint(hit.position) / Ldu;
        Vector3 normal = NearestAxis(_space.InverseTransformDirection(hit.rotation * Vector3.forward));
        float grid = Grids[_grid];
        for (int a = 0; a < 3; a++)
        {
            bool across = Mathf.Abs(normal[a]) < 0.5f;
            // Across the surface: the grid. Along its normal: the nearest half LDU (faces sit at whole numbers).
            float step = across ? grid : 0.5f;
            local[a] = Mathf.Round(local[a] / step) * step;
        }
        return Template(type, local * Ldu, normal, _hingeKind);
    }

    static Connector Template(PointType type, Vector3 position, Vector3 normal, int hingeKind)
    {
        if (type == PointType.Hinge)
        {
            // A hinge's fingers wrap round an axis running along the surface, just outside it.
            var kind = HingeKinds[hingeKind];
            Vector3 along = Perpendicular(normal);
            return new Connector
            {
                kind = Connector.Kind.Finger,
                male = kind.male,
                position = position + normal * (6f * Ldu),
                axis = along,
                reference = normal,
                length = kind.length * Ldu,
                center = true,
                openEnded = true,
                profile = kind.profile,
                group = kind.group,
                sections = "",
            };
        }

        // (male, kind, profile, length LDU, slides, open-ended): shapes as the LDCad shadow library gives them.
        (bool male, Connector.Kind kind, string profile, float length, bool slide, bool open) shape = type switch
        {
            PointType.Stud => (true, Connector.Kind.Cylinder, "R6", 4f, false, false),
            PointType.Socket => (false, Connector.Kind.Cylinder, "R6", 4f, false, false),
            PointType.Bar => (true, Connector.Kind.Cylinder, "R4", 16f, true, true),
            PointType.BarHole => (false, Connector.Kind.Cylinder, "R4", 8f, true, true),
            PointType.Clip => (false, Connector.Kind.Clip, "R4", 8f, false, true),
            PointType.Axle => (true, Connector.Kind.Cylinder, "A6", 20f, true, true),
            PointType.AxleHole => (false, Connector.Kind.Cylinder, "A6", 20f, true, true),
            PointType.Pin => (true, Connector.Kind.Cylinder, "R6", 20f, true, true),
            _ => (false, Connector.Kind.Cylinder, "R8 R6", 20f, true, true), // pin hole, like a Technic beam's
        };

        Vector3 axis = shape.male ? normal : -normal;
        bool clip = shape.kind == Connector.Kind.Clip;
        if (clip)
            axis = Perpendicular(normal); // a clip holds a bar running along the surface
        return new Connector
        {
            kind = shape.kind,
            male = shape.male,
            position = position,
            axis = axis,
            reference = Perpendicular(axis),
            length = shape.length * Ldu,
            center = clip,
            slide = shape.slide,
            openEnded = shape.open,
            profile = shape.profile,
            group = "",
            sections = clip ? "" : SectionsFor(shape.profile, shape.length),
        };
    }

    // The physical sections along a shape (see Connections.Fouls): a pin hole has collar recesses at both ends.
    static string SectionsFor(string profile, float length)
    {
        if (profile == "R8 R6" && length > 4f)
            return $"R8:2 R6:{length - 4f:0.##} R8:2";
        string first = (profile ?? "").Split(' ')[0];
        return first.Length > 0 ? $"{first}:{length:0.##}" : "";
    }

    void ShowPreview(Connector point)
    {
        HidePreview();
        _preview = CreateMarker(point, "Preview", pointable: false);
        MenuStyle.Tint(_preview.GetComponent<Renderer>(), Color.Lerp(ColorOf(point), Color.white, 0.4f));
    }

    void HidePreview()
    {
        if (_preview != null)
            Destroy(_preview);
        _preview = null;
    }

    static Vector3 NearestAxis(Vector3 v)
    {
        Vector3 best = Vector3.up;
        float most = float.MinValue;
        foreach (Vector3 d in Directions)
        {
            float dot = Vector3.Dot(d, v);
            if (dot > most)
            {
                most = dot;
                best = d;
            }
        }
        return best;
    }

    static Vector3 Perpendicular(Vector3 axis) =>
        Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up;

    // ---------- Panel ----------

    void BuildPanel()
    {
        _panel.ClearContent();
        _panel.SetTitle($"Fix Snapping: {_part.ShortName}");

        // Left: the kinds of point. Right: what goes with the picked kind (adding, its own settings), then moving
        // and deleting the selected point. Along the bottom: grid, save, restore, close.
        var family = _family != null ? Families.First(f => f.family == _family) : default;
        bool lengthens = _family == Family.Bar || _family == Family.Axle || _family == Family.Pin;
        int rightRows = 1                                               // add buttons (or a hint)
                        + (_family == Family.Hinge ? 3 : lengthens ? 1 : 0) // hinge kinds / longer-shorter
                        + 2;                                            // X/Y/Z, and Point Another Way + Delete
        float rightHeight = rightRows * (ButtonHeight + Gap) + Gap * 2f;
        float leftHeight = Families.Length * (ButtonHeight + Gap);
        float contentHeight = 0.034f + Mathf.Max(leftHeight, rightHeight) + Gap * 2f + (ButtonHeight + Gap) + 0.008f;
        float titleBar = _panel.Size.y * 0.5f - _panel.ContentTop; // the same at any size
        _panel.SetSize(new Vector2(PanelWidth, titleBar + contentHeight));

        float inner = PanelWidth - 0.02f, left = -inner * 0.5f;
        float y = _panel.ContentTop - 0.008f;
        _status = MenuStyle.CreateText(_panel.Content, "", new Vector3(0f, y, -0.001f), new Vector2(inner, 0.012f));
        y -= 0.014f;
        _details = MenuStyle.CreateText(_panel.Content, "", new Vector3(0f, y, -0.001f), new Vector2(inner, 0.01f));
        y -= 0.012f + ButtonHeight * 0.5f;
        ShowStatus();
        float top = y;

        // A row of buttons across [x0, x0 + width] at height y.
        void Row(float x0, float width, ref float rowY, params (string label, System.Action click, bool active)[] buttons)
        {
            float each = (width - Gap * (buttons.Length - 1)) / buttons.Length;
            for (int i = 0; i < buttons.Length; i++)
            {
                (string label, System.Action click, bool active) = buttons[i];
                MenuButton button = MenuButton.Create(_panel.Content, label,
                    new Vector3(x0 + each * 0.5f + i * (each + Gap), rowY, 0f), new Vector2(each, ButtonHeight),
                    active ? MenuStyle.ButtonActive : MenuStyle.Button, label);
                if (click != null)
                    button.Clicked += click;
            }
            rowY -= ButtonHeight + Gap;
        }

        // The kinds, with how many of each the piece has.
        float leftY = top;
        foreach ((Family f, string label, (PointType, string)[] _) in Families)
        {
            int count = _points.Count(p => FamilyOf(p) == f);
            Family pick = f;
            Row(left, TypeColumn, ref leftY, ($"{label} <size=70%><color=#AAB>{count}</color></size>", () => PickFamily(pick), _family == f));
        }

        // The picked kind's buttons.
        float rx = left + TypeColumn + Gap * 2f, rw = inner - TypeColumn - Gap * 2f;
        float rightY = top;
        if (_family == null)
        {
            MenuStyle.CreateText(_panel.Content, "Pick a kind on the left to add one or see where they are",
                new Vector3(rx + rw * 0.5f, rightY, -0.001f), new Vector2(rw, 0.011f));
            rightY -= ButtonHeight + Gap;
        }
        else if (family.adds.Length == 0)
        {
            MenuStyle.CreateText(_panel.Content, "Special fittings and ball joints can be moved or deleted",
                new Vector3(rx + rw * 0.5f, rightY, -0.001f), new Vector2(rw, 0.011f));
            rightY -= ButtonHeight + Gap;
        }
        else
        {
            Row(rx, rw, ref rightY, family.adds.Select(a =>
                ("+ " + a.label, (System.Action)(() => StartPlacing(a.type)), _placing == a.type)).ToArray());
        }
        if (_family == Family.Hinge)
        {
            for (int k = 0; k < HingeKinds.Length; k += 2)
            {
                int first = k, second = k + 1;
                Row(rx, rw, ref rightY,
                    (HingeKinds[first].button, () => SetHingeKind(first), _hingeKind == first),
                    (HingeKinds[second].button, () => SetHingeKind(second), _hingeKind == second));
            }
        }
        else if (lengthens)
        {
            Row(rx, rw, ref rightY, ("Longer", () => Lengthen(2f), false), ("Shorter", () => Lengthen(-2f), false));
        }
        rightY -= Gap * 2f;

        // The selected point.
        Row(rx, rw, ref rightY, ("X-", () => Nudge(Vector3.left), false), ("X+", () => Nudge(Vector3.right), false),
            ("Y-", () => Nudge(Vector3.down), false), ("Y+", () => Nudge(Vector3.up), false),
            ("Z-", () => Nudge(Vector3.back), false), ("Z+", () => Nudge(Vector3.forward), false));
        Row(rx, rw, ref rightY, ("Point Another Way", PointAnotherWay, false), ("Delete", DeleteSelected, false));

        y = Mathf.Min(leftY, rightY) - Gap * 2f;
        Row(left, inner, ref y, ($"Grid: {Grids[_grid]:0} LDU", () => { _grid = (_grid + 1) % Grids.Length; BuildPanel(); }, false),
            ("Save", Save, _changed),
            ("Restore Original", RestoreOriginal, false),
            ("Close", CloseClicked, false));
    }

    void ShowStatus()
    {
        if (_status == null)
            return;
        if (_placing != null)
        {
            _status.text = $"Point at the piece where the new {LabelOf(_placing.Value).ToLower()} goes and pull the trigger";
            _details.text = $"It clicks to the {Grids[_grid]:0} LDU grid (a stud is 20 LDU across). Click the + button again to stop";
            return;
        }
        if (_selected < 0 && _family != null)
        {
            string label = Families.First(f => f.family == _family).label.ToLower();
            int count = _points.Count(p => FamilyOf(p) == _family);
            _status.text = count == 0
                ? $"No {label} on this piece yet. Add one with the buttons on the right"
                : $"{count} {label}: the flashing markers. Click one to select it, or add another";
            _details.text = "Grab the piece to turn it. The X, Y and Z lines show which way the nudge buttons move";
            return;
        }
        if (_selected < 0)
        {
            _status.text = $"{_points.Count} connection points. Click a marker to select it. Grab the piece to turn it";
            _details.text = "<color=#6D6>green</color> male   <color=#7AF>blue</color> female   <color=#FA4>orange</color> clip   " +
                            "<color=#FE4>yellow</color> hinge   <color=#C7F>purple</color> special";
            return;
        }
        Connector point = _points[_selected];
        Vector3 at = point.position / Ldu;
        _status.text = $"{Describe(point)}, pointing {DirectionName(point.axis)}";
        _details.text = $"at x {at.x:0.#}, y {at.y:0.#}, z {at.z:0.#} LDU · {point.length / Ldu:0.#} LDU long · {point.profile}";
    }

    static string Describe(Connector point)
    {
        string profile = point.profile ?? "";
        switch (point.kind)
        {
            case Connector.Kind.Clip: return "Clip";
            case Connector.Kind.Finger:
                int kind = HingeKindOf(point);
                return kind >= 0 ? "Hinge, " + HingeKinds[kind].name : "Hinge (" + point.profile + ")";
            case Connector.Kind.Ball: return point.male ? "Ball" : "Ball socket";
            case Connector.Kind.Generic: return "Special fitting";
        }
        if (profile.Contains("A"))
            return point.male ? "Axle" : "Axle hole";
        if (profile.StartsWith("R4"))
            return point.male ? "Bar" : "Bar hole";
        if (point.slide || profile.Contains("R8"))
            return point.male ? "Pin" : "Pin hole";
        return point.male ? "Stud" : "Stud socket";
    }

    static string DirectionName(Vector3 axis)
    {
        string[] names = { "Y+ (up)", "Y- (down)", "X+", "X-", "Z+", "Z-" };
        for (int i = 0; i < Directions.Length; i++)
        {
            if (Vector3.Dot(axis.normalized, Directions[i]) > 0.99f)
                return names[i];
        }
        return "at an angle";
    }

    // ---------- Saving ----------

    void Save()
    {
        ConnectionOverrides.Save(_part, _points.ToArray());
        _changed = false;
        BuildPanel();
        _sandbox.ShowMessage($"Saved. Every {_part.ShortName} snaps with these points now");
    }

    void RestoreOriginal()
    {
        ConnectionOverrides.Restore(_part);
        _points.Clear();
        _points.AddRange(Brick.ConnectorsOf(_part));
        _changed = false;
        _selected = -1;
        RebuildMarkers();
        BuildPanel();
        _sandbox.ShowMessage($"{_part.ShortName} is back to its original connection points");
    }

    void CloseClicked()
    {
        if (_changed && Time.time > _discardArmedUntil)
        {
            _discardArmedUntil = Time.time + 3f;
            _status.text = "Not saved: click Close again to throw these changes away";
            return;
        }
        _panel.Close(); // (closes the display too)
    }
}
