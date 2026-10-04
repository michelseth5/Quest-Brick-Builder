using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The "Saved Models" panel: a grid of spinning 3D previews of saved models, each with
/// Load, Rename (Quest keyboard), and Delete (click twice to confirm). Pages through long lists.
/// </summary>
public class ModelMenu : MonoBehaviour
{
    const int Columns = 3, Rows = 2;
    const float Cell = 0.09f, LabelHeight = 0.014f, ButtonHeight = 0.018f, Gap = 0.008f, Margin = 0.012f;
    const float SpinDegreesPerSecond = 30f;
    const float DeleteConfirmSeconds = 3f;

    Sandbox _sandbox;
    MenuPanel _panel;
    int _page;
    List<ModelFile.Info> _models = new List<ModelFile.Info>();
    readonly List<Transform> _spinners = new List<Transform>();

    TouchScreenKeyboard _keyboard;
    string _renamePath;

    MenuButton _deleteButton;
    string _deletePath;
    float _deleteExpires;

    public bool IsOpen => _panel != null && _panel.IsOpen;

    float RowHeight => Cell + LabelHeight + ButtonHeight + Gap;

    public void Init(Sandbox sandbox)
    {
        _sandbox = sandbox;
        var size = new Vector2(Columns * Cell + (Columns - 1) * Gap + Margin * 2f,
                               0.04f + Rows * RowHeight + 0.03f + Margin);
        _panel = MenuPanel.Create("Models Menu", "Saved Models", size, sandbox.CreateHandle());
        _panel.Close();
    }

    public void Open(Vector3 position, Quaternion rotation)
    {
        _page = 0;
        Refresh();
        _panel.Open(position, rotation);
    }

    public void Close() => _panel.Close();

    /// <summary>Re-reads the saved models and rebuilds the current page.</summary>
    public void Refresh()
    {
        _models = ModelFile.List();
        int pageCount = Mathf.Max(1, Mathf.CeilToInt(_models.Count / (float)(Columns * Rows)));
        _page = Mathf.Clamp(_page, 0, pageCount - 1);

        _panel.ClearContent();
        _spinners.Clear();
        _deleteButton = null;
        _deletePath = null;

        Vector2 size = _panel.Size;
        if (_models.Count == 0)
        {
            MenuStyle.CreateText(_panel.Content, "No saved models yet.\nUse Save Everything, or grip a model and press X.",
                new Vector3(0f, 0f, -0.001f), new Vector2(size.x - 0.03f, 0.04f));
            return;
        }

        int first = _page * Columns * Rows;
        for (int i = 0; i < Columns * Rows && first + i < _models.Count; i++)
        {
            ModelFile.Info info = _models[first + i];
            int column = i % Columns, row = i / Columns;
            var cellCenter = new Vector3(
                -size.x * 0.5f + Margin + Cell * 0.5f + column * (Cell + Gap),
                _panel.ContentTop - Cell * 0.5f - row * RowHeight,
                0f);
            BuildCell(info, cellCenter);
        }

        // Paging row.
        float y = -size.y * 0.5f + Margin + 0.01f;
        MenuStyle.CreateText(_panel.Content, $"Page {_page + 1} of {pageCount}", new Vector3(0f, y, -0.001f), new Vector2(0.08f, 0.012f));
        if (_page > 0)
        {
            MenuButton prev = MenuButton.Create(_panel.Content, "Previous", new Vector3(-size.x * 0.5f + 0.04f, y, 0f),
                                                new Vector2(0.06f, 0.018f), MenuStyle.Button, "< Prev");
            prev.Clicked += () => { _page--; Refresh(); };
        }
        if (_page < pageCount - 1)
        {
            MenuButton next = MenuButton.Create(_panel.Content, "Next", new Vector3(size.x * 0.5f - 0.04f, y, 0f),
                                                new Vector2(0.06f, 0.018f), MenuStyle.Button, "Next >");
            next.Clicked += () => { _page++; Refresh(); };
        }
    }

    void BuildCell(ModelFile.Info info, Vector3 cellCenter)
    {
        BuildThumbnail(info, cellCenter);

        float labelY = cellCenter.y - Cell * 0.5f - LabelHeight * 0.5f;
        MenuStyle.CreateText(_panel.Content, $"{info.title}  <size=70%><color=#AAB>{info.saved:MMM d}</color></size>",
            new Vector3(cellCenter.x, labelY, -0.001f), new Vector2(Cell - 0.004f, LabelHeight * 0.8f));

        // Load | Rename | Delete
        float buttonY = labelY - LabelHeight * 0.5f - ButtonHeight * 0.5f - 0.002f;
        float buttonWidth = (Cell - 0.004f) / 3f;
        string path = info.path;

        MenuButton load = MenuButton.Create(_panel.Content, "Load", new Vector3(cellCenter.x - buttonWidth - 0.002f, buttonY, 0f),
                                            new Vector2(buttonWidth, ButtonHeight), MenuStyle.ButtonActive, "Load");
        load.Clicked += () => _sandbox.LoadModel(path);

        MenuButton rename = MenuButton.Create(_panel.Content, "Rename", new Vector3(cellCenter.x, buttonY, 0f),
                                              new Vector2(buttonWidth, ButtonHeight), MenuStyle.Button, "Rename");
        rename.Clicked += () => StartRename(path, info.title);

        MenuButton delete = MenuButton.Create(_panel.Content, "Delete", new Vector3(cellCenter.x + buttonWidth + 0.002f, buttonY, 0f),
                                              new Vector2(buttonWidth, ButtonHeight), MenuStyle.Close, "Delete");
        delete.Clicked += () => OnDeleteClicked(delete, path);
    }

    // A scaled-down copy of the model (plain meshes, not real bricks), tilted toward you and spinning.
    void BuildThumbnail(ModelFile.Info info, Vector3 cellCenter)
    {
        var root = new GameObject("Thumbnail").transform;
        root.SetParent(_panel.Content, false);
        root.localPosition = cellCenter + new Vector3(0f, 0f, -Cell * 0.5f);
        root.localRotation = Quaternion.Euler(-20f, 0f, 0f);

        var spinner = new GameObject("Spin").transform;
        spinner.SetParent(root, false);
        _spinners.Add(spinner);

        var model = new GameObject("Model").transform;
        model.SetParent(spinner, false);

        List<ModelFile.Placement> placements = ModelFile.Load(info.path, out _);
        var bounds = new Bounds();
        bool any = false;

        foreach (ModelFile.Placement placement in placements)
        {
            BrickPartData part = _sandbox.FindPart(placement.partId);
            if (part == null || part.mesh == null)
                continue;

            var piece = new GameObject(part.partId);
            piece.transform.SetParent(model, false);
            piece.transform.localPosition = placement.position;
            piece.transform.localRotation = placement.rotation;
            piece.AddComponent<MeshFilter>().sharedMesh = part.mesh;

            Color color = _sandbox.ColorForCode(placement.colorCode);
            var renderer = piece.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = color.a < 1f && _sandbox.transparentMaterial != null
                ? _sandbox.transparentMaterial
                : MenuStyle.Material;
            MenuStyle.Tint(renderer, color);

            Bounds meshBounds = part.mesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = new Vector3(
                    (corner & 1) == 0 ? meshBounds.min.x : meshBounds.max.x,
                    (corner & 2) == 0 ? meshBounds.min.y : meshBounds.max.y,
                    (corner & 4) == 0 ? meshBounds.min.z : meshBounds.max.z);
                Vector3 p = placement.position + placement.rotation * local;
                if (!any)
                {
                    bounds = new Bounds(p, Vector3.zero);
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(p);
                }
            }
        }

        if (!any)
        {
            MenuStyle.CreateText(root, "?", Vector3.zero, new Vector2(Cell * 0.5f, Cell * 0.5f));
            return;
        }

        // Fit the model in the cell, centered on the spin axis.
        float largest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.001f);
        spinner.localScale = Vector3.one * (Cell * 0.8f / largest);
        model.localPosition = -bounds.center;
    }

    void OnDeleteClicked(MenuButton button, string path)
    {
        // First click arms it; a second click within a few seconds deletes.
        if (_deleteButton == button && _deletePath == path && Time.time < _deleteExpires)
        {
            ModelFile.Delete(path);
            Refresh();
            return;
        }

        ResetDeleteButton();
        _deleteButton = button;
        _deletePath = path;
        _deleteExpires = Time.time + DeleteConfirmSeconds;
        if (button.Label != null)
            button.Label.text = "Sure?";
    }

    void ResetDeleteButton()
    {
        if (_deleteButton != null && _deleteButton.Label != null)
            _deleteButton.Label.text = "Delete";
        _deleteButton = null;
        _deletePath = null;
    }

    void StartRename(string path, string currentTitle)
    {
        _renamePath = path;
        _keyboard = TouchScreenKeyboard.Open(currentTitle, TouchScreenKeyboardType.Default, false, false, false, false, "Model name");
    }

    void Update()
    {
        foreach (Transform spinner in _spinners)
        {
            if (spinner != null)
                spinner.Rotate(0f, SpinDegreesPerSecond * Time.deltaTime, 0f, Space.Self);
        }

        if (_deleteButton != null && Time.time >= _deleteExpires)
            ResetDeleteButton();

        if (_keyboard != null)
        {
            switch (_keyboard.status)
            {
                case TouchScreenKeyboard.Status.Done:
                    string name = _keyboard.text.Trim();
                    if (name.Length > 0)
                    {
                        ModelFile.Rename(_renamePath, name);
                        Refresh();
                    }
                    _keyboard = null;
                    break;
                case TouchScreenKeyboard.Status.Canceled:
                case TouchScreenKeyboard.Status.LostFocus:
                    _keyboard = null;
                    break;
            }
        }
    }
}
