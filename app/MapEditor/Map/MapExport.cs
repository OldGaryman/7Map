using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MapEditor.Map;

/// Export of a map for other players (the user's request 2026-11-26): one .zip with the map folder, a 1024 × 1024 overview
/// PNG (<map>_map_1024.png), the bridge mod YAMEE_Bridges (only if the map uses its pieces) and the install guides in German
/// and English. The guides are templates (Guide\Map_Install_and_Features_DE/EN.md in the project, copied next to YAMEE.exe
/// into "Export"), the user may edit them; placeholders {MAP_NAME}, {GAME_VERSION}, {WORLD_FOLDER}, blocks
/// {IF_BRIDGES}…{END_BRIDGES} and {IF_NO_BRIDGES}…{END_NO_BRIDGES}, HTML comments are left out.
public static class MapExport
{
    public const string ModName = "YAMEE_Bridges";
    public const int ImageSize = 1024;
    public static readonly string[] Languages = { "DE", "EN" };
    public static string TemplateName(string lang) => $"Map_Install_and_Features_{lang}.md";
    public static string ImageName(string mapName) => $"{mapName}_map_{ImageSize}.png";

    /// folder with the templates and the mod: "Export" next to YAMEE.exe
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "Export");

    public sealed record Result(string Zip, bool Bridges, int Files, List<string> Missing);

    /// names of the prefabs in the mod (file names of the .xml in Prefabs, without extension)
    public static HashSet<string> ModPrefabs(string modFolder)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string prefabs = Path.Combine(modFolder, "Prefabs");
        if (Directory.Exists(prefabs))
            foreach (var f in Directory.EnumerateFiles(prefabs, "*.xml", SearchOption.AllDirectories)) set.Add(Path.GetFileNameWithoutExtension(f));
        return set;
    }

    /// does prefabs.xml of the project use a piece of the mod?
    public static bool UsesMod(string project, HashSet<string> modPrefabs)
    {
        string file = Path.Combine(project, "prefabs.xml");
        if (!File.Exists(file) || modPrefabs.Count == 0) return false;
        var doc = XmlSafe.Load(file);
        foreach (System.Xml.XmlElement d in doc.GetElementsByTagName("decoration"))
            if (modPrefabs.Contains(d.GetAttribute("name"))) return true;
        return false;
    }

    /// text of the game version for the guide, e.g. "3.4" / "2.6 mit Undead Legacy" / "2.6 with Undead Legacy"
    public static string VersionText(GameProfile profile, string lang)
    {
        string v = profile?.Version?.Short ?? (lang == "DE" ? "(unbekannte Fassung)" : "(unknown version)");
        if (profile?.Mod == null) return v;
        return v + (lang == "DE" ? " mit " : " with ") + GameProfile.ModName(profile.Mod);
    }

    /// world folder of the game for this profile (vanilla or the mod's own world folder)
    public static string WorldFolderText(GameProfile profile) =>
        profile?.Mod == null ? @"%APPDATA%\7DaysToDie\GeneratedWorlds\" : $@"%APPDATA%\7DaysToDie\{profile.Mod}\GeneratedWorlds\";

    /// fill a template: comments out, blocks kept or removed, placeholders replaced
    public static string Fill(string template, string mapName, string version, string worldFolder, bool bridges)
    {
        string t = template.Replace("\r\n", "\n");
        t = Regex.Replace(t, @"<!--.*?-->\n?", "", RegexOptions.Singleline);
        t = Block(t, "IF_BRIDGES", "END_BRIDGES", bridges);
        t = Block(t, "IF_NO_BRIDGES", "END_NO_BRIDGES", !bridges);
        t = t.Replace("{MAP_NAME}", mapName).Replace("{GAME_VERSION}", version).Replace("{WORLD_FOLDER}", worldFolder);
        t = Regex.Replace(t, @"\n{3,}", "\n\n").TrimStart('\n');
        return t;
    }

    static string Block(string t, string open, string close, bool keep) =>
        Regex.Replace(t, @"^[ \t]*\{" + open + @"\}[ \t]*\n(.*?)^[ \t]*\{" + close + @"\}[ \t]*(\n|$)",
            m => keep ? m.Groups[1].Value : "", RegexOptions.Singleline | RegexOptions.Multiline);

    /// write the .zip. project = saved project folder, profile = its game version, folder = templates + mod (DefaultFolder),
    /// mapImage = optional 1024 × 1024 overview bitmap (written as <mapName>_map_1024.png at the root of the .zip)
    public static Result Write(string project, string mapName, GameProfile profile, string zip, string folder = null, IProgress<string> progress = null, System.Windows.Media.Imaging.BitmapSource mapImage = null)
    {
        folder ??= DefaultFolder;
        var missing = new List<string>();
        string mod = Path.Combine(folder, ModName);
        bool bridges = UsesMod(project, ModPrefabs(mod));
        if (bridges && !File.Exists(Path.Combine(mod, "ModInfo.xml"))) throw new FileNotFoundException(Loc.F("The bridge mod is missing:\n{0}", mod));

        string tmp = zip + ".tmp";
        int files = 0;
        // what goes in (source file, name in the .zip), for a real progress fraction by bytes
        var entries = new List<(string Src, string Name)>();
        foreach (var f in GameSync.Sources)
        {
            // spawn points: the play points, never the test point
            string src = Path.Combine(project, f);
            if (f == WorldData.SpawnFile && File.Exists(Path.Combine(project, WorldData.PlaySpawnFile))) src = Path.Combine(project, WorldData.PlaySpawnFile);
            if (!File.Exists(src)) { missing.Add(f); continue; }
            entries.Add((src, mapName + "/" + f));
        }
        if (bridges)
            foreach (var f in Directory.EnumerateFiles(mod, "*", SearchOption.AllDirectories))
                entries.Add((f, ModName + "/" + Path.GetRelativePath(mod, f).Replace('\\', '/')));
        long total = Math.Max(1, entries.Sum(e => new FileInfo(e.Src).Length)), done = 0;
        try
        {
            using (var fs = File.Create(tmp))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var (src, name) in entries)
                {
                    progress.Report(Loc.F("Packing: {0} …", Path.GetFileName(src)), 0.95 * done / total);
                    z.CreateEntryFromFile(src, name, CompressionLevel.Optimal);
                    done += new FileInfo(src).Length;
                    files++;
                }
                if (mapImage != null)
                {
                    string imgName = ImageName(mapName);
                    progress.Report(Loc.F("Packing: {0} …", imgName), 0.96);
                    var e = z.CreateEntry(imgName, CompressionLevel.NoCompression);
                    using var s = e.Open();
                    PngWriter.WriteRgb(s, mapImage);
                    files++;
                }
                progress.Report(Loc.T("Writing the install guides …"), 0.99);
                foreach (var lang in Languages)
                {
                    string tpl = Path.Combine(folder, TemplateName(lang));
                    if (!File.Exists(tpl)) { missing.Add(TemplateName(lang)); continue; }
                    string text = Fill(File.ReadAllText(tpl), mapName, VersionText(profile, lang), WorldFolderText(profile), bridges);
                    var e = z.CreateEntry(TemplateName(lang), CompressionLevel.Optimal);
                    using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                    w.Write(text);
                    files++;
                }
            }
            File.Move(tmp, zip, true);
        }
        catch { try { File.Delete(tmp); } catch (IOException) { } throw; }
        return new Result(zip, bridges, files, missing);
    }
}
