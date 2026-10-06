using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

/// <summary>
/// "Build It Yourself" mode: a panel holding exactly one set's parts, in their colors and quantities.
/// Filter by category, page through, and grab a part straight out of the bin (its count goes down).
/// Dropping one of the set's parts in the trash puts it back in the bin. "Finish Model" bonds what
/// you've built into one model. Only the parts on screen exist as real bricks, so big sets stay light.
/// </summary>
public class PartsBin : MonoBehaviour
{
    const int Columns = 4, Rows = 4, PartsPerPage = Columns * Rows;
    const int CategoriesPerPage = 9;
    const float CategoryWidth = 0.085f, ButtonHeight = 0.022f, Gap = 0.005f;
    const float LabelHeight = 0.012f, Margin = 0.01f, HeaderHeight = 0.026f, PagingHeight = 0.028f;
    const string AllCategory = "All";

    static readonly string[] CategoryOrder = { "Brick", "Plate", "Tile", "Slope" };

    class Entry
    {
        public BrickPartData part;
        public int colorCode;
        public int total;
        public int remaining;
    }

    Sandbox _sandbox;
    MenuPanel _panel;

    SetData _set;
    readonly List<Entry> _entries = new List<Entry>();
    readonly List<string> _categories = new List<string>();
    string _category = AllCategory;
    int _page;
    int _categoryPage;
    float _cellSize;

    readonly Dictionary<Brick, (Entry entry, Vector3 cellCenter)> _previews = new Dictionary<Brick, (Entry, Vector3)>();
    readonly List<Brick> _built = new List<Brick>();

    // A grip grab from the bin takes the whole stack: the brick in hand stands for itself plus these extras.
    class HeldStack
    {
        public Entry entry;
        public int extra;
        public TextMeshPro label;
    }
    readonly Dictionary<Brick, HeldStack> _heldStacks = new Dictionary<Brick, HeldStack>();

    /// <summary>True while a set is being built from its parts (even if the panel is hidden).</summary>
    public bool IsActive => _set != null;
    public string SetTitle => _set != null ? _set.title : "";

    public void Init(Sandbox sandbox)
    {
        _sandbox = sandbox;
        _panel = MenuPanel.Create("Parts Bin", "", new Vector2(0.3f, 0.3f), sandbox.CreateHandle());
        _panel.Close();
        Brick.PreviewTaken += OnPreviewTaken;
        Brick.Trashed += OnTrashed;
        WristInventory.Stored += OnStored;
        UndoHistory.Restored += OnRestored;
    }

    void OnDestroy()
    {
        Brick.PreviewTaken -= OnPreviewTaken;
        Brick.Trashed -= OnTrashed;
        WristInventory.Stored -= OnStored;
        UndoHistory.Restored -= OnRestored;
    }

    // Undo brought a trashed set part back: it leaves the bin again.
    void OnRestored(Brick brick)
    {
        if (_set == null)
            return;
        Entry entry = _entries.Find(e => e.part == brick.part && e.colorCode == brick.colorCode);
        if (entry == null || entry.remaining == 0)
            return;
        entry.remaining--;
        _built.Add(brick);
        if (_panel.IsOpen)
            Rebuild();
    }

    // ---------- Starting, showing, finishing ----------

    public void Begin(SetData set)
    {
        End();
        _set = set;

        int missing = 0;
        foreach (SetData.InventoryItem item in set.PartsList())
        {
            BrickPartData part = _sandbox.FindPart(item.partId);
            if (part == null)
            {
                missing += item.quantity;
                continue;
            }
            _entries.Add(new Entry { part = part, colorCode = item.colorCode, total = item.quantity, remaining = item.quantity });
        }

        // Sorted like a parts list: by type, then part, then color.
        _entries.Sort((a, b) =>
        {
            int byCategory = CategoryRank(a.part.Category).CompareTo(CategoryRank(b.part.Category));
            if (byCategory != 0)
                return byCategory;
            int byName = string.CompareOrdinal(a.part.description, b.part.description);
            return byName != 0 ? byName : a.colorCode.CompareTo(b.colorCode);
        });

        _categories.Add(AllCategory);
        _categories.AddRange(_entries.Select(e => e.part.Category).Distinct());
        _category = AllCategory;
        _page = 0;
        _categoryPage = 0;

        Show();
        if (missing > 0)
            Sandbox.Message($"{missing} parts aren't available and were left out");
    }

    public void Show()
    {
        if (_set == null)
            return;
        (Vector3 position, Quaternion rotation) = _sandbox.InFrontOfUser(0.12f, 0.05f);
        if (!_panel.IsOpen)
            _panel.Open(position, rotation);
        Rebuild();
    }

    public void Hide() => _panel.Close();

    /// <summary>Ends the session. Bricks you've built stay where they are.</summary>
    public void End()
    {
        foreach (Brick brick in _heldStacks.Keys.ToList())
            RemoveStack(brick);
        ClearPreviews();
        _panel.Close();
        _entries.Clear();
        _categories.Clear();
        _built.Clear();
        _set = null;
    }

    /// <summary>Bonds everything built from this set into one model.</summary>
    public void FinishModel()
    {
        List<Brick> model = _built.Where(b => b != null && !b.IsStored && !b.IsPreview).ToList();
        if (model.Count == 0)
        {
            Sandbox.Message("Nothing built yet");
            return;
        }
        Brick.BondTogether(model);
        Sandbox.Message($"{_set.title}: {model.Count} parts bonded into one model");
    }

    // ---------- Panel ----------

    void Rebuild()
    {
        ClearPreviews();
        _panel.ClearContent();
        _panel.SetTitle(_set.title);

        // Used-up parts (and categories with nothing left) drop out, so what's left is easy to find.
        List<string> categories = VisibleCategories();
        if (!categories.Contains(_category))
        {
            _category = AllCategory;
            _page = 0;
        }
        List<Entry> shown = _entries.Where(e => e.remaining > 0 && (_category == AllCategory || e.part.Category == _category)).ToList();
        int pageCount = Mathf.Max(1, Mathf.CeilToInt(shown.Count / (float)PartsPerPage));
        _page = Mathf.Clamp(_page, 0, pageCount - 1);
        List<Entry> pageEntries = shown.Skip(_page * PartsPerPage).Take(PartsPerPage).ToList();

        // Cells sized for the biggest part on this page.
        float largest = pageEntries.Count > 0
            ? pageEntries.Max(e => Mathf.Max(e.part.bodyBounds.size.x, e.part.bodyBounds.size.y, e.part.bodyBounds.size.z))
            : 0.03f;
        _cellSize = Mathf.Max(0.05f, largest + 0.015f);

        int rows = Mathf.Max(1, Mathf.CeilToInt(Mathf.Min(shown.Count, PartsPerPage) / (float)Columns));
        float gridWidth = Columns * _cellSize;
        float gridHeight = HeaderHeight + rows * (_cellSize + LabelHeight) + PagingHeight;
        int categoryRows = Mathf.Min(categories.Count, CategoriesPerPage) + (categories.Count > CategoriesPerPage ? 1 : 0);
        float categoryHeight = categoryRows * (ButtonHeight + Gap);
        var size = new Vector2(Margin * 3f + CategoryWidth + gridWidth, 0.04f + Mathf.Max(gridHeight, categoryHeight) + Margin);
        _panel.SetSize(size);

        float left = -size.x * 0.5f + Margin;
        float top = _panel.ContentTop;
        BuildCategoryColumn(categories, left + CategoryWidth * 0.5f, top);

        // Header: how many parts are left, and Finish Model.
        float gridLeft = left + CategoryWidth + Margin;
        float headerY = top - HeaderHeight * 0.5f;
        int remaining = _entries.Sum(e => e.remaining), total = _entries.Sum(e => e.total);
        float textWidth = gridWidth - 0.165f; // the space left of the two header buttons
        MenuStyle.CreateText(_panel.Content, $"{remaining} of {total} parts left",
            new Vector3(gridLeft + textWidth * 0.5f, headerY, -0.001f), new Vector2(textWidth, 0.012f),
            TextAlignmentOptions.Left);
        MenuButton finish = MenuButton.Create(_panel.Content, "Finish", new Vector3(gridLeft + gridWidth - 0.04f, headerY, 0f),
                                              new Vector2(0.075f, 0.02f), MenuStyle.ButtonActive, "Finish Model");
        finish.Clicked += FinishModel;
        MenuButton book = MenuButton.Create(_panel.Content, "Instructions", new Vector3(gridLeft + gridWidth - 0.12f, headerY, 0f),
                                            new Vector2(0.075f, 0.02f), MenuStyle.Button, "Instructions");
        book.Clicked += () => _sandbox.OpenBook(_set);

        if (shown.Count == 0)
        {
            MenuStyle.CreateText(_panel.Content, "Every part has been used!",
                new Vector3(gridLeft + gridWidth * 0.5f, top - HeaderHeight - _cellSize * 0.5f, -0.001f),
                new Vector2(gridWidth - 0.02f, 0.016f));
        }

        // The parts.
        for (int i = 0; i < pageEntries.Count; i++)
        {
            int column = i % Columns, row = i / Columns;
            var cellCenter = new Vector3(
                gridLeft + _cellSize * (column + 0.5f),
                top - HeaderHeight - row * (_cellSize + LabelHeight) - _cellSize * 0.5f,
                0f);
            Entry entry = pageEntries[i];
            if (entry.remaining > 0)
                SpawnPreview(entry, cellCenter);

            string count = entry.remaining > 0 ? $"<color=#9CF>x{entry.remaining}</color>" : "<color=#777>none left</color>";
            MenuStyle.CreateText(_panel.Content, $"{entry.part.ShortName}  {count}",
                cellCenter + new Vector3(0f, -_cellSize * 0.5f - LabelHeight * 0.4f, -0.001f),
                new Vector2(_cellSize - 0.004f, LabelHeight * 0.8f));
        }

        if (pageCount > 1)
        {
            // Centered under the grid rather than the whole panel.
            var holder = new GameObject("Part Pages").transform;
            holder.SetParent(_panel.Content, false);
            holder.localPosition = new Vector3(gridLeft + gridWidth * 0.5f, 0f, 0f);
            MenuStyle.AddPagingRow(holder, -size.y * 0.5f + Margin + PagingHeight * 0.35f, gridWidth,
                                   _page, pageCount, p => { _page = p; Rebuild(); });
        }
    }

    // "All" plus every category that still has parts left.
    List<string> VisibleCategories() =>
        _categories.Where(c => c == AllCategory || _entries.Any(e => e.remaining > 0 && e.part.Category == c)).ToList();

    void BuildCategoryColumn(List<string> categories, float x, float top)
    {
        int pageCount = Mathf.Max(1, Mathf.CeilToInt(categories.Count / (float)CategoriesPerPage));
        _categoryPage = Mathf.Clamp(_categoryPage, 0, pageCount - 1);

        float y = top - ButtonHeight * 0.5f;
        foreach (string category in categories.Skip(_categoryPage * CategoriesPerPage).Take(CategoriesPerPage))
        {
            string label = category == AllCategory ? "All Parts" : Sandbox.Plural(category);
            MenuButton button = MenuButton.Create(_panel.Content, category, new Vector3(x, y, 0f),
                                                  new Vector2(CategoryWidth, ButtonHeight), MenuStyle.Button, label);
            button.SetSelected(category == _category);
            button.Clicked += () => { _category = category; _page = 0; Rebuild(); };
            y -= ButtonHeight + Gap;
        }

        if (pageCount > 1)
        {
            var holder = new GameObject("Category Pages").transform;
            holder.SetParent(_panel.Content, false);
            holder.localPosition = new Vector3(x, 0f, 0f);
            MenuStyle.AddPagingRow(holder, top - CategoriesPerPage * (ButtonHeight + Gap) - ButtonHeight * 0.5f,
                                   CategoryWidth, _categoryPage, pageCount, p => { _categoryPage = p; Rebuild(); });
        }
    }

    // ---------- Previews ----------

    // A real brick sitting in a bin cell, tilted to show its studs. Grabbing it takes it out of the bin.
    void SpawnPreview(Entry entry, Vector3 cellCenter)
    {
        Brick brick = _sandbox.SpawnBrickWithColorCode(entry.part, entry.colorCode);
        brick.IsPreview = true;
        brick.transform.SetParent(_panel.Content, worldPositionStays: false);

        Quaternion tilt = Quaternion.Euler(-25f, 0f, 0f);
        brick.transform.localRotation = tilt;
        brick.transform.localPosition = cellCenter + new Vector3(0f, 0f, -_cellSize * 0.5f) - tilt * brick.BodyBounds.center;
        _previews[brick] = (entry, cellCenter);
    }

    void OnPreviewTaken(Brick brick, bool wholeGroupGrab)
    {
        if (!_previews.TryGetValue(brick, out (Entry entry, Vector3 cellCenter) slot))
            return;
        _previews.Remove(brick);
        slot.entry.remaining--;
        _built.Add(brick);

        // Grip: take every copy that's left. The extras follow the brick until it's let go.
        if (wholeGroupGrab && slot.entry.remaining > 0)
        {
            var stack = new HeldStack { entry = slot.entry, extra = slot.entry.remaining };
            slot.entry.remaining = 0;
            stack.label = MenuStyle.CreateText(null, $"x{stack.extra + 1}", Vector3.zero, new Vector2(0.03f, 0.012f));
            stack.label.color = new Color(0.6f, 0.85f, 1f);
            _heldStacks[brick] = stack;
        }

        if (_panel.IsOpen)
            Rebuild(); // updates the counts, and refills the cell if any are left
    }

    // A whole stack let go at a wrist: the extras join it there as one "xN" slot.
    void OnStored(Brick brick, int hand)
    {
        if (!_heldStacks.TryGetValue(brick, out HeldStack stack))
            return;
        RemoveStack(brick);

        for (int i = 0; i < stack.extra; i++)
        {
            Brick copy = _sandbox.SpawnBrickWithColorCode(brick.part, brick.colorCode);
            copy.transform.position = new Vector3(0f, -100f, 0f); // out of sight until it's on the wrist
            _built.Add(copy);
            WristInventory.AddLoaded(hand, new List<Brick> { copy });
        }
    }

    void Update()
    {
        if (_heldStacks.Count == 0)
            return;

        foreach (Brick brick in _heldStacks.Keys.ToList())
        {
            HeldStack stack = _heldStacks[brick];
            if (brick == null)
            {
                RemoveStack(brick);
                continue;
            }

            // Let go somewhere other than a wrist: keep the one, the rest go back in the bin.
            if (!Brick.Held.Contains(brick))
            {
                stack.entry.remaining += stack.extra;
                RemoveStack(brick);
                if (_panel.IsOpen)
                    Rebuild();
            }
        }
    }

    // The "xN" label rides just above the stack in your hand, facing you.
    void LateUpdate()
    {
        Camera head = Camera.main;
        foreach (KeyValuePair<Brick, HeldStack> pair in _heldStacks)
        {
            if (pair.Key == null || pair.Value.label == null)
                continue;
            Transform label = pair.Value.label.transform;
            label.position = pair.Key.transform.TransformPoint(pair.Key.BodyBounds.center) + Vector3.up * 0.03f;
            if (head != null)
                label.rotation = Quaternion.LookRotation(label.position - head.transform.position, Vector3.up);
        }
    }

    void RemoveStack(Brick brick)
    {
        if (_heldStacks.TryGetValue(brick, out HeldStack stack) && stack.label != null)
            Destroy(stack.label.gameObject);
        _heldStacks.Remove(brick);
    }

    // A set part dropped in the trash goes back into the bin (a whole held stack goes back too).
    void OnTrashed(Brick brick)
    {
        if (!_built.Remove(brick))
            return;
        Entry entry = _entries.Find(e => e.part == brick.part && e.colorCode == brick.colorCode);
        if (entry == null)
            return;

        int returned = 1;
        if (_heldStacks.TryGetValue(brick, out HeldStack stack))
        {
            returned += stack.extra;
            RemoveStack(brick);
        }
        entry.remaining = Mathf.Min(entry.total, entry.remaining + returned);
        if (_panel.IsOpen)
            Rebuild();
    }

    void ClearPreviews()
    {
        foreach (Brick preview in _previews.Keys)
        {
            if (preview != null)
                Destroy(preview.gameObject);
        }
        _previews.Clear();
    }

    static int CategoryRank(string category)
    {
        int index = System.Array.IndexOf(CategoryOrder, category);
        return index >= 0 ? index : CategoryOrder.Length;
    }
}
