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

    [Tooltip("Optional material for instruction book pages (an Unlit material looks best). Leave empty to use the brick material.")]
    public Material bookPageMaterial;

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
    SetDownloadMenu _downloadMenu;
    SnapEditor _snapEditor;
    SetBuilder _setBuilder;
    PartsBin _partsBin;
    InstructionBook _book;
    MenuPanel _startMenu;

    /// <summary>Opens the instruction book for a set (and its "Add PDF" option if it has none yet).</summary>
    public void OpenBook(SetData set) => _book.Open(set);
    GameObject _trashCan;
    GameObject _message;

    BrickColors.Entry CurrentColor => BrickColors.All[_colorIndex];

    static Sandbox _instance;

    void Awake()
    {
        _instance = this;
        _colorIndex = Mathf.Max(0, BrickColors.IndexOfCode(DefaultColorCode));
        Brick.PreviewTaken += OnPreviewTaken;
        Brick.AssemblyPlaced += OnAssemblyPlaced;

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
        Brick.AssemblyPlaced -= OnAssemblyPlaced;
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
            {
                _partsById[part.partId] = part;
                ConnectionOverrides.Apply(part); // points fixed in the game (Fix Snapping)
            }
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

        _downloadMenu = gameObject.AddComponent<SetDownloadMenu>();
        _downloadMenu.Init(this);

        _snapEditor = gameObject.AddComponent<SnapEditor>();
        _snapEditor.Init(this);

        ApplyShadows();

        _setBuilder = gameObject.AddComponent<SetBuilder>();
        _setBuilder.Init(this, brickPrefab.ghostMaterial);

        _partsBin = gameObject.AddComponent<PartsBin>();
        _partsBin.Init(this);

        gameObject.AddComponent<PerformanceHud>();
        gameObject.AddComponent<LockLight>();

        _book = gameObject.AddComponent<InstructionBook>();
        _book.Init(this, bookPageMaterial != null ? bookPageMaterial : MenuStyle.Material);

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

    internal void ShowStartHome()
    {
        var buttons = new List<(string, System.Action)>
        {
            ("Free Build", () => _startMenu.Close()),
            ("Build a Set", ShowSetList),
            ("Build It Yourself", ShowBuildYourselfList),
            ("Get New Sets", _downloadMenu.Show),
        };
        if (_setBuilder.IsBuilding)
            buttons.Add(("Exit Current Set", () => { _setBuilder.ExitSet(); _startMenu.Close(); }));
        if (_partsBin.IsActive)
        {
            buttons.Add(($"Show Parts Bin  <size=70%><color=#AAB>{_partsBin.SetTitle}</color></size>",
                         () => { _startMenu.Close(); _partsBin.Show(); }));
            buttons.Add(("Finish Model", () => { _partsBin.FinishModel(); }));
            buttons.Add(("Stop Building It Yourself", () => { _partsBin.End(); ShowStartHome(); }));
        }
        buttons.Add(("Settings", ShowSettings));
        buttons.Add(("Start Over", OnStartOverClicked));
        List<MenuButton> created = FillStartMenu("Brick Builder", "The left controller's menu button brings you back here", buttons);
        _startOverButton = created[created.Count - 1];
        _startOverArmedUntil = 0f;
    }

    MenuButton _startOverButton;
    float _startOverArmedUntil;

    // First click asks for confirmation; a second click within 3 seconds starts over.
    void OnStartOverClicked()
    {
        if (Time.time < _startOverArmedUntil)
        {
            StartOver();
            return;
        }
        _startOverArmedUntil = Time.time + 3f;
        if (_startOverButton != null && _startOverButton.Label != null)
            _startOverButton.Label.text = "Click again to clear everything";
    }

    /// <summary>
    /// Clears everything (bricks, sets, bins, books, wrists, trash cans, undo) by reloading the scene, just
    /// like opening the app. Saved models, instruction PDFs, and settings are kept.
    /// </summary>
    void StartOver()
    {
        UndoHistory.Clear();
        UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
    }

    // Every set with a parts list (or building steps to work one out from).
    void ShowBuildYourselfList()
    {
        var buttons = new List<(string, System.Action)>();
        foreach (SetData set in SetData.LoadAll().Where(s => s.HasInventory || s.HasSteps).OrderBy(s => s.title))
        {
            SetData chosen = set;
            buttons.Add(($"{set.title}  <size=70%><color=#AAB>{set.setNumber} · {set.PartCount} parts</color></size>",
                         () =>
                         {
                             _startMenu.Close();
                             CloseBuildMenus();
                             _setBuilder.ExitSet();
                             _partsBin.Begin(chosen);
                         }));
        }
        if (buttons.Count > 0)
            buttons.Add(("Remove a Set", () => ShowRemoveSetList(ShowBuildYourselfList)));
        buttons.Add(("Back", ShowStartHome));
        FillStartMenu("Build It Yourself", buttons.Count == 1 ? "No sets added yet" : "Get a set's parts and build it your way", buttons);
    }

    // Every set, to pick one to remove. The first click on a set asks to confirm; a second one within
    // 3 seconds removes it.
    void ShowRemoveSetList(System.Action back)
    {
        var sets = SetData.LoadAll().Where(s => s.HasInventory || s.HasSteps).OrderBy(s => s.title).ToList();
        var buttons = new List<(string, System.Action)>();
        List<MenuButton> created = null;
        SetData armed = null;
        float armedUntil = 0f;
        for (int i = 0; i < sets.Count; i++)
        {
            SetData set = sets[i];
            int index = i;
            buttons.Add(($"{set.title}  <size=70%><color=#AAB>{set.setNumber}</color></size>", () =>
            {
                if (armed == set && Time.time < armedUntil)
                {
                    RemoveSet(set);
                    ShowRemoveSetList(back);
                    return;
                }
                if (armed != null && armed != set)
                {
                    // Only one at a time: the other row gets its own text back.
                    int other = sets.IndexOf(armed);
                    if (created[other] != null && created[other].Label != null)
                        created[other].Label.text = buttons[other].Item1;
                }
                armed = set;
                armedUntil = Time.time + 3f;
                MenuButton button = created[index];
                if (button != null && button.Label != null)
                    button.Label.text = $"Click again to remove {set.title} and its instructions";
            }));
        }
        buttons.Add(("Back", back));
        created = FillStartMenu("Remove a Set", sets.Count == 0 ? "No sets to remove"
                                                                : "Click a set twice to remove it (Settings can bring it back)", buttons);
    }

    // Takes a set out of the game: stops building it, deletes its instruction PDFs, and hides it from the lists.
    // Models you've built and saved stay.
    void RemoveSet(SetData set)
    {
        if (_setBuilder.CurrentSet == set)
            _setBuilder.ExitSet();
        if (_partsBin.Set == set)
            _partsBin.End();
        if (_book.Set == set)
            _book.Close();
        int booklets = InstructionBook.DeleteInstructions(set.setNumber);
        SetData.Remove(set.setNumber);
        ShowMessage(booklets > 0
            ? $"Removed {set.title} and {booklets} instruction booklet{(booklets == 1 ? "" : "s")}"
            : $"Removed {set.title}");
    }

    void ShowSettings()
    {
        // One row per setting; clicking a row steps to its next option.
        int ghostCount = GameSettings.GhostVisibilityNames.Length;
        int jointCount = GameSettings.JointStepNames.Length;
        var buttons = new List<(string, System.Action)>
        {
            ($"Ghosts: {GameSettings.GhostVisibilityNames[(int)GameSettings.Ghosts]}",
             () => { GameSettings.Ghosts = (GameSettings.GhostVisibility)(((int)GameSettings.Ghosts + 1) % ghostCount); ShowSettings(); }),
            ($"Joints: {GameSettings.JointStepNames[GameSettings.JointStep]}",
             () => { GameSettings.JointStep = (GameSettings.JointStep + 1) % jointCount; ShowSettings(); }),
            ($"Performance readout: {(GameSettings.ShowPerformance ? "On" : "Off")}",
             () => { GameSettings.ShowPerformance = !GameSettings.ShowPerformance; ShowSettings(); }),
            ($"Fast drawing for big models: {(GameSettings.MergeModels ? "On" : "Off")}",
             () => { GameSettings.MergeModels = !GameSettings.MergeModels; ShowSettings(); }),
            ($"Locked pieces as one object: {(GameSettings.CollapseLocked ? "On" : "Off")}",
             () => { GameSettings.CollapseLocked = !GameSettings.CollapseLocked; ShowSettings(); }),
            ($"Mark pieces not fully plugged in: {(GameSettings.ShowDriftMarkers ? "On" : "Off")}",
             () => { GameSettings.ShowDriftMarkers = !GameSettings.ShowDriftMarkers; ShowSettings(); }),
            ($"Hide covered studs in locked models: {(GameSettings.HideCoveredStuds ? "On" : "Off")}",
             () => { GameSettings.HideCoveredStuds = !GameSettings.HideCoveredStuds; ShowSettings(); }),
            (SetData.RemovedCount > 0 ? $"Bring back removed sets ({SetData.RemovedCount})" : "No removed sets",
             () => { if (SetData.RemovedCount > 0) { SetData.RestoreRemoved(); ShowMessage("Removed sets are back"); } ShowSettings(); }),
            ($"Rebrickable key: {(SetDownloader.HasKey ? "Change" : "Enter")}", () => _downloadMenu.AskKey(ShowSettings)),
            ($"Auto-lock: {(GameSettings.AutoLockSeconds > 0f ? $"after {GameSettings.AutoLockSeconds:0} s" : "Off")}",
             () => { GameSettings.AutoLockSeconds = NextAutoLock(); ShowSettings(); }),
            ($"Shadows: {(GameSettings.Shadows ? "On" : "Off")}",
             () => { GameSettings.Shadows = !GameSettings.Shadows; ApplyShadows(); ShowSettings(); }),
            ("Back", ShowStartHome),
        };
        FillStartMenu("Settings", "Click a setting to change it", buttons);
    }

    void ShowSetList()
    {
        var buttons = new List<(string, System.Action)>();
        foreach (SetData set in SetData.LoadAll().Where(s => s.HasSteps).OrderBy(s => s.setNumber))
        {
            SetData chosen = set;
            buttons.Add(($"{set.title}  <size=70%><color=#AAB>{set.setNumber} · {set.PartCount} parts</color></size>",
                         () => { _startMenu.Close(); CloseBuildMenus(); _partsBin.End(); _setBuilder.StartSet(chosen); }));
        }
        if (buttons.Count > 0)
            buttons.Add(("Remove a Set", () => ShowRemoveSetList(ShowSetList)));
        buttons.Add(("Back", ShowStartHome));
        FillStartMenu("Build a Set", buttons.Count == 1 ? "No sets imported yet" : "Choose a set", buttons);
    }

    /// <summary>Counts start menu screens shown, so a screen can tell whether it's still the one showing.</summary>
    internal int StartMenuVersion { get; private set; }

    // Rebuilds the start menu as a subtitle plus a column of buttons.
    internal List<MenuButton> FillStartMenu(string title, string subtitle, List<(string label, System.Action onClick)> buttons)
    {
        StartMenuVersion++;
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
        // The Start Over confirmation times out.
        if (_startOverArmedUntil > 0f && Time.time >= _startOverArmedUntil)
        {
            _startOverArmedUntil = 0f;
            if (_startOverButton != null && _startOverButton.Label != null)
                _startOverButton.Label.text = "Start Over";
        }

        if (OVRInput.GetDown(OVRInput.RawButton.Y))
            TogglePartsMenu();
        if (OVRInput.GetDown(OVRInput.RawButton.B))
            ToggleColorMenu();
        if (OVRInput.GetDown(OVRInput.RawButton.A))
            DuplicateHeld();
        if (OVRInput.GetDown(OVRInput.RawButton.X))
            ToggleLock();
        UpdatePendingLock();
        AutoLock();
        if (OVRInput.GetDown(OVRInput.RawButton.Start))
            ToggleStartMenu();
        TurnHeldWithThumbsticks();
        if (OVRInput.GetDown(OVRInput.RawButton.LThumbstick))
            Undo();
    }

    // ---------- Turning a held piece ----------

    // Each push of a thumbstick turns what that hand is holding a quarter turn: left/right about straight up,
    // up/down tipping it toward or away from you. The stick has to come back to the middle between pushes.
    const float StickPush = 0.7f, StickRest = 0.3f;
    readonly bool[] _stickPushed = new bool[2];

    void TurnHeldWithThumbsticks()
    {
        for (int hand = 0; hand < 2; hand++)
        {
            Vector2 stick = OVRInput.Get(hand == 0 ? OVRInput.RawAxis2D.LThumbstick : OVRInput.RawAxis2D.RThumbstick);
            if (_stickPushed[hand])
            {
                if (stick.magnitude < StickRest)
                    _stickPushed[hand] = false;
                continue;
            }
            if (stick.magnitude < StickPush)
                continue;
            _stickPushed[hand] = true;

            Brick held = Brick.Held.FirstOrDefault(b => b != null && b.HeldHand == hand);
            if (held == null)
                continue;
            Quaternion turn;
            if (Mathf.Abs(stick.x) >= Mathf.Abs(stick.y))
            {
                turn = Quaternion.AngleAxis(stick.x > 0f ? 90f : -90f, Vector3.up);
            }
            else
            {
                // About the level line across your view, so up tips the top away from you.
                Vector3 across = Vector3.Cross(Vector3.up, Camera.main != null ? Camera.main.transform.forward : Vector3.forward);
                if (across.sqrMagnitude < 1e-6f)
                    across = Vector3.right;
                turn = Quaternion.AngleAxis(stick.y > 0f ? 90f : -90f, across.normalized);
            }
            held.TurnInHand(turn);
        }
    }

    void Undo()
    {
        if (Brick.Held.Count > 0)
            ShowMessage("Let go first, then undo");
        else
            ShowMessage(UndoHistory.Undo() ? "Undone" : "Nothing to undo");
    }

    /// <summary>Whether a part is in the game (with the app or made on the headset), without loading it.</summary>
    public bool HasPart(string partId) => partId != null && (_partsById.ContainsKey(partId) || PartStore.Has(partId));

    public BrickPartData FindPart(string partId) =>
        partId == null ? null
        : _partsById.TryGetValue(partId, out BrickPartData part) ? part
        : PartStore.Find(partId); // made on the headset for a set downloaded in the game

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

        int buttonCount = categoryRows + (hasBaseplate ? 1 : 0) + 1 + 1 + 3 + 1;
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
        y -= buttonHeight + gap;

        MenuButton undo = MenuButton.Create(_mainMenu.Content, "Undo", new Vector3(0f, y, 0f), buttonSize,
                                            MenuStyle.Button, "Undo  <size=70%><color=#AAB>(left stick click)</color></size>");
        undo.Clicked += Undo;
        y -= buttonHeight + gap + sectionGap;

        MenuButton save = MenuButton.Create(_mainMenu.Content, "Save", new Vector3(0f, y, 0f), buttonSize,
                                            MenuStyle.Button, "Save Everything");
        save.Clicked += SaveEverything;
        y -= buttonHeight + gap;

        MenuButton load = MenuButton.Create(_mainMenu.Content, "Load", new Vector3(0f, y, 0f), buttonSize,
                                            MenuStyle.Button, "Load Model");
        load.Clicked += OpenModelMenu;
        y -= buttonHeight + gap;

        MenuButton fix = MenuButton.Create(_mainMenu.Content, "Fix Snapping", new Vector3(0f, y, 0f), buttonSize,
                                           MenuStyle.Button, "Fix Snapping  <size=70%><color=#AAB>(hold the piece)</color></size>");
        fix.Clicked += OpenSnapEditor;
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
            MenuStyle.AddPagingRow(_categoryArea, y, MainWidth - 0.02f, _categoryPage, pageCount, ShowCategoryPage);
        }
    }

    public static string Plural(string category) => category.EndsWith("s") ? category : category + "s";

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
            MenuStyle.AddPagingRow(_partsPanel.Content, -size.y * 0.5f + margin + pagingHeight * 0.4f, size.x - margin * 2f,
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
    void OnPreviewTaken(Brick brick, bool wholeGroupGrab)
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
    internal GameObject CreateGrabbableProp(string name, Mesh mesh, Bounds bounds, Color color)
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

    // Everything in the room that a save holds: placed parts and locked models.
    static List<Brick> SaveableBricks() => Brick.AllPlaced().Where(b => b.part != null || b.IsLockedBody).ToList();

    void SaveEverything()
    {
        List<Brick> bricks = SaveableBricks();
        if (bricks.Count == 0 && WristInventory.Export().Count == 0)
        {
            ShowMessage("Nothing to save yet");
            return;
        }
        // Building a set from its bin: also save the set, which parts came out of it, and the instruction page.
        ModelFile.SetProgress progress = null;
        if (_partsBin.IsActive)
        {
            (int booklet, int page) = InstructionBook.SavedPlace(_partsBin.Set.setNumber);
            progress = new ModelFile.SetProgress { setNumber = _partsBin.Set.setNumber, booklet = booklet, page = page };
        }
        SaveOrAskToReplace(bricks, progress);
    }

    // X locks the loose pieces of a model into it (so they can't be knocked or pulled off), or, when the
    // model has none, unlocks the pieces locked most recently. The model is the one being held, or else
    // the one the left controller is touching.
    //
    // Locked pieces are collapsed into locked bodies (see LockedBody): one object per rigid part of the
    // model, split at hinges and other joints so those still pose. That swaps bricks for other objects,
    // which can't be done under a hand, so a model that's being held is locked once it's put down.
    Brick _pendingLock;

    void ToggleLock()
    {
        Brick target = Brick.Held.FirstOrDefault();
        bool held = target != null;
        if (target == null)
            target = Brick.Nearest(WristInventory.HandPosition(0), 0.12f);
        if (target == null)
        {
            ShowMessage("Hold a model, or touch it with your left controller, then press X to lock it");
            return;
        }

        if (held && GameSettings.CollapseLocked)
        {
            _pendingLock = target;
            ShowMessage(target.WholeModel().Any(b => !b.IsSectionLocked)
                ? "It will lock when you put it down"
                : "The last pieces you locked will unlock when you put it down");
            return;
        }
        ApplyLockToggle(target);
    }

    void UpdatePendingLock()
    {
        if (_pendingLock == null || Brick.Held.Count > 0)
            return;
        Brick target = _pendingLock;
        _pendingLock = null;
        if (target.isActiveAndEnabled)
            ApplyLockToggle(target);
    }

    void ApplyLockToggle(Brick target)
    {
        List<Brick> model = target.WholeModel();
        List<Brick> loose = model.Where(b => !b.IsSectionLocked).ToList();
        if (loose.Count > 0)
        {
            LockLoose(model, loose, automatic: false);
        }
        else if (GameSettings.CollapseLocked && model.Where(b => b.IsLockedBody).Select(b => b.Locked.unit).Distinct().Count() > 1)
        {
            // Nothing loose, but the model is several locked groups snapped together: join them first, so the whole
            // model comes along even with a one-finger grab. (Pressing X again unlocks, as usual.)
            int groups = JoinGroups(model);
            ShowMessage($"Joined {groups} locked groups into one ({Pieces(model.Sum(b => b.PieceCount))}). " +
                        "Press X again to unlock the last pieces you locked");
        }
        else
        {
            // Everything's locked: undo the most recent lock only.
            int newest = model.Max(b => b.LockedSection);
            int total = model.Sum(b => b.PieceCount);
            int unlocked = Unlock(model, newest);
            ShowMessage(unlocked == total
                ? $"Unlocked {Pieces(unlocked)}"
                : $"Unlocked the last {Pieces(unlocked)} ({total - unlocked} still locked)");
        }
        UndoHistory.Clear(); // its steps refer to bricks that may have just been swapped for other objects
    }

    static string Pieces(int count) => count == 1 ? "1 piece" : $"{count} pieces";

    // Locks a model's loose pieces as a new section. Automatic locks (see AutoLock) only speak up when the drift
    // check finds something not fully plugged in.
    void LockLoose(List<Brick> model, List<Brick> loose, bool automatic)
    {
        int section = Brick.NewestSection + 1;
        foreach (Brick brick in loose)
            brick.SetLockedSection(section);
        int total = model.Sum(b => b.PieceCount);
        LockedBody.Drift.Clear();
        if (GameSettings.CollapseLocked)
            Collapse(model);
        string locked = automatic
            ? $"Locked {Pieces(loose.Count)} automatically. X unlocks them"
            : loose.Count == total
            ? $"Locked {Pieces(total)}. Press X again to unlock"
            : $"Locked {Pieces(loose.Count)} more ({total} locked). Press X again to unlock them";
        int drift = LockedBody.Drift.Count;
        if (drift > 0 && GameSettings.ShowDriftMarkers)
        {
            // Something isn't quite in: say so now, and show where, rather than 100 steps later.
            ShowDriftMarkers(LockedBody.Drift);
            locked += drift == 1
                ? "\nHeads up: 1 spot isn't fully plugged in (red marker)"
                : $"\nHeads up: {drift} spots aren't fully plugged in (red markers)";
        }
        else if (automatic)
        {
            return;
        }
        ShowMessage(locked);
    }

    // Makes all of a model's locked groups one group (the biggest one's), rebuilt as one locked body.
    // Returns how many groups there were.
    int JoinGroups(List<Brick> model)
    {
        List<Brick> bodies = model.Where(b => b.IsLockedBody).ToList();
        int groups = bodies.Select(b => b.Locked.unit).Distinct().Count();
        int unit = bodies.OrderByDescending(b => b.PieceCount).First().Locked.unit;
        var pieces = new List<LockedBody.Piece>();
        foreach (Brick body in bodies)
        {
            foreach (LockedBody.Piece piece in PiecesOf(body))
            {
                LockedBody.Piece joined = piece;
                joined.unit = unit;
                pieces.Add(joined);
            }
        }
        foreach (Brick body in bodies)
            Remove(body);
        SpawnLockedBodies(pieces);
        return groups;
    }

    // The next Auto-lock choice in Settings.
    static float NextAutoLock()
    {
        float[] choices = GameSettings.AutoLockChoices;
        int current = System.Array.IndexOf(choices, GameSettings.AutoLockSeconds);
        return choices[(current + 1) % choices.Length];
    }

    // ---------- Shadows ----------

    // Lights that cast shadows, and how, so switching shadows back on restores them.
    readonly Dictionary<Light, LightShadows> _shadowCasters = new Dictionary<Light, LightShadows>();

    // Settings > Shadows. Shadows only reach 3 m, set here at startup: the Quest uses the URP package's default asset
    // (2,048-pixel shadow map over 50 m, ~2.5 cm per shadow pixel, so brick shadows flickered as you moved your head;
    // 3 m is the whole play space, ~1.5 mm per pixel). That asset is read-only in the package, so not edited.
    void ApplyShadows()
    {
        if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp)
            urp.shadowDistance = Mathf.Min(urp.shadowDistance, 3f);
        foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (light.shadows != LightShadows.None)
                _shadowCasters[light] = light.shadows;
        }
        foreach (KeyValuePair<Light, LightShadows> pair in _shadowCasters)
        {
            if (pair.Key != null)
                pair.Key.shadows = GameSettings.Shadows ? pair.Value : LightShadows.None;
        }
    }

    // ---------- Locking automatically ----------

    // With Settings > Auto-lock on, pieces put into a model lock by themselves once nothing has been held for a
    // few seconds: a model's loose pieces are separate objects, and carrying hundreds of them made the frame
    // rate drop (screen shudder, pieces seeming to slide) on the 8,000-piece castle. A piece on its own stays
    // loose; X still unlocks the newest locked pieces to change them.
    float _autoLockAt = float.MaxValue;
    bool _wasHolding;

    void AutoLock()
    {
        bool holding = Brick.Held.Count > 0;
        if (holding || _wasHolding && !holding)
            _autoLockAt = Time.time + GameSettings.AutoLockSeconds; // restart the wait after every release
        _wasHolding = holding;
        if (holding || GameSettings.AutoLockSeconds <= 0f || Time.time < _autoLockAt || _setBuilder.IsBuilding)
            return;
        _autoLockAt = float.MaxValue;

        var loose = new HashSet<Brick>(Brick.AllPlaced().Where(b => !b.IsSectionLocked && !b.IsLockedBody && b.part != null));
        bool any = false;
        while (loose.Count > 0)
        {
            Brick start = loose.First();
            List<Brick> model = start.WholeModel();
            List<Brick> modelLoose = model.Where(b => loose.Contains(b)).ToList();
            loose.ExceptWith(model);
            loose.Remove(start);
            if (model.Count < 2 || modelLoose.Count == 0)
                continue; // a piece on its own isn't part of a model yet
            LockLoose(model, modelLoose, automatic: true);
            any = true;
        }
        if (any)
            UndoHistory.Clear(); // its steps refer to bricks that were just swapped for locked bodies
    }

    // Small red markers where the drift check found a stud or pin not fully in, shown for a few seconds.
    const float DriftMarkerSeconds = 10f;

    void ShowDriftMarkers(List<Vector3> spots)
    {
        foreach (Vector3 spot in spots)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "Drift Marker";
            Destroy(marker.GetComponent<Collider>());
            marker.transform.position = spot;
            marker.transform.localScale = Vector3.one * 0.006f;
            MeshRenderer renderer = marker.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = MenuStyle.Material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            MenuStyle.Tint(renderer, new Color(1f, 0.15f, 0.1f));
            Destroy(marker, DriftMarkerSeconds);
        }
    }

    // Puts newly locked bricks into locked groups. Pieces attached to a locked group join it (the biggest one,
    // if they touch several); pieces attached to none start a new group. Locked groups never merge with each
    // other, so two of them snapped together can still be taken apart until they're unlocked. Only the
    // groups that changed are rebuilt.
    void Collapse(List<Brick> model)
    {
        List<Brick> fresh = model.Where(b => !b.IsLockedBody && b.IsSectionLocked && b.part != null).ToList();
        List<Brick> bodies = model.Where(b => b.IsLockedBody).ToList();

        var pieces = new List<LockedBody.Piece>();
        var rebuilt = new HashSet<Brick>();
        var placed = new HashSet<Brick>();
        foreach (Brick start in fresh)
        {
            if (placed.Contains(start))
                continue;

            // This brick and the other newly locked bricks joined to it.
            var cluster = new List<Brick> { start };
            placed.Add(start);
            for (int i = 0; i < cluster.Count; i++)
            {
                foreach (Brick other in fresh)
                {
                    if (!placed.Contains(other) && cluster[i].IsConnectedTo(other))
                    {
                        placed.Add(other);
                        cluster.Add(other);
                    }
                }
            }

            // The locked group it joins: the biggest one it's attached to, or a new one.
            Brick joins = null;
            foreach (Brick body in bodies)
            {
                if ((joins == null || body.PieceCount > joins.PieceCount) && cluster.Any(b => b.IsConnectedTo(body)))
                    joins = body;
            }
            int unit = joins != null ? joins.Locked.unit : LockedBody.NewUnit();
            if (joins != null && rebuilt.Add(joins))
                pieces.AddRange(PiecesOf(joins));

            foreach (Brick brick in cluster)
            {
                LockedBody.Piece piece = PieceOf(brick);
                piece.unit = unit;
                pieces.Add(piece);
            }
        }

        foreach (Brick brick in fresh)
            Remove(brick);
        foreach (Brick body in rebuilt)
            Remove(body);
        SpawnLockedBodies(pieces);
    }

    // Turns the pieces locked in the given section back into loose bricks, which flash so you can see what
    // came loose: green for a piece on its own, yellow for pieces that came off still joined together.
    // Returns how many.
    int Unlock(List<Brick> model, int section)
    {
        int unlocked = 0;
        var stay = new List<LockedBody.Piece>();
        var loose = new List<Brick>();
        foreach (Brick brick in model)
        {
            if (!brick.IsLockedBody)
            {
                if (brick.LockedSection == section)
                {
                    brick.SetLockedSection(0);
                    loose.Add(brick);
                    unlocked++;
                }
                continue;
            }
            if (brick.Locked.newestSection < section)
                continue; // nothing of this body was in that lock

            foreach (LockedBody.Piece piece in PiecesOf(brick))
            {
                if (piece.section == section)
                {
                    loose.Add(SpawnPiece(piece));
                    unlocked++;
                }
                else
                {
                    stay.Add(piece);
                }
            }
            Remove(brick);
        }
        SpawnLockedBodies(stay);
        FlashUnlocked(loose);
        return unlocked;
    }

    static readonly Color FlashSingle = new Color(0.2f, 1f, 0.3f, 0.75f);
    static readonly Color FlashGroup = new Color(1f, 0.85f, 0.1f, 0.75f);

    void FlashUnlocked(List<Brick> loose)
    {
        Physics.SyncTransforms();
        var seen = new HashSet<Brick>();
        foreach (Brick start in loose)
        {
            if (!seen.Add(start))
                continue;
            var cluster = new List<Brick> { start };
            for (int i = 0; i < cluster.Count; i++)
            {
                foreach (Brick other in loose)
                {
                    if (!seen.Contains(other) && cluster[i].IsConnectedTo(other))
                    {
                        seen.Add(other);
                        cluster.Add(other);
                    }
                }
            }
            foreach (Brick brick in cluster)
                brick.Flash(cluster.Count == 1 ? FlashSingle : FlashGroup);
        }
    }
    LockedBody.Piece PieceOf(Brick brick) => new LockedBody.Piece
    {
        part = brick.part,
        colorCode = brick.colorCode,
        color = brick.color,
        position = brick.transform.position,
        rotation = brick.transform.rotation,
        section = brick.LockedSection,
        fromSet = _partsBin.IsFromBin(brick),
        bond = brick.CurrentBond,
    };

    // A locked body's pieces where they are in the room, carrying the body's bond.
    static List<LockedBody.Piece> PiecesOf(Brick body)
    {
        List<LockedBody.Piece> pieces = body.Locked.Explode(body.transform);
        for (int i = 0; i < pieces.Count; i++)
        {
            LockedBody.Piece piece = pieces[i];
            piece.bond = body.CurrentBond;
            pieces[i] = piece;
        }
        return pieces;
    }

    // Takes a brick out of the game right away (Destroy alone would leave it around until the frame ends).
    void Remove(Brick brick)
    {
        _partsBin.Forget(brick);
        brick.gameObject.SetActive(false);
        Destroy(brick.gameObject);
    }

    // A loose brick for a piece that's been unlocked.
    Brick SpawnPiece(LockedBody.Piece piece)
    {
        Brick brick = SpawnBrickWithColorCode(piece.part, piece.colorCode);
        if (brick.color != piece.color)
        {
            brick.color = piece.color;
            brick.Rebuild();
        }
        brick.transform.SetPositionAndRotation(piece.position, piece.rotation);
        if (piece.bond is Brick.Bond bond)
            brick.RejoinBond(bond);
        if (piece.fromSet)
            _partsBin.Adopt(brick);
        return brick;
    }

    /// <summary>
    /// Builds locked bodies out of pieces posed in the room: one brick per rigid group. Bodies whose pieces
    /// belonged to a bond join it (a bond that's still a saved number is left for the caller, in Locked.bonds).
    /// </summary>
    List<Brick> SpawnLockedBodies(List<LockedBody.Piece> pieces) =>
        SpawnBodies(LockedBody.Build(pieces, MenuStyle.Material, transparentMaterial));

    // A carrier brick for each built body, at its pose.
    List<Brick> SpawnBodies(List<(LockedBody body, Vector3 position, Quaternion rotation)> built)
    {
        var bricks = new List<Brick>();
        foreach ((LockedBody body, Vector3 position, Quaternion rotation) in built)
        {
            Brick brick = Instantiate(brickPrefab, position, rotation);
            brick.name = $"Locked Body ({body.pieces.Count})";
            brick.transparentMaterial = transparentMaterial;
            brick.BecomeLockedBody(body);
            foreach (object key in body.bonds)
            {
                if (key is Brick.Bond bond)
                {
                    brick.RejoinBond(bond);
                    break;
                }
            }
            bricks.Add(brick);
        }
        return bricks;
    }

    // ---------- Assemblies ----------

    void OnAssemblyPlaced(Brick assembly) => SplitAssembly(assembly);

    /// <summary>
    /// Swaps a brick whose part is a ready-made assembly (e.g. a complete universal joint) for the parts it's
    /// made of, in the same place, so the joints between them can be posed. Left alone if any of those parts
    /// isn't imported or they wouldn't hold together by their connection points. Returns the new bricks
    /// (or just the original).
    /// </summary>
    List<Brick> SplitAssembly(Brick assembly)
    {
        var whole = new List<Brick> { assembly };
        if (assembly == null || assembly.part == null || !assembly.part.IsAssembly || assembly.IsPreview || assembly.IsStored)
            return whole;

        var pieces = new List<Brick>();
        foreach (BrickPartData.Component component in assembly.part.components)
        {
            BrickPartData part = FindPart(component.partId);
            if (part == null)
                break;
            Brick piece = SpawnBrickWithColorCode(part, assembly.colorCode);
            if (piece.color != assembly.color)
            {
                piece.color = assembly.color;
                piece.Rebuild();
            }
            piece.transform.SetPositionAndRotation(assembly.transform.TransformPoint(component.position),
                                                   assembly.transform.rotation * component.rotation);
            pieces.Add(piece);
        }

        // Every piece must be plugged into the others, or the assembly would fall apart in the hand.
        bool holds = pieces.Count == assembly.part.components.Length;
        if (holds)
        {
            Physics.SyncTransforms();
            var joined = new List<Brick> { pieces[0] };
            for (int i = 0; i < joined.Count; i++)
            {
                foreach (Brick other in pieces)
                {
                    if (!joined.Contains(other) && joined[i].IsConnectedTo(other))
                        joined.Add(other);
                }
            }
            holds = joined.Count == pieces.Count;
        }
        if (!holds)
        {
            foreach (Brick piece in pieces)
                Destroy(piece.gameObject);
            return whole;
        }

        bool fromBin = _partsBin.IsFromBin(assembly);
        foreach (Brick piece in pieces)
        {
            piece.SetLockedSection(assembly.LockedSection);
            if (assembly.CurrentBond != null)
                piece.RejoinBond(assembly.CurrentBond);
            if (fromBin)
                _partsBin.Adopt(piece);
        }
        Remove(assembly);
        return pieces;
    }

    static Color ColorOf(int colorCode)
    {
        int index = BrickColors.IndexOfCode(colorCode);
        return index >= 0 ? BrickColors.All[index].color : LDrawColors.Get(colorCode);
    }

    // The save last loaded (or saved): Save Everything offers to replace it rather than always adding a new one.
    string _workingSave, _workingTitle;
    MenuPanel _saveChoice;

    // Asks whether to replace the save you loaded or make a new one (straight to a new one if there's none).
    void SaveOrAskToReplace(List<Brick> bricks, ModelFile.SetProgress progress)
    {
        if (_workingSave == null || !System.IO.File.Exists(_workingSave))
        {
            SaveModel(bricks, includeInventory: true, progress);
            return;
        }

        if (_saveChoice == null)
            _saveChoice = MenuPanel.Create("Save Choice", "Save Everything", new Vector2(0.24f, 0.13f), CreateHandle());
        _saveChoice.ClearContent();
        float y = _saveChoice.ContentTop - 0.012f;
        MenuStyle.CreateText(_saveChoice.Content, $"You loaded \"{_workingTitle}\". Replace it with what's here now?",
            new Vector3(0f, y, -0.001f), new Vector2(0.22f, 0.012f));
        y -= 0.026f;
        var size = new Vector2(0.21f, 0.024f);
        MenuButton replace = MenuButton.Create(_saveChoice.Content, "Replace", new Vector3(0f, y, 0f), size,
                                               MenuStyle.ButtonActive, $"Replace \"{_workingTitle}\"");
        replace.Clicked += () => { _saveChoice.Close(); SaveModel(SaveableBricks(), includeInventory: true, progress, _workingSave); };
        y -= 0.028f;
        MenuButton asNew = MenuButton.Create(_saveChoice.Content, "Save as New", new Vector3(0f, y, 0f), size,
                                             MenuStyle.Button, "Keep It and Save a New Model");
        asNew.Clicked += () => { _saveChoice.Close(); SaveModel(SaveableBricks(), includeInventory: true, progress); };
        y -= 0.028f;
        MenuButton cancel = MenuButton.Create(_saveChoice.Content, "Cancel", new Vector3(0f, y, 0f), size, MenuStyle.Button, "Cancel");
        cancel.Clicked += _saveChoice.Close;

        (Vector3 position, Quaternion rotation) = InFrontOfUser(0f, 0f, 0.45f);
        _saveChoice.Open(position, rotation);
    }

    void SaveModel(List<Brick> bricks, bool includeInventory = false, ModelFile.SetProgress progress = null,
                   string replacePath = null)
    {
        string name = replacePath != null ? _workingTitle : ModelFile.NextAutoName();
        ModelFile.Info saved = ModelFile.Save(name, bricks, includeInventory, progress, _partsBin.IsFromBin, replacePath);
        ModelThumbnail.Make(saved.path, FindPart, ColorForCode); // the load screen shows it without rebuilding the model
        _workingSave = saved.path; // saving again offers to replace this one
        _workingTitle = name;

        int partCount = bricks.Sum(b => b.PieceCount);
        string details = $"{partCount} {(partCount == 1 ? "part" : "parts")}";
        int stacks = includeInventory ? WristInventory.Export().Count : 0;
        if (stacks > 0)
            details += $" + {stacks} on wrists";
        if (progress != null)
            details += $", {_partsBin.SetTitle} in progress";
        ShowMessage(replacePath != null ? $"Replaced \"{name}\" ({details})" : $"Saved \"{name}\" ({details})");
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

    // Fix Snapping: the editor for the connection points of the piece in your hand.
    void OpenSnapEditor()
    {
        Brick held = Brick.Held.FirstOrDefault(b => b != null && !b.IsLockedBody && b.part != null && b.part.mesh != null);
        if (held == null)
        {
            ShowMessage(Brick.Held.Any(b => b != null && b.IsLockedBody)
                ? "Locked models can't be edited. Hold a single piece (unlock it with X first)"
                : "Hold the piece to fix in one hand, then click Fix Snapping with the other");
            return;
        }
        _snapEditor.Open(held.part, held.color);
    }

    /// <summary>Builds a saved model from real bricks, sitting in front of you, ready to grab.</summary>
    public void LoadModel(string path)
    {
        if (_loading)
        {
            ShowMessage("Still loading the last model");
            return;
        }
        StartCoroutine(LoadingModel(path));
    }

    bool _loading;

    // How long loading may run each frame before showing progress and letting a frame be drawn.
    const float LoadSliceSeconds = 0.012f;

    System.Collections.IEnumerator LoadingModel(string path)
    {
        _loading = true;
        (Vector3 barPosition, Quaternion barRotation) = InFrontOfUser(0f, -0.12f, 0.6f);
        ProgressDisplay bar = ProgressDisplay.Create(barPosition, barRotation, "Loading…");
        float sliceStart = Time.realtimeSinceStartup;
        try
        {
            yield return null; // the bar shows before the file is read

            // How long each stage takes (wall time, including the frames shown in between), for LogLoad.
            var stages = new List<(string, float)>();
            float stageStart = Time.realtimeSinceStartup;
            void Mark(string stage)
            {
                stages.Add((stage, Time.realtimeSinceStartup - stageStart));
                stageStart = Time.realtimeSinceStartup;
            }

            List<ModelFile.Placement> placements = ModelFile.Load(path, out string title, out List<ModelFile.InventoryStack> inventory,
                                                                  out ModelFile.SetProgress progress);
            Mark("reading the file");
            int total = placements.Count + inventory.Sum(s => s.count * s.parts.Count);
            int lockedTotal = GameSettings.CollapseLocked ? placements.Count(p => p.lockSection > 0) : 0;
            // Placing parts is the first part of the bar; building locked groups (slower per piece) the rest.
            float placeShare = lockedTotal == 0 ? 1f : Mathf.Clamp(1f - (float)lockedTotal / Mathf.Max(1, total), 0.15f, 0.5f);
            int done = 0;
            string loadingText = $"Loading \"{title}\"";
            bar.Set(loadingText, 0f);
            sliceStart = Time.realtimeSinceStartup;
            var fromSet = new List<Brick>(); // parts that came out of the saved set's bin
            int sectionBase = Brick.NewestSection;
            (Vector3 origin, Quaternion facing) = InFrontOfUser(0f, 0.3f, menuDistance + 0.1f);

            int missing = 0;
            var bonds = new Dictionary<int, List<Brick>>();
            // Locked pieces go straight into locked bodies, without ever existing as separate bricks.
            var lockedPieces = new List<LockedBody.Piece>();
            // Locked groups in the file get fresh numbers here, so they don't clash with groups already in the room.
            var groups = new Dictionary<int, int>();
            int GroupFor(int saved)
            {
                if (!groups.TryGetValue(saved, out int unit))
                    groups[saved] = unit = LockedBody.NewUnit();
                return unit;
            }
            foreach (ModelFile.Placement placement in placements)
            {
                done++;
                if (Time.realtimeSinceStartup - sliceStart > LoadSliceSeconds)
                {
                    bar.Set(loadingText, placeShare * done / Mathf.Max(1, total));
                    yield return null;
                    sliceStart = Time.realtimeSinceStartup;
                }

                if (placement.lockSection > 0 && GameSettings.CollapseLocked)
                {
                    BrickPartData part = FindPart(placement.partId);
                    if (part == null)
                    {
                        missing++;
                        continue;
                    }
                    lockedPieces.Add(new LockedBody.Piece
                    {
                        part = part,
                        colorCode = placement.colorCode,
                        color = ColorOf(placement.colorCode),
                        position = origin + facing * placement.position,
                        rotation = LockedBody.Unit(facing * placement.rotation),
                        section = sectionBase + placement.lockSection, // after the sections already in the room
                        fromSet = placement.fromSet,
                        bond = placement.bond != 0 ? (object)placement.bond : null,
                        unit = placement.group != 0 ? GroupFor(placement.group) : 0,
                    });
                    continue;
                }

                Brick brick = SpawnPlacement(placement, origin, facing);
                if (brick == null)
                {
                    missing++;
                    continue;
                }
                if (placement.lockSection > 0)
                    brick.SetLockedSection(sectionBase + placement.lockSection); // after the sections already in the room
                if (placement.fromSet)
                    fromSet.Add(brick);
                if (placement.bond != 0)
                {
                    if (!bonds.TryGetValue(placement.bond, out List<Brick> members))
                        bonds[placement.bond] = members = new List<Brick>();
                    members.Add(brick);
                }
            }

            Mark("placing parts");

            // Wrist inventory: build each copy out of sight, then hand it to the inventory.
            var outOfSight = new Vector3(0f, -100f, 0f);
            foreach (ModelFile.InventoryStack stack in inventory)
            {
                for (int copy = 0; copy < stack.count; copy++)
                {
                    done += stack.parts.Count;
                    if (Time.realtimeSinceStartup - sliceStart > LoadSliceSeconds)
                    {
                        bar.Set(loadingText, placeShare * done / Mathf.Max(1, total));
                        yield return null;
                        sliceStart = Time.realtimeSinceStartup;
                    }

                    var members = new List<Brick>();
                    foreach (ModelFile.Placement placement in stack.parts)
                    {
                        Brick brick = SpawnPlacement(placement, outOfSight, Quaternion.identity);
                        if (brick != null)
                            members.Add(brick);
                        else if (copy == 0)
                            missing++;
                    }
                    if (stack.fromSet)
                        fromSet.AddRange(members);
                    WristInventory.AddLoaded(stack.hand, members);
                }
            }

            Mark("wrist items");

            // Locked groups, a body at a time.
            var built = new List<(LockedBody body, Vector3 position, Quaternion rotation)>();
            string lockedText = $"Loading \"{title}\": locked groups";
            foreach (float fraction in LockedBody.BuildSteps(lockedPieces, MenuStyle.Material, transparentMaterial, built))
            {
                if (Time.realtimeSinceStartup - sliceStart > LoadSliceSeconds)
                {
                    bar.Set(lockedText, placeShare + (1f - placeShare) * fraction);
                    yield return null;
                    sliceStart = Time.realtimeSinceStartup;
                }
            }
            bar.Set(lockedText, 1f);
            Mark("building locked groups");
            foreach (Brick body in SpawnBodies(built))
            {
                foreach (object key in body.Locked.bonds)
                {
                    if (!(key is int id))
                        continue;
                    if (!bonds.TryGetValue(id, out List<Brick> members))
                        bonds[id] = members = new List<Brick>();
                    members.Add(body);
                }
            }
            Mark("setting up locked groups");
            foreach (List<Brick> members in bonds.Values)
                Brick.BondTogether(members);

            Mark("bonds");

            // Saved partway through building a set from its bin: pick that back up.
            string resumed = progress != null ? ResumeSet(progress, fromSet) : null;
            if (resumed != null)
            {
                _partsBin.TakePieces(lockedPieces.Where(p => p.fromSet).Select(p => (p.part, p.colorCode)));
            }

            Mark("set bin + book");
            StartCoroutine(LogLoad(title, placements.Count, lockedPieces.Count, stages, LockedBody.LastTimes));
            // The save you're now working on: Save Everything offers to replace it.
            _workingSave = path;
            _workingTitle = title;
            Destroy(bar.gameObject); // before the message, which sits in the same spot
            ShowMessage(resumed != null
                ? $"Loaded \"{title}\" and picked up {resumed} where you left off"
                : missing == 0
                ? $"Loaded \"{title}\""
                : $"Loaded \"{title}\" ({missing} parts not imported yet)");
        }
        finally
        {
            if (bar != null)
                Destroy(bar.gameObject);
            _loading = false;
        }
    }

    // Records how long a load took, stage by stage, then watches the next few seconds of frames (new bricks
    // and bodies settling in), in Models/../load-times.txt so slow loads can be looked into.
    System.Collections.IEnumerator LogLoad(string title, int parts, int locked, List<(string stage, float seconds)> stages,
                                           string groupTimes)
    {
        float watchStart = Time.realtimeSinceStartup, slowest = 0f, slowTotal = 0f;
        int slowFrames = 0;
        float last = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - watchStart < 5f)
        {
            yield return null;
            float frame = Time.realtimeSinceStartup - last;
            last = Time.realtimeSinceStartup;
            slowest = Mathf.Max(slowest, frame);
            if (frame > 0.05f)
            {
                slowFrames++;
                slowTotal += frame;
            }
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine($"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}  \"{title}\": {parts} parts, {locked} locked, " +
                        $"total {stages.Sum(s => s.seconds):0.0} s");
        foreach ((string stage, float seconds) in stages)
            text.AppendLine($"  {stage}: {seconds:0.00} s");
        if (locked > 0)
            text.AppendLine($"  locked groups work: {groupTimes}");
        text.AppendLine($"  next 5 s: slowest frame {slowest * 1000f:0} ms, {slowFrames} frames over 50 ms ({slowTotal:0.0} s)");
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(Application.persistentDataPath, "load-times.txt"), text.ToString());
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Couldn't write the load-time log: " + e.Message);
        }
        Debug.Log(text.ToString());
    }

    // Reopens a set's parts bin with the loaded set parts already taken out, and its instructions at the saved
    // page. Returns the set's title, or null if that set isn't in the game any more.
    string ResumeSet(ModelFile.SetProgress progress, List<Brick> alreadyTaken)
    {
        SetData set = SetData.LoadAll().FirstOrDefault(s => s.setNumber == progress.setNumber);
        if (set == null)
            return null;

        CloseBuildMenus();
        _setBuilder.ExitSet();
        _partsBin.Resume(set, alreadyTaken);
        _book.ResumeAt(set, progress.booklet, progress.page);
        return set.title;
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
            if (members.Any(m => m.IsLockedBody))
            {
                ShowMessage("Locked pieces can't be copied. Unlock them first (X)");
                continue;
            }

            float height = GroupHeight(members);
            Brick copy = CloneBrick(held, held.transform.position + Vector3.up * (height + 0.02f), held.transform.rotation);

            var copies = new List<Brick> { copy };
            foreach (Brick member in held.CarriedBricks)
            {
                Transform t = member.transform; // carried bricks are children of the held one
                copies.Add(CloneBrick(member, copy.transform.TransformPoint(t.localPosition), copy.transform.rotation * t.localRotation));
            }
            UndoHistory.RecordSpawn(copies); // undo removes the copy

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
