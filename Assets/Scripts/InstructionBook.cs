using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

/// <summary>
/// A floating instruction book for a set, showing LEGO instruction PDFs the player downloaded
/// (in the Quest browser) and added with the Android file picker. PDFs are copied into the app's
/// storage as "&lt;set&gt; booklet &lt;n&gt;.pdf"; pages are drawn once by Android's PDF renderer and cached
/// as JPEGs. Portrait pages show two at a time like an open book; landscape pages show one at a time.
/// Move and tilt the book with its handle; the buttons turn pages, switch booklets, resize, and rotate.
/// PDF rendering only works on the headset (Android), not in the Unity editor.
/// </summary>
public class InstructionBook : MonoBehaviour
{
    const int RenderWidth = 1024;          // pixels across each cached page image
    const float DefaultPageHeight = 0.26f; // meters
    const float ControlsHeight = 0.024f;
    const int KeepAround = 3;              // page images kept in memory either side of the open page

    public static string Folder => Path.Combine(Application.persistentDataPath, "Instructions");

    Sandbox _sandbox;
    Material _pageMaterial;
    Mesh _quad;

    Transform _root;
    Transform _pages;
    Transform _handle;
    MeshRenderer _left;
    MeshRenderer _right;
    Transform _controls;
    TextMeshPro _pageLabel;
    TextMeshPro _bookLabel;
    TextMeshPro _message;

    SetData _set;
    List<string> _booklets = new List<string>();
    int _booklet;
    int _pageCount;
    int _page;            // left-hand page (0-based)
    bool _showingTwo;
    float _pageHeight = DefaultPageHeight;
    int _quarterTurns;

    Transform _chooser;
    bool _choosing;
    int _chooserPage;
    MeshRenderer _preview;
    Texture2D _previewTexture;
    string _previewWanted;

    readonly Dictionary<int, Texture2D> _textures = new Dictionary<int, Texture2D>();
    readonly List<int> _renderQueue = new List<int>();
    bool _rendering;

    public bool IsOpen => _root != null && _root.gameObject.activeSelf;

    public void Init(Sandbox sandbox, Material pageMaterial)
    {
        _sandbox = sandbox;
        _pageMaterial = pageMaterial;

        // Unity's built-in quad faces -Z, toward the player like the rest of the menus.
        GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _quad = temp.GetComponent<MeshFilter>().sharedMesh;
        DestroyImmediate(temp);

        BuildBook();
        _root.gameObject.SetActive(false);
        _handle.gameObject.SetActive(false);
    }

    // ---------- Opening ----------

    public void Open(SetData set)
    {
        if (_set != set)
        {
            _set = set;
            _booklet = 0;
            _page = 0;
            LoadBooklets();
            OpenBooklet(_booklet);
        }

        if (!IsOpen)
        {
            (Vector3 position, Quaternion rotation) = _sandbox.InFrontOfUser(0.35f, -0.08f, 0.55f);
            _handle.SetPositionAndRotation(position - rotation * HandleToRoot(), rotation);
            _root.gameObject.SetActive(true);
            _handle.gameObject.SetActive(true);
        }
        Layout();
    }

    public void Close()
    {
        if (_root == null)
            return;
        _root.gameObject.SetActive(false);
        _handle.gameObject.SetActive(false);
    }

    static List<string> FindBooklets(string setNumber)
    {
        if (!Directory.Exists(Folder))
            return new List<string>();
        return Directory.GetFiles(Folder, $"{setNumber} booklet *.pdf")
                        .OrderBy(f => BookletNumber(f))
                        .ToList();
    }

    static int BookletNumber(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int.TryParse(name.Substring(name.LastIndexOf(' ') + 1), out int number);
        return number;
    }

    void LoadBooklets() => _booklets = FindBooklets(_set.setNumber);

    void OpenBooklet(int index)
    {
        ClearTextures();
        _booklet = Mathf.Clamp(index, 0, Mathf.Max(0, _booklets.Count - 1));
        _page = 0;
        _pageCount = 0;

        if (_booklets.Count == 0)
        {
            ShowMessage("No instructions yet.\n\nDownload the set's PDF in the Quest browser,\nthen press \"Add PDF\" and pick it from Downloads.");
            return;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        using (var bridge = new AndroidJavaClass("com.michelseth5.brickbuilder.PdfBridge"))
            _pageCount = bridge.CallStatic<int>("pageCount", _booklets[_booklet]);
#endif
        if (_pageCount == 0)
        {
            ShowMessage(Application.isEditor
                ? "Instruction books open on the headset."
                : "This PDF couldn't be opened.");
            return;
        }

        ShowMessage(null);
        ShowPage(0);
    }

    // ---------- Adding a PDF ----------

    const int DownloadsPerPage = 6;
    const float ChooserWidth = 0.34f;

    string NextBookletPath()
    {
        Directory.CreateDirectory(Folder);
        int next = _booklets.Count == 0 ? 1 : _booklets.Max(BookletNumber) + 1;
        return Path.Combine(Folder, $"{_set.setNumber} booklet {next}.pdf");
    }

    // "Add PDF": lists the PDFs in the Downloads folder right in the book, so nothing else opens.
    void ShowDownloads(int page)
    {
        if (_set == null)
            return;
        _choosing = true;
        _chooserPage = page;
        ClearChooser();
        ShowMessage(null);

        float top = _pageHeight * 0.5f - 0.02f;
        var buttonSize = new Vector2(ChooserWidth - 0.03f, 0.024f);

        if (!HasFileAccess())
        {
            ChooserText("To add PDFs straight from Downloads, the game needs file access.\n" +
                        "Press Allow Access, switch on \"Allow access to manage all files\", then come back.", top - 0.04f, 0.07f);
            ChooserButton("Allow Access", top - 0.1f, buttonSize, RequestFileAccess);
            ChooserButton("Use File Picker Instead", top - 0.13f, buttonSize, AddPdfWithPicker);
            ChooserButton("Cancel", top - 0.16f, buttonSize, CloseChooser);
            Layout();
            return;
        }

        List<FileInfo> pdfs = ListDownloadedPdfs();
        ChooserText(pdfs.Count == 0 ? "No PDFs in Downloads yet." : $"Choose a PDF for {_set.title}", top - 0.008f, 0.014f);

        int pageCount = Mathf.Max(1, Mathf.CeilToInt(pdfs.Count / (float)DownloadsPerPage));
        _chooserPage = Mathf.Clamp(_chooserPage, 0, pageCount - 1);
        float y = top - 0.035f;
        foreach (FileInfo pdf in pdfs.Skip(_chooserPage * DownloadsPerPage).Take(DownloadsPerPage))
        {
            string path = pdf.FullName;
            string label = $"{Path.GetFileNameWithoutExtension(pdf.Name)}  <size=70%><color=#AAB>{pdf.Length / 1048576f:0.0} MB · {pdf.LastWriteTime:MMM d}</color></size>";
            MenuButton button = ChooserButton(label, y, buttonSize, () => ImportFromDownloads(path));
            FileInfo file = pdf;
            button.HoverChanged += pointing => { if (pointing) ShowPreview(file); };
            y -= buttonSize.y + 0.005f;
        }

        if (pageCount > 1)
            MenuStyle.AddPagingRow(_chooser, -_pageHeight * 0.5f + 0.045f, buttonSize.x, _chooserPage, pageCount, ShowDownloads);
        ChooserButton("Cancel", -_pageHeight * 0.5f + 0.018f, new Vector2(0.08f, 0.02f), CloseChooser);
        Layout();
    }

    List<FileInfo> ListDownloadedPdfs()
    {
        try
        {
            var folder = new DirectoryInfo(DownloadsFolder());
            return folder.Exists
                ? folder.GetFiles("*.pdf").OrderByDescending(f => f.LastWriteTime).ToList()
                : new List<FileInfo>();
        }
        catch (System.Exception)
        {
            return new List<FileInfo>();
        }
    }

    void ImportFromDownloads(string path)
    {
        string destination = NextBookletPath();
        try
        {
            File.Copy(path, destination, overwrite: true);
        }
        catch (System.Exception)
        {
            Sandbox.Message("Couldn't copy that PDF");
            return;
        }
        CloseChooser();
        OnBookPdfPicked(destination);
    }

    void CloseChooser()
    {
        _choosing = false;
        HidePreview();
        ClearChooser();
        if (_booklets.Count == 0)
            OpenBooklet(0); // shows the "how to add" message again
        Layout();
    }

    void ClearChooser()
    {
        foreach (Transform child in _chooser)
            Destroy(child.gameObject);
    }

    void ChooserText(string text, float y, float height) =>
        MenuStyle.CreateText(_chooser, text, new Vector3(0f, y, 0f), new Vector2(ChooserWidth - 0.03f, height)).color =
            new Color(0.15f, 0.15f, 0.2f);

    MenuButton ChooserButton(string label, float y, Vector2 size, System.Action onClick)
    {
        MenuButton button = MenuButton.Create(_chooser, label, new Vector3(0f, y, 0f), size, MenuStyle.Button, label);
        button.Clicked += onClick;
        return button;
    }

    // ---------- First-page previews in the Downloads list ----------

    const int PreviewWidth = 512;

    // Pointing at a file shows its first page beside the list (drawn once, then cached).
    void ShowPreview(FileInfo pdf)
    {
        string key = $"{Path.GetFileNameWithoutExtension(pdf.Name)}-{pdf.Length}-{pdf.LastWriteTimeUtc.Ticks}";
        string cached = Path.Combine(Folder, "cache", "previews", key + ".jpg");
        _previewWanted = cached;

        if (File.Exists(cached))
        {
            SetPreview(cached);
            return;
        }
        SetPreview(null);
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var bridge = new AndroidJavaClass("com.michelseth5.brickbuilder.PdfBridge"))
        {
            bridge.CallStatic("renderPageAsync", pdf.FullName, 0, PreviewWidth, cached, gameObject.name, nameof(OnBookPreviewRendered));
        }
#endif
    }

    // Called from Android when a preview is drawn: "0|path" or "0|error:...".
    public void OnBookPreviewRendered(string result)
    {
        string[] parts = result.Split(new[] { '|' }, 2);
        if (parts.Length == 2 && parts[1] == _previewWanted && File.Exists(parts[1]))
            SetPreview(parts[1]);
    }

    void SetPreview(string path)
    {
        if (_previewTexture != null)
            Destroy(_previewTexture);
        _previewTexture = null;

        if (path != null)
        {
            _previewTexture = new Texture2D(2, 2) { wrapMode = TextureWrapMode.Clamp };
            _previewTexture.LoadImage(File.ReadAllBytes(path));
        }

        float aspect = _previewTexture != null ? _previewTexture.width / (float)_previewTexture.height : 0.75f;
        float width = _pageHeight * aspect;
        _preview.transform.localScale = new Vector3(width, _pageHeight, 1f);
        _preview.transform.localPosition = new Vector3(ChooserWidth * 0.5f + 0.01f + width * 0.5f, 0f, 0f);
        _preview.material.mainTexture = _previewTexture;
        _preview.material.color = _previewTexture != null ? Color.white : new Color(0.85f, 0.85f, 0.85f);
        _preview.gameObject.SetActive(_choosing);
    }

    void HidePreview()
    {
        _previewWanted = null;
        if (_previewTexture != null)
            Destroy(_previewTexture);
        _previewTexture = null;
        if (_preview != null)
            _preview.gameObject.SetActive(false);
    }

    // After switching on file access in the system settings and coming back, show the list right away.
    void OnApplicationFocus(bool focused)
    {
        if (focused && _choosing && IsOpen)
            ShowDownloads(_chooserPage);
    }

    static bool HasFileAccess()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var bridge = new AndroidJavaClass("com.michelseth5.brickbuilder.PdfBridge"))
            return bridge.CallStatic<bool>("hasFileAccess");
#else
        return true;
#endif
    }

    static void RequestFileAccess()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var bridge = new AndroidJavaClass("com.michelseth5.brickbuilder.PdfBridge"))
        {
            bridge.CallStatic("requestFileAccess", activity);
        }
#endif
    }

    static string DownloadsFolder()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var bridge = new AndroidJavaClass("com.michelseth5.brickbuilder.PdfBridge"))
            return bridge.CallStatic<string>("downloadsFolder");
#else
        return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Downloads");
#endif
    }

    // Fallback: Android's file picker (may bring another app forward when it closes).
    void AddPdfWithPicker()
    {
        if (_set == null)
            return;
        CloseChooser();
        string destination = NextBookletPath();

#if UNITY_ANDROID && !UNITY_EDITOR
        using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var bridge = new AndroidJavaClass("com.michelseth5.brickbuilder.PdfBridge"))
        {
            bridge.CallStatic("pickPdf", activity, destination, gameObject.name, nameof(OnBookPdfPicked));
        }
#else
        Sandbox.Message("Adding PDFs works on the headset");
#endif
    }

    // Called from Android when the file picker closes: the saved path, "" if cancelled, or "error:...".
    public void OnBookPdfPicked(string result)
    {
        if (string.IsNullOrEmpty(result))
            return;
        if (result.StartsWith("error:"))
        {
            Sandbox.Message("Couldn't add that PDF");
            return;
        }

        LoadBooklets();
        OpenBooklet(_booklets.IndexOf(result) >= 0 ? _booklets.IndexOf(result) : _booklets.Count - 1);
        Sandbox.Message($"Added booklet {_booklets.Count} for {_set.title}");
    }

    // ---------- Pages ----------

    void ShowPage(int page)
    {
        if (_pageCount == 0)
            return;
        _page = Mathf.Clamp(page, 0, _pageCount - 1);

        for (int p = _page - 1; p <= _page + KeepAround; p++)
            RequestPage(p);
        ForgetDistantPages();
        Layout();
    }

    void Turn(int direction)
    {
        if (_pageCount == 0)
            return;
        if (direction > 0)
            ShowPage(_page + (_showingTwo ? 2 : 1));
        else
            ShowPage(_page - (IsLandscape(_page - 1) || _page - 2 < 0 ? 1 : 2));
    }

    bool IsLandscape(int page) =>
        _textures.TryGetValue(page, out Texture2D texture) && texture != null && texture.width > texture.height;

    // Loads a page from the cache, or asks Android to draw it.
    void RequestPage(int page)
    {
        if (page < 0 || page >= _pageCount || _textures.ContainsKey(page) || _renderQueue.Contains(page))
            return;

        string cached = CachePath(page);
        if (File.Exists(cached))
        {
            LoadTexture(page, cached);
            return;
        }
        _renderQueue.Add(page);
        RenderNext();
    }

    void RenderNext()
    {
        if (_rendering || _renderQueue.Count == 0)
            return;
        _rendering = true;
        int page = _renderQueue[0];
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var bridge = new AndroidJavaClass("com.michelseth5.brickbuilder.PdfBridge"))
        {
            bridge.CallStatic("renderPageAsync", _booklets[_booklet], page, RenderWidth, CachePath(page),
                              gameObject.name, nameof(OnBookPageRendered));
        }
#else
        _renderQueue.Clear();
        _rendering = false;
#endif
    }

    // Called from Android when a page has been drawn: "page|path" or "page|error:...".
    public void OnBookPageRendered(string result)
    {
        _rendering = false;
        string[] parts = result.Split(new[] { '|' }, 2);
        if (parts.Length == 2 && int.TryParse(parts[0], out int page))
        {
            _renderQueue.Remove(page);
            if (!parts[1].StartsWith("error:") && File.Exists(parts[1]))
            {
                LoadTexture(page, parts[1]);
                Layout();
            }
        }
        RenderNext();
    }

    void LoadTexture(int page, string path)
    {
        var texture = new Texture2D(2, 2) { anisoLevel = 4, wrapMode = TextureWrapMode.Clamp };
        texture.LoadImage(File.ReadAllBytes(path));
        _textures[page] = texture;
    }

    void ForgetDistantPages()
    {
        foreach (int page in _textures.Keys.ToList())
        {
            if (page < _page - 2 || page > _page + KeepAround + 1)
            {
                Destroy(_textures[page]);
                _textures.Remove(page);
            }
        }
        _renderQueue.RemoveAll(p => p < _page - 2 || p > _page + KeepAround + 1);
    }

    void ClearTextures()
    {
        foreach (Texture2D texture in _textures.Values)
            Destroy(texture);
        _textures.Clear();
        _renderQueue.Clear();
    }

    string CachePath(int page)
    {
        string book = Path.GetFileNameWithoutExtension(_booklets[_booklet]);
        return Path.Combine(Folder, "cache", book, $"{page}.jpg");
    }

    // ---------- Layout ----------

    // Portrait pages sit side by side like an open book; a landscape page (or the cover) shows alone.
    void Layout()
    {
        if (_root == null)
            return;

        _textures.TryGetValue(_page, out Texture2D leftTexture);
        _textures.TryGetValue(_page + 1, out Texture2D rightTexture);
        bool leftLandscape = leftTexture != null && leftTexture.width > leftTexture.height;
        bool rightLandscape = rightTexture != null && rightTexture.width > rightTexture.height;
        _showingTwo = !_choosing && _pageCount > 0 && _page > 0 && _page + 1 < _pageCount && !leftLandscape && !rightLandscape;

        // While choosing a PDF, a wide blank page backs the list.
        if (_choosing)
        {
            SetPage(_left, null, ChooserWidth, 0f);
            _left.gameObject.SetActive(true);
            _right.gameObject.SetActive(false);
            _pages.localRotation = Quaternion.identity;
            _controls.localPosition = new Vector3(0f, -_pageHeight * 0.5f - ControlsHeight, 0f);
            return;
        }

        float leftWidth = PageWidth(leftTexture);
        float rightWidth = PageWidth(rightTexture);
        const float gutter = 0.004f;

        SetPage(_left, _pageCount > 0 ? leftTexture : null, leftWidth,
                _showingTwo ? -(leftWidth + gutter) * 0.5f : 0f);
        SetPage(_right, _showingTwo ? rightTexture : null, rightWidth,
                _showingTwo ? (rightWidth + gutter) * 0.5f : 0f);
        _left.gameObject.SetActive(true); // a blank page backs the message when there's no PDF
        _right.gameObject.SetActive(_showingTwo);

        _pages.localRotation = Quaternion.Euler(0f, 0f, -90f * _quarterTurns);

        if (_pageCount > 0)
        {
            _pageLabel.text = _showingTwo
                ? $"Pages {_page + 1}-{_page + 2} of {_pageCount}"
                : $"Page {_page + 1} of {_pageCount}";
        }
        else
        {
            _pageLabel.text = "";
        }
        _bookLabel.text = _booklets.Count > 1 ? $"Book {_booklet + 1}/{_booklets.Count}" : "";

        _controls.localPosition = new Vector3(0f, -_pageHeight * 0.5f - ControlsHeight, 0f);
    }

    float PageWidth(Texture2D texture)
    {
        float aspect = texture != null ? texture.width / (float)texture.height : 0.75f;
        return _pageHeight * aspect;
    }

    void SetPage(MeshRenderer page, Texture2D texture, float width, float x)
    {
        page.transform.localScale = new Vector3(width, _pageHeight, 1f);
        page.transform.localPosition = new Vector3(x, 0f, 0f);
        page.material.mainTexture = texture;
        page.material.color = texture != null ? Color.white : new Color(0.85f, 0.85f, 0.85f);
    }

    void ShowMessage(string text)
    {
        _message.gameObject.SetActive(!string.IsNullOrEmpty(text));
        _message.text = text ?? "";
        Layout();
    }

    // ---------- Building the book ----------

    void BuildBook()
    {
        _root = new GameObject("Instruction Book").transform;
        _pages = new GameObject("Pages").transform;
        _pages.SetParent(_root, false);

        _left = CreatePage("Left Page");
        _right = CreatePage("Right Page");

        _message = MenuStyle.CreateText(_root, "", new Vector3(0f, 0f, -0.002f), new Vector2(0.3f, 0.12f));
        _message.color = new Color(0.15f, 0.15f, 0.2f);

        _chooser = new GameObject("Downloads").transform;
        _chooser.SetParent(_root, false);
        _chooser.localPosition = new Vector3(0f, 0f, -0.002f);

        // First-page preview of the PDF being pointed at, to the right of the list.
        _preview = CreatePage("Preview");
        _preview.transform.SetParent(_root, false);
        _preview.gameObject.SetActive(false);

        // Controls under the pages.
        _controls = new GameObject("Controls").transform;
        _controls.SetParent(_root, false);
        float x = -0.25f;
        AddButton("First", "|<", 0.03f, ref x, () => ShowPage(0));
        AddButton("Back", "<", 0.04f, ref x, () => Turn(-1));
        _pageLabel = MenuStyle.CreateText(_controls, "", new Vector3(x + 0.055f, 0f, -0.001f), new Vector2(0.1f, 0.012f));
        x += 0.11f;
        AddButton("Next", ">", 0.04f, ref x, () => Turn(1));
        AddButton("Booklet", "Book", 0.045f, ref x, () => { if (_booklets.Count > 1) OpenBooklet((_booklet + 1) % _booklets.Count); });
        _bookLabel = MenuStyle.CreateText(_controls, "", new Vector3(x - 0.0225f, -0.018f, -0.001f), new Vector2(0.05f, 0.008f));
        AddButton("Add", "Add PDF", 0.06f, ref x, () => ShowDownloads(0));
        AddButton("Turn", "Rotate", 0.05f, ref x, () => { _quarterTurns = (_quarterTurns + 1) % 4; Layout(); });
        AddButton("Smaller", "-", 0.03f, ref x, () => { _pageHeight = Mathf.Max(0.12f, _pageHeight - 0.03f); Layout(); });
        AddButton("Bigger", "+", 0.03f, ref x, () => { _pageHeight = Mathf.Min(0.6f, _pageHeight + 0.03f); Layout(); });
        AddButton("Close", "X", 0.03f, ref x, Close);

        _handle = _sandbox.CreateHandle();
        Layout();
    }

    MeshRenderer CreatePage(string name)
    {
        var page = new GameObject(name);
        page.transform.SetParent(_pages, false);
        page.AddComponent<MeshFilter>().sharedMesh = _quad;
        var renderer = page.AddComponent<MeshRenderer>();
        renderer.material = new Material(_pageMaterial);
        if (renderer.material.HasProperty("_Smoothness"))
            renderer.material.SetFloat("_Smoothness", 0f); // matte paper, no shine
        return renderer;
    }

    void AddButton(string name, string label, float width, ref float x, System.Action onClick)
    {
        MenuButton button = MenuButton.Create(_controls, name, new Vector3(x + width * 0.5f, 0f, 0f),
                                              new Vector2(width, ControlsHeight * 0.9f), MenuStyle.Button, label);
        button.Clicked += onClick;
        x += width + 0.004f;
    }

    // ---------- Following the handle ----------

    // The handle sits just under the controls; the book hangs above it.
    Vector3 HandleToRoot() => new Vector3(0f, _pageHeight * 0.5f + ControlsHeight + 0.03f, 0f);

    void LateUpdate()
    {
        if (_root == null || _handle == null || !IsOpen)
            return;
        _root.SetPositionAndRotation(_handle.TransformPoint(HandleToRoot()), _handle.rotation);
    }

    void OnDestroy()
    {
        ClearTextures();
    }
}
