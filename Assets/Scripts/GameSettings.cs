using UnityEngine;

/// <summary>Player preferences, remembered on the headset between sessions.</summary>
public static class GameSettings
{
    public enum GhostVisibility { Least = 0, Medium = 1, Most = 2 }

    public static readonly string[] GhostVisibilityNames = { "Least see-through", "Medium", "Most see-through" };

    const string GhostKey = "GhostVisibility";

    // Opacity of set-building ghosts while waiting, and when the part in your hand is ready to drop in.
    static readonly float[] GhostAlphas = { 0.16f, 0.11f, 0.05f };
    static readonly float[] GhostReadyAlphas = { 0.45f, 0.35f, 0.25f };

    public static GhostVisibility Ghosts
    {
        get => (GhostVisibility)Mathf.Clamp(PlayerPrefs.GetInt(GhostKey, 0), 0, GhostAlphas.Length - 1);
        set
        {
            PlayerPrefs.SetInt(GhostKey, (int)value);
            PlayerPrefs.Save();
        }
    }

    public static float GhostAlpha => GhostAlphas[(int)Ghosts];
    public static float GhostReadyAlpha => GhostReadyAlphas[(int)Ghosts];

    // Joints: turn freely, or click at set angles while posing.
    public static readonly string[] JointStepNames = { "Turn freely", "Click every 15°", "Click every 45°" };
    static readonly float[] JointSteps = { 0f, 15f, 45f };
    const string JointKey = "JointStep";

    public static int JointStep
    {
        get => Mathf.Clamp(PlayerPrefs.GetInt(JointKey, 0), 0, JointSteps.Length - 1);
        set
        {
            PlayerPrefs.SetInt(JointKey, value);
            PlayerPrefs.Save();
        }
    }

    const string PerformanceKey = "ShowPerformance";

    /// <summary>Whether the performance readout (frame rate, parts, triangles, draw calls) is shown.</summary>
    public static bool ShowPerformance
    {
        get => PlayerPrefs.GetInt(PerformanceKey, 0) == 1;
        set
        {
            PlayerPrefs.SetInt(PerformanceKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    const string DriftMarkersKey = "ShowDriftMarkers";

    /// <summary>Whether locking points out (red markers + a heads-up line) studs and pins that aren't fully in.</summary>
    public static bool ShowDriftMarkers
    {
        get => PlayerPrefs.GetInt(DriftMarkersKey, 1) == 1;
        set
        {
            PlayerPrefs.SetInt(DriftMarkersKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    const string AutoLockKey = "AutoLockSeconds";

    /// <summary>Choices for Auto-lock: seconds after you let go before a model's loose pieces lock (0 = off).</summary>
    public static readonly float[] AutoLockChoices = { 5f, 15f, 0f };

    /// <summary>How long after letting go a model's loose pieces lock by themselves (0: never).</summary>
    public static float AutoLockSeconds
    {
        get => PlayerPrefs.GetFloat(AutoLockKey, 5f);
        set
        {
            PlayerPrefs.SetFloat(AutoLockKey, value);
            PlayerPrefs.Save();
        }
    }

    const string ShadowsKey = "Shadows";

    /// <summary>Whether bricks cast shadows.</summary>
    public static bool Shadows
    {
        get => PlayerPrefs.GetInt(ShadowsKey, 1) == 1;
        set
        {
            PlayerPrefs.SetInt(ShadowsKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    const string MergeKey = "MergeModels";

    /// <summary>Whether bricks sitting still are drawn as combined meshes (far fewer draw calls for big models).</summary>
    public static bool MergeModels
    {
        get => PlayerPrefs.GetInt(MergeKey, 1) == 1;
        set
        {
            PlayerPrefs.SetInt(MergeKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    const string HideStudsKey = "HideCoveredStuds";

    /// <summary>
    /// Whether locked models leave out the studs and underside tubes that are covered by other pieces
    /// (most of a brick's triangles). Takes effect the next time a model is locked or loaded.
    /// </summary>
    public static bool HideCoveredStuds
    {
        get => PlayerPrefs.GetInt(HideStudsKey, 1) == 1;
        set
        {
            PlayerPrefs.SetInt(HideStudsKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    const string CollapseKey = "CollapseLocked";

    /// <summary>
    /// Whether locked pieces are collapsed into a few lightweight objects (needed for very big models).
    /// Off, a lock only stops pieces coming off and every piece stays its own brick.
    /// </summary>
    public static bool CollapseLocked
    {
        get => PlayerPrefs.GetInt(CollapseKey, 1) == 1;
        set
        {
            PlayerPrefs.SetInt(CollapseKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    /// <summary>Degrees between joint clicks, or 0 for free turning.</summary>
    public static float JointStepDegrees => JointSteps[JointStep];
}
