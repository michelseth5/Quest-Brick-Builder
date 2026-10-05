using System;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEngine;

/// <summary>Shared look for menus. Sandbox sets the material at startup.</summary>
public static class MenuStyle
{
    public static Material Material;

    public static readonly Color Panel = new Color(0.12f, 0.13f, 0.15f);
    public static readonly Color Button = new Color(0.25f, 0.27f, 0.31f);
    public static readonly Color ButtonHover = new Color(0.36f, 0.40f, 0.46f);
    public static readonly Color ButtonActive = new Color(0.18f, 0.45f, 0.85f);
    public static readonly Color Close = new Color(0.55f, 0.16f, 0.16f);
    public static readonly Color Handle = new Color(0.55f, 0.57f, 0.60f);
    public static readonly Color Text = Color.white;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    public static void Tint(Renderer renderer, Color color)
    {
        var block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block);
        block.SetColor(BaseColorId, color);
        renderer.SetPropertyBlock(block);
    }

    /// <summary>A text label that shrinks to fit the given size (meters). Readable from the panel's front.</summary>
    public static TextMeshPro CreateText(Transform parent, string text, Vector3 localPosition, Vector2 size,
                                         TextAlignmentOptions alignment = TextAlignmentOptions.Center)
    {
        // Created on its own first so TextMesh Pro finishes its own setup (which applies its defaults)
        // before ours; otherwise, under a switched-off parent, its defaults would overwrite these settings later.
        var go = new GameObject("Text");
        var label = go.AddComponent<TextMeshPro>();
        label.rectTransform.sizeDelta = size;
        label.text = text;
        label.color = Text;
        label.alignment = alignment;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.enableAutoSizing = true;
        label.fontSizeMin = 0.01f;
        label.fontSizeMax = 0.4f;

        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        return label;
    }

    /// <summary>"&lt;   1 / 3   &gt;" across the given width, for paging through a menu.</summary>
    public static void AddPagingRow(Transform parent, float y, float width, int page, int pageCount, Action<int> showPage)
    {
        const float buttonWidth = 0.035f, height = 0.02f;
        CreateText(parent, $"{page + 1} / {pageCount}", new Vector3(0f, y, -0.001f), new Vector2(0.04f, height * 0.6f));
        if (page > 0)
        {
            MenuButton prev = MenuButton.Create(parent, "Previous", new Vector3(-width * 0.5f + buttonWidth * 0.5f, y, 0f),
                                                new Vector2(buttonWidth, height), Button, "<");
            prev.Clicked += () => showPage(page - 1);
        }
        if (page < pageCount - 1)
        {
            MenuButton next = MenuButton.Create(parent, "Next", new Vector3(width * 0.5f - buttonWidth * 0.5f, y, 0f),
                                                new Vector2(buttonWidth, height), Button, ">");
            next.Clicked += () => showPage(page + 1);
        }
    }

    /// <summary>A solid box, used for panel backgrounds and swatches.</summary>
    public static MeshRenderer CreateBox(Transform parent, string name, Vector3 localPosition, Vector3 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.AddComponent<MeshFilter>().sharedMesh = MeshBuilder.Box(size);
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = Material;
        Tint(renderer, color);
        return renderer;
    }
}

/// <summary>
/// A 3D button you point at with the controller laser (or hand ray) and click with the trigger (or pinch).
/// Menus face the player along their +Z axis, so buttons stick out toward -Z.
/// </summary>
public class MenuButton : MonoBehaviour
{
    public const float Depth = 0.008f;

    public event Action Clicked;

    /// <summary>Raised when the laser starts (true) or stops (false) pointing at the button.</summary>
    public event Action<bool> HoverChanged;

    MeshRenderer _renderer;
    RayInteractable _ray;
    Color _color;
    bool _hovered;
    bool _active;

    public TextMeshPro Label { get; private set; }

    public static MenuButton Create(Transform parent, string name, Vector3 localPosition, Vector2 size, Color color, string label)
    {
        var go = new GameObject(name);
        go.SetActive(false);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition + new Vector3(0f, 0f, -Depth * 0.5f);

        var boxSize = new Vector3(size.x, size.y, Depth);
        go.AddComponent<MeshFilter>().sharedMesh = MeshBuilder.Box(boxSize);
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = MenuStyle.Material;

        var collider = go.AddComponent<BoxCollider>();
        collider.size = boxSize;
        collider.isTrigger = true; // only for pointing at; shouldn't block bricks

        // Interaction SDK pieces must be wired up before the object is switched on.
        var surface = go.AddComponent<ColliderSurface>();
        surface.InjectAllColliderSurface(collider);
        var ray = go.AddComponent<RayInteractable>();
        ray.InjectAllRayInteractable(surface);

        var button = go.AddComponent<MenuButton>();
        button._renderer = renderer;
        button._ray = ray;
        button._color = color;
        if (!string.IsNullOrEmpty(label))
            button.Label = MenuStyle.CreateText(go.transform, label, new Vector3(0f, 0f, -Depth * 0.5f - 0.0005f), size * 0.85f);

        go.SetActive(true);
        button.Refresh();
        return button;
    }

    public void SetColor(Color color)
    {
        _color = color;
        Refresh();
    }

    /// <summary>Marks the button as the current choice (e.g. the open category).</summary>
    public void SetSelected(bool active)
    {
        _active = active;
        Refresh();
    }

    void OnEnable()
    {
        if (_ray != null)
            _ray.WhenPointerEventRaised += OnPointerEvent;
    }

    void OnDisable()
    {
        if (_ray != null)
            _ray.WhenPointerEventRaised -= OnPointerEvent;
        _hovered = false;
        Refresh();
    }

    void OnPointerEvent(PointerEvent evt)
    {
        switch (evt.Type)
        {
            case PointerEventType.Hover:
                _hovered = true;
                Refresh();
                HoverChanged?.Invoke(true);
                break;
            case PointerEventType.Unhover:
                _hovered = false;
                Refresh();
                HoverChanged?.Invoke(false);
                break;
            case PointerEventType.Select:
                Clicked?.Invoke();
                break;
        }
    }

    void Refresh()
    {
        if (_renderer == null)
            return;
        Color color = _active ? MenuStyle.ButtonActive : _color;
        if (_hovered)
            color = Color.Lerp(color, Color.white, 0.25f);
        MenuStyle.Tint(_renderer, color);
    }
}

/// <summary>
/// A floating menu panel with a title, a close button, and a grab handle underneath for moving it.
/// The panel's origin is its center; content goes at negative Z (toward the player).
/// </summary>
public class MenuPanel : MonoBehaviour
{
    const float TitleHeight = 0.03f;
    const float HandleGap = 0.015f;

    public event Action Closed;

    /// <summary>Holds the panel's changeable content; cleared by ClearContent().</summary>
    public Transform Content { get; private set; }

    public Vector2 Size { get; private set; }

    MeshFilter _background;
    TextMeshPro _title;
    MenuButton _closeButton;
    Transform _handle;
    Vector3 _handleOffset;

    public static MenuPanel Create(string name, string title, Vector2 size, Transform handle)
    {
        var go = new GameObject(name);
        var panel = go.AddComponent<MenuPanel>();

        MeshRenderer background = MenuStyle.CreateBox(go.transform, "Background", new Vector3(0f, 0f, 0.0025f),
                                                      new Vector3(size.x, size.y, 0.005f), MenuStyle.Panel);
        panel._background = background.GetComponent<MeshFilter>();
        panel._title = MenuStyle.CreateText(go.transform, title, Vector3.zero, new Vector2(size.x - 0.06f, TitleHeight * 0.7f));
        panel._closeButton = MenuButton.Create(go.transform, "Close", Vector3.zero, new Vector2(0.022f, 0.022f), MenuStyle.Close, "X");
        panel._closeButton.Clicked += panel.Close;

        panel.Content = new GameObject("Content").transform;
        panel.Content.SetParent(go.transform, false);

        panel._handle = handle;
        panel.SetSize(size);
        return panel;
    }

    /// <summary>Space below the title bar where content can go, as (top Y, height).</summary>
    public float ContentTop => Size.y * 0.5f - TitleHeight - 0.005f;

    public void SetTitle(string title) => _title.text = title;

    public void SetSize(Vector2 size)
    {
        Size = size;
        _background.sharedMesh = MeshBuilder.Box(new Vector3(size.x, size.y, 0.005f));
        _title.rectTransform.sizeDelta = new Vector2(size.x - 0.06f, TitleHeight * 0.7f);
        _title.transform.localPosition = new Vector3(0f, size.y * 0.5f - TitleHeight * 0.5f - 0.003f, -0.001f);
        _closeButton.transform.localPosition = new Vector3(size.x * 0.5f - 0.018f, size.y * 0.5f - 0.018f, -MenuButton.Depth * 0.5f);
        _handleOffset = new Vector3(0f, -size.y * 0.5f - HandleGap, 0f);
        PlaceHandle();
    }

    public void ClearContent()
    {
        foreach (Transform child in Content)
            Destroy(child.gameObject);
    }

    /// <summary>Opens the panel at the given pose.</summary>
    public void Open(Vector3 position, Quaternion rotation)
    {
        transform.SetPositionAndRotation(position, rotation);
        gameObject.SetActive(true);
        PlaceHandle();
    }

    public void Close()
    {
        gameObject.SetActive(false);
        Closed?.Invoke();
    }

    public bool IsOpen => gameObject.activeSelf;

    void OnEnable()
    {
        if (_handle != null)
            _handle.gameObject.SetActive(true);
    }

    void OnDisable()
    {
        if (_handle != null)
            _handle.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (_handle != null)
            Destroy(_handle.gameObject);
    }

    // The panel follows its handle, so grabbing the handle moves the panel.
    void LateUpdate()
    {
        if (_handle == null)
            return;
        transform.SetPositionAndRotation(_handle.position - _handle.rotation * _handleOffset, _handle.rotation);
    }

    void PlaceHandle()
    {
        if (_handle != null)
            _handle.SetPositionAndRotation(transform.TransformPoint(_handleOffset), transform.rotation);
    }
}
