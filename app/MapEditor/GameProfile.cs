using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace MapEditor;

/// Version of 7 Days to Die as the game numbers it internally (the user's request 2026-11-13: support 2.6, 2.6 with Undead Legacy
/// and 3.x side by side). Major/Minor/Build are the constants cVersionMajor/cVersionMinor/cVersionBuild in Assembly-CSharp.dll.
/// In 3.x the minor number holds two digits: 40 = "3.4.0", 0 = "3.0.0"; in 2.x one: 6 = "2.6".
/// Texts of the game: log/main.ttw "V 2.6 (b14)" or "V 3.4.0 (b10)", map_info.xml "V.2.6.14" or "V.3.40.10".
public sealed record GameVersion(int Major, int Minor, int Build)
{
    /// "2.6", "3.4" – the version without build, used to match projects and game folders
    public string Short => Major >= 3 ? $"{Major}.{Minor / 10}" : $"{Major}.{Minor}";

    /// text like in the game log and in main.ttw: "V 2.6 (b14)", "V 3.4.0 (b10)"
    public string Display => Major >= 3 ? $"V {Major}.{Minor / 10}.{Minor % 10} (b{Build})" : $"V {Major}.{Minor} (b{Build})";

    /// text like in map_info.xml (GameVersion): "V.2.6.14", "V.3.40.10"
    public string MapInfo => $"V.{Major}.{Minor}.{Build}";

    public override string ToString() => Display;

    /// reads "V 2.6 (b14)", "V 3.4.0 (b10)", "V.3.40.10", "V.2.6.14"; null = not recognized
    public static GameVersion Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        var m = Regex.Match(text, @"^V\.(\d+)\.(\d+)\.(\d+)$");   // map_info.xml
        if (m.Success) return new GameVersion(Int(m.Groups[1].Value), Int(m.Groups[2].Value), Int(m.Groups[3].Value));
        m = Regex.Match(text, @"^V\s*(\d+)\.(\d+)(?:\.(\d+))?\s*\(b(\d+)\)");   // log and main.ttw
        if (!m.Success) return null;
        int major = Int(m.Groups[1].Value), minor = Int(m.Groups[2].Value);
        if (major >= 3) minor = minor * 10 + (m.Groups[3].Success ? Int(m.Groups[3].Value) : 0);
        return new GameVersion(major, minor, Int(m.Groups[4].Value));
    }

    static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    /// version of an installed game: constants in 7DaysToDie_Data\Managed\Assembly-CSharp.dll (checked: copy of 2.6 → 2/6/14,
    /// 3.4 → 3/40/10); null = not readable
    public static GameVersion FromGameFolder(string folder)
    {
        try
        {
            string dll = Path.Combine(folder, "7DaysToDie_Data", "Managed", "Assembly-CSharp.dll");
            if (!File.Exists(dll)) return null;
            using var fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                if (md.GetString(t.Name) != "Constants" || md.GetString(t.Namespace) != "") continue;
                int? major = null, minor = null, build = null;
                foreach (var fh in t.GetFields())
                {
                    var f = md.GetFieldDefinition(fh);
                    var c = f.GetDefaultValue();
                    if (c.IsNil) continue;
                    var blob = md.GetBlobReader(md.GetConstant(c).Value);
                    if (blob.Length != 4) continue;
                    int v = blob.ReadInt32();
                    switch (md.GetString(f.Name))
                    {
                        case "cVersionMajor": major = v; break;
                        case "cVersionMinor": minor = v; break;
                        case "cVersionBuild": build = v; break;
                    }
                }
                if (major != null && minor != null && build != null) return new GameVersion(major.Value, minor.Value, build.Value);
            }
        }
        catch (Exception) { }
        return null;
    }

    /// version written into a world: map_info.xml (GameVersion), otherwise the text in main.ttw; null = unknown
    public static GameVersion FromWorld(string folder)
    {
        try
        {
            string info = Path.Combine(folder, "map_info.xml");
            if (File.Exists(info))
            {
                var v = XmlSafe.Load(info).SelectSingleNode("//property[@name='GameVersion']/@value")?.Value;
                if (Parse(v) is GameVersion gv) return gv;
            }
            string ttw = Path.Combine(folder, "main.ttw");
            if (File.Exists(ttw)) return Parse(TtwVersionText(File.ReadAllBytes(ttw)));
        }
        catch (Exception) { }
        return null;
    }

    /// version text in main.ttw: "ttw" 0, int32 file version, then a string (length byte + characters)
    public static string TtwVersionText(byte[] ttw)
    {
        if (ttw == null || ttw.Length < 10 || ttw[0] != 't' || ttw[1] != 't' || ttw[2] != 'w') return null;
        int len = ttw[8];
        return len > 0 && len < 128 && 9 + len <= ttw.Length ? System.Text.Encoding.ASCII.GetString(ttw, 9, len) : null;
    }
}

/// Game version plus the mod that brings its own world and save folder (Undead Legacy: %APPDATA%\7DaysToDie\UndeadLegacy).
/// Mod = null: the game without such a mod. Other mods (e.g. own prefabs) do not change the profile.
public sealed record GameProfile(GameVersion Version, string Mod)
{
    /// "2.6 + Undead Legacy", "3.4"; "?" for an unknown version
    public string Text => (Version?.Short ?? "?") + (Mod == null ? "" : " + " + ModName(Mod));

    /// readable name of a mod folder: "UndeadLegacy" → "Undead Legacy"
    public static string ModName(string mod) => Regex.Replace(mod ?? "", "(?<=[a-z])(?=[A-Z])", " ");

    /// same version (without build) and same mod
    public bool Matches(GameProfile other) => other != null && Version?.Short == other.Version?.Short
        && string.Equals(Mod, other.Mod, StringComparison.OrdinalIgnoreCase);
}

/// Which game a project belongs to, stored in <project>\yamee_project.json (the user's request 2026-11-13).
/// Missing in older projects: recognized once from the world (map_info.xml / main.ttw) and the folder in the game it came from.
public sealed class ProjectInfo
{
    public const string FileName = "yamee_project.json";
    /// game version as text like in the log ("V 2.6 (b14)")
    public string GameVersion { get; set; }
    /// mod with its own world folder (e.g. "UndeadLegacy"), null = without
    public string Mod { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public GameProfile Profile => new(MapEditor.GameVersion.Parse(GameVersion), string.IsNullOrWhiteSpace(Mod) ? null : Mod);

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// reads the file; null = not there or not readable
    public static ProjectInfo Read(string project)
    {
        try
        {
            string file = Path.Combine(project, FileName);
            return File.Exists(file) ? JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(file), Options) : null;
        }
        catch (Exception) { return null; }
    }

    public void Write(string project)
    {
        try { File.WriteAllText(Path.Combine(project, FileName), JsonSerializer.Serialize(this, Options)); }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    /// profile of a project: from the file, otherwise recognized and written. gameWorld = folder of the world in the game (may be null)
    public static GameProfile For(string project, string gameWorld)
    {
        var info = Read(project);
        if (info?.Profile.Version != null) return info.Profile;
        var detected = Detect(project, gameWorld);
        if (detected.Version != null) new ProjectInfo { GameVersion = detected.Version.Display, Mod = detected.Mod }.Write(project);
        return detected;
    }

    /// version from the world files, mod from the folder in the game (…\7DaysToDie\<Mod>\GeneratedWorlds\<World>)
    public static GameProfile Detect(string project, string gameWorld) =>
        new(MapEditor.GameVersion.FromWorld(project), GameLocator.ModOfWorldFolder(gameWorld));

    public static void Set(string project, GameProfile profile) =>
        new ProjectInfo { GameVersion = profile.Version?.Display, Mod = profile.Mod }.Write(project);
}
