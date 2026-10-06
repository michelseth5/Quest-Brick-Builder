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

    /// <summary>Degrees between joint clicks, or 0 for free turning.</summary>
    public static float JointStepDegrees => JointSteps[JointStep];
}
