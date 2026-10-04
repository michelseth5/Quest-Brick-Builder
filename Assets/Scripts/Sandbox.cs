using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

/// <summary>
/// Sandbox mode: free building with any part in any color.
///   Y (left controller)  - open/close the parts menu
///   B (right controller) - open/close the color menu
///   A (right controller) - duplicate whatever you're holding
///   X (left controller)  - save the model you're holding
/// </summary>
public class Sandbox : MonoBehaviour
{
    static readonly string[] CategoryOrder = { "Brick", "Plate", "Tile", "Slope" };
    const string BaseplateCategory = "Baseplate";
    const int DefaultColorCode = 4;   // Red
    const int BaseplateColorCode = 2; // Green

    [Tooltip("The brick prefab. Also used as the template for other grabbable things (trash can, menu handles).")]
    public Brick brickPrefab;

    [Tooltip("Imported parts (Assets/LDrawParts/PartCatalog).")]
    public BrickCatalog catalog;

    [Tooltip("See-through material used for transparent colors. BrickGhost works.")]
    public Material transparentMaterial;

    [Header("Menus")]
    [Tooltip("How far in front of you menus open, in meters.")]
    public float menuDistance = 0.45f;
    [Min(1)] public int partColumns = 4;

    int _colorIndex;
    MenuPanel _mainMenu;
    MenuPanel _partsPanel;
    MenuPanel _colorPanel;

    readonly Dictionary<string, MenuButton> _categoryButtons = new Dictionary<string, MenuButton>();
    readonly List<MenuButton> _swatches = new List<MenuButton>();
    MeshRenderer _colorIndicatorSwatch;
    TextMeshPro _colorIndicatorLabel;
    TextMeshPro _colorPanelName;

    // Menu previews and the grid cell (panel-local center) each one sits in.
    readonly Dictionary<Brick, (BrickPartData part, Vector3 cellCenter)> _previews =
        new Dictionary<Brick, (BrickPartData, Vector3)>();
    float _cellSize;

    readonly Dictionary<string, BrickPartData> _partsById =
        new Dictionary<string, BrickPartData>(System.StringComparer.OrdinalIgnoreCase);
    ModelMenu _modelMenu;
    SetBuilder _setBuilder;
    MenuPanel _startMenu;
    GameObject _trashCan;
    GameObject _message;

    BrickColors.Entry CurrentColor => BrickColors.All[_colorIndex];

    static Sandbox _instance;

    void Awake()
    {
        _instance = this;
        _colorIndex = Mathf.Max(0, BrickColors.IndexOfCode(DefaultColorCode));
        Brick.PreviewTaken += OnPreviewTaken;

        WristInventory inventory = gameObject.AddComponent<WristInventory>();
        inventory.ghostMaterial = brickPrefab != null ? brickPrefab.ghostMaterial : null;
    }

    /// <summary>Shows a short floating message, if the sandbox is running.</summary>
    public static void Message(string text)
    {
        if (_instance != null)
            _instance.ShowMessage(text);
    }

    void OnDestroy()
    {
        Brick.PreviewTaken -= OnPreviewTaken;
    }

    void Start()
    {
        if (brickPrefab == null || catalog == null)
        {
            Debug.LogError("Sandbox: assign the Brick Prefab and Catalog.", this);
            enabled = false;
            return;
        }

        foreach (BrickPartData part in catalog.parts)
        {
            if (part != null && !string.IsNullOrEmpty(part.partId))
                _partsById[part.partId] = part;
        }

        MenuStyle.Material = brickPrefab.GetComponent<MeshRenderer>().sharedMaterial;
        BuildMainMenu();
        BuildPartsPanel();
        BuildColorPanel();
        _mainMenu.Close();
        _partsPanel.Close();
        _colorPanel.Close();

        _modelMenu = gameObject.AddComponent<ModelMenu>();
        _modelMenu.Init(this);

        _setBuilder = gameObject.AddComponent<SetBuilder>();
        _setBuilder.Init(this, brickPrefab.ghostMaterial);

        BuildStartMenu();
        StartCoroutine(OpenStartMenuWhenTracking());
    }

    // ---------- Start menu (game modes) ----------

    const float StartMenuWidth = 0.22f;

    void BuildStartMenu()
    {
        _startMenu = MenuPanel.Create("Start Menu", "Brick Builder", new Vector2(StartMenuWidth, 0.15f), CreateHandle());
        _startMenu.Close();
    }

    // Head tracking reports a zero pose for the first few frames; wait so the menu opens in front of you.
    System.Collections.IEnumerator OpenStartMenuWhenTracking()
    {
        for (float waited = 0f; waited < 3f; waited += Time.deltaTime)
        {
            if (Camera.main != null && Camera.main.transform.position != Vector3.zero)
                break;
            yield return null;
        }
        OpenStartMenu();
    }

    void ToggleStartMenu()
    {
        if (_startMenu.IsOpen)
            _startMenu.Close();
        else
            OpenStartMenu();
    }

    void OpenStartMenu()
    {
        ShowStartHome();
        (Vector3 position, Quaternion rotation) = InFrontOfUser(0f, 0.05f);
        _startMenu.Open(position, rotation);
    }

    void ShowStartHome()
    {
        var buttons = new List<(string, System.Action)>
        {
            ("Free Build", () => _startMenu.Close()),
            ("Build a Set", ShowSetList),
        };
        if (_setBuilder.IsBuilding)
            buttons.Add(("Exit Current Set", () => { _setBuilder.ExitSet(); _startMenu.Close(); }));
        buttons.Add(("Settings", ShowSettings));
        FillStartMenu("Brick Builder", "The left controller's menu button brings you back here", buttons);
    }

    void ShowSettings()
    {
        var buttons = new List<(string, System.Action)>();
        for (int i = 0; i < GameSettings.GhostVisibilityNames.Length; i++)
        {
            var choice = (GameSettings.GhostVisibility)i;
            buttons.Add((GameSettings.GhostVisibilityNames[i], () => { GameSettings.Ghosts = choice; ShowSettings(); }));
        }
        buttons.Add(("Back", ShowStartHome));

        List<MenuButton> created = FillStartMenu("Settings", "Set-building ghosts", buttons);
        created[(int)GameSettings.Ghosts].SetSelected(true);
    }

    void ShowSetList()
    {
        var buttons = new List<(string, System.Action)>();
        foreach (SetData set in SetData.LoadAll().OrderBy(s => s.setNumber))
        {
            SetData chosen = set;
            buttons.Add(($"{set.title}  <size=70%><color=#AAB>{set.setNumber} · {set.PartCount} parts</color></size>",
                         () => { _startMenu.Close(); CloseBuildMenus(); _setBuilder.StartSet(chosen); }));
        }
        buttons.Add(("Back", ShowStartHome));
        FillStartMenu("Build a Set", buttons.Count == 1 ? "No sets imported yet" : "Choose a set", buttons);
    }

    // Rebuilds the start menu as a subtitle plus a column of buttons.
    List<MenuButton> FillStartMenu(string title, string subtitle, List<(string label, System.Action onClick)> buttons)
    {
        var created = new List<MenuButton>();
        const float buttonHeight = 0.028f, gap = 0.006f;
        _startMenu.ClearContent();
        _startMenu.SetTitle(title);
        _startMenu.SetSize(new Vector2(StartMenuWidth, 0.075f + buttons.Count * (buttonHeight + gap)));

        float y = _startMenu.ContentTop - 0.008f;
        MenuStyle.CreateText(_startMenu.Content, subtitle, new Vector3(0f, y, -0.001f), new Vector2(StartMenuWidth - 0.03f, 0.011f));
        y -= 0.012f + gap + buttonHeight * 0.5f;

        foreach ((string label, System.Action onClick) in buttons)
        {
            MenuButton button = MenuButton.Create(_startMenu.Content, label, new Vector3(0f, y, 0f),
                                                  new Vector2(StartMenuWidth - 0.03f, buttonHeight), MenuStyle.Button, label);
            button.Clicked += onClick;
            created.Add(button);
            y -= buttonHeight + gap;
        }
        return created;
    }

    void CloseBuildMenus()
    {
        _mainMenu.Close();
        _partsPanel.Close();
        _modelMenu.Close();
        _colorPanel.Close();
    }

    void Update()
    {
        if (OVRInput.GetDown(OVRInput.RawButton.Y))
            TogglePartsMenu();
        if (OVRInput.GetDown(OVRInput.RawButton.B))
            ToggleColorMenu();
        if (OVRInput.GetDown(OVRInput.RawButton.A))
            DuplicateHeld();
        if (OVRInput.GetDown(OVRInput.RawButton.X))
            SaveHeldModel();
        if (OVRInput.GetDown(OVRInput.RawButton.Start))
            ToggleStartMenu();
    }

    public BrickPartData FindPart(string partId) =>
        partId != null && _partsById.TryGetValue(partId, out BrickPartData part) ? part : null;

    /// <summary>The display color for any LDraw color code.</summary>
    public Color ColorForCode(int code) => LDrawColors.Get(code);

    // ---------- Opening and closing ----------

    void TogglePartsMenu()
    {
        if (_mainMenu.IsOpen || _partsPanel.IsOpen || _modelMenu.IsOpen)
        {
            _mainMenu.Close();
            _partsPanel.Close();
            _modelMenu.Close();
            return;
        }

        (Vector3 position, Quaternion rotation) = InFrontOfUser(-0.1f, 0.08f);
        _mainMenu.Open(position, rotation);
    }

    void ToggleColorMenu()
    {
        if (_colorPanel.IsOpen)
        {
            _colorPanel.Close();
            return;
        }

        (Vector3 position, Quaternion rotation) = InFrontOfUser(0.22f, 0.02f);
        _colorPanel.Open(position, rotation);
    }

    // A pose in front of the player's head: offset to the right and down, upright, facing them.
    public (Vector3, Quaternion) InFrontOfUser(float right, float down, float distance = -1f)
    {
        Transform head = Camera.main != null ? Camera.main.transform : transform;
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.forward;
        forward.Normalize();
        Vector3 rightDir = Vector3.Cross(Vector3.up, forward);

        float d = distance > 0f ? distance : menuDistance;
        Vector3 position = head.position + forward * d + rightDir * right + Vector3.down * down;
        return (position, Quaternion.LookRotation(forward, Vector3.up));
    }

    // ---------- Main parts menu ----------

    const float MainWidth = 0.15f, ButtonHeight = 0.026f, ButtonGap = 0.006f, SectionGap = 0.012f;
    const int CategoriesPerPage = 8;
    const int PartsPerPage = 16;

    List<string> _categories = new List<string>();
    int _categoryPage;
    Transform _categoryArea;
    float _categoryTop;
    string _openCategory;
    int _partsPage;

    void BuildMainMenu()
    {
        const float width = MainWidth, buttonHeight = ButtonHeight, gap = ButtonGap, sectionGap = SectionGap;

        List<string> categories = catalog.parts
            .Where(p => p != null)
            .Select(p => p.Category)
            .Distinct()
            .Where(c => c != BaseplateCategory)
            .OrderBy(c => System.Array.IndexOf(CategoryOrder, c) is int i && i >= 0 ? i : CategoryOrder.Length)
            .ThenBy(c => c)
            .ToList();
        bool hasBaseplate = catalog.parts.Any(p => p != null && p.Category == BaseplateCategory);

        // Categories are shown a page at a time, with a Prev/Next row when there are more than fit.
        _categories = categories;
        bool paged = categories.Count > CategoriesPerPage;
        int categoryRows = Mathf.Min(categories.Count, CategoriesPerPage) + (paged ? 1 : 0);

        int buttonCount = categoryRows + (hasBaseplate ? 1 : 0) + 1 + 2 + 1;
        float height = 0.045f + buttonCount * (buttonHeight + gap) + sectionGap * 3f;

        _mainMenu = MenuPanel.Create("Parts Menu", "Parts", new Vector2(width, height), CreateHandle());
        float y = _mainMenu.ContentTop - buttonHeight * 0.5f;
        var buttonSize = new Vector2(width - 0.02f, buttonHeight);

        _categoryArea = new GameObject("Categories").transform;
        _categoryArea.SetParent(_mainMenu.Content, false);
        _categoryTop = y;
        ShowCategoryPage(0);
        y -= categoryRows * (buttonHeight + gap);

        y -= sectionGap;
        if (hasBaseplate)
        {
            MenuButton baseplate = MenuButton.Create(_mainMenu.Content, "Baseplate", new Vector3(0f, y, 0f), buttonSize,
                                                     MenuStyle.Button, "Get Baseplate");
            baseplate.Clicked += SpawnBaseplate;
            y -= buttonHeight + gap;
        }

        MenuButton trash = MenuButton.Create(_mainMenu.Content, "Trash", new Vector3(0f, y, 0f), buttonSize,
                                             MenuStyle.Button, "Get Trash Can");
        trash.Clicked += SpawnTrashCan;
        y -= buttonHeight + gap + sectionGap;

        MenuButton save = MenuButton.Create(_mainMenu.Content, "Save", new Vector3(0f, y, 0f), buttonSize,
                                            MenuStyle.Button, "Save Everything");
        save.Clicked += SaveEverything;
        y -= buttonHeight + gap;

        MenuButton load = MenuButton.Create(_mainMenu.Content, "Load", new Vector3(0f, y, 0f), buttonSize,
                                            MenuStyle.Button, "Load Model");
        load.Clicked += OpenModelMenu;
        y -= buttonHeight + gap + sectionGap;

        // Current color, with a swatch. Clicking it opens the color menu.
        MenuButton color = MenuButton.Create(_mainMenu.Content, "Color", new Vector3(0f, y, 0f), buttonSize,
                                             MenuStyle.Button, "");
        color.Clicked += () => { if (!_colorPanel.IsOpen) ToggleColorMenu(); };
        _colorIndicatorSwatch = MenuStyle.CreateBox(color.transform, "Swatch",
            new Vector3(-buttonSize.x * 0.5f + 0.014f, 0f, -MenuButton.Depth * 0.5f - 0.001f),
            new Vector3(0.016f, 0.016f, 0.002f), CurrentColor.color);
        _colorIndicatorLabel = MenuStyle.CreateText(color.transform, CurrentColor.name,
            new Vector3(0.01f, 0f, -MenuButton.Depth * 0.5f - 0.0005f), new Vector2(buttonSize.x - 0.04f, buttonHeight * 0.6f));
    }

    void ShowCategoryPage(int page)
    {
        foreach (Transform child in _categoryArea)
            Destroy(child.gameObject);
        _categoryButtons.Clear();

        int pageCount = Mathf.Max(1, Mathf.CeilToInt(_categories.Count / (float)CategoriesPerPage));
        _categoryPage = Mathf.Clamp(page, 0, pageCount - 1);

        var buttonSize = new Vector2(MainWidth - 0.02f, ButtonHeight);
        float y = _categoryTop;
        foreach (string category in _categories.Skip(_categoryPage * CategoriesPerPage).Take(CategoriesPerPage))
        {
            MenuButton button = MenuButton.Create(_categoryArea, category, new Vector3(0f, y, 0f), buttonSize,
                                                  MenuStyle.Button, Plural(category));
            button.Clicked += () => OpenCategory(category);
            button.SetSelected(category == _openCategory && _partsPanel != null && _partsPanel.IsOpen);
            _categoryButtons[category] = button;
            y -= ButtonHeight + ButtonGap;
        }

        if (pageCount > 1)
        {
            y = _categoryTop - CategoriesPerPage * (ButtonHeight + ButtonGap);
            AddPagingRow(_categoryArea, y, MainWidth - 0.02f, _categoryPage, pageCount, ShowCategoryPage);
        }
    }

    // "< Prev   Page 1 of 3   Next >" across the given width.
    static void AddPagingRow(Transform parent, float y, float width, int page, int pageCount, System.Action<int> showPage)
    {
        const float buttonWidth = 0.035f, height = 0.02f;
        MenuStyle.CreateText(parent, $"{page + 1} / {pageCount}", new Vector3(0f, y, -0.001f), new Vector2(0.04f, height * 0.6f));
        if (page > 0)
        {
            MenuButton prev = MenuButton.Create(parent, "Previous", new Vector3(-width * 0.5f + buttonWidth * 0.5f, y, 0f),
                                                new Vector2(buttonWidth, height), MenuStyle.Button, "<");
            prev.Clicked += () => showPage(page - 1);
        }
        if (page < pageCount - 1)
        {
            MenuButton next = MenuButton.Create(parent, "Next", new Vector3(width * 0.5f - buttonWidth * 0.5f, y, 0f),
                                                new Vector2(buttonWidth, height), MenuStyle.Button, ">");
            next.Clicked += () => showPage(page + 1);
        }
    }

    static string Plural(string category) => category.EndsWith("s") ? category : category + "s";

    // ---------- Part previews ----------

    void BuildPartsPanel()
    {
        _partsPanel = MenuPanel.Create("Parts Panel", "", new Vector2(0.2f, 0.2f), CreateHandle());
        _partsPanel.Closed += () =>
        {
            foreach (MenuButton button in _categoryButtons.Values)
                button.SetSelected(false);
        };
    }

    void OpenCategory(string category) => ShowPartsPage(category, 0);

    void ShowPartsPage(string category, int page)
    {
        _openCategory = category;
        foreach (KeyValuePair<string, MenuButton> pair in _categoryButtons)
            pair.Value.SetSelected(pair.Key == category);

        List<BrickPartData> parts = catalog.parts.Where(p => p != null && p.Category == category).ToList();
        if (parts.Count == 0)
            return;
        _modelMenu.Close(); // it opens in the same spot

        int pageCount = Mathf.CeilToInt(parts.Count / (float)PartsPerPage);
        _partsPage = Mathf.Clamp(page, 0, pageCount - 1);
        List<BrickPartData> pageParts = parts.Skip(_partsPage * PartsPerPage).Take(PartsPerPage).ToList();

        // One cell size per category, big enough for its largest part, so every page matches.
        float largest = parts.Max(p => Mathf.Max(p.bodyBounds.size.x, p.bodyBounds.size.y, p.bodyBounds.size.z));
        _cellSize = Mathf.Max(0.05f, largest + 0.02f);
        const float labelHeight = 0.012f, margin = 0.01f, pagingHeight = 0.03f;

        int columns = Mathf.Min(partColumns, parts.Count);
        int rows = Mathf.CeilToInt(Mathf.Min(parts.Count, PartsPerPage) / (float)columns);
        var size = new Vector2(columns * _cellSize + margin * 2f,
                               0.04f + rows * (_cellSize + labelHeight) + margin + (pageCount > 1 ? pagingHeight : 0f));

        _partsPanel.ClearContent();
        _previews.Clear();
        _partsPanel.SetTitle(Plural(category));
        _partsPanel.SetSize(size);

        for (int i = 0; i < pageParts.Count; i++)
        {
            int column = i % columns, row = i / columns;
            var cellCenter = new Vector3(
                -size.x * 0.5f + margin + _cellSize * (column + 0.5f),
                _partsPanel.ContentTop - row * (_cellSize + labelHeight) - _cellSize * 0.5f,
                0f);

            SpawnPreview(pageParts[i], cellCenter);
            MenuStyle.CreateText(_partsPanel.Content, pageParts[i].ShortName,
                cellCenter + new Vector3(0f, -_cellSize * 0.5f - labelHeight * 0.4f, -0.001f),
                new Vector2(_cellSize - 0.004f, labelHeight * 0.8f));
        }

        if (pageCount > 1)
        {
            AddPagingRow(_partsPanel.Content, -size.y * 0.5f + margin + pagingHeight * 0.4f, size.x - margin * 2f,
                         _partsPage, pageCount, p => ShowPartsPage(category, p));
        }

        if (!_partsPanel.IsOpen)
        {
            // Open beside the main menu, top edges lined up.
            Transform main = _mainMenu.transform;
            Vector3 offset = new Vector3(_mainMenu.Size.x * 0.5f + 0.02f + size.x * 0.5f,
                                         _mainMenu.Size.y * 0.5f - size.y * 0.5f, 0f);
            _partsPanel.Open(main.TransformPoint(offset), main.rotation);
        }
    }

    // A real brick sitting in a menu cell, tilted so you can see its studs. Grabbing it takes it out.
    void SpawnPreview(BrickPartData part, Vector3 cellCenter)
    {
        Brick brick = SpawnBrick(part, _colorIndex, _partsPanel.Content);
        brick.IsPreview = true;

        Quaternion tilt = Quaternion.Euler(-25f, 0f, 0f);
        Vector3 center = cellCenter + new Vector3(0f, 0f, -_cellSize * 0.5f);
        brick.transform.localRotation = tilt;
        brick.transform.localPosition = center - tilt * brick.BodyBounds.center;

        _previews[brick] = (part, cellCenter);
    }

    // When a preview is taken, put a fresh one in its place so the menu is never empty.
    void OnPreviewTaken(Brick brick)
    {
        if (!_previews.TryGetValue(brick, out (BrickPartData part, Vector3 cellCenter) slot))
            return;
        _previews.Remove(brick);
        if (_partsPanel != null && _partsPanel.IsOpen)
            SpawnPreview(slot.part, slot.cellCenter);
    }

    // ---------- Colors ----------

    void BuildColorPanel()
    {
        const int columns = 6;
        const float swatch = 0.028f, gap = 0.008f, nameHeight = 0.022f;
        int rows = Mathf.CeilToInt(BrickColors.All.Length / (float)columns);
        var size = new Vector2(columns * swatch + (columns - 1) * gap + 0.03f,
                               0.045f + rows * (swatch + gap) + nameHeight + 0.01f);

        _colorPanel = MenuPanel.Create("Color Menu", "Colors", size, CreateHandle());

        for (int i = 0; i < BrickColors.All.Length; i++)
        {
            int index = i;
            BrickColors.Entry entry = BrickColors.All[i];
            int column = i % columns, row = i / columns;
            var position = new Vector3(
                -size.x * 0.5f + 0.015f + swatch * 0.5f + column * (swatch + gap),
                _colorPanel.ContentTop - swatch * 0.5f - row * (swatch + gap),
                0f);

            Color display = entry.color;
            display.a = 1f;
            MenuButton button = MenuButton.Create(_colorPanel.Content, entry.name, position,
                                                  new Vector2(swatch, swatch), display, entry.IsTransparent ? "T" : "");
            button.Clicked += () => SelectColor(index);
            _swatches.Add(button);
        }

        _colorPanelName = MenuStyle.CreateText(_colorPanel.Content, "",
            new Vector3(0f, -size.y * 0.5f + nameHeight * 0.5f + 0.006f, -0.001f), new Vector2(size.x - 0.03f, nameHeight * 0.7f));
        RefreshColorDisplays();
    }

    void SelectColor(int index)
    {
        _colorIndex = index;
        RefreshColorDisplays();

        foreach (Brick preview in _previews.Keys)
        {
            if (preview == null)
                continue;
            ApplyColor(preview, _colorIndex);
            preview.Rebuild();
        }
    }

    void RefreshColorDisplays()
    {
        for (int i = 0; i < _swatches.Count; i++)
            _swatches[i].transform.localScale = Vector3.one * (i == _colorIndex ? 1.35f : 1f);

        if (_colorPanelName != null)
            _colorPanelName.text = CurrentColor.name;
        if (_colorIndicatorLabel != null)
            _colorIndicatorLabel.text = CurrentColor.name;
        if (_colorIndicatorSwatch != null)
        {
            Color display = CurrentColor.color;
            display.a = 1f;
            MenuStyle.Tint(_colorIndicatorSwatch, display);
        }
    }

    // ---------- Spawning ----------

    Brick SpawnBrick(BrickPartData part, int colorIndex, Transform parent = null)
    {
        Brick brick = parent != null ? Instantiate(brickPrefab, parent) : Instantiate(brickPrefab);
        brick.part = part;
        brick.transparentMaterial = transparentMaterial;
        ApplyColor(brick, colorIndex);
        brick.Rebuild();
        return brick;
    }

    static void ApplyColor(Brick brick, int colorIndex)
    {
        BrickColors.Entry entry = BrickColors.All[colorIndex];
        brick.color = entry.color;
        brick.colorCode = entry.code;
    }

    void SpawnBaseplate()
    {
        BrickPartData part = catalog.parts.FirstOrDefault(p => p != null && p.Category == BaseplateCategory);
        if (part == null)
            return;

        Brick baseplate = SpawnBrick(part, Mathf.Max(0, BrickColors.IndexOfCode(BaseplateColorCode)));
        (Vector3 position, Quaternion rotation) = InFrontOfUser(0f, 0.3f, menuDistance + 0.1f);
        baseplate.transform.SetPositionAndRotation(position - rotation * baseplate.BodyBounds.center, rotation);
    }

    // Only one trash can at a time: getting a new one removes the old one.
    void SpawnTrashCan()
    {
        if (_trashCan != null)
            Destroy(_trashCan);

        var bounds = new Bounds(new Vector3(0f, TrashCan.Size.y * 0.5f, 0f), TrashCan.Size);
        GameObject trash = CreateGrabbableProp("Trash Can", TrashCan.BuildMesh(), bounds, MenuStyle.Panel);
        trash.AddComponent<TrashCan>();
        _trashCan = trash;

        (Vector3 position, Quaternion rotation) = InFrontOfUser(0.25f, 0.4f);
        trash.transform.SetPositionAndRotation(position, rotation);
    }

    public Transform CreateHandle()
    {
        var size = new Vector3(0.09f, 0.012f, 0.012f);
        GameObject handle = CreateGrabbableProp("Menu Handle", MeshBuilder.Box(size), new Bounds(Vector3.zero, size), MenuStyle.Handle);
        return handle.transform;
    }

    // Copies the brick prefab's grab setup onto a different shape, giving a grabbable non-brick object.
    GameObject CreateGrabbableProp(string name, Mesh mesh, Bounds bounds, Color color)
    {
        Brick template = Instantiate(brickPrefab);
        GameObject prop = template.gameObject;
        DestroyImmediate(template);

        prop.name = name;
        prop.transform.localScale = Vector3.one;
        prop.GetComponent<MeshFilter>().sharedMesh = mesh;

        MeshRenderer renderer = prop.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = MenuStyle.Material;
        MenuStyle.Tint(renderer, color);

        BoxCollider box = prop.GetComponent<BoxCollider>();
        if (box != null)
        {
            box.center = bounds.center;
            box.size = bounds.size;
        }
        return prop;
    }

    // ---------- Saving and loading ----------

    void SaveEverything()
    {
        List<Brick> bricks = Brick.AllPlaced().Where(b => b.part != null).ToList();
        if (bricks.Count == 0 && WristInventory.Export().Count == 0)
        {
            ShowMessage("Nothing to save yet");
            return;
        }
        SaveModel(bricks, includeInventory: true);
    }

    // X while gripping a model saves that model (the held brick plus everything connected to it).
    void SaveHeldModel()
    {
        Brick held = Brick.Held.FirstOrDefault();
        if (held == null)
        {
            ShowMessage("Grip a model, then press X to save it");
            return;
        }

        var bricks = new List<Brick> { held };
        bricks.AddRange(held.CarriedBricks);
        SaveModel(bricks);
    }

    void SaveModel(List<Brick> bricks, bool includeInventory = false)
    {
        string name = ModelFile.NextAutoName();
        ModelFile.Save(name, bricks, includeInventory);

        string details = $"{bricks.Count} {(bricks.Count == 1 ? "part" : "parts")}";
        int stacks = includeInventory ? WristInventory.Export().Count : 0;
        if (stacks > 0)
            details += $" + {stacks} on wrists";
        ShowMessage($"Saved \"{name}\" ({details})");
        if (_modelMenu.IsOpen)
            _modelMenu.Refresh();
    }

    void OpenModelMenu()
    {
        if (_modelMenu.IsOpen)
            return;

        // Beside the main menu, where the parts panel would go.
        _partsPanel.Close();
        Transform main = _mainMenu.transform;
        _modelMenu.Open(main.TransformPoint(new Vector3(_mainMenu.Size.x * 0.5f + 0.17f, 0f, 0f)), main.rotation);
    }

    /// <summary>Builds a saved model from real bricks, sitting in front of you, ready to grab.</summary>
    public void LoadModel(string path)
    {
        List<ModelFile.Placement> placements = ModelFile.Load(path, out string title, out List<ModelFile.InventoryStack> inventory);
        (Vector3 origin, Quaternion facing) = InFrontOfUser(0f, 0.3f, menuDistance + 0.1f);

        int missing = 0;
        var bonds = new Dictionary<int, List<Brick>>();
        foreach (ModelFile.Placement placement in placements)
        {
            Brick brick = SpawnPlacement(placement, origin, facing);
            if (brick == null)
            {
                missing++;
                continue;
            }
            if (placement.bond != 0)
            {
                if (!bonds.TryGetValue(placement.bond, out List<Brick> members))
                    bonds[placement.bond] = members = new List<Brick>();
                members.Add(brick);
            }
        }
        foreach (List<Brick> members in bonds.Values)
            Brick.BondTogether(members);

        // Wrist inventory: build each copy out of sight, then hand it to the inventory.
        var outOfSight = new Vector3(0f, -100f, 0f);
        foreach (ModelFile.InventoryStack stack in inventory)
        {
            for (int copy = 0; copy < stack.count; copy++)
            {
                var members = new List<Brick>();
                foreach (ModelFile.Placement placement in stack.parts)
                {
                    Brick brick = SpawnPlacement(placement, outOfSight, Quaternion.identity);
                    if (brick != null)
                        members.Add(brick);
                    else if (copy == 0)
                        missing++;
                }
                WristInventory.AddLoaded(stack.hand, members);
            }
        }

        ShowMessage(missing == 0
            ? $"Loaded \"{title}\""
            : $"Loaded \"{title}\" ({missing} parts not imported yet)");
    }

    // A saved part as a real brick, placed relative to the given origin and facing. Null if the part isn't imported.
    Brick SpawnPlacement(ModelFile.Placement placement, Vector3 origin, Quaternion facing)
    {
        BrickPartData part = FindPart(placement.partId);
        if (part == null)
            return null;

        Brick brick = SpawnBrickWithColorCode(part, placement.colorCode);
        brick.transform.SetPositionAndRotation(origin + facing * placement.position, facing * placement.rotation);
        return brick;
    }

    /// <summary>A new brick in any LDraw color (not just the picker's palette).</summary>
    public Brick SpawnBrickWithColorCode(BrickPartData part, int colorCode)
    {
        int colorIndex = BrickColors.IndexOfCode(colorCode);
        Brick brick = SpawnBrick(part, Mathf.Max(0, colorIndex));
        if (colorIndex < 0)
        {
            brick.color = LDrawColors.Get(colorCode);
            brick.colorCode = colorCode;
            brick.Rebuild();
        }
        return brick;
    }

    /// <summary>A short message floating in front of you for a few seconds.</summary>
    public void ShowMessage(string text)
    {
        if (_message != null)
            Destroy(_message);

        _message = new GameObject("Message");
        (Vector3 position, Quaternion rotation) = InFrontOfUser(0f, -0.12f, 0.6f);
        _message.transform.SetPositionAndRotation(position, rotation);
        MenuStyle.CreateBox(_message.transform, "Background", new Vector3(0f, 0f, 0.002f), new Vector3(0.28f, 0.035f, 0.004f), MenuStyle.Panel);
        MenuStyle.CreateText(_message.transform, text, new Vector3(0f, 0f, -0.001f), new Vector2(0.26f, 0.02f));
        Destroy(_message, 2.5f);
    }

    // ---------- Duplicate ----------

    // Makes a copy of each held brick (and the group it's carrying), just above it.
    void DuplicateHeld()
    {
        foreach (Brick held in Brick.Held.ToList())
        {
            List<Brick> members = new List<Brick> { held };
            members.AddRange(held.CarriedBricks);

            float height = GroupHeight(members);
            Brick copy = CloneBrick(held, held.transform.position + Vector3.up * (height + 0.02f), held.transform.rotation);

            var copies = new List<Brick> { copy };
            foreach (Brick member in held.CarriedBricks)
            {
                Transform t = member.transform; // carried bricks are children of the held one
                copies.Add(CloneBrick(member, copy.transform.TransformPoint(t.localPosition), copy.transform.rotation * t.localRotation));
            }

            // The copy holds together like the original, including parts that don't connect by studs.
            if (copies.Count > 1)
                Brick.BondTogether(copies);
        }
    }

    static float GroupHeight(List<Brick> members)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (Brick member in members)
        {
            Bounds b = member.BodyBounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = new Vector3(
                    (corner & 1) == 0 ? b.min.x : b.max.x,
                    (corner & 2) == 0 ? b.min.y : b.max.y,
                    (corner & 4) == 0 ? b.min.z : b.max.z);
                float y = member.transform.TransformPoint(local).y;
                min = Mathf.Min(min, y);
                max = Mathf.Max(max, y);
            }
        }
        return max - min;
    }

    Brick CloneBrick(Brick source, Vector3 position, Quaternion rotation)
    {
        Brick brick = Instantiate(brickPrefab, position, rotation);
        brick.part = source.part;
        brick.studsX = source.studsX;
        brick.studsZ = source.studsZ;
        brick.heightInPlates = source.heightInPlates;
        brick.color = source.color;
        brick.colorCode = source.colorCode;
        brick.transparentMaterial = transparentMaterial;
        brick.Rebuild();
        return brick;
    }
}
