using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LDrawImport
{
    /// <summary>How the parser reads library files (see LDrawParser.Files).</summary>
    public interface ILibraryFiles
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        string[] ReadAllLines(string path);
    }

    /// <summary>Library folders on disk (the editor's LDraw/ folder).</summary>
    public sealed class DiskFiles : ILibraryFiles
    {
        public static readonly DiskFiles Instance = new DiskFiles();

        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public string[] ReadAllLines(string path) => File.ReadAllLines(path);
    }

    /// <summary>
    /// The libraries as downloaded, still zipped: each zip stands in for a folder (e.g. ".../Library/ldraw" for
    /// LDraw's complete.zip), so the parser's usual paths work without unpacking tens of thousands of files.
    /// Names are matched ignoring case, as LDraw files refer to each other on case-insensitive Windows.
    /// Extra in-memory files can be added too (the game's own connection files, which ship inside the app).
    /// Not thread-safe: use it from one thread at a time.
    /// </summary>
    public sealed class ZipLibraryFiles : ILibraryFiles, IDisposable
    {
        readonly List<ZipArchive> _archives = new List<ZipArchive>();
        readonly Dictionary<string, ZipArchiveEntry> _entries = new Dictionary<string, ZipArchiveEntry>();
        readonly Dictionary<string, string[]> _memory = new Dictionary<string, string[]>();
        readonly HashSet<string> _directories = new HashSet<string>();

        static string Key(string path) => Slashes(path).TrimEnd('/').ToLowerInvariant();

        static string Slashes(string path) => path.Replace((char)92, '/'); // backslashes (LDraw, Windows) to slashes

        /// <summary>Makes the zip's entries under `insideZip` (e.g. "ldraw/", or "" for the top) appear under `folder`.</summary>
        public void AddZip(string zipPath, string folder, string insideZip)
        {
            var archive = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read);
            _archives.Add(archive);
            string prefix = Slashes(insideZip).ToLowerInvariant();
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = Slashes(entry.FullName);
                if (!name.ToLowerInvariant().StartsWith(prefix) || name.EndsWith("/"))
                    continue;
                AddPath(Key(folder + "/" + name.Substring(prefix.Length)));
                _entries[Key(folder + "/" + name.Substring(prefix.Length))] = entry;
            }
        }

        /// <summary>Adds a file held in memory.</summary>
        public void AddFile(string path, string text)
        {
            AddPath(Key(path));
            _memory[Key(path)] = text.Replace("\r\n", "\n").Split('\n');
        }

        // Records every folder above a file, so DirectoryExists works.
        void AddPath(string key)
        {
            for (int slash = key.LastIndexOf('/'); slash > 0; slash = key.LastIndexOf('/', slash - 1))
            {
                if (!_directories.Add(key.Substring(0, slash)))
                    break;
            }
        }

        public bool FileExists(string path) => _entries.ContainsKey(Key(path)) || _memory.ContainsKey(Key(path));

        public bool DirectoryExists(string path) => _directories.Contains(Key(path));

        public string[] ReadAllLines(string path)
        {
            string key = Key(path);
            if (_memory.TryGetValue(key, out string[] lines))
                return lines;
            if (!_entries.TryGetValue(key, out ZipArchiveEntry entry))
                throw new FileNotFoundException(path);
            var result = new List<string>();
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    result.Add(line);
            }
            return result.ToArray();
        }

        public void Dispose()
        {
            foreach (ZipArchive archive in _archives)
                archive.Dispose();
            _archives.Clear();
        }
    }
}
