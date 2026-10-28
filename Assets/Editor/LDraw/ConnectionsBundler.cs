using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

/// <summary>
/// Packs the project's own connection files (Connections/, next to Assets) into one text file in Resources,
/// so the headset can use them when it makes parts for sets downloaded in the game (see SetDownloader).
/// Runs before every build, and from Tools > Pack Connection Files.
/// </summary>
public class ConnectionsBundler : IPreprocessBuildWithReport
{
    public const string BundlePath = "Assets/Resources/" + SetDownloader.ConnectionsResource + ".txt";

    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report) => Pack();

    [MenuItem("Tools/Pack Connection Files")]
    public static void Pack()
    {
        string root = Path.Combine(LDrawImportWindow.ProjectRoot, "Connections");
        var text = new StringBuilder();
        if (Directory.Exists(root))
        {
            foreach (string path in Directory.GetFiles(root, "*.dat", SearchOption.AllDirectories).OrderBy(p => p))
            {
                string relative = path.Substring(root.Length + 1).Replace('\\', '/');
                text.Append(SetDownloader.ConnectionsFileMarker).Append(relative).Append('\n');
                text.Append(File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n')).Append('\n');
            }
        }

        string full = Path.Combine(LDrawImportWindow.ProjectRoot, BundlePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        if (!File.Exists(full) || File.ReadAllText(full) != text.ToString())
        {
            File.WriteAllText(full, text.ToString());
            AssetDatabase.ImportAsset(BundlePath);
        }
    }
}
