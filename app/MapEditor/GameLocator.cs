using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;

namespace MapEditor;

/// Gefundene Installation von 7 Days to Die: Ordner, Steam-Build (null = unbekannt), Dedicated Server oder Spiel
public sealed record GameInstall(string Folder, string BuildId, bool Server);

/// Findet Spiel, Version und Weltordner ohne Eingaben des Nutzers (Wunsch des Nutzers 30.10.2026: die App soll auf jedem PC
/// laufen). Steam: Registry → steamapps\libraryfolders.vdf (alle Bibliotheken) → appmanifest_251570.acf (Spiel) bzw.
/// appmanifest_294420.acf (Dedicated Server) mit installdir und buildid. Version als Text aus dem neuesten Spielprotokoll.
public static class GameLocator
{
    const string ClientAppId = "251570", ServerAppId = "294420";
    static GameInstall found;
    static bool searched;

    static string UserData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie");

    /// Spiel (bevorzugt) oder Dedicated Server; null = nicht gefunden. Einmal gesucht, danach gemerkt.
    public static GameInstall Find()
    {
        if (!searched) { found = Search(); searched = true; }
        return found;
    }

    /// Ordner enthält die Gebäudebeschreibungen des Spiels
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
        // ohne Steam-Angaben: die üblichen Orte
        foreach (string folder in new[] { @"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die", @"C:\Program Files\Steam\steamapps\common\7 Days To Die" })
            if (IsGameFolder(folder)) return new GameInstall(folder, null, false);
        return null;
    }

    // Steam-Ordner aus der Registry und alle Bibliotheken aus libraryfolders.vdf
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
        // jeden Pfad einzeln prüfen: ein ungültiger Eintrag (Registry oder libraryfolders.vdf) darf die anderen Bibliotheken
        // und den Start der App nicht stören (Prüfbericht F10)
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
                    Add(list, m.Groups[1].Value.Replace(@"\\", @"\"));   // die Datei schreibt jeden \ doppelt
        }
        catch (Exception) { }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string Value(string acf, string key)
    {
        var m = Regex.Match(acf, "\"" + key + "\"\\s+\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// Spielversion laut neuestem Protokoll („V 2.6 (b14)“); null = kein Protokoll (Spiel noch nie gestartet)
    public static string VersionFromLog()
    {
        string logs = Path.Combine(UserData, "logs");
        if (!Directory.Exists(logs)) return null;
        foreach (string file in Directory.GetFiles(logs, "output_log*.txt").OrderByDescending(File.GetLastWriteTime).Take(3))
        {
            try
            {
                // das Spiel hält das Protokoll offen, solange es läuft
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

    /// Weltordner des Spiels: zuerst die von Mods (Unterordner mit GeneratedWorlds, z. B. UndeadLegacy), dann der normale,
    /// beim Dedicated Server zusätzlich UserDataFolder aus serverconfig.xml
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
            var doc = XmlSafe.Load(config);   // auskommentierte Zeilen (Standard) liest XmlDocument nicht als Element
            return (doc.SelectSingleNode("//property[@name='UserDataFolder']") as XmlElement)?.GetAttribute("value") is { Length: > 0 } v ? v : null;
        }
        catch (Exception) { return null; }
    }
}
