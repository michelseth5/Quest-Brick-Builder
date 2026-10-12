using System.Collections.Generic;
using TMPro;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// An optional readout (Settings > Performance) for finding how big a build the headset handles:
/// frame rate and the slowest recent frame, parts in the world, triangles, and draw calls. It floats at
/// the lower left of the view and follows the head lazily.
/// </summary>
public class PerformanceHud : MonoBehaviour
{
    const float RefreshSeconds = 0.5f;
    const float Distance = 0.7f;

    TextMeshPro _text;
    Transform _panel;

    // Unity's own render counters, where the build provides them.
    ProfilerRecorder _drawCalls;
    ProfilerRecorder _triangles;

    float _nextRefresh;
    int _frames;
    float _elapsed;
    float _worstFrame;
    float _worstShown;
    float _fpsShown;

    readonly Dictionary<Mesh, int> _meshTriangles = new Dictionary<Mesh, int>();

    void Start()
    {
        _panel = new GameObject("Performance Readout").transform;
        MenuStyle.CreateBox(_panel, "Background", new Vector3(0f, 0f, 0.002f), new Vector3(0.16f, 0.105f, 0.002f), MenuStyle.Panel);
        _text = MenuStyle.CreateText(_panel, "", new Vector3(0f, 0f, -0.001f), new Vector2(0.15f, 0.095f), TextAlignmentOptions.Left);
        _panel.gameObject.SetActive(false);
    }

    void OnEnable()
    {
        _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
    }

    void OnDisable()
    {
        _drawCalls.Dispose();
        _triangles.Dispose();
    }

    void OnDestroy()
    {
        if (_panel != null)
            Destroy(_panel.gameObject);
    }

    void Update()
    {
        bool show = GameSettings.ShowPerformance;
        if (_panel == null)
            return;
        if (_panel.gameObject.activeSelf != show)
            _panel.gameObject.SetActive(show);
        if (!show)
            return;

        float dt = Time.unscaledDeltaTime;
        _frames++;
        _elapsed += dt;
        _worstFrame = Mathf.Max(_worstFrame, dt);

        if (Time.unscaledTime >= _nextRefresh)
        {
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
            _fpsShown = _elapsed > 0f ? _frames / _elapsed : 0f;
            _worstShown = _worstFrame;
            _frames = 0;
            _elapsed = 0f;
            _worstFrame = 0f;
            Refresh();
        }
    }

    void LateUpdate()
    {
        if (_panel == null || !_panel.gameObject.activeSelf)
            return;

        // Lower left of the view, catching up with the head gently.
        Camera head = Camera.main;
        if (head == null)
            return;
        Transform h = head.transform;
        Vector3 target = h.position + h.forward * Distance - h.right * 0.22f - h.up * 0.18f;
        float follow = 1f - Mathf.Exp(-4f * Time.unscaledDeltaTime);
        _panel.position = Vector3.Lerp(_panel.position, target, follow);
        _panel.rotation = Quaternion.LookRotation(_panel.position - h.position, Vector3.up);
    }

    void Refresh()
    {
        int parts = 0;
        foreach (Brick brick in Brick.AllPlaced())
            parts += brick.PieceCount;

        // Unity's counters if this build reports them; otherwise count what's being drawn ourselves.
        long drawCalls = _drawCalls.Valid ? _drawCalls.LastValue : 0;
        long triangles = _triangles.Valid ? _triangles.LastValue : 0;
        bool estimated = drawCalls <= 0 || triangles <= 0;
        if (estimated)
            EstimateRendering(out drawCalls, out triangles);

        (int chunks, int bricks) merged = BrickMerger.Stats;
        float frameMs = _fpsShown > 0f ? 1000f / _fpsShown : 0f;
        string fpsColor = _fpsShown >= 70f ? "#8F8" : _fpsShown >= 55f ? "#FD6" : "#F77";
        _text.text =
            $"<color={fpsColor}>{_fpsShown:0} fps</color>  {frameMs:0.0} ms  <size=80%>(slowest {_worstShown * 1000f:0} ms)</size>\n" +
            $"Parts: {parts:N0}\n" +
            $"Triangles: {triangles:N0}{(estimated ? " (est.)" : "")}\n" +
            $"Draw calls: {drawCalls:N0}{(estimated ? " (est.)" : "")}\n" +
            (GameSettings.MergeModels ? $"Merged: {merged.bricks:N0} bricks in {merged.chunks:N0} shapes" : "Merged: off") +
            $"\nSnap search: {Brick.SnapSearchMilliseconds:0.0} ms";
        Brick.SnapSearchMilliseconds = 0f;
    }

    // Visible renderers and their triangles, as a stand-in when Unity's counters aren't available.
    void EstimateRendering(out long drawCalls, out long triangles)
    {
        drawCalls = 0;
        triangles = 0;
        foreach (MeshRenderer renderer in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (!renderer.enabled || !renderer.isVisible)
                continue;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
                continue;
            drawCalls += Mathf.Max(1, mesh.subMeshCount);
            triangles += TrianglesIn(mesh);
        }
    }

    int TrianglesIn(Mesh mesh)
    {
        if (_meshTriangles.TryGetValue(mesh, out int count))
            return count;
        count = 0;
        for (int i = 0; i < mesh.subMeshCount; i++)
            count += (int)(mesh.GetIndexCount(i) / 3);
        _meshTriangles[mesh] = count;
        return count;
    }
}
