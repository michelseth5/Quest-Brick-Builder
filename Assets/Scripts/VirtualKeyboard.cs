using System;
using TMPro;
using UnityEngine;

/// <summary>
/// The game's own keyboard: a floating panel of keys clicked with the laser like any other menu button, with the
/// text typed so far shown along the top. Used where the Quest's system keyboard didn't open (Get New Sets).
/// </summary>
public class VirtualKeyboard : MonoBehaviour
{
    const float Key = 0.026f, Gap = 0.004f, Margin = 0.012f, DisplayHeight = 0.03f;

    static readonly string[] Rows = { "1234567890", "qwertyuiop", "asdfghjkl-", "zxcvbnm'.," };

    Sandbox _sandbox;
    MenuPanel _panel;
    TextMeshPro _display;
    string _text = "";
    bool _shift;
    Action<string> _done;
    bool _finishing; // closing because Done was clicked (the X only cancels)

    public bool IsOpen => _panel != null && _panel.IsOpen;

    public void Init(Sandbox sandbox)
    {
        _sandbox = sandbox;
        float width = Margin * 2f + Rows[0].Length * (Key + Gap) - Gap;
        float height = 0.045f + DisplayHeight + Gap + (Rows.Length + 1) * (Key + Gap) + Margin;
        _panel = MenuPanel.Create("Keyboard", "", new Vector2(width, height), sandbox.CreateHandle());
        _panel.Closed += () =>
        {
            if (!_finishing)
                _done = null; // closed with the X: nothing typed
        };
        _panel.Close();
    }

    /// <summary>Opens the keyboard in front of you; `done` gets the text when Done is clicked (not if it's closed).</summary>
    public void Open(string title, string text, Action<string> done)
    {
        _text = text ?? "";
        _shift = false;
        _done = done;
        _panel.SetTitle(title);
        (Vector3 position, Quaternion rotation) = _sandbox.InFrontOfUser(0f, 0.12f, 0.42f);
        _panel.Open(position, rotation);
        Build();
    }

    void Build()
    {
        _panel.ClearContent();
        float width = _panel.Size.x;
        float top = _panel.ContentTop;

        // What's been typed, with a cursor at the end (the start scrolls off if it's long).
        MenuStyle.CreateBox(_panel.Content, "Display Background", new Vector3(0f, top - DisplayHeight * 0.5f, -0.0005f),
                            new Vector3(width - Margin * 2f, DisplayHeight, 0.001f), MenuStyle.Button);
        _display = MenuStyle.CreateText(_panel.Content, "", new Vector3(0f, top - DisplayHeight * 0.5f, -0.0015f),
                                        new Vector2(width - Margin * 2f - 0.008f, DisplayHeight * 0.6f), TextAlignmentOptions.Right);
        _display.enableAutoSizing = false;
        _display.fontSize = 0.12f;
        _display.overflowMode = TextOverflowModes.Overflow;
        ShowText();

        float left = -width * 0.5f + Margin + Key * 0.5f;
        float y = top - DisplayHeight - Gap - Key * 0.5f;
        foreach (string row in Rows)
        {
            for (int i = 0; i < row.Length; i++)
            {
                char c = _shift ? char.ToUpperInvariant(row[i]) : row[i];
                AddKey(c.ToString(), new Vector3(left + i * (Key + Gap), y, 0f), Key, MenuStyle.Button, () => Type(c));
            }
            y -= Key + Gap;
        }

        // Bottom row: Shift, Space, Delete, Done.
        float x = -width * 0.5f + Margin;
        void Wide(string label, float keys, Color color, Action click)
        {
            float w = keys * Key + (keys - 1f) * Gap;
            AddKey(label, new Vector3(x + w * 0.5f, y, 0f), w, color, click);
            x += w + Gap;
        }
        Wide(_shift ? "SHIFT" : "Shift", 2f, _shift ? MenuStyle.ButtonActive : MenuStyle.Button, () => { _shift = !_shift; Build(); });
        Wide("Space", 4f, MenuStyle.Button, () => Type(' '));
        Wide("Delete", 2f, MenuStyle.Button, Backspace);
        Wide("Done", 2f, MenuStyle.ButtonActive, Finish);
    }

    void AddKey(string label, Vector3 position, float width, Color color, Action click)
    {
        MenuButton button = MenuButton.Create(_panel.Content, "Key " + label, position, new Vector2(width, Key), color, label);
        button.Clicked += click;
    }

    void Type(char c)
    {
        _text += c;
        if (_shift)
        {
            _shift = false; // one capital at a time, like a phone keyboard
            Build();
            return;
        }
        ShowText();
    }

    void Backspace()
    {
        if (_text.Length > 0)
            _text = _text.Substring(0, _text.Length - 1);
        ShowText();
    }

    void ShowText()
    {
        if (_display != null)
            _display.text = TMP_Escaped(_text) + "<color=#9CF>|</color>";
    }

    // Typed text shown as it is, not read as formatting tags.
    static string TMP_Escaped(string text) => "<noparse>" + text + "</noparse>";

    void Finish()
    {
        Action<string> done = _done;
        _done = null;
        _finishing = true;
        _panel.Close();
        _finishing = false;
        done?.Invoke(_text.Trim());
    }
}
