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
    // How far one-line text may shrink to fit its width before it's cut off instead.
    const float ReadableShare = 0.8f;

    // All menu text, smaller than filling its box: the owner found full-height text too big for the buttons
    // ("2 points smaller", Oct 26).
    const float TextScale = 0.83f;

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
        label.fontSizeMax = 0.4f * TextScale;

        // One line of text: the size that fills the box's height, shrinking a little at most for long text.
        // Past that it's cut off with "…" (pointing at a button scrolls the rest into view: MarqueeText)
        // instead of shrinking to unreadable. Text set up with several lines, or none yet, still fits itself.
        if (!string.IsNullOrEmpty(text) && text.IndexOf('\n') < 0)
        {
            label.enableAutoSizing = false;
            label.fontSize = 1f;
            float lineHeight = label.GetPreferredValues("Ag").y; // at font size 1
            label.enableAutoSizing = true;
            if (lineHeight > 0f)
            {
                float fill = size.y / lineHeight * TextScale;
                label.fontSizeMax = Mathf.Min(0.4f, fill);
                label.fontSizeMin = Mathf.Min(label.fontSizeMax, fill * ReadableShare);
                label.overflowMode = TextOverflowModes.Ellipsis;
            }
        }

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
        {
            button.Label = MenuStyle.CreateText(go.transform, label, new Vector3(0f, 0f, -Depth * 0.5f - 0.0005f), size * 0.85f);
            // Text too long for the button scrolls while it's pointed at.
            var marquee = button.Label.gameObject.AddComponent<MarqueeText>();
            button.HoverChanged += marquee.SetPointedAt;
        }

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
            color = Color.Lerp(color, Color.white, 0.45f);
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

/// <summary>
/// Scrolls a line of text that's too long for its space while it's pointed at, so the cut-off part can be
/// read: it waits a moment, steps along a character at a time, holds at the end, then starts over. The text
/// goes back as it was when the pointer leaves. (Shown without its colors while scrolling.)
/// </summary>
public class MarqueeText : MonoBehaviour
{
    const float StartPause = 0.6f, Step = 0.11f, EndPause = 1.2f;

    TextMeshPro _label;
    string _original;
    string _plain;
    string _shown; // the text as the scroller last left it
    int _offset;
    float _next;
    bool _scrolling;

    RayInteractable _ray;

    void Awake() => _label = GetComponent<TextMeshPro>();

    /// <summary>
    /// Makes a plain text (not a button) scroll while the laser points at it: gives it an invisible
    /// pointing target the size of its box. Clicking it does nothing.
    /// </summary>
    public static MarqueeText AddTo(TextMeshPro label)
    {
        GameObject go = label.gameObject;
        bool wasActive = go.activeSelf;
        go.SetActive(false); // Interaction SDK pieces must be wired up before the object is switched on
        var collider = go.AddComponent<BoxCollider>();
        collider.size = new Vector3(label.rectTransform.sizeDelta.x, label.rectTransform.sizeDelta.y, 0.002f);
        collider.isTrigger = true;
        var surface = go.AddComponent<ColliderSurface>();
        surface.InjectAllColliderSurface(collider);
        var ray = go.AddComponent<RayInteractable>();
        ray.InjectAllRayInteractable(surface);
        var marquee = go.AddComponent<MarqueeText>();
        marquee._ray = ray;
        go.SetActive(wasActive);
        return marquee;
    }

    void OnEnable()
    {
        if (_ray != null)
            _ray.WhenPointerEventRaised += OnPointerEvent;
    }

    void OnPointerEvent(PointerEvent evt)
    {
        if (evt.Type == PointerEventType.Hover)
            SetPointedAt(true);
        else if (evt.Type == PointerEventType.Unhover)
            SetPointedAt(false);
    }

    public void SetPointedAt(bool pointed)
    {
        if (_label == null)
            return;
        if (pointed && !_scrolling)
        {
            _label.ForceMeshUpdate();
            if (!IsCutOff())
                return; // all of it shows already
            _original = _shown = _label.text;
            _plain = System.Text.RegularExpressions.Regex.Replace(_original, "<[^>]*>", "");
            _offset = 0;
            _scrolling = true;
            _next = Time.unscaledTime + StartPause;
        }
        else if (!pointed)
        {
            Restore();
        }
    }

    void Update()
    {
        if (_scrolling && _label.text != _shown)
        {
            // Something else changed the text: start over with the new one (scrolling it if it's cut off).
            _scrolling = false;
            SetPointedAt(true);
        }
        if (!_scrolling || Time.unscaledTime < _next)
            return;

        _label.ForceMeshUpdate();
        if (!IsCutOff() && _offset > 0)
        {
            // The end is showing: hold there, then go back to the start.
            _offset = 0;
            _label.text = _shown = _plain;
            _next = Time.unscaledTime + EndPause + StartPause;
            return;
        }
        _offset = Mathf.Min(_offset + 1, Mathf.Max(0, _plain.Length - 1));
        _label.text = _shown = "…" + _plain.Substring(_offset);
        _next = Time.unscaledTime + Step;
    }

    // True if the line didn't fit and TextMesh Pro put in a "…" (its isTextOverflowing only reports running out of
    // height, never a single line cut off at the side). Expects ForceMeshUpdate first.
    bool IsCutOff()
    {
        TMP_TextInfo info = _label.textInfo;
        int shown = 0;
        for (int i = 0; i < info.characterCount; i++)
        {
            if (info.characterInfo[i].character == '\u2026')
                shown++;
        }
        int written = 0;
        foreach (char c in _label.text)
        {
            if (c == '\u2026')
                written++;
        }
        return shown > written;
    }

    void Restore()
    {
        // Unless something else has changed the text meanwhile (e.g. "Click again to ..."): that stays.
        if (_scrolling && _label != null && _label.text == _shown)
            _label.text = _original;
        _scrolling = false;
    }

    void OnDisable()
    {
        if (_ray != null)
            _ray.WhenPointerEventRaised -= OnPointerEvent;
        Restore();
    }
}

/// <summary>
/// A small floating panel with a line of text and a bar that fills from left to right, for things that take
/// a while (loading a big model). Destroy its GameObject when done.
/// </summary>
public class ProgressDisplay : MonoBehaviour
{
    const float Width = 0.26f, BarHeight = 0.008f;

    TextMeshPro _label;
    Transform _fill;

    public static ProgressDisplay Create(Vector3 position, Quaternion rotation, string text)
    {
        var go = new GameObject("Progress");
        go.transform.SetPositionAndRotation(position, rotation);
        var display = go.AddComponent<ProgressDisplay>();
        MenuStyle.CreateBox(go.transform, "Background", new Vector3(0f, 0f, 0.002f), new Vector3(Width + 0.02f, 0.045f, 0.004f), MenuStyle.Panel);
        display._label = MenuStyle.CreateText(go.transform, text, new Vector3(0f, 0.008f, -0.001f), new Vector2(Width, 0.016f));
        MenuStyle.CreateBox(go.transform, "Track", new Vector3(0f, -0.011f, -0.0005f), new Vector3(Width, BarHeight, 0.001f), MenuStyle.Button);
        display._fill = MenuStyle.CreateBox(go.transform, "Fill", new Vector3(0f, -0.011f, -0.0012f), new Vector3(1f, BarHeight, 0.001f),
                                            MenuStyle.ButtonActive).transform;
        display.Set(text, 0f);
        return display;
    }

    /// <summary>Shows the text and how far along it is (0 to 1).</summary>
    public void Set(string text, float fraction)
    {
        if (_label.text != text)
            _label.text = text;
        float width = Width * Mathf.Clamp01(fraction);
        _fill.gameObject.SetActive(width > 0.0005f);
        _fill.localScale = new Vector3(Mathf.Max(width, 0.0001f), 1f, 1f);
        _fill.localPosition = new Vector3(-Width * 0.5f + width * 0.5f, _fill.localPosition.y, _fill.localPosition.z);
    }
}

/// <summary>
/// Makes any object something the laser can point at and click, like a menu button but with its own look
/// (e.g. the markers and the enlarged piece in Fix Snapping). Pointer events include where the laser hits.
/// </summary>
public class Pointable : MonoBehaviour
{
    /// <summary>Hover, Unhover, Move (while pointed at) and Select (clicked), with where the laser hits.</summary>
    public event Action<PointerEvent> Pointed;

    RayInteractable _ray;

    /// <summary>Adds pointing to an object, using the given collider as what the laser hits.</summary>
    public static Pointable Add(GameObject go, Collider collider)
    {
        bool wasActive = go.activeSelf;
        go.SetActive(false); // Interaction SDK pieces must be wired up before the object is switched on
        var surface = go.AddComponent<ColliderSurface>();
        surface.InjectAllColliderSurface(collider);
        var ray = go.AddComponent<RayInteractable>();
        ray.InjectAllRayInteractable(surface);
        var pointable = go.AddComponent<Pointable>();
        pointable._ray = ray;
        go.SetActive(wasActive);
        return pointable;
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
    }

    void OnPointerEvent(PointerEvent evt) => Pointed?.Invoke(evt);
}
