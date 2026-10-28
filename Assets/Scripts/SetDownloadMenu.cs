using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Start menu > Get New Sets: the player's Rebrickable key, the one-time parts library download, searching
/// Rebrickable, and adding a set (see SetDownloader). Uses the start menu's column of buttons; long jobs show a
/// progress bar in front of you and carry on if the menu is closed.
/// </summary>
public class SetDownloadMenu : MonoBehaviour
{
    const int PageSize = 7;
    const string Title = "Get New Sets";

    Sandbox _sandbox;

    // Typing: the game's own keyboard (the Quest's system keyboard didn't open from these screens).
    VirtualKeyboard _keyboard;

    // What's going on: a download or a set being added (one at a time).
    bool _busy;
    string _status = "";
    ProgressDisplay _progress;

    // Search results, and whether the OMR has step-by-step instructions for each ("" = no, missing = still checking).
    string _query = "";
    int _page = 1;
    SetDownloader.SetSearchResponse _results;
    readonly Dictionary<string, string> _instructions = new Dictionary<string, string>();
    string _armed;           // the set waiting for a second click to add it
    float _armedUntil;

    // Which start menu screen is ours (see Sandbox.StartMenuVersion), so finished work only redraws the menu
    // while it's still showing one of these screens, not one the player has gone on to since.
    int _shownVersion = -1;
    bool _resultsShown;

    bool OnOurScreen => _sandbox.StartMenuVersion == _shownVersion;

    public void Init(Sandbox sandbox)
    {
        _sandbox = sandbox;
        _keyboard = gameObject.AddComponent<VirtualKeyboard>();
        _keyboard.Init(sandbox);
    }

    void Fill(string subtitle, List<(string, Action)> buttons, bool results = false)
    {
        _sandbox.FillStartMenu(Title, subtitle, buttons);
        _shownVersion = _sandbox.StartMenuVersion;
        _resultsShown = results;
    }

    /// <summary>Opens the right screen for where things are: key, library, search, or results.</summary>
    public void Show()
    {
        if (_busy)
        {
            Fill(_status, new List<(string, Action)> { ("Back", _sandbox.ShowStartHome) });
            return;
        }
        if (!SetDownloader.HasKey)
        {
            Fill("First, your Rebrickable API key (free at rebrickable.com/api)",
                new List<(string, Action)> { ("Enter Your Rebrickable Key", () => AskKey(Show)), ("Back", _sandbox.ShowStartHome) });
            return;
        }
        if (!SetDownloader.HasLibrary)
        {
            Fill($"Sets are made from the LDraw parts library: a one-time download of about {SetDownloader.LibraryMegabytes:0} MB",
                new List<(string, Action)> { ("Download the Parts Library", DownloadLibrary), ("Back", _sandbox.ShowStartHome) });
            return;
        }
        if (_results != null)
        {
            ShowResults();
            return;
        }
        Fill("Search Rebrickable by name or set number",
            new List<(string, Action)> { ("Search Sets", AskSearch), ("Back", _sandbox.ShowStartHome) });
    }

    // ---------- Typing ----------

    /// <summary>Asks for the Rebrickable key, then goes to `after`.</summary>
    public void AskKey(Action after)
    {
        Type("", "Rebrickable API key", key =>
        {
            if (key.Length > 0)
            {
                try
                {
                    SetDownloader.SaveKey(key);
                    _sandbox.ShowMessage("Rebrickable key saved");
                }
                catch (Exception e)
                {
                    _sandbox.ShowMessage("Couldn't save the key: " + e.Message);
                }
            }
            after();
        });
    }

    void AskSearch()
    {
        Type(_query, "Set name or number", query =>
        {
            if (query.Length == 0)
                return;
            _query = query;
            _page = 1;
            StartCoroutine(Searching());
        });
    }

    void Type(string text, string title, Action<string> typed) => _keyboard.Open(title, text, typed);

    void Update()
    {
        // An armed "click again to add" runs out after a few seconds.
        if (_armed != null && Time.time > _armedUntil)
        {
            _armed = null;
            if (OnOurScreen && _resultsShown)
                ShowResults();
        }
    }

    // ---------- Library ----------

    void DownloadLibrary()
    {
        Begin("Downloading the parts library…");
        StartCoroutine(Run(SetDownloader.DownloadLibrary(Progress, Failed),
            () => _sandbox.ShowMessage("The parts library is ready. Now search for a set")));
    }

    // ---------- Searching ----------

    IEnumerator Searching()
    {
        Fill($"Searching for \"{_query}\"…", new List<(string, Action)> { ("Back", _sandbox.ShowStartHome) });
        SetDownloader.SetSearchResponse found = null;
        string error = null;
        yield return SetDownloader.Search(_query, _page, PageSize, r => found = r, e => error = e);
        bool stillShowing = OnOurScreen; // not if the player went to another screen meanwhile
        if (error != null)
        {
            _results = null;
            if (stillShowing)
                Fill(error == "Not found." ? $"Nothing found for \"{_query}\"" : error,
                     new List<(string, Action)> { ("Search Again", AskSearch), ("Back", _sandbox.ShowStartHome) });
            yield break;
        }
        _results = found;
        if (stillShowing)
            ShowResults();

        // Then check which have step-by-step instructions, updating the list as answers come in.
        foreach (SetDownloader.SetInfo set in found.results ?? new SetDownloader.SetInfo[0])
        {
            if (_instructions.ContainsKey(set.set_num))
                continue;
            string url = null;
            yield return SetDownloader.FindInstructions(set.set_num, u => url = u);
            _instructions[set.set_num] = url ?? "";
            if (OnOurScreen && _resultsShown && _results == found)
                ShowResults();
        }
    }

    void ShowResults()
    {
        var buttons = new List<(string, Action)>();
        SetDownloader.SetInfo[] sets = _results.results ?? new SetDownloader.SetInfo[0];
        foreach (SetDownloader.SetInfo set in sets)
        {
            SetDownloader.SetInfo chosen = set;
            string instructions = !_instructions.TryGetValue(set.set_num, out string url) ? "checking…"
                                : url.Length > 0 ? "step-by-step" : "parts list only";
            bool inGame = Array.Exists(SetData.LoadAll(), s => s.setNumber == set.set_num);
            string label = _armed == set.set_num
                ? $"Click again to add {set.name}"
                : $"{set.name}  <size=70%><color=#AAB>{set.set_num} · {set.num_parts} parts · {set.year} · {instructions}" +
                  $"{(inGame ? " · in the game" : "")}</color></size>";
            buttons.Add((label, () => OnSetClicked(chosen)));
        }
        if (!string.IsNullOrEmpty(_results.previous))
            buttons.Add(("Previous Results", () => { _page--; StartCoroutine(Searching()); }));
        if (!string.IsNullOrEmpty(_results.next))
            buttons.Add(("More Results", () => { _page++; StartCoroutine(Searching()); }));
        buttons.Add(("New Search", AskSearch));
        buttons.Add(("Back", _sandbox.ShowStartHome));

        int first = (_page - 1) * PageSize + 1;
        string subtitle = sets.Length == 0
            ? $"Nothing found for \"{_query}\""
            : $"\"{_query}\": {first}-{first + sets.Length - 1} of {_results.count}. Click a set twice to add it";
        Fill(subtitle, buttons, results: true);
    }

    void OnSetClicked(SetDownloader.SetInfo set)
    {
        if (_busy)
            return;
        if (_armed != set.set_num || Time.time > _armedUntil)
        {
            _armed = set.set_num;
            _armedUntil = Time.time + 3f;
            ShowResults();
            return;
        }
        _armed = null;
        if (!_instructions.TryGetValue(set.set_num, out string url))
            url = null; // not checked yet: AddSet looks again
        StartCoroutine(Adding(set, url));
    }

    IEnumerator Adding(SetDownloader.SetInfo set, string omrUrl)
    {
        Begin($"Adding {set.name}…");
        if (omrUrl == null)
            yield return SetDownloader.FindInstructions(set.set_num, u => omrUrl = u ?? "");
        string summary = null;
        yield return Run(SetDownloader.AddSet(set, omrUrl.Length > 0 ? omrUrl : null, _sandbox.HasPart, Progress,
                                              s => summary = s, Failed),
            () => _sandbox.ShowMessage(summary + ". Find it under Build a Set or Build It Yourself"));
    }

    // ---------- Long jobs ----------

    bool _failed;

    void Begin(string status)
    {
        _busy = true;
        _failed = false;
        _status = status;
        (Vector3 position, Quaternion rotation) = _sandbox.InFrontOfUser(0f, -0.2f, 0.55f);
        _progress = ProgressDisplay.Create(position, rotation, status);
        Show();
    }

    void Progress(string status, float fraction)
    {
        _status = status;
        if (_progress != null)
            _progress.Set(status, fraction);
    }

    void Failed(string reason)
    {
        _failed = true;
        _status = reason;
        _sandbox.ShowMessage(reason);
    }

    // Runs a job, then (unless it failed) `then`. The progress bar goes away either way, and the menu moves on
    // from "working on it" if that's still what it shows.
    IEnumerator Run(IEnumerator job, Action then)
    {
        yield return job;
        _busy = false;
        if (_progress != null)
            Destroy(_progress.gameObject);
        _progress = null;
        if (!_failed)
            then();
        if (OnOurScreen)
            Show();
    }
}
