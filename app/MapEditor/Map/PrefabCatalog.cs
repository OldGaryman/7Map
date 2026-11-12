using System.Globalization;
using System.Text;
using System.Xml;

namespace MapEditor.Map;

public enum PrefabKind { Poi, Tile, Part, Filler }

/// Description of a building type from the game folder (size, difficulty, display name).
public sealed class PrefabDef
{
    public int SizeX = 20, SizeY = 10, SizeZ = 20, Tier;
    public PrefabKind Kind = PrefabKind.Poi;
    public string Label = "";
    public bool Found;
    /// tier as stars (the user's request, 2026-10-31: list and map), empty without a tier
    public static string Stars(int tier) => tier > 0 ? new string('★', tier) : "";
    /// entrances/exits (POIMarkerType "RoadExit"): center of the marker, unrotated in building coordinates (m)
    public List<(double X, double Z)> Exits = new();
    /// size (x, z) of the RoadExit markers, same order as Exits
    public List<(int SX, int SZ)> ExitSizes = new();
    /// YOffset: by this many blocks the building reaches below the ground height (mostly negative)
    public int YOffset;
    /// parts that the game places at the building when generating the world (POIMarkerType "PartSpawn", mainly driveways)
    public List<PartMarker> Parts = new();
    /// tags (e.g. "countrytown, nodiagonal"): determine which lots of the city tiles the building fits
    public HashSet<string> Tags = new(StringComparer.OrdinalIgnoreCase);
    /// RotationToFaceNorth: rotation at which the front faces north (null = not in the XML; RWG then calculates with 0)
    public int? RotationToFaceNorth;
    /// DuplicateRepeatDistance: a second copy has to be this far away (m; null = not in the XML)
    public int? DuplicateRepeatDistance;
    /// lots of a city tile (POIMarkerType "POISpawn")
    public List<LotMarker> Lots = new();
}

/// lot of a tile: rectangle in tile coordinates (unrotated), direction of the lot, additional tag (empty = none)
public sealed record LotMarker(int X, int Y, int Z, int SizeX, int SizeZ, int Rotation, string Tag);

/// marker "PartSpawn": rectangle in building coordinates (unrotated), part(s) to choose from, rotation of the part relative to the building
public sealed record PartMarker(int X, int Y, int Z, int SizeX, int SizeY, int SizeZ, string[] PartNames, int Rotation, double Chance, string Group);

/// searches the descriptions in the game (Data\Prefabs) and in all mods (Mods\*\Prefabs, override those of the same name).
public static class PrefabCatalog
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Dictionary<string, PrefabDef> Load(string gameFolder, IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots(gameFolder))
            foreach (var f in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
                files[Path.GetFileNameWithoutExtension(f)] = f;
        var labels = ReadLabels(gameFolder, wanted);

        var defs = new Dictionary<string, PrefabDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in wanted)
        {
            var d = new PrefabDef();
            string lower = name.ToLowerInvariant();
            if (files.TryGetValue(name, out var file))
            {
                d.Found = true;
                try { ReadDef(file, d); } catch (Exception) { d.Found = false; }
                string folder = file.Replace('/', '\\').ToLowerInvariant();
                if (folder.Contains("\\parts\\")) d.Kind = PrefabKind.Part;
                else if (folder.Contains("\\rwgtiles\\")) d.Kind = PrefabKind.Tile;
            }
            if (d.Kind == PrefabKind.Poi)
            {
                if (lower.StartsWith("part_")) d.Kind = PrefabKind.Part;
                else if (lower.StartsWith("rwg_tile")) { d.Kind = PrefabKind.Tile; if (!d.Found) { d.SizeX = 150; d.SizeZ = 150; } }
                else if (lower.Contains("_filler")) d.Kind = PrefabKind.Filler;
            }
            if (labels.TryGetValue(name, out var label)) d.Label = label;
            defs[name] = d;
        }
        return defs;
    }

    /// all building descriptions from game and mods (mods override those of the same name), without the test folder: name → XML file
    public static Dictionary<string, string> AllFiles(string gameFolder)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots(gameFolder))
            foreach (var f in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
                if (!f.Replace('/', '\\').Contains("\\Test\\", StringComparison.OrdinalIgnoreCase))
                    files[Path.GetFileNameWithoutExtension(f)] = f;
        return files;
    }

    /// description from a single XML file (size, tier, entrances …)
    public static PrefabDef Describe(string file)
    {
        var d = new PrefabDef();
        try { ReadDef(file, d); d.Found = true; } catch (Exception) { }
        return d;
    }

    /// display names (German, otherwise English) for the given buildings
    public static Dictionary<string, string> Labels(string gameFolder, IEnumerable<string> names) =>
        ReadLabels(gameFolder, new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));

    static IEnumerable<string> Roots(string gameFolder)
    {
        if (gameFolder == null || !Directory.Exists(gameFolder)) yield break;
        string data = Path.Combine(gameFolder, "Data", "Prefabs");
        if (Directory.Exists(data)) yield return data;
        string mods = Path.Combine(gameFolder, "Mods");
        if (!Directory.Exists(mods)) yield break;
        foreach (var m in Directory.GetDirectories(mods))
        {
            string p = Path.Combine(m, "Prefabs");
            if (Directory.Exists(p)) yield return p;
        }
    }

    static void ReadDef(string file, PrefabDef d)
    {
        var doc = XmlSafe.Load(file);
        string[] types = null, starts = null, sizes = null, parts = null, rotations = null, chances = null, groups = null, tags = null;
        foreach (XmlElement p in doc.SelectNodes("/prefab/property[@name]"))
        {
            string v = p.GetAttribute("value");
            switch (p.GetAttribute("name"))
            {
                case "PrefabSize":
                    var s = v.Split(',');
                    d.SizeX = int.Parse(s[0].Trim(), Inv); d.SizeY = int.Parse(s[1].Trim(), Inv); d.SizeZ = int.Parse(s[2].Trim(), Inv);
                    break;
                case "DifficultyTier": d.Tier = int.Parse(v.Trim(), Inv); break;
                case "YOffset": d.YOffset = int.Parse(v.Trim(), Inv); break;
                case "Tags":
                    foreach (var t in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) d.Tags.Add(t);
                    break;
                case "RotationToFaceNorth": if (int.TryParse(v.Trim(), NumberStyles.Integer, Inv, out int rtfn)) d.RotationToFaceNorth = rtfn; break;
                case "DuplicateRepeatDistance": if (int.TryParse(v.Trim(), NumberStyles.Integer, Inv, out int rep)) d.DuplicateRepeatDistance = rep; break;
                case "POIMarkerTags": tags = v.Split('#'); break;
                case "POIMarkerType": types = v.Split(','); break;
                case "POIMarkerStart": starts = v.Split('#'); break;
                case "POIMarkerSize": sizes = v.Split('#'); break;
                case "POIMarkerPartToSpawn": parts = v.Split(','); break;
                case "POIMarkerPartRotations": rotations = v.Split(','); break;
                case "POIMarkerPartSpawnChance": chances = v.Split(','); break;
                case "POIMarkerGroup": groups = v.Split(','); break;
            }
        }
        // markers: types separated by comma, start and size (x, y, z) separated by #, same order
        if (types == null || starts == null || sizes == null) return;
        for (int i = 0; i < types.Length && i < starts.Length && i < sizes.Length; i++)
        {
            var a = starts[i].Split(','); var b = sizes[i].Split(',');
            if (a.Length < 3 || b.Length < 3) continue;
            string Get(string[] arr) => arr != null && i < arr.Length ? arr[i].Trim() : "";
            if (types[i].Trim() == "POISpawn")
            {
                // lot of a tile: the direction is in POIMarkerPartRotations, as with the parts
                int.TryParse(Get(rotations), NumberStyles.Integer, Inv, out int lotRot);
                d.Lots.Add(new LotMarker(int.Parse(a[0].Trim(), Inv), int.Parse(a[1].Trim(), Inv), int.Parse(a[2].Trim(), Inv),
                    int.Parse(b[0].Trim(), Inv), int.Parse(b[2].Trim(), Inv), lotRot, Get(tags)));
                continue;
            }
            if (types[i].Trim() == "PartSpawn")
            {
                var names = Get(parts).Split(new[] { ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (names.Length == 0) continue;
                int.TryParse(Get(rotations), NumberStyles.Integer, Inv, out int rot);
                if (!double.TryParse(Get(chances), NumberStyles.Float, Inv, out double chance)) chance = 1;
                d.Parts.Add(new PartMarker(int.Parse(a[0].Trim(), Inv), int.Parse(a[1].Trim(), Inv), int.Parse(a[2].Trim(), Inv),
                    int.Parse(b[0].Trim(), Inv), int.Parse(b[1].Trim(), Inv), int.Parse(b[2].Trim(), Inv), names, rot, chance, Get(groups)));
                continue;
            }
            if (types[i].Trim() != "RoadExit") continue;
            d.Exits.Add((int.Parse(a[0].Trim(), Inv) + int.Parse(b[0].Trim(), Inv) / 2.0,
                         int.Parse(a[2].Trim(), Inv) + int.Parse(b[2].Trim(), Inv) / 2.0));
            d.ExitSizes.Add((int.Parse(b[0].Trim(), Inv), int.Parse(b[2].Trim(), Inv)));
        }
    }

    // display names from Localization.txt (German, otherwise English); game first, mods override
    static Dictionary<string, string> ReadLabels(string gameFolder, HashSet<string> wanted)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (gameFolder == null || !Directory.Exists(gameFolder)) return labels;
        var files = new List<string>();
        string main = Path.Combine(gameFolder, "Data", "Config", "Localization.txt");
        if (File.Exists(main)) files.Add(main);
        string mods = Path.Combine(gameFolder, "Mods");
        if (Directory.Exists(mods)) files.AddRange(Directory.GetFiles(mods, "Localization*.txt", SearchOption.AllDirectories));

        foreach (var file in files)
        {
            string[] lines = File.ReadAllLines(file, Encoding.UTF8);
            if (lines.Length == 0) continue;
            var head = ParseCsvLine(lines[0]);
            int en = head.FindIndex(x => x.Trim().Equals("english", StringComparison.OrdinalIgnoreCase));
            int de = head.FindIndex(x => x.Trim().Equals("german", StringComparison.OrdinalIgnoreCase));
            if (en < 0) continue;
            for (int i = 1; i < lines.Length; i++)
            {
                int comma = lines[i].IndexOf(',');
                if (comma <= 0 || !wanted.Contains(lines[i][..comma])) continue;
                var cols = ParseCsvLine(lines[i]);
                // names from the game in the language of the interface (German only if it is German)
                string text = Loc.Language == "German" && de >= 0 && de < cols.Count && cols[de].Trim() != "" ? cols[de] : en < cols.Count ? cols[en] : "";
                if (text.Trim() != "") labels[cols[0]] = text.Trim();
            }
        }
        return labels;
    }

    static List<string> ParseCsvLine(string line)
    {
        var cols = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cols.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cols.Add(cur.ToString());
        return cols;
    }
}
