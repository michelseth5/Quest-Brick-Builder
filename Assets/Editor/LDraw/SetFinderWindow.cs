using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using LDrawImport;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Set Finder. Searches Rebrickable for sets, shows which have LDraw step-by-step instructions
/// (in the LDraw Official Model Repository), and adds a set to the game: its parts list from Rebrickable,
/// its instructions from the OMR when available, and every part it needs imported.
/// Needs a Rebrickable API key in Secrets/rebrickable.key (that folder is never committed).
/// </summary>
public class SetFinderWindow : EditorWindow
{
    const string RebrickableApi = "https://rebrickable.com/api/v3/lego/";
    const string OmrFiles = "https://library.ldraw.org/library/omr/";

    static readonly HttpClient Http = new HttpClient();

    enum Instructions { Checking, StepByStep, PartsListOnly }

    class Result
    {
        public SetInfo info;
        public Instructions instructions = Instructions.Checking;
        public string omrUrl;
    }

    string _query = "";
    readonly List<Result> _results = new List<Result>();
    string _nextPage;
    string _previousPage;
    int _total;
    bool _busy;
    string _status = "";
    string _log = "";
    Vector2 _scroll;
    Vector2 _logScroll;

    static string KeyPath => Path.Combine(LDrawImportWindow.ProjectRoot, "Secrets", "rebrickable.key");

    [MenuItem("Tools/Set Finder")]
    static void Open() => GetWindow<SetFinderWindow>("Set Finder");

    // ---------- Window ----------

    void OnGUI()
    {
        if (!File.Exists(KeyPath))
        {
            EditorGUILayout.HelpBox("Put your Rebrickable API key in Secrets/rebrickable.key (next to Assets).", MessageType.Error);
            return;
        }

        EditorGUILayout.LabelField("Search Rebrickable by set name or number", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.SetNextControlName("Query");
            _query = EditorGUILayout.TextField(_query);
            bool enter = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                         && GUI.GetNameOfFocusedControl() == "Query";
            using (new EditorGUI.DisabledScope(_busy || string.IsNullOrWhiteSpace(_query)))
            {
                if (GUILayout.Button("Search", GUILayout.Width(80)) || (enter && !_busy && !string.IsNullOrWhiteSpace(_query)))
                    Search(RebrickableApi + "sets/?page_size=25&search=" + Uri.EscapeDataString(_query.Trim()));
            }
        }

        if (!string.IsNullOrEmpty(_status))
            EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);

        _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(200));
        foreach (Result result in _results)
            DrawResult(result);
        EditorGUILayout.EndScrollView();

        if (_previousPage != null || _nextPage != null)
        {
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(_busy))
            {
                GUI.enabled = !_busy && _previousPage != null;
                if (GUILayout.Button("< Previous"))
                    Search(_previousPage);
                GUI.enabled = !_busy && _nextPage != null;
                if (GUILayout.Button("Next >"))
                    Search(_nextPage);
                GUI.enabled = true;
            }
        }

        EditorGUILayout.Space();
        _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.MinHeight(120));
        EditorGUILayout.LabelField(_log, EditorStyles.wordWrappedLabel);
        EditorGUILayout.EndScrollView();
    }

    void DrawResult(Result result)
    {
        SetInfo set = result.info;
        bool added = File.Exists(Path.Combine(LDrawImportWindow.ProjectRoot, LDrawImportWindow.SetAssetPath(set.set_num)));

        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUILayout.LabelField($"{set.name}", EditorStyles.boldLabel);
                string instructions = result.instructions switch
                {
                    Instructions.StepByStep => "Step-by-step instructions available",
                    Instructions.PartsListOnly => "Parts list only (build it yourself)",
                    _ => "Checking for instructions...",
                };
                EditorGUILayout.LabelField($"{set.set_num} · {set.year} · {set.num_parts} parts · {instructions}", EditorStyles.miniLabel);
            }

            using (new EditorGUI.DisabledScope(_busy || result.instructions == Instructions.Checking))
            {
                if (GUILayout.Button(added ? "Update" : "Add", GUILayout.Width(70), GUILayout.Height(32)))
                    AddSet(result);
            }
        }
    }

    // ---------- Search ----------

    async void Search(string url)
    {
        _busy = true;
        _status = "Searching...";
        Repaint();
        try
        {
            SetSearchResponse response = JsonUtility.FromJson<SetSearchResponse>(await Get(url, rebrickable: true));
            _results.Clear();
            _results.AddRange((response.results ?? new SetInfo[0]).Select(s => new Result { info = s }));
            // (JsonUtility turns a missing page into "" rather than null.)
            _nextPage = string.IsNullOrEmpty(response.next) ? null : response.next;
            _previousPage = string.IsNullOrEmpty(response.previous) ? null : response.previous;
            _total = response.count;
            _status = _total == 0 ? "No sets found." : $"{_total} sets found.";
            _scroll = Vector2.zero;
        }
        catch (Exception e)
        {
            _status = "Search failed: " + e.Message;
        }
        finally
        {
            _busy = false;
            Repaint();
        }

        // Check each result for LDraw instructions in the background.
        foreach (Result result in _results.ToList())
        {
            result.omrUrl = await FindOmrFile(result.info.set_num);
            result.instructions = result.omrUrl != null ? Instructions.StepByStep : Instructions.PartsListOnly;
            Repaint();
        }
    }

    static async Task<string> FindOmrFile(string setNumber)
    {
        foreach (string extension in new[] { ".mpd", ".ldr" })
        {
            string url = OmrFiles + setNumber + extension;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                using (HttpResponseMessage response = await Http.SendAsync(request))
                {
                    if (response.IsSuccessStatusCode)
                        return url;
                }
            }
            catch (HttpRequestException)
            {
                // Treat network trouble as "not available".
            }
        }
        return null;
    }

    // ---------- Adding a set ----------

    async void AddSet(Result result)
    {
        SetInfo set = result.info;
        string libraryRoot = LDrawParser.FindLibraryRoot(LDrawImportWindow.ProjectRoot);
        if (libraryRoot == null)
        {
            _log = "The LDraw library wasn't found (it goes in an 'LDraw' folder next to Assets).";
            return;
        }

        _busy = true;
        var log = new StringBuilder($"Adding {set.set_num} {set.name}\n\n");
        try
        {
            // 1. Parts list from Rebrickable (all pages, minifigure parts included, spares left out).
            _status = "Getting the parts list from Rebrickable...";
            Repaint();
            List<SetData.InventoryItem> inventory = await FetchInventory(set.set_num, libraryRoot, log);
            log.AppendLine($"Parts list: {inventory.Sum(i => i.quantity)} parts in {inventory.Count} part/color combinations.");

            // 2. Step-by-step instructions from the OMR, if they exist.
            string modelFile = null;
            if (result.omrUrl != null)
            {
                _status = "Downloading LDraw instructions...";
                Repaint();
                Directory.CreateDirectory(LDrawImportWindow.SetsFolder);
                modelFile = Path.Combine(LDrawImportWindow.SetsFolder, set.set_num + Path.GetExtension(result.omrUrl));
                File.WriteAllText(modelFile, await Get(result.omrUrl, rebrickable: false));
                log.AppendLine($"Instructions downloaded to Sets/{Path.GetFileName(modelFile)}.");
            }

            // 3. Import every part either source uses.
            _status = "Importing parts...";
            Repaint();
            var partIds = new List<string>(inventory.Select(i => i.partId));
            if (modelFile != null)
                partIds.AddRange(LDrawParser.ListModelParts(modelFile));
            partIds = partIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string importLog = LDrawImportWindow.ImportParts(libraryRoot, partIds, null);
            int failed = importLog.Split('\n').Count(line => line.StartsWith("✗"));
            log.AppendLine($"Parts imported: {partIds.Count - failed} of {partIds.Count}.");
            if (failed > 0)
            {
                log.AppendLine("Not in your LDraw library (these will be left out):");
                foreach (string line in importLog.Split('\n').Where(l => l.StartsWith("✗")))
                    log.AppendLine("   " + line.Trim());
            }

            // 4. Save the set for the game.
            log.AppendLine(LDrawImportWindow.SaveSet(set.set_num, modelFile, set.name, set.year, inventory));
            if (modelFile != null)
                AddCredit(set, modelFile);

            _status = $"Added {set.name}.";
        }
        catch (Exception e)
        {
            log.AppendLine("Failed: " + e.Message);
            _status = "Adding the set failed.";
        }
        finally
        {
            _busy = false;
            _log = log.ToString();
            Repaint();
        }
    }

    static async Task<List<SetData.InventoryItem>> FetchInventory(string setNumber, string libraryRoot, StringBuilder log)
    {
        var totals = new Dictionary<(string, int), int>();
        var unmapped = new HashSet<string>();
        string url = $"{RebrickableApi}sets/{setNumber}/parts/?page_size=500&inc_part_details=1&inc_color_details=1&inc_minifig_parts=1";

        while (url != null)
        {
            InventoryResponse page = JsonUtility.FromJson<InventoryResponse>(await Get(url, rebrickable: true));
            foreach (InventoryEntry entry in page.results ?? new InventoryEntry[0])
            {
                if (entry.is_spare)
                    continue;

                // Rebrickable's part and color numbers differ from LDraw's; use their LDraw cross-references.
                string partId = PickLDrawPart(entry.part, libraryRoot);
                int colorCode = entry.color.external_ids?.LDraw?.ext_ids != null && entry.color.external_ids.LDraw.ext_ids.Length > 0
                    ? entry.color.external_ids.LDraw.ext_ids[0]
                    : 16;
                if (colorCode == 16)
                    unmapped.Add(entry.color.name);

                var key = (partId.ToLowerInvariant(), colorCode);
                totals[key] = totals.TryGetValue(key, out int quantity) ? quantity + entry.quantity : entry.quantity;
            }
            url = string.IsNullOrEmpty(page.next) ? null : page.next;
            if (url != null)
                await Task.Delay(1100); // Rebrickable allows about one request per second
        }

        if (unmapped.Count > 0)
            log.AppendLine($"Colors with no LDraw match (shown in the brick's default color): {string.Join(", ", unmapped)}");

        return totals.Select(t => new SetData.InventoryItem { partId = t.Key.Item1, colorCode = t.Key.Item2, quantity = t.Value })
                     .OrderBy(i => i.partId).ThenBy(i => i.colorCode).ToList();
    }

    // Rebrickable may list several LDraw numbers for one part, including old ones LDraw has since renamed
    // ("~Moved to ..."). Prefer a current part that's in the library, so each part is imported once.
    static string PickLDrawPart(PartInfo part, string libraryRoot)
    {
        string[] candidates = part.external_ids?.LDraw != null && part.external_ids.LDraw.Length > 0
            ? part.external_ids.LDraw
            : new[] { part.part_num };

        foreach (string id in candidates)
        {
            string path = LDrawParser.FindPartFile(libraryRoot, id);
            if (path == null)
                continue;
            string firstLine = File.ReadLines(path).FirstOrDefault() ?? "";
            if (!firstLine.StartsWith("0 ~Moved"))
                return id;
        }
        foreach (string id in candidates)
        {
            if (LDrawParser.FindPartFile(libraryRoot, id) != null)
                return id;
        }

        // No LDraw version of this exact part: fall back to a plain one rather than leaving a gap.
        foreach (string id in candidates.Append(part.part_num))
        {
            foreach (string fallback in PlainVersions(id))
            {
                if (LDrawParser.FindPartFile(libraryRoot, fallback) != null)
                    return fallback;
            }
        }
        return candidates[0];
    }

    // Unprinted stand-ins: "3023pr0007" -> "3023"; minifigure leg and torso assemblies -> the standard ones.
    static IEnumerable<string> PlainVersions(string id)
    {
        string withoutPrint = System.Text.RegularExpressions.Regex.Replace(id, "pr\\d+[a-z]?$", "");
        if (withoutPrint != id)
            yield return withoutPrint;
        if (id.StartsWith("970c"))
            yield return "3815c01"; // hips and legs
        if (id.StartsWith("973c"))
            yield return "973";     // torso
    }

    // The OMR's license requires crediting each model's author.
    static void AddCredit(SetInfo set, string modelFile)
    {
        string creditsPath = Path.Combine(LDrawImportWindow.ProjectRoot, "CREDITS.md");
        SetData data = AssetDatabase.LoadAssetAtPath<SetData>(LDrawImportWindow.SetAssetPath(set.set_num));
        if (!File.Exists(creditsPath) || data == null)
            return;

        string fileName = Path.GetFileName(modelFile);
        string credits = File.ReadAllText(creditsPath);
        if (credits.Contains($"`{fileName}`"))
            return;

        string line = $"- `{fileName}` — {set.name} ({set.set_num.Split('-')[0]}), modeled by {data.author}";
        const string marker = "\nLEGO® is a trademark";
        int at = credits.IndexOf(marker, StringComparison.Ordinal);
        credits = at >= 0 ? credits.Insert(at, line + "\n") : credits + "\n" + line + "\n";
        File.WriteAllText(creditsPath, credits);
    }

    static async Task<string> Get(string url, bool rebrickable)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            if (rebrickable)
                request.Headers.TryAddWithoutValidation("Authorization", "key " + File.ReadAllText(KeyPath).Trim());
            using (HttpResponseMessage response = await Http.SendAsync(request))
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new Exception("Rebrickable didn't accept the API key.");
                if (response.StatusCode == (HttpStatusCode)429)
                    throw new Exception("Rebrickable says too many requests; wait a minute and try again.");
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
        }
    }

    // ---------- Rebrickable JSON (only the fields we use) ----------

#pragma warning disable 0649 // fields are filled in by JsonUtility
    [Serializable] class SetSearchResponse { public int count; public string next; public string previous; public SetInfo[] results; }
    [Serializable] class SetInfo { public string set_num; public string name; public int year; public int num_parts; }
    [Serializable] class InventoryResponse { public string next; public InventoryEntry[] results; }
    [Serializable] class InventoryEntry { public PartInfo part; public ColorInfo color; public int quantity; public bool is_spare; }
    [Serializable] class PartInfo { public string part_num; public string name; public PartIds external_ids; }
    [Serializable] class PartIds { public string[] LDraw; }
    [Serializable] class ColorInfo { public string name; public ColorIds external_ids; }
    [Serializable] class ColorIds { public LDrawColorIds LDraw; }
    [Serializable] class LDrawColorIds { public int[] ext_ids; }
#pragma warning restore 0649
}
