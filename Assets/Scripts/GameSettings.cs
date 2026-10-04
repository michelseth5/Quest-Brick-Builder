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
}
