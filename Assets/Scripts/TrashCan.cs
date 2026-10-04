using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// An open-topped bin. Any brick (or group) let go of inside it is deleted.
/// Turns red while a held brick is over it. Pivot is at the bottom center.
/// </summary>
public class TrashCan : MonoBehaviour
{
    public static readonly Vector3 Size = new Vector3(0.14f, 0.12f, 0.14f);
    const float WallThickness = 0.006f;
    const float ExtraHeight = 0.04f; // counts as "inside" a little above the rim too

    static readonly List<TrashCan> All = new List<TrashCan>();
    static readonly Color Normal = new Color(0.30f, 0.32f, 0.35f);
    static readonly Color Warning = new Color(0.80f, 0.15f, 0.15f);

    MeshRenderer _renderer;
    bool _warning;

    public static bool Contains(Vector3 worldPoint)
    {
        foreach (TrashCan trash in All)
        {
            if (trash.IsInside(worldPoint))
                return true;
        }
        return false;
    }

    bool IsInside(Vector3 worldPoint)
    {
        Vector3 local = transform.InverseTransformPoint(worldPoint);
        return Mathf.Abs(local.x) < Size.x * 0.5f && Mathf.Abs(local.z) < Size.z * 0.5f
               && local.y > 0f && local.y < Size.y + ExtraHeight;
    }

    /// <summary>Bottom plus four walls, open at the top.</summary>
    public static Mesh BuildMesh()
    {
        var builder = new MeshBuilder();
        float hx = Size.x * 0.5f, hz = Size.z * 0.5f, h = Size.y, t = WallThickness;
        builder.AddBox(new Vector3(-hx, 0f, -hz), new Vector3(hx, t, hz));
        builder.AddBox(new Vector3(-hx, 0f, -hz), new Vector3(-hx + t, h, hz));
        builder.AddBox(new Vector3(hx - t, 0f, -hz), new Vector3(hx, h, hz));
        builder.AddBox(new Vector3(-hx, 0f, -hz), new Vector3(hx, h, -hz + t));
        builder.AddBox(new Vector3(-hx, 0f, hz - t), new Vector3(hx, h, hz));
        return builder.ToMesh("Trash Can");
    }

    void Awake()
    {
        _renderer = GetComponent<MeshRenderer>();
    }

    void OnEnable()
    {
        All.Add(this);
        _warning = true; // force a color refresh
    }

    void OnDisable() => All.Remove(this);

    void Update()
    {
        bool warning = false;
        foreach (Brick brick in Brick.Held)
        {
            if (IsInside(brick.transform.TransformPoint(brick.BodyBounds.center)))
                warning = true;
        }

        if (warning != _warning && _renderer != null)
        {
            _warning = warning;
            MenuStyle.Tint(_renderer, warning ? Warning : Normal);
        }
    }
}
