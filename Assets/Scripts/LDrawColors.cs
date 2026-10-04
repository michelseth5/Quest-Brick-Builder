using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every LDraw color (from the library's LDConfig.ldr), written to Assets/Resources by the LDraw importer
/// so sets can use colors beyond the color picker's palette.
/// </summary>
public class LDrawColors : ScriptableObject
{
    public const string ResourceName = "LDrawColors";

    public int[] codes = new int[0];
    public string[] names = new string[0];
    public Color[] colors = new Color[0];

    static Dictionary<int, int> _index;
    static LDrawColors _table;

    static readonly Color Fallback = new Color(0.6f, 0.6f, 0.6f);

    /// <summary>The color for an LDraw color code (gray if unknown).</summary>
    public static Color Get(int code)
    {
        return TryGet(code, out Color color, out _) ? color : Fallback;
    }

    public static bool TryGet(int code, out Color color, out string name)
    {
        // The picker palette first (exact names we chose), then the full table.
        int paletteIndex = BrickColors.IndexOfCode(code);
        if (paletteIndex >= 0)
        {
            color = BrickColors.All[paletteIndex].color;
            name = BrickColors.All[paletteIndex].name;
            return true;
        }

        if (_table == null)
        {
            _table = Resources.Load<LDrawColors>(ResourceName);
            _index = new Dictionary<int, int>();
            if (_table != null)
            {
                for (int i = 0; i < _table.codes.Length; i++)
                    _index[_table.codes[i]] = i;
            }
        }

        if (_index.TryGetValue(code, out int at))
        {
            color = _table.colors[at];
            name = _table.names[at];
            return true;
        }

        color = Fallback;
        name = $"Color {code}";
        return false;
    }
}
