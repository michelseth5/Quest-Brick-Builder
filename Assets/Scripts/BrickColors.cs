using UnityEngine;

/// <summary>
/// The colors offered in the sandbox: common real brick colors, using LDraw's color codes and values
/// (from LDConfig.ldr) so they line up with LDraw and Rebrickable data later.
/// </summary>
public static class BrickColors
{
    public readonly struct Entry
    {
        public readonly int code;
        public readonly string name;
        public readonly Color color;

        public Entry(int code, string name, string hex, float alpha = 1f)
        {
            this.code = code;
            this.name = name;
            ColorUtility.TryParseHtmlString(hex, out Color parsed);
            parsed.a = alpha;
            color = parsed;
        }

        public bool IsTransparent => color.a < 1f;
    }

    public static readonly Entry[] All =
    {
        new Entry(0, "Black", "#1B2A34"),
        new Entry(72, "Dark Bluish Gray", "#646464"),
        new Entry(71, "Light Bluish Gray", "#969696"),
        new Entry(15, "White", "#F4F4F4"),
        new Entry(19, "Tan", "#D7BA8C"),
        new Entry(28, "Dark Tan", "#897D62"),

        new Entry(320, "Dark Red", "#720012"),
        new Entry(4, "Red", "#B40000"),
        new Entry(25, "Orange", "#D67923"),
        new Entry(191, "Bright Light Orange", "#FCAC00"),
        new Entry(14, "Yellow", "#FAC80A"),
        new Entry(226, "Bright Light Yellow", "#FFEC6C"),

        new Entry(288, "Dark Green", "#00451A"),
        new Entry(2, "Green", "#00852B"),
        new Entry(27, "Lime", "#A5CA18"),
        new Entry(70, "Reddish Brown", "#5F3109"),
        new Entry(6, "Brown", "#543324"),
        new Entry(29, "Bright Pink", "#FF9ECD"),

        new Entry(272, "Dark Blue", "#19325A"),
        new Entry(1, "Blue", "#1E5AA8"),
        new Entry(73, "Medium Blue", "#7396C8"),
        new Entry(322, "Medium Azure", "#68C3E2"),
        new Entry(85, "Medium Lilac", "#441A91"),
        new Entry(26, "Magenta", "#901F76"),

        new Entry(47, "Trans Clear", "#FCFCFC", 0.5f),
        new Entry(36, "Trans Red", "#C91A09", 0.5f),
        new Entry(57, "Trans Orange", "#F08F1C", 0.5f),
        new Entry(46, "Trans Yellow", "#F5CD2F", 0.5f),
        new Entry(34, "Trans Green", "#237841", 0.5f),
        new Entry(33, "Trans Dark Blue", "#0020A0", 0.5f),
    };

    public static int IndexOfCode(int code)
    {
        for (int i = 0; i < All.Length; i++)
        {
            if (All[i].code == code)
                return i;
        }
        return -1;
    }
}
