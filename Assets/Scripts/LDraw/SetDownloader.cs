using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LDrawImport;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Adding sets from inside the game (the headset's version of the editor's Set Finder):
/// - the LDraw parts libraries, downloaded once as zips and read without unpacking (ZipLibraryFiles);
/// - Rebrickable search and parts lists, with the player's own API key (kept in app storage, rebrickable.key);
/// - step-by-step instructions from the LDraw OMR when it has the set;
/// - every part the set needs that the app doesn't already have, made on a background thread and kept
///   in PartStore; the set itself is kept with SetData.SaveDownloaded.
/// The methods are coroutines for a MonoBehaviour to run; results and failures come back through callbacks.
/// </summary>
public static class SetDownloader
{
    const string RebrickableApi = "https://rebrickable.com/api/v3/lego/";
    const string OmrFiles = "https://library.ldraw.org/library/omr/";

    /// <summary>The project's own connection files, packed into Resources for the headset (see ConnectionsBundler).</summary>
    public const string ConnectionsResource = "ConnectionsBundle";
    public const string ConnectionsFileMarker = "#questbrick-file ";

    // The libraries: where each comes from, the file it's kept as, where its files appear to the parser,
    // which folder inside the zip holds them, and roughly how big it is (for the progress bar).
    static readonly (string url, string file, string folder, string inside, float megabytes)[] Libraries =
    {
        ("https://library.ldraw.org/library/updates/complete.zip", "complete.zip", "ldraw", "ldraw/", 146f),
        ("https://library.ldraw.org/library/unofficial/ldrawunf.zip", "ldrawunf.zip", "unofficial", "", 88f),
        ("https://codeload.github.com/RolandMelkert/LDCadShadowLibrary/zip/refs/heads/main", "shadow.zip", "shadow",
         "LDCadShadowLibrary-main/", 2f),
    };

    public static float LibraryMegabytes => Libraries.Sum(l => l.megabytes);

    public static string LibraryFolder => Path.Combine(Application.persistentDataPath, "Library");

    static string LibraryRoot => LibraryFolder + "/ldraw";

    public static bool HasLibrary => Libraries.All(l => File.Exists(Path.Combine(LibraryFolder, l.file)));

    // ---------- The player's Rebrickable key ----------

    public static string KeyPath => Path.Combine(Application.persistentDataPath, "rebrickable.key");

    public static bool HasKey => File.Exists(KeyPath) && File.ReadAllText(KeyPath).Trim().Length > 0;

    public static void SaveKey(string key)
    {
        // (Replaced rather than overwritten: a key copied on with adb belongs to another user and may be read-only.)
        if (File.Exists(KeyPath))
            File.Delete(KeyPath);
        File.WriteAllText(KeyPath, key.Trim());
    }

    static string Key => File.ReadAllText(KeyPath).Trim();

    // ---------- Rebrickable JSON (only the fields used) ----------

#pragma warning disable 0649 // filled in by JsonUtility
    [Serializable] public class SetSearchResponse { public int count; public string next; public string previous; public SetInfo[] results; }
    [Serializable] public class SetInfo { public string set_num; public string name; public int year; public int num_parts; }
    [Serializable] class InventoryResponse { public string next; public InventoryEntry[] results; }
    [Serializable] class InventoryEntry { public PartInfo part; public ColorInfo color; public int quantity; public bool is_spare; }
    [Serializable] class PartInfo { public string part_num; public string name; public PartIds external_ids; }
    [Serializable] class PartIds { public string[] LDraw; }
    [Serializable] class ColorInfo { public string name; public ColorIds external_ids; }
    [Serializable] class ColorIds { public LDrawColorIds LDraw; }
    [Serializable] class LDrawColorIds { public int[] ext_ids; }
#pragma warning restore 0649

    // ---------- Downloading the libraries ----------

    /// <summary>Downloads whichever libraries are missing. progress(text, 0..1); failed(reason) ends it early.</summary>
    public static IEnumerator DownloadLibrary(Action<string, float> progress, Action<string> failed)
    {
        Directory.CreateDirectory(LibraryFolder);
        float total = LibraryMegabytes, before = 0f;
        foreach ((string url, string file, string _, string _, float megabytes) in Libraries)
        {
            string path = Path.Combine(LibraryFolder, file), temp = path + ".download";
            if (!File.Exists(path))
            {
                using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
                {
                    request.downloadHandler = new DownloadHandlerFile(temp) { removeFileOnAbort = true };
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        float done = before + megabytes * request.downloadProgress;
                        progress($"Downloading the parts library: {done:0} of about {total:0} MB", done / total);
                        yield return null;
                    }
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        if (File.Exists(temp))
                            File.Delete(temp);
                        failed($"The download didn't finish ({request.error}). Check the headset's Wi-Fi and try again.");
                        yield break;
                    }
                }
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
            }
            before += megabytes;
        }
        CloseLibrary(); // pick up the new files next time
        progress("Parts library ready", 1f);
    }

    /// <summary>Deletes the downloaded libraries (parts already made stay).</summary>
    public static void DeleteLibrary()
    {
        CloseLibrary();
        if (Directory.Exists(LibraryFolder))
            Directory.Delete(LibraryFolder, true);
    }

    static ZipLibraryFiles s_library;

    // Opens the zips for reading (indexing their entries takes a moment: call from a background thread).
    static void OpenLibrary(string connections)
    {
        if (s_library != null)
            return;
        var library = new ZipLibraryFiles();
        foreach ((string _, string file, string folder, string inside, float _) in Libraries)
            library.AddZip(Path.Combine(LibraryFolder, file), LibraryFolder + "/" + folder, inside);
        // Our own connection files, packed in "#questbrick-file parts/xxx.dat" sections.
        string current = null;
        var text = new System.Text.StringBuilder();
        foreach (string line in (connections ?? "").Replace("\r\n", "\n").Split('\n').Append(ConnectionsFileMarker))
        {
            if (!line.StartsWith(ConnectionsFileMarker))
            {
                text.Append(line).Append('\n');
                continue;
            }
            if (current != null)
                library.AddFile(LibraryFolder + "/connections/" + current, text.ToString());
            current = line.Substring(ConnectionsFileMarker.Length).Trim();
            text.Clear();
        }
        s_library = library;
    }

    static void CloseLibrary()
    {
        s_library?.Dispose();
        s_library = null;
    }

    // ---------- Rebrickable ----------

    /// <summary>One page of sets matching the search (page 1 first).</summary>
    public static IEnumerator Search(string query, int page, int pageSize, Action<SetSearchResponse> done, Action<string> failed)
    {
        string url = $"{RebrickableApi}sets/?search={UnityWebRequest.EscapeURL(query)}&page_size={pageSize}&page={page}";
        string json = null;
        yield return Get(url, true, text => json = text, failed);
        if (json != null)
            done(JsonUtility.FromJson<SetSearchResponse>(json));
    }

    /// <summary>The OMR file for a set (its step-by-step instructions), or null if the OMR doesn't have it.</summary>
    public static IEnumerator FindInstructions(string setNumber, Action<string> done)
    {
        foreach (string extension in new[] { ".mpd", ".ldr" })
        {
            string url = OmrFiles + setNumber + extension;
            using (UnityWebRequest request = UnityWebRequest.Head(url))
            {
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    done(url);
                    yield break;
                }
            }
        }
        done(null);
    }

    // A GET, tried up to three times when the server has trouble (Rebrickable answers big parts lists with
    // "502 Bad Gateway" now and then, and always for 500-part pages of a 4,000-piece set).
    static IEnumerator Get(string url, bool rebrickable, Action<string> done, Action<string> failed)
    {
        for (int attempt = 1; ; attempt++)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 90;
                if (rebrickable)
                    request.SetRequestHeader("Authorization", "key " + Key);
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    done(request.downloadHandler.text);
                    yield break;
                }
                bool serverTrouble = request.responseCode >= 500 || request.result == UnityWebRequest.Result.ConnectionError;
                if (serverTrouble && attempt < 3)
                {
                    yield return new WaitForSecondsRealtime(2f * attempt);
                    continue;
                }
                if (request.responseCode == 401 || request.responseCode == 403)
                    failed("Rebrickable didn't accept the API key. Check it in Settings.");
                else if (request.responseCode == 429)
                    failed("Rebrickable says too many requests. Wait a minute and try again.");
                else if (request.responseCode == 404)
                    failed("Not found.");
                else
                    failed($"Couldn't reach {(rebrickable ? "Rebrickable" : "the LDraw library")} ({request.error}). Check the Wi-Fi.");
                yield break;
            }
        }
    }

    // ---------- Adding a set ----------

    /// <summary>
    /// Adds a set to the game: parts list, instructions (if `omrUrl`), and every part it needs that isn't
    /// available yet (`have` says which are). done(summary) or failed(reason).
    /// </summary>
    public static IEnumerator AddSet(SetInfo info, string omrUrl, Func<string, bool> have,
                                     Action<string, float> progress, Action<string> done, Action<string> failed)
    {
        bool stop = false;
        void Fail(string reason)
        {
            stop = true;
            failed(reason);
        }

        // 1. Open the library (in the background: indexing the zips takes a few seconds on the headset).
        progress("Opening the parts library…", 0.02f);
        string connections = Resources.Load<TextAsset>(ConnectionsResource)?.text;
        Task open = Task.Run(() => OpenLibrary(connections));
        while (!open.IsCompleted)
            yield return null;
        if (open.IsFaulted)
        {
            CloseLibrary();
            Fail("The parts library couldn't be opened. Delete it in Settings and download it again.");
            yield break;
        }
        LDrawParser.Files = s_library;

        // 2. The parts list from Rebrickable (every page; minifigure parts in, spares out).
        progress("Getting the parts list from Rebrickable…", 0.05f);
        var totals = new Dictionary<(string, int), int>();
        string url = $"{RebrickableApi}sets/{info.set_num}/parts/?page_size=100&inc_part_details=1&inc_color_details=1&inc_minifig_parts=1";
        while (url != null && !stop)
        {
            string json = null;
            yield return Get(url, true, text => json = text, Fail);
            if (stop)
                yield break;
            InventoryResponse page = JsonUtility.FromJson<InventoryResponse>(json);
            foreach (InventoryEntry entry in page.results ?? new InventoryEntry[0])
            {
                if (entry.is_spare)
                    continue;
                // Rebrickable's part and color numbers differ from LDraw's; use their LDraw cross-references.
                string partId = PickLDrawPart(entry.part);
                int[] colors = entry.color?.external_ids?.LDraw?.ext_ids;
                int colorCode = colors != null && colors.Length > 0 ? colors[0] : 16;
                var key = (partId.ToLowerInvariant(), colorCode);
                totals[key] = totals.TryGetValue(key, out int quantity) ? quantity + entry.quantity : entry.quantity;
            }
            url = string.IsNullOrEmpty(page.next) ? null : page.next;
            if (url != null)
                yield return new WaitForSecondsRealtime(1.1f); // Rebrickable allows about one request a second
        }
        List<SetData.InventoryItem> inventory = totals
            .Select(t => new SetData.InventoryItem { partId = t.Key.Item1, colorCode = t.Key.Item2, quantity = t.Value })
            .OrderBy(i => i.partId).ThenBy(i => i.colorCode).ToList();

        // 3. Step-by-step instructions from the OMR, if it has them.
        List<SetData.Step> steps = null;
        string author = null;
        var modelParts = new List<string>();
        if (omrUrl != null)
        {
            progress("Getting the step-by-step instructions…", 0.15f);
            string model = null;
            yield return Get(omrUrl, false, text => model = text, reason => { });
            if (model != null)
            {
                string modelFile = Path.Combine(SetData.DownloadFolder, info.set_num + Path.GetExtension(omrUrl));
                Directory.CreateDirectory(SetData.DownloadFolder);
                File.WriteAllText(modelFile, model);
                try
                {
                    steps = PartBuilder.ReadSteps(modelFile, out author);
                    modelParts = LDrawParser.ListModelParts(modelFile);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Couldn't read the instructions for {info.set_num}: {e.Message}");
                    steps = null;
                }
            }
        }

        // 4. Make every part the set uses that isn't in the game yet (parsing in the background).
        var needed = inventory.Select(i => i.partId).Concat(modelParts)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .Where(id => !have(id))
                              .ToList();
        var made = new List<(string id, LDrawMeshData data)>();
        var missing = new List<string>();
        int parsed = 0;
        Task parse = Task.Run(() =>
        {
            var parser = new LDrawParser(LibraryRoot) { ExtraShadowRoot = LibraryFolder + "/connections" };
            var queue = new List<string>(needed);
            for (int i = 0; i < queue.Count; i++)
            {
                string id = queue[i];
                try
                {
                    LDrawMeshData data = parser.Parse(id);
                    made.Add((id, data));
                    // Ready-made assemblies are made of other parts: those are needed too (any the game already
                    // has are skipped when saving: `have` can only be asked on the main thread).
                    foreach ((string componentId, float[] _) in data.Components)
                    {
                        if (!queue.Contains(componentId, StringComparer.OrdinalIgnoreCase))
                            queue.Add(componentId);
                    }
                }
                catch (Exception)
                {
                    missing.Add(id);
                }
                Interlocked.Increment(ref parsed);
            }
        });
        while (!parse.IsCompleted)
        {
            progress($"Making parts: {parsed} of {needed.Count}", 0.2f + 0.6f * parsed / Mathf.Max(1, needed.Count));
            yield return null;
        }
        if (parse.IsFaulted)
        {
            Fail("Making the parts failed: " + parse.Exception?.GetBaseException().Message);
            yield break;
        }

        // 5. Meshes and part files, a few per frame (Unity objects are made on the main thread).
        float sliceStart = Time.realtimeSinceStartup;
        for (int i = 0; i < made.Count; i++)
        {
            (string id, LDrawMeshData data) = made[i];
            if (!needed.Contains(id) && have(id))
                continue; // an assembly's piece the game already has
            BrickPartData part = ScriptableObject.CreateInstance<BrickPartData>();
            part.name = id;
            PartBuilder.Fill(part, id, data, PartBuilder.BuildMesh(data, id));
            PartStore.Save(part);
            if (Time.realtimeSinceStartup - sliceStart > 0.012f)
            {
                progress($"Saving parts: {i + 1} of {made.Count}", 0.8f + 0.2f * (i + 1) / Mathf.Max(1, made.Count));
                yield return null;
                sliceStart = Time.realtimeSinceStartup;
            }
        }

        // 6. The set.
        SetData set = ScriptableObject.CreateInstance<SetData>();
        set.name = info.set_num;
        set.setNumber = info.set_num;
        set.title = info.name;
        set.year = info.year;
        set.author = author;
        set.inventory = inventory;
        if (steps != null)
            set.steps = steps;
        SetData.SaveDownloaded(set);

        string summary = $"Added {info.name}: {inventory.Sum(i => i.quantity)} parts";
        summary += steps != null ? $", {steps.Count} building steps" : " (parts list only, no step-by-step instructions)";
        if (missing.Count > 0)
            summary += $". {missing.Count} part{(missing.Count == 1 ? " isn't" : "s aren't")} in the LDraw library and will be left out";
        progress(summary, 1f);
        done(summary);
    }

    // Rebrickable may list several LDraw numbers for one part, including old ones LDraw has since renamed
    // ("~Moved to ..."). Prefer a current part that's in the library, so each part is made once.
    static string PickLDrawPart(PartInfo part)
    {
        string[] candidates = part.external_ids?.LDraw != null && part.external_ids.LDraw.Length > 0
            ? part.external_ids.LDraw
            : new[] { part.part_num };

        foreach (string id in candidates)
        {
            string path = LDrawParser.FindPartFile(LibraryRoot, id);
            if (path == null)
                continue;
            string[] lines = LDrawParser.Files.ReadAllLines(path);
            if (lines.Length == 0 || !lines[0].StartsWith("0 ~Moved"))
                return id;
        }
        foreach (string id in candidates)
        {
            if (LDrawParser.FindPartFile(LibraryRoot, id) != null)
                return id;
        }
        // No LDraw version of this exact part: a plain one rather than a gap.
        foreach (string id in candidates.Append(part.part_num))
        {
            foreach (string fallback in PlainVersions(id))
            {
                if (LDrawParser.FindPartFile(LibraryRoot, fallback) != null)
                    return fallback;
            }
        }
        return candidates[0];
    }

    // Unprinted stand-ins: "3023pr0007" -> "3023"; minifigure leg and torso assemblies -> the standard ones.
    static IEnumerable<string> PlainVersions(string id)
    {
        string withoutPrint = Regex.Replace(id, "pr\\d+[a-z]?$", "");
        if (withoutPrint != id)
            yield return withoutPrint;
        if (id.StartsWith("970c"))
            yield return "3815c01"; // hips and legs
        if (id.StartsWith("973c"))
            yield return "973";     // torso
    }
}
