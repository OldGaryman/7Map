using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;

namespace MapEditor;

/// Found installation of 7 Days to Die: folder, Steam build (null = unknown), dedicated server or game
public sealed record GameInstall(string Folder, string BuildId, bool Server);

/// Finds the game, version and world folder without input from the user (the user's request, 2026-10-30: the app should run on every
/// PC). Steam: registry → steamapps\libraryfolders.vdf (all libraries) → appmanifest_251570.acf (game) or
/// appmanifest_294420.acf (dedicated server) with installdir and buildid. Version as text from the newest game log.
public static class GameLocator
{
    const string ClientAppId = "251570", ServerAppId = "294420";
    static GameInstall found;
    static bool searched;

    static string UserData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie");

    /// game (preferred) or dedicated server; null = not found. Searched once, remembered afterwards.
    public static GameInstall Find()
    {
        if (!searched) { found = Search(); searched = true; }
        return found;
    }

    /// folder contains the building descriptions of the game
    public static bool IsGameFolder(string folder) => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(Path.Combine(folder, "Data", "Prefabs"));

    static GameInstall Search()
    {
        var libraries = SteamLibraries();
        foreach (var (id, server) in new[] { (ClientAppId, false), (ServerAppId, true) })
            foreach (var lib in libraries)
            {
                string acf = Path.Combine(lib, "steamapps", $"appmanifest_{id}.acf");
                if (!File.Exists(acf)) continue;
                string text;
                try { text = File.ReadAllText(acf); } catch (Exception) { continue; }
                string dir = Value(text, "installdir");
                if (dir == null) continue;
                string folder = Path.Combine(lib, "steamapps", "common", dir);
                if (IsGameFolder(folder)) return new GameInstall(folder, Value(text, "buildid"), server);
            }
        // without Steam data: the usual places
        foreach (string folder in new[] { @"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die", @"C:\Program Files\Steam\steamapps\common\7 Days To Die" })
            if (IsGameFolder(folder)) return new GameInstall(folder, null, false);
        return null;
    }

    // Steam folder from the registry and all libraries from libraryfolders.vdf
    static List<string> SteamLibraries()
    {
        var list = new List<string>();
        string steam = null;
        try
        {
            steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        }
        catch (Exception) { }
        if (string.IsNullOrWhiteSpace(steam)) return list;
        // check every path individually: an invalid entry (registry or libraryfolders.vdf) must not disturb the other libraries
        // and the start of the app (audit finding F10)
        static void Add(List<string> to, string path)
        {
            try { to.Add(Path.GetFullPath(path.Replace('/', '\\'))); } catch (Exception) { }
        }
        Add(list, steam);
        if (list.Count == 0) return list;
        string vdf = Path.Combine(list[0], "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    Add(list, m.Groups[1].Value.Replace(@"\\", @"\"));   // the file writes every \ twice
        }
        catch (Exception) { }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string Value(string acf, string key)
    {
        var m = Regex.Match(acf, "\"" + key + "\"\\s+\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// game version according to the newest log ("V 2.6 (b14)"); null = no log (game never started)
    public static string VersionFromLog()
    {
        string logs = Path.Combine(UserData, "logs");
        if (!Directory.Exists(logs)) return null;
        foreach (string file in Directory.GetFiles(logs, "output_log*.txt").OrderByDescending(File.GetLastWriteTime).Take(3))
        {
            try
            {
                // the game keeps the log open as long as it runs
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                for (int i = 0; i < 300 && reader.ReadLine() is string line; i++)
                {
                    var m = Regex.Match(line, @"INF Version: (V \S+ \(b\d+\))");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch (Exception) { }
        }
        return null;
    }

    /// world folder of the game: first those of mods (subfolders with GeneratedWorlds, e.g. UndeadLegacy), then the normal one,
    /// for the dedicated server additionally UserDataFolder from serverconfig.xml
    public static List<string> WorldFolders()
    {
        var list = new List<string>();
        if (Directory.Exists(UserData))
        {
            foreach (string dir in Directory.GetDirectories(UserData))
            {
                string g = Path.Combine(dir, "GeneratedWorlds");
                if (Directory.Exists(g)) list.Add(g);
            }
            string main = Path.Combine(UserData, "GeneratedWorlds");
            if (Directory.Exists(main)) list.Add(main);
        }
        if (Find() is { Server: true } server && ServerUserData(server.Folder) is string data)
        {
            string g = Path.Combine(data, "GeneratedWorlds");
            if (Directory.Exists(g)) list.Add(g);
        }
        return list;
    }

    static string ServerUserData(string folder)
    {
        string config = Path.Combine(folder, "serverconfig.xml");
        if (!File.Exists(config)) return null;
        try
        {
            var doc = XmlSafe.Load(config);   // commented-out lines (default) are not read as an element by XmlDocument
            return (doc.SelectSingleNode("//property[@name='UserDataFolder']") as XmlElement)?.GetAttribute("value") is { Length: > 0 } v ? v : null;
        }
        catch (Exception) { return null; }
    }
}
