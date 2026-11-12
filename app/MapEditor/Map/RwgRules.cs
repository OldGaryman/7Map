using System.IO;
using System.Xml;

namespace MapEditor.Map;

/// How the game builds cities (from Data\Config\rwgmixer.xml): biome → township type → district → tile name
/// rwg_tile_&lt;district&gt;_&lt;shape&gt;. Only for display in the list (the user's request, 2026-11-05).
public static class RwgRules
{
    sealed class District
    {
        public string Name, Tag, Prefix;
        public HashSet<string> Biomes = new(StringComparer.OrdinalIgnoreCase);   // empty = everywhere
    }

    static List<District> districts = new();
    static readonly string[] allBiomes = { "forest", "burntforest", "desert", "snow", "wasteland" };

    /// two letters per district (international abbreviations, explanation in the tooltip)
    static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["countrytown"] = "CT", ["countryresidential"] = "CR", ["commercial"] = "CO", ["industrial"] = "IN", ["residential"] = "RE",
        ["rural"] = "RU", ["downtown"] = "DT", ["oldwest"] = "OW", ["gateway"] = "GW", ["wilderness"] = "WI",
    };

    static readonly Dictionary<string, (string Code, string Title)> BiomeInfo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forest"] = ("F", "Pine forest"), ["burntforest"] = ("B", "Burnt forest"), ["desert"] = ("D", "Desert"),
        ["snow"] = ("S", "Snow"), ["wasteland"] = ("W", "Wasteland"),
    };

    public static void Load(string gameFolder)
    {
        var list = new List<District>();
        try
        {
            string file = gameFolder == null ? null : Path.Combine(gameFolder, "Data", "Config", "rwgmixer.xml");
            if (file != null && File.Exists(file))
            {
                var doc = XmlSafe.Load(file);
                var townships = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (XmlElement t in doc.SelectNodes("//township"))
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string b = Prop(t, "biomes");
                    if (b != null) foreach (var x in b.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) set.Add(x);
                    townships[t.GetAttribute("name")] = set;
                }
                foreach (XmlElement d in doc.SelectNodes("//district"))
                {
                    var di = new District { Name = d.GetAttribute("name") };
                    di.Tag = Prop(d, "tag") ?? di.Name;
                    di.Prefix = Prop(d, "prefab_name") ?? di.Name;
                    string req = Prop(d, "required_township");
                    if (req != null)
                        foreach (var tn in req.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!townships.TryGetValue(tn, out var bs)) continue;
                            if (bs.Count == 0) { di.Biomes.Clear(); di.Biomes.UnionWith(allBiomes); break; }   // township type without a biome entry: everywhere
                            di.Biomes.UnionWith(bs);
                        }
                    list.Add(di);
                }
            }
        }
        catch { list = new(); }
        districts = list;
    }

    static string Prop(XmlElement e, string name)
    {
        foreach (XmlElement p in e.ChildNodes.OfType<XmlElement>())
            if (p.Name == "property" && p.GetAttribute("name") == name) return p.GetAttribute("value");
        return null;
    }

    /// district of a city tile from its name (rwg_tile_countrytown_corner → countrytown); longest matching beginning
    public static string DistrictOfTile(string tileName)
    {
        if (!tileName.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase)) return null;
        string rest = tileName.Substring(9);
        string best = null;
        foreach (var d in districts)
            if (rest.StartsWith(d.Prefix + "_", StringComparison.OrdinalIgnoreCase) && (best == null || d.Prefix.Length > best.Length)) best = d.Prefix;
        if (best == null)
        {
            int i = rest.IndexOf('_');
            best = i > 0 ? rest.Substring(0, i) : null;
        }
        return best;
    }


    /// abbreviation, explanation and biomes for one or more districts (names or tags); null if the game knows none of them.
    /// biomes = union of the biomes of all named districts.
    public static (string Code, string Tip, string Biomes, string BiomesTip)? Summarize(IEnumerable<string> keys)
    {
        var codes = new List<string>(); var tips = new List<string>(); var biomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys.Where(k => k != null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var d = districts.FirstOrDefault(x => x.Prefix.Equals(key, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            string tag = d?.Tag ?? key;
            if (!Codes.TryGetValue(tag, out var code) && !Codes.TryGetValue(key, out code)) continue;
            var own = d == null || d.Biomes.Count == 0 ? allBiomes.ToList() : allBiomes.Where(b => d.Biomes.Contains(b)).ToList();
            biomes.UnionWith(own);
            if (!codes.Contains(code)) codes.Add(code);
            tips.Add(Loc.F("District \"{0}\": built in {1}", tag, BiomeNames(own)));
        }
        if (codes.Count == 0) return null;
        var all = allBiomes.Where(biomes.Contains).ToList();
        string letters = all.Count == allBiomes.Length ? "*" : string.Join("", all.Select(b => BiomeInfo[b].Code));
        return (string.Join(" ", codes), string.Join("\n", tips), letters, Loc.T("Biomes in which the game builds this:") + " " + BiomeNames(all));
    }

    static string BiomeNames(List<string> list) => list.Count == allBiomes.Length ? Loc.T("all biomes") : string.Join(", ", list.Select(b => Loc.T(BiomeInfo[b].Title)));
}