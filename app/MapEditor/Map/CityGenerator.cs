using System.Windows;
using System.Xml;

namespace MapEditor.Map;

/// town type from rwgmixer.xml (township): biomes, number and tiles per city for the map size, districts
public sealed class TownType
{
    public string Name;                 // name of the township type in the game (countrytown, city …)
    public int[] Biomes = Array.Empty<int>();   // biome indices (0 pine, 1 burnt, 2 desert, 3 snow, 4 wasteland)
    public int Count, TilesMin, TilesMax;
    /// core districts (prefix in the tile name, weight)
    public List<(string Prefix, double Weight)> Districts = new();
    /// district at the town edge (rural) and share of the edge tiles
    public string Outskirt;
    public double OutskirtShare;
    /// gateway tile at the city entrance or trader on it (township spawn_gateway / spawn_trader; Old West has neither)
    public bool Gateway = true, Trader = true;
    /// town type for several biomes split per biome (the user's request 2026-11-09: number adjustable per biome) – then the biome is in the name
    public bool PerBiome;
    public string Label => CityGenerator.TypeLabel(Name) + (PerBiome && Biomes.Length == 1 ? " – " + Loc.T(BiomePalette.Names[Biomes[0]]) : "");
    public TownType Clone() => (TownType)MemberwiseClone();
}

/// settings for "Create cities" (the user's request 2026-11-07)
public sealed class CitySpec
{
    public List<TownType> Types = new();
    public int MaxParts = 3;              // at most this many districts per city (the user's request 2026-11-07, model Fidife-Land)
    public int DeltaMin = 6, DeltaMax = 25;   // height difference district – main part (m)
    public double RoadGrade = 0.18;      // highest gradient of the roads between the parts
    public int TradersPerBiome = -1;     // -1 = automatic by map size (DefaultTraders), otherwise traders per biome (Rekt pine forest, Jen burnt, Bob desert, Hugh snow, Joel wasteland)
    public int Elevation = 1;             // location of the cities: 0 plains and valleys (as before), 1 mixed (about every second city), 2 preferably hills
    /// default by map size (the user's request: 2 km 1, 8 km 2–3)
    public static int DefaultTraders(int size) => Math.Clamp((int)Math.Round(size / 3000.0), 1, 4);
    public bool FillLots = true;
    public string Seed = "YAMEE";
    public CitySpec Clone()
    {
        var c = (CitySpec)MemberwiseClone();
        c.Types = Types.Select(t => t.Clone()).ToList();
        return c;
    }
}

/// rules of the game for cities (Data\Config\rwgmixer.xml): township types per map size, districts, maximum numbers of single tiles
public sealed class CityRules
{
    public List<TownType> Types = new();
    public Dictionary<string, int> MaxTiles = new(StringComparer.OrdinalIgnoreCase);
    /// districts that should not lie next to each other (downtown – rural)
    public List<(string A, string B)> Avoided = new();
    /// traders per biome index from prefab_spawn_adjust (trader_rekt → forest …); null = none known
    public string[] Traders = new string[5];
    public string WorldClass;

    public static CityRules Load(string gameFolder, int worldSize)
    {
        var rules = new CityRules();
        string file = gameFolder == null ? null : Path.Combine(gameFolder, "Data", "Config", "rwgmixer.xml");
        if (file == null || !File.Exists(file)) return rules;
        var doc = XmlSafe.Load(file);
        static string Prop(XmlElement e, string name) =>
            e.ChildNodes.OfType<XmlElement>().FirstOrDefault(p => p.Name == "property" && p.GetAttribute("name") == name)?.GetAttribute("value");
        static double[] Nums(string v) => v == null ? Array.Empty<double>()
            : v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var biomeIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["forest"] = 0, ["burntforest"] = 1, ["desert"] = 2, ["snow"] = 3, ["wasteland"] = 4 };

        // map size → world (tiny, small, medium, large)
        XmlElement world = null;
        foreach (XmlElement wEl in doc.SelectNodes("/rwgmixer/world"))
        {
            var r = Nums(Prop(wEl, "world_size"));
            if (r.Length == 2 && worldSize >= r[0] && worldSize < r[1]) { world = wEl; break; }
        }
        rules.WorldClass = world?.GetAttribute("name");

        // districts: prefix in the tile name, weight, township types
        var districts = new List<(string Name, string Prefix, double Weight, string[] Towns)>();
        foreach (XmlElement d in doc.SelectNodes("/rwgmixer/district"))
        {
            string name = d.GetAttribute("name");
            string prefix = Prop(d, "prefab_name") ?? name;
            double weight = Nums(Prop(d, "spawn_weight")).FirstOrDefault(1);
            var towns = (Prop(d, "required_township") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            districts.Add((name, prefix, weight, towns));
            string avoid = Prop(d, "avoided_neighbor_districts");
            if (avoid != null)
                foreach (var a in avoid.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string pa = districts.FirstOrDefault(x => x.Name == a).Prefix ?? a;
                    rules.Avoided.Add((prefix, pa));
                }
        }
        // prefixes of the districts that appear later in the file
        rules.Avoided = rules.Avoided.Select(p => (p.A, districts.FirstOrDefault(x => x.Name == p.B).Prefix ?? p.B)).ToList();

        foreach (XmlElement t in doc.SelectNodes("/rwgmixer/township"))
        {
            string name = t.GetAttribute("name");
            var size = world?.ChildNodes.OfType<XmlElement>().FirstOrDefault(p => p.Name == "property" && p.GetAttribute("class") == name);
            var tiles = size == null ? Array.Empty<double>() : Nums(Prop(size, "tiles"));
            if (tiles.Length < 2) continue;   // without tiles (wilderness, roadside)
            var count = Nums(Prop(size, "count"));
            var type = new TownType
            {
                Name = name,
                Biomes = (Prop(t, "biomes") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(biomeIndex.ContainsKey).Select(b => biomeIndex[b]).ToArray(),
                Count = count.Length >= 2 ? (int)count[1] : count.Length == 1 ? (int)count[0] : 1,
                TilesMin = (int)tiles[0], TilesMax = (int)tiles[1],
            };
            if (type.Biomes.Length == 0) type.Biomes = new[] { 0, 1, 2, 3, 4 };
            foreach (var d in districts.Where(d => d.Towns.Contains(name, StringComparer.OrdinalIgnoreCase) && d.Weight > 0))
                type.Districts.Add((d.Prefix, d.Weight));
            if (type.Districts.Count == 0) continue;
            if (Prop(t, "spawn_gateway") == "false") type.Gateway = false;
            if (Prop(t, "spawn_trader") == "false") type.Trader = false;
            var outskirt = (Prop(t, "outskirt_district") ?? "").Split(',', StringSplitOptions.TrimEntries);
            if (outskirt.Length == 2 && outskirt[0].Length > 0)
            {
                type.Outskirt = districts.FirstOrDefault(d => d.Name == outskirt[0]).Prefix ?? outskirt[0];
                type.OutskirtShare = Nums(outskirt[1]).FirstOrDefault();
            }
            if (type.Biomes.Length is > 1 and < 5)
            {
                // one own row per biome; the number of the game is distributed (rest to the first biomes)
                for (int k = 0; k < type.Biomes.Length; k++)
                {
                    var part = type.Clone();
                    part.Biomes = new[] { type.Biomes[k] };
                    part.PerBiome = true;
                    part.Count = type.Count / type.Biomes.Length + (k < type.Count % type.Biomes.Length ? 1 : 0);
                    part.Districts = new List<(string, double)>(type.Districts);
                    rules.Types.Add(part);
                }
                continue;
            }
            rules.Types.Add(type);
        }
        foreach (XmlElement s in doc.SelectNodes("/rwgmixer/streettile"))
            if (Nums(Prop(s, "maxtiles")).Length == 1) rules.MaxTiles[s.GetAttribute("name")] = (int)Nums(Prop(s, "maxtiles"))[0];
        foreach (XmlElement a in doc.SelectNodes("/rwgmixer/prefab_spawn_adjust"))
        {
            string name = a.GetAttribute("partial_name"), tags = a.GetAttribute("biomeTags");
            if (!name.StartsWith("trader_", StringComparison.OrdinalIgnoreCase) || tags.Length == 0) continue;
            foreach (var b in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (biomeIndex.TryGetValue(b, out int bi)) rules.Traders[bi] ??= name;
        }
        return rules;
    }
}


/// road between two parts of a city: points from exit A (1 m in the tile) to exit B, width at both ends
public sealed record CityRoad(List<Point> Points, double WidthA, double WidthB, Surface Surface);

/// a planned city: tiles (with height), roads between the parts, open exits for later roads to other cities
public sealed class CityPlan
{
    public TownType Type;
    public List<PlacedPrefab> Tiles = new();
    public List<CityRoad> Roads = new();
    public List<Point> OpenExits = new();
    public int MainFloor;              // ground height of the main part (first air block)
    public List<int> PartFloors = new();   // ground heights of the districts
    public PlacedPrefab Gateway;       // gateway tile at the city entrance (null = none)
    public string Trader;              // trader on the lot of the gateway (null = none)
    public int Biome;                  // biome under the main part
    public int X0, Z0, X1, Z1;         // rectangle of all tiles
    public double CenterX, CenterZ;    // center of the main part
    public bool Elevated;              // on a hill, main part leveled (location of the cities)
    public double MaxCut;              // largest removal under the main part (m)
    public bool Terrace;               // cut into the slope as a terrace (mountains, fallback)
    public List<(int X0, int Z0, int X1, int Z1)> Corridors = new();   // areas to keep free in front of open exits (later roads)
    public List<PlacedPrefab> Parts = new();   // parts (taper at the gateway), to be entered after the tiles
}

/// plan cities on a finished landscape (the user's request 2026-11-07, step 2 after the terrain generator).
/// Structure after the model of the user (pine forest city in Fidife-Land): a level main part (tiles directly adjoining,
/// exits meet exits), plus 0–3 districts with 1–3 tiles at their natural height (6–25 m above or below the
/// main part, e.g. on a hill or in the valley) and a gateway at the city entrance (with the trader of the biome). The parts are connected by
/// one curved road each that follows the terrain (RoadRouter, at most RoadGrade). No more small steps in the main part.
public sealed class CityGenerator
{
    const int Tile = 150;
    const int Coarse = 16;          // grid for the site search (m)
    const int UnitGap = 35;         // at least this much room between two parts of a city (slope, road)

    readonly WorldData w;
    readonly CitySpec spec;
    readonly CityRules rules;
    readonly Random rnd;
    readonly List<TileKind> kinds, gateways;
    readonly Dictionary<string, int> used = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(int X0, int Z0, int X1, int Z1, int MaxGap)> blocked = new();   // existing buildings, spawn points (distance at most MaxGap) and already planned cities
    readonly List<List<Point>> plannedRoads = new();   // roads of already planned cities (the path search avoids them)
    readonly List<(int X0, int Z0, int X1, int Z1)> plannedCorridors = new();   // corridors of already planned cities
    // coarse grid: mean height, water, biome per 16 m
    int cn;
    float[] ch;
    bool[] cw;
    byte[] cb;
    int minTiles;
    bool elevatedTown;   // the city currently planned is to lie on a hill
    bool terraceTown;    // the city currently planned lies as a terrace in the mountains (fallback if there is no room otherwise)
    int DeltaMaxNow => terraceTown ? (int)Math.Round(spec.DeltaMax * 1.6) : spec.DeltaMax;

    public List<string> Problems { get; } = new();

    sealed class TileKind
    {
        public string Name, Prefix;
        public PrefabDef Def;
        public int[] Mask = new int[4];   // sides with a road exit per rotation: bit 0 south, 1 east, 2 north, 3 west
    }

    sealed class Cell
    {
        public int I, J, Mask, Rot;
        public string Prefix;
        public TileKind Kind;
    }

    /// a part of the city: cells in its own 150 m grid (cell i,j at OX + 150·i, OZ + 150·j), one ground height
    sealed class Unit
    {
        public Dictionary<(int, int), Cell> Cells = new();
        public int OX, OZ, Floor;
        public bool Gateway;
        public Cell AttachCell;   // gateway directly at the main part (cell of the main part and side; no road needed)
        public int AttachSide;
        public Cell Inner;        // gateway in the middle of the main part (the user's request 2026-11-08: trader sometimes near the center)
        public int InnerMask;     // its two exits (0b0101 north–south, 0b1010 east–west), both to cells of the main part
        public int X(Cell c) => OX + c.I * Tile;
        public int Z(Cell c) => OZ + c.J * Tile;
        public (int X0, int Z0, int X1, int Z1) Rect => (OX + Cells.Keys.Min(k => k.Item1) * Tile, OZ + Cells.Keys.Min(k => k.Item2) * Tile,
            OX + (Cells.Keys.Max(k => k.Item1) + 1) * Tile, OZ + (Cells.Keys.Max(k => k.Item2) + 1) * Tile);
        public (double X, double Z) Center { get { var r = Rect; return ((r.X0 + r.X1) / 2.0, (r.Z0 + r.Z1) / 2.0); } }
    }

    /// connection: cell CA (side SA) of the main part with cell CB (side SB) of another part
    sealed record Link(Unit A, Cell CA, int SA, Unit B, Cell CB, int SB, bool Direct = false);

    // footprint of a tile without YOffset (for ground height and earthwork before the tile is chosen)
    static readonly PrefabDef Probe = new() { SizeX = Tile, SizeZ = Tile, YOffset = 0, Found = true, Kind = PrefabKind.Tile };

    public CityGenerator(WorldData world, CitySpec spec, CityRules rules, IEnumerable<string> tileNames, Func<string, PrefabDef> defOf)
    {
        w = world;
        this.spec = spec;
        this.rules = rules;
        rnd = new Random(new GeneratorSpec { Seed = (spec.Seed ?? "") + "/towns" }.SeedValue);
        kinds = new List<TileKind>();
        gateways = new List<TileKind>();
        var prefixes = spec.Types.SelectMany(t => t.Districts.Select(d => d.Prefix).Append(t.Outskirt)).Where(p => p != null)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length).ToList();
        foreach (var name in tileNames.Where(n => n.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            string rest = name[9..];
            bool gateway = rest.StartsWith("gateway_", StringComparison.OrdinalIgnoreCase);
            string prefix = gateway ? "gateway" : prefixes.FirstOrDefault(p => rest.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase));
            if (prefix == null) continue;
            var def = defOf(name);
            if (def == null || def.SizeX != Tile || def.SizeZ != Tile) continue;
            // gateways only with a 60 × 60 lot (every trader fits there) and with two opposite exits
            if (gateway && !def.Lots.Any(l => l.SizeX == 60 && l.SizeZ == 60)) continue;
            var k = new TileKind { Name = name, Prefix = prefix, Def = def };
            bool ok = true;
            for (int r = 0; r < 4 && ok; r++)
            {
                var probe = new PlacedPrefab { Name = name, Def = def, X = 0, Z = 0, Rotation = r };
                if (w.TileSurface(probe, out var exits) == null) { ok = false; break; }
                foreach (var (x, z) in exits) k.Mask[r] |= 1 << SideOf(probe, x, z);
            }
            if (!ok || k.Mask[0] == 0) continue;
            if (gateway) { if (k.Mask[0] == 0b0101 || k.Mask[0] == 0b1010) gateways.Add(k); }
            else kinds.Add(k);
        }
        foreach (var p in w.Prefabs.Where(p => p.Def != null))
            blocked.Add((p.X, p.Z, p.X + p.SizeX, p.Z + p.SizeZ, int.MaxValue));
        foreach (var s in w.Spawns)   // spawn points not into the city (the height would no longer be right)
            blocked.Add(((int)s.X - 2, (int)s.Z - 2, (int)s.X + 2, (int)s.Z + 2, 120));   // only not into the city and its slopes
        BuildCoarse();
    }

    public int KindCount => kinds.Count;
    public int GatewayKindCount => gateways.Count;

    // side of a point at the edge of the tile: 0 south, 1 east, 2 north, 3 west
    static int SideOf(PlacedPrefab t, double x, double z)
    {
        if (Math.Abs(z - t.Z) < 0.01) return 0;
        if (Math.Abs(x - (t.X + t.SizeX)) < 0.01) return 1;
        if (Math.Abs(z - (t.Z + t.SizeZ)) < 0.01) return 2;
        return 3;
    }

    static readonly (int DI, int DJ)[] Dir = { (0, -1), (1, 0), (0, 1), (-1, 0) };   // south, east, north, west

    // ---------- coarse grid ----------

    void BuildCoarse()
    {
        cn = w.Size / Coarse;
        ch = new float[cn * cn];
        cw = new bool[cn * cn];
        cb = new byte[cn * cn];
        int bn = w.BiomeSize;
        Parallel.For(0, cn, cz =>
        {
            for (int cx = 0; cx < cn; cx++)
            {
                double sum = 0;
                bool water = false;
                for (int sz = 2; sz < Coarse; sz += 4)
                    for (int sx = 2; sx < Coarse; sx += 4)
                    {
                        int x = cx * Coarse + sx, z = cz * Coarse + sz;
                        int i = z * w.Size + x;
                        sum += w.Heights[i] / 256.0;
                        if (w.WaterMask[i] != 0) water = true;
                    }
                int g = cz * cn + cx;
                ch[g] = (float)(sum / 16);
                cw[g] = water;
                int bx = Math.Clamp((cx * Coarse + Coarse / 2) * bn / w.Size, 0, bn - 1), by = Math.Clamp(bn - 1 - (cz * Coarse + Coarse / 2) * bn / w.Size, 0, bn - 1);
                cb[g] = w.Biomes[by * bn + bx];
            }
        });
    }

    int CoarseIndex(double x, double z)
    {
        int cx = Math.Clamp((int)((x + w.Half) / Coarse), 0, cn - 1), cz = Math.Clamp((int)((z + w.Half) / Coarse), 0, cn - 1);
        return cz * cn + cx;
    }

    // ---------- Procedure ----------

    public List<CityPlan> Plan(IProgress<string> progress)
    {
        var plans = new List<CityPlan>();
        // large cities first (they need the most room)
        var order = spec.Types.Where(t => t.Count > 0 && t.TilesMax > 0).OrderByDescending(t => t.TilesMax).ToList();
        foreach (var type in order)
        {
            int placed = 0;
            for (int n = 0; n < type.Count; n++)
            {
                progress?.Report(Loc.F("Planning towns: {0} ({1} of {2}) …", type.Label, n + 1, type.Count));
                var plan = PlaceTown(type);
                if (plan == null) break;
                plans.Add(plan);
                blocked.Add((plan.X0, plan.Z0, plan.X1, plan.Z1, int.MaxValue));
                plannedRoads.AddRange(plan.Roads.Select(r => r.Points));
                plannedCorridors.AddRange(plan.Corridors);
                placed++;
            }
            if (placed < type.Count)
                Problems.Add(Loc.F("{0}: only {1} of {2} found a suitable place.", type.Label, placed, type.Count));
        }
        AssignTraders(plans);
        return plans;
    }

    /// traders that are already on the map, per biome (when adding they count along so that there are not too many)
    public int[] ExistingTraders { get; set; } = new int[5];

    /// biome index at a place of the map (biomes.png, row 0 = north)
    public static int BiomeAt(WorldData w, double x, double z)
    {
        int bn = w.BiomeSize;
        int bx = Math.Clamp((int)((x + w.Half) * bn / w.Size), 0, bn - 1), by = Math.Clamp(bn - 1 - (int)((z + w.Half) * bn / w.Size), 0, bn - 1);
        return w.Biomes[by * bn + bx];
    }

    /// traders per biome (the user's request): Rekt pine forest, Jen burnt, Bob desert, Hugh snow, Joel wasteland (rwgmixer.xml,
    /// prefab_spawn_adjust); per biome as many as set (minus those already present), in the largest cities of the biome with a gateway
    void AssignTraders(List<CityPlan> plans)
    {
        for (int b = 0; b < 5; b++)
        {
            string trader = rules.Traders[b];
            int want = spec.TradersPerBiome - ExistingTraders[b];
            if (trader == null || want <= 0) continue;
            var towns = plans.Where(p => p.Biome == b && p.Gateway != null && p.Type.Trader).OrderByDescending(p => p.Tiles.Count).Take(want).ToList();
            foreach (var p in towns) p.Trader = trader;
            if (towns.Count < want)
                Problems.Add(Loc.F("{0}: only {1} of {2} trader(s) placed ({3}) – not enough towns with a gateway in this biome.",
                    Loc.T(BiomePalette.Names[b]), towns.Count, want, trader));
        }
    }

    /// display name of a township type
    public static string TypeLabel(string township) => township switch
    {
        "oldwest" => Loc.T("Old West town"),
        "forest_countrytown" => Loc.T("Country town (pine forest)"),
        "bforest_countrytown" => Loc.T("Country town (burnt forest)"),
        "bforest_town" => Loc.T("Town (burnt forest)"),
        "countrytown" => Loc.T("Country town"),
        "town" => Loc.T("Town"),
        "city" => Loc.T("City"),
        "wasteland_city" => Loc.T("City (wasteland)"),
        "citybig" => Loc.T("Big city"),
        _ => township,
    };

    CityPlan PlaceTown(TownType type)
    {
        int n = rnd.Next(type.TilesMin, type.TilesMax + 1);
        // districts: small cities 0–1, medium up to 2, large up to 3 (at most as set), 1–3 tiles each
        int maxParts = Math.Min(spec.MaxParts, n <= 2 ? 0 : n <= 4 ? 1 : n <= 9 ? 2 : 3);
        int partCount = maxParts == 0 ? 0 : rnd.Next((maxParts + 1) / 2, maxParts + 1);
        var partSizes = Enumerable.Range(0, partCount).Select(_ => 1 + rnd.Next(Math.Clamp(n / 5, 1, 3))).ToList();
        int mainTarget = Math.Max(1, n - partSizes.Sum());
        double radius = Math.Sqrt(mainTarget) * Tile / 2.0 + 50;
        // location (the user's request 2026-11-07): preferably hills or mixed about every second city
        elevatedTown = spec.Elevation == 2 || spec.Elevation == 1 && rnd.NextDouble() < 0.5;
        var sites = Sites(type, radius);
        if (elevatedTown && sites.Count == 0) { elevatedTown = false; sites = Sites(type, radius); }
        var plan = TryPlace(type, mainTarget, partSizes, sites);
        if (plan == null && elevatedTown)   // no hill fits: then a level place as usual
        {
            elevatedTown = false;
            plan = TryPlace(type, mainTarget, partSizes, Sites(type, radius));
        }
        if (plan == null)
        {
            // mountains (the user's request 2026-11-09: Roriso-Land with 160 % mountains got no city in the snow): small level
            // main part cut into the slope as a terrace, the remaining tiles as districts at their own height
            terraceTown = true;
            int main = Math.Max(2, (int)Math.Round(n * 0.4));
            var sizes = new List<int>();
            for (int rest = n - main; rest > 0 && sizes.Count < 5; ) { int s = Math.Min(rest, 1 + rnd.Next(3)); sizes.Add(s); rest -= s; }
            plan = TryPlace(type, main, sizes, Sites(type, Math.Sqrt(main) * Tile / 2.0 + 50));
            terraceTown = false;
            if (plan != null) plan.Terrace = true;
        }
        return plan;
    }

    CityPlan TryPlace(TownType type, int mainTarget, List<int> partSizes, List<(double X, double Z, double Score, int Biome)> sites)
    {
        // first in full size (like in the game), otherwise smaller (at least 60 % of the smallest size)
        // as a terrace in the mountains it may become as small as 40 % (rather a smaller city than none)
        foreach (int min in new[] { type.TilesMin, Math.Max(1, (int)Math.Ceiling(type.TilesMin * (terraceTown ? 0.4 : 0.6))) })
        {
            minTiles = min;
            int tries = 0;
            foreach (var (sx, sz, _, biome) in sites)
            {
                if (++tries > 40) break;
                var plan = Layout(type, mainTarget, partSizes, sx, sz, biome);
                if (plan != null) return plan;
            }
            if (min == type.TilesMin && type.TilesMin <= 2) break;
        }
        return null;
    }

    // candidates for the center of the main part, best first: matching biome, no water, as level as possible
    List<(double X, double Z, double Score, int Biome)> Sites(TownType type, double radius)
    {
        var list = new List<(double, double, double, int)>();
        var counts = new int[5];
        double margin = radius + 250;
        int step = 96;
        var biomes = new HashSet<int>(type.Biomes);
        int diagBiome = 0, diagWater = 0; double diagBest = 999, diagBestRms = 0;
        for (double z = -w.Half + margin; z <= w.Half - margin; z += step)
            for (double x = -w.Half + margin; x <= w.Half - margin; x += step)
            {
                if (Near(x - radius, z - radius, x + radius, z + radius, 450)) continue;
                double s0 = 0, sxx = 0, szz = 0, sh = 0, shx = 0, shz = 0;
                int total = 0;
                Array.Clear(counts);
                bool water = false;
                var samples = new List<(double, double, double)>();
                for (double dz = -radius; dz <= radius; dz += 32)
                    for (double dx = -radius; dx <= radius; dx += 32)
                    {
                        int g = CoarseIndex(x + dx, z + dz);
                        if (cw[g]) water = true;
                        total++;
                        counts[cb[g]]++;
                        double h = ch[g];
                        samples.Add((dx, dz, h));
                        s0++; sxx += dx * dx; szz += dz * dz; sh += h; shx += h * dx; shz += h * dz;
                    }
                // biome of the city = most frequent matching one; the area must lie almost entirely in it (every tile checks it exactly later)
                int biome = type.Biomes.OrderByDescending(b => counts[b]).First();
                if (counts[biome] >= total * 0.97) diagBiome++;
                if (water || total == 0 || counts[biome] < total * 0.97) continue;
                if (WaterNear(x, z, radius + 60)) { diagWater++; continue; }
                // main part lies at one height: deviation from the mean counts, a slope costs too
                double a = sh / s0, b = sxx > 0 ? shx / sxx : 0, c = szz > 0 ? shz / szz : 0;
                double flat = 0, res = 0;
                foreach (var (dx, dz, h) in samples) { flat += (h - a) * (h - a); double d = h - (a + b * dx + c * dz); res += d * d; }
                double rmsFlat = Math.Sqrt(flat / s0), rms = Math.Sqrt(res / s0);
                if (rmsFlat < diagBest) { diagBest = rmsFlat; diagBestRms = rms; }
                if (terraceTown)
                {
                    // terrace: also on the slope, the flattest places first
                    if (rms > 34 || rmsFlat > 32) continue;
                    list.Add((x, z, rmsFlat + 0.5 * rms + rnd.NextDouble() * 1.5, biome));
                    continue;
                }
                if (!elevatedTown)
                {
                    if (rms > 16 || rmsFlat > 14) continue;
                    list.Add((x, z, rmsFlat + 0.5 * rms + rnd.NextDouble() * 1.5, biome));
                    continue;
                }
                // hill (dome, plateau, ridge): mean of the area above the mean of a ring 100–400 m outside; it may be more
                // uneven, the main part is leveled (removed on top)
                if (rms > 20 || rmsFlat > 18) continue;
                double ring = 0;
                int rn = 0;
                for (double dz = -radius - 400; dz <= radius + 400; dz += 48)
                    for (double dx = -radius - 400; dx <= radius + 400; dx += 48)
                    {
                        double ex = Math.Max(0, Math.Abs(dx) - radius), ez = Math.Max(0, Math.Abs(dz) - radius), e = Math.Sqrt(ex * ex + ez * ez);
                        if (e < 100 || e > 400) continue;
                        ring += ch[CoarseIndex(x + dx, z + dz)];
                        rn++;
                    }
                double rel = rn > 0 ? a - ring / rn : 0;
                if (rel < 5) continue;
                list.Add((x, z, 0.6 * rmsFlat + 0.3 * rms - 0.6 * Math.Min(rel, 40) + rnd.NextDouble() * 1.5, biome));
            }
        Log?.Invoke($"sites {type.Label} r{radius:0} elev {elevatedTown}: {list.Count} ok, biome {diagBiome}, water {diagWater}, best rmsFlat {diagBest:0.0}/rms {diagBestRms:0.0}");
        return list.OrderBy(s => s.Item3).ToList();
    }

    /// test output (test program)
    public static Action<string> Log;

    bool WaterNear(double x, double z, double r)
    {
        for (double dz = -r; dz <= r; dz += Coarse)
            for (double dx = -r; dx <= r; dx += Coarse)
                if (cw[CoarseIndex(x + dx, z + dz)]) return true;
        return false;
    }

    // does the rectangle lie closer than gap to an existing building or a planned city?
    bool Near(double x0, double z0, double x1, double z1, double gap)
    {
        foreach (var (bx0, bz0, bx1, bz1, maxGap) in blocked)
        {
            double g = Math.Min(gap, maxGap);
            double dx = Math.Max(0, Math.Max(bx0 - x1, x0 - bx1)), dz = Math.Max(0, Math.Max(bz0 - z1, z0 - bz1));
            if (dx * dx + dz * dz < g * g) return true;
        }
        return false;
    }

    static double RectGap((int X0, int Z0, int X1, int Z1) a, (int X0, int Z0, int X1, int Z1) b)
    {
        double dx = Math.Max(0, Math.Max(a.X0 - b.X1, b.X0 - a.X1)), dz = Math.Max(0, Math.Max(a.Z0 - b.Z1, b.Z0 - a.Z1));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    PlacedPrefab ProbeTile(int x, int z, int floor) => new() { Name = "probe", Def = Probe, X = x, Z = z, Y = floor };

    int FloorOf(Unit u) => (int)Math.Round(u.Cells.Values.Average(c => MeanHeight(u.X(c), u.Z(c))) + 0.85);

    /// ground height of a leveled main part on a hill: 65 % of the terrain below lies lower (removed on top,
    /// less filled at the edges than with the mean – otherwise high embankments would arise on the slope)
    int FloorHigh(Unit u)
    {
        var s = new List<double>();
        foreach (var c in u.Cells.Values)
            for (int z = u.Z(c) + 2; z < u.Z(c) + Tile; z += 5)
                for (int x = u.X(c) + 2; x < u.X(c) + Tile; x += 5) s.Add(w.HeightAt(x, z));
        s.Sort();
        return (int)Math.Round(s[(int)(s.Count * 0.65)] + 0.85);
    }

    // ---------- place a city at a site ----------

    CityPlan Layout(TownType type, int mainTarget, List<int> partSizes, double cx, double cz, int biome)
    {
        // the whole city lies in one biome (the user's request 2026-11-07: you do not get into a larger city so easily)
        var biomes = new HashSet<int> { biome };
        // 1. main part grows from the center
        var main = new Unit { OX = (int)Math.Round(cx - Tile / 2.0), OZ = (int)Math.Round(cz - Tile / 2.0) };
        bool CellOk(int i, int j)
        {
            int x0 = main.OX + i * Tile, z0 = main.OZ + j * Tile;
            if (!TileAreaOk(x0, z0, biomes)) return false;
            double lo = double.MaxValue, hi = double.MinValue;
            for (double z = z0; z <= z0 + Tile; z += Coarse)
                for (double x = x0; x <= x0 + Tile; x += Coarse) { lo = Math.Min(lo, ch[CoarseIndex(x, z)]); hi = Math.Max(hi, ch[CoarseIndex(x, z)]); }
            return hi - lo <= (terraceTown ? 48 : 26);   // too steep or rugged for a level tile
        }
        if (!CellOk(0, 0)) return null;
        double h0 = MeanHeight(main.OX, main.OZ);
        main.Cells[(0, 0)] = new Cell { I = 0, J = 0 };
        while (main.Cells.Count < mainTarget)
        {
            (int, int)? best = null;
            double bestScore = double.MaxValue;
            foreach (var c in main.Cells.Values.ToList())
                foreach (var (di, dj) in Dir)
                {
                    var key = (c.I + di, c.J + dj);
                    if (main.Cells.ContainsKey(key) || !CellOk(key.Item1, key.Item2)) continue;
                    int nb = Dir.Count(d => main.Cells.ContainsKey((key.Item1 + d.DI, key.Item2 + d.DJ)));
                    double dist = Math.Sqrt(key.Item1 * key.Item1 + key.Item2 * key.Item2);
                    // main part at one height: prefer cells near the height of the center
                    double dh = Math.Abs(MeanHeight(main.OX + key.Item1 * Tile, main.OZ + key.Item2 * Tile) - h0);
                    double score = -2.0 * nb + dh / 2.5 + dist * 0.6 + rnd.NextDouble() * 1.6;
                    if (score < bestScore) { bestScore = score; best = key; }
                }
            if (best == null) break;
            main.Cells[best.Value] = new Cell { I = best.Value.Item1, J = best.Value.Item2 };
        }
        // 2. earthwork: cells that lie too far from the common height drop out (the largest connected piece stays)
        for (int rep = 0; ; rep++)
        {
            main.Floor = elevatedTown ? FloorHigh(main) : FloorOf(main);
            var bad = main.Cells.Values.Where(c => !EarthworkOk(ProbeTile(main.X(c), main.Z(c), main.Floor), terraceTown ? 2 : elevatedTown ? 1 : 0)).ToList();
            if (bad.Count == 0) break;
            if (rep >= 8) return null;
            foreach (var c in bad) main.Cells.Remove((c.I, c.J));
            KeepLargestPart(main.Cells);
            if (main.Cells.Count == 0) return null;
        }

        // 3. districts at their natural height, then the gateway
        var units = new List<Unit>();
        foreach (int size in partSizes)
        {
            var u = FindUnit(main, units, size, false, biomes);
            if (u != null) units.Add(u);
        }
        // gateway (with the trader): at about half of the cities near the center – in the middle of the main part, otherwise directly beside it;
        // otherwise as before somewhat outside with its own road
        main.Inner = null;
        bool central = type.Gateway && gateways.Count > 0 && rnd.NextDouble() < 0.5;
        if (central && !PickInnerGateway(main))
        {
            var g = AttachGateway(main, units, biomes);
            if (g != null) units.Add(g);
            else central = false;
        }
        if (type.Gateway && gateways.Count > 0 && !central)
        {
            var g = FindUnit(main, units, 1, true, biomes) ?? FindUnit(main, units, 1, true, biomes, relaxed: true) ?? AttachGateway(main, units, biomes);   // second attempt: further away, more earthwork (trader!)
            if (g != null) units.Add(g);
        }
        if (main.Cells.Count + units.Where(u => !u.Gateway).Sum(u => u.Cells.Count) < minTiles) return null;

        // 4. tiles, exits and roads; if no fitting road is found to a part, it is searched at another place (up to 3×)
        var rejected = new List<(int X0, int Z0, int X1, int Z1)>();
        for (int attempt = 0; attempt < units.Count + 6; attempt++)
        {
            var plan = Build(type, main, units, out var failed);
            if (plan != null) return plan;
            if (failed == null || !units.Remove(failed)) return null;
            rejected.Add(failed.Rect);
            if (rejected.Count <= 3)
            {
                var again = FindUnit(main, units, failed.Cells.Count, failed.Gateway, biomes, rejected);
                if (again == null && failed.Gateway)   // do not give up the gateway (trader!): further away or directly at the main part
                    again = FindUnit(main, units, 1, true, biomes, rejected, relaxed: true) ?? (failed.AttachCell == null ? AttachGateway(main, units, biomes) : null);
                if (again != null) units.Add(again);
            }
            if (main.Cells.Count + units.Where(u => !u.Gateway).Sum(u => u.Cells.Count) < minTiles) return null;
        }
        return null;
    }

    // area of a tile usable? In the map, biome 60 %, no water, no building/city nearby
    bool TileAreaOk(int x0, int z0, HashSet<int> biomes)
    {
        if (x0 < -w.Half + 120 || z0 < -w.Half + 120 || x0 + Tile > w.Half - 120 || z0 + Tile > w.Half - 120) return false;
        int good = 0, total = 0;
        for (double z = z0 - 24; z <= z0 + Tile + 24; z += Coarse)
            for (double x = x0 - 24; x <= x0 + Tile + 24; x += Coarse)
            {
                int g = CoarseIndex(x, z);
                if (cw[g]) return false;
                total++;
                if (biomes.Contains(cb[g])) good++;
            }
        return good == total && !Near(x0, z0, x0 + Tile, z0 + Tile, 60);   // completely in the biome of the city (with a 24 m margin)
    }

    // shapes of a district with m tiles (cells relative)
    static IEnumerable<(int, int)[]> Shapes(int m) => m switch
    {
        1 => new[] { new[] { (0, 0) } },
        2 => new[] { new[] { (0, 0), (1, 0) }, new[] { (0, 0), (0, 1) } },
        _ => new[]
        {
            new[] { (0, 0), (1, 0), (2, 0) }, new[] { (0, 0), (0, 1), (0, 2) },
            new[] { (0, 0), (1, 0), (0, 1) }, new[] { (0, 0), (1, 0), (1, 1) }, new[] { (1, 0), (0, 1), (1, 1) }, new[] { (0, 0), (0, 1), (1, 1) },
        },
    };

    /// search a district (m tiles) or gateway (1 tile) around the main part: natural height of the terrain, for the district
    /// DeltaMin…DeltaMax above/below the main part, for the gateway at most DeltaMax; little earthwork, not too far away
    Unit FindUnit(Unit main, List<Unit> others, int m, bool gateway, HashSet<int> biomes, List<(int X0, int Z0, int X1, int Z1)> rejected = null, bool relaxed = false)
    {
        var mr = main.Rect;
        var (mcx, mcz) = main.Center;
        // direction of the districts already placed: the gateway should rather lie on the other side
        double ox = 0, oz = 0;
        foreach (var o in others) { var (x, z) = o.Center; double l = Math.Max(1, Math.Sqrt((x - mcx) * (x - mcx) + (z - mcz) * (z - mcz))); ox += (x - mcx) / l; oz += (z - mcz) / l; }
        var candidates = new List<(int X, int Z, (int, int)[] Shape, int Floor, double Score)>();
        foreach (var shape in Shapes(Math.Clamp(m, 1, 3)))
        {
            int sw = (shape.Max(s => s.Item1) + 1) * Tile, sd = (shape.Max(s => s.Item2) + 1) * Tile;
            for (int gap = 40; gap <= (relaxed || terraceTown ? 400 : 240); gap += 20)
                for (int side = 0; side < 4; side++)
                {
                    // position along the side in 30 m steps
                    int from = side % 2 == 0 ? mr.X0 - sw - 60 : mr.Z0 - sd - 60, to = side % 2 == 0 ? mr.X1 + 60 : mr.Z1 + 60;
                    for (int along = from; along <= to; along += 30)
                    {
                        int x0 = side switch { 1 => mr.X1 + gap, 3 => mr.X0 - gap - sw, _ => along };
                        int z0 = side switch { 0 => mr.Z0 - gap - sd, 2 => mr.Z1 + gap, _ => along };
                        var rect = (x0, z0, x0 + sw, z0 + sd);
                        if (RectGap(rect, mr) < 40 || others.Any(o => RectGap(rect, o.Rect) < UnitGap)) continue;
                        if (rejected != null && rejected.Any(r => RectGap(rect, r) < 30)) continue;
                        bool ok = true;
                        double sum = 0;
                        int cnt = 0;
                        foreach (var (i, j) in shape)
                        {
                            if (!TileAreaOk(x0 + i * Tile, z0 + j * Tile, biomes)) { ok = false; break; }
                            for (int z = z0 + j * Tile + 8; z < z0 + (j + 1) * Tile; z += Coarse)
                                for (int x = x0 + i * Tile + 8; x < x0 + (i + 1) * Tile; x += Coarse) { sum += ch[CoarseIndex(x, z)]; cnt++; }
                        }
                        if (!ok) continue;
                        double mean = sum / cnt;
                        int floor = (int)Math.Round(mean + 0.85);
                        int delta = Math.Abs(floor - main.Floor);
                        if (gateway ? delta > DeltaMaxNow + (relaxed ? 10 : 0) : delta < spec.DeltaMin || delta > DeltaMaxNow) continue;
                        // enough distance for the road: without long loops about height difference / gradient
                        if (gap < delta / spec.RoadGrade + 15) continue;
                        double dev = 0;
                        foreach (var (i, j) in shape)
                            for (int z = z0 + j * Tile + 8; z < z0 + (j + 1) * Tile; z += Coarse)
                                for (int x = x0 + i * Tile + 8; x < x0 + (i + 1) * Tile; x += Coarse) dev += Math.Abs(ch[CoarseIndex(x, z)] - mean);
                        dev /= cnt;
                        if (dev > (terraceTown ? 14 : relaxed ? 8 : 6)) continue;
                        double cxu = x0 + sw / 2.0 - mcx, czu = z0 + sd / 2.0 - mcz, lu = Math.Max(1, Math.Sqrt(cxu * cxu + czu * czu));
                        double score = dev + gap / 50.0 + rnd.NextDouble() * 1.5;
                        if (gateway) score += 2.5 * (cxu * ox + czu * oz) / lu;   // away from the districts
                        candidates.Add((x0, z0, shape, floor, score));
                    }
                }
        }
        foreach (var cand in candidates.OrderBy(c => c.Score).Take(30))
        {
            var u = new Unit { OX = cand.X, OZ = cand.Z, Gateway = gateway };
            foreach (var (i, j) in cand.Shape) u.Cells[(i, j)] = new Cell { I = i, J = j };
            u.Floor = FloorOf(u);
            int delta = Math.Abs(u.Floor - main.Floor);
            if (gateway ? delta > DeltaMaxNow + (relaxed ? 10 : 0) : delta < spec.DeltaMin || delta > DeltaMaxNow) continue;
            if (u.Cells.Values.All(c => EarthworkOk(ProbeTile(u.X(c), u.Z(c), u.Floor), terraceTown ? 2 : 0) && !WaterAround(ProbeTile(u.X(c), u.Z(c), u.Floor))))
                return u;
        }
        return null;
    }

    /// gateway in the middle of the main part: a cell that has both neighbors on one axis in the main part (city road on both sides),
    /// as near the center as possible; the main part must stay connected without the two cross sides of this cell. false = none possible
    bool PickInnerGateway(Unit main)
    {
        double ci = main.Cells.Keys.Average(k => k.Item1), cj = main.Cells.Keys.Average(k => k.Item2);
        Cell best = null; int bestMask = 0; double bestScore = double.MaxValue;
        foreach (var c in main.Cells.Values)
            foreach (int mask in new[] { 0b0101, 0b1010 })
            {
                if (!Enumerable.Range(0, 4).Where(s => (mask & (1 << s)) != 0).All(s => main.Cells.ContainsKey((c.I + Dir[s].DI, c.J + Dir[s].DJ)))) continue;
                if (!ConnectedWithout(main, c, mask)) continue;
                double score = Math.Sqrt((c.I - ci) * (c.I - ci) + (c.J - cj) * (c.J - cj)) + rnd.NextDouble() * 0.6;
                if (score < bestScore) { bestScore = score; best = c; bestMask = mask; }
            }
        main.Inner = best; main.InnerMask = bestMask;
        return best != null;
    }

    // is the main part connected if cell g is joined only via the sides in mask?
    static bool ConnectedWithout(Unit u, Cell g, int mask)
    {
        var start = u.Cells.Keys.First();
        var seen = new HashSet<(int, int)> { start };
        var stack = new Stack<(int, int)>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var k = stack.Pop();
            for (int s = 0; s < 4; s++)
            {
                var nk = (k.Item1 + Dir[s].DI, k.Item2 + Dir[s].DJ);
                if (!u.Cells.ContainsKey(nk)) continue;
                bool fromG = k == (g.I, g.J), toG = nk == (g.I, g.J);
                if (fromG && (mask & (1 << s)) == 0) continue;
                if (toG && (mask & (1 << ((s + 2) % 4))) == 0) continue;
                if (seen.Add(nk)) stack.Push(nk);
            }
        }
        return seen.Count == u.Cells.Count;
    }

    /// gateway directly at an outer edge of the main part (like in the game, same height, no road needed) – fallback if no
    /// free-standing place was found (so that the trader is not missing)
    Unit AttachGateway(Unit main, List<Unit> others, HashSet<int> biomes)
    {
        var (mcx, mcz) = main.Center;
        Unit best = null;
        double bestScore = double.MaxValue;
        foreach (var c in main.Cells.Values)
            for (int s = 0; s < 4; s++)
            {
                if (main.Cells.ContainsKey((c.I + Dir[s].DI, c.J + Dir[s].DJ))) continue;
                int x0 = main.X(c) + Dir[s].DI * Tile, z0 = main.Z(c) + Dir[s].DJ * Tile;
                var rect = (x0, z0, x0 + Tile, z0 + Tile);
                if (others.Any(o => RectGap(rect, o.Rect) < 20) || !TileAreaOk(x0, z0, biomes)) continue;
                var (ox, oz) = s switch { 0 => (x0 + 75.0, (double)z0), 1 => (x0 + Tile, z0 + 75.0), 2 => (x0 + 75.0, (double)(z0 + Tile)), _ => ((double)x0, z0 + 75.0) };
                if (FacesEdge(ox, oz, s)) continue;   // open exit of the gateway otherwise points to the map edge
                var probe = ProbeTile(x0, z0, main.Floor);
                if (!EarthworkOk(probe, terraceTown ? 2 : 0) || WaterAround(probe)) continue;
                double outward = ((x0 + 75 - mcx) * Dir[s].DI + (z0 + 75 - mcz) * Dir[s].DJ) / Tile;
                double score = -outward + rnd.NextDouble();
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new Unit { OX = x0, OZ = z0, Floor = main.Floor, Gateway = true, AttachCell = c, AttachSide = s };
                    best.Cells[(0, 0)] = new Cell { I = 0, J = 0 };
                }
            }
        return best;
    }

    // keep only the largest connected piece
    static void KeepLargestPart(Dictionary<(int, int), Cell> cells)
    {
        var seen = new HashSet<(int, int)>();
        List<(int, int)> best = new();
        foreach (var start in cells.Keys)
        {
            if (seen.Contains(start)) continue;
            var part = new List<(int, int)>();
            var stack = new Stack<(int, int)>();
            stack.Push(start);
            seen.Add(start);
            while (stack.Count > 0)
            {
                var k = stack.Pop();
                part.Add(k);
                foreach (var (di, dj) in Dir)
                {
                    var nk = (k.Item1 + di, k.Item2 + dj);
                    if (cells.ContainsKey(nk) && seen.Add(nk)) stack.Push(nk);
                }
            }
            if (part.Count > best.Count) best = part;
        }
        foreach (var k in cells.Keys.Except(best).ToList()) cells.Remove(k);
    }

    static (double X, double Z) SideMid(Unit u, Cell c, int side)
    {
        double x = u.X(c), z = u.Z(c);
        return side switch { 0 => (x + 75, z), 1 => (x + Tile, z + 75), 2 => (x + 75, z + Tile), _ => (x, z + 75) };
    }

    static bool Free(Unit u, Cell c, int side) => !u.Cells.ContainsKey((c.I + Dir[side].DI, c.J + Dir[side].DJ)) && (c.Mask & (1 << side)) == 0;

    /// connection of a part to the main part: free outer sides that point to each other and lie close together
    Link Connect(Unit main, Unit u) => Connect(main, u, out _);

    Link Connect(Unit main, Unit u, out double bestScore)
    {
        Link best = null;
        bestScore = double.MaxValue;
        foreach (var ca in main.Cells.Values)
            for (int sa = 0; sa < 4; sa++)
            {
                if (ca == main.Inner || !Free(main, ca, sa) || Bits(ca.Mask) >= 4) continue;
                var pa = SideMid(main, ca, sa);
                foreach (var cb in u.Cells.Values)
                    for (int sb = 0; sb < 4; sb++)
                    {
                        if (!Free(u, cb, sb)) continue;
                        if (u.Gateway && Bits(cb.Mask) > 0) continue;
                        var pb = SideMid(u, cb, sb);
                        // estimated path length: 20 m straight out of the tile each, in between the connection, plus the curves (25 m per
                        // 90°); so the exits point to each other and the road does not have to go around a tile (loop avoided)
                        double ax = pa.X + Dir[sa].DI * 20, az = pa.Z + Dir[sa].DJ * 20, bx = pb.X + Dir[sb].DI * 20, bz = pb.Z + Dir[sb].DJ * 20;
                        double dx = bx - ax, dz = bz - az, d = Math.Max(1, Math.Sqrt(dx * dx + dz * dz));
                        double turnA = Math.Acos(Math.Clamp((Dir[sa].DI * dx + Dir[sa].DJ * dz) / d, -1, 1));
                        double turnB = Math.Acos(Math.Clamp(-(Dir[sb].DI * dx + Dir[sb].DJ * dz) / d, -1, 1));
                        double score = d + 40 + (turnA + turnB) / (Math.PI / 2) * 25;
                        // exit that points away from the other part: the road would have to go around its own tile
                        if ((Dir[sa].DI * (pb.X - pa.X) + Dir[sa].DJ * (pb.Z - pa.Z)) < 0) score += 300;
                        if ((Dir[sb].DI * (pa.X - pb.X) + Dir[sb].DJ * (pa.Z - pb.Z)) < 0) score += 300;
                        if (score < bestScore) { bestScore = score; best = new Link(main, ca, sa, u, cb, sb); }
                    }
            }
        return best;
    }

    void Streets(Unit u, double extra)
    {
        var edges = new List<(Cell A, Cell B, int Side)>();
        foreach (var c in u.Cells.Values)
        {
            if (u.Cells.TryGetValue((c.I + 1, c.J), out var e)) edges.Add((c, e, 1));
            if (u.Cells.TryGetValue((c.I, c.J + 1), out var nn)) edges.Add((c, nn, 2));
        }
        var parent = u.Cells.Values.ToDictionary(c => c, c => c);
        Cell Root(Cell c) { while (parent[c] != c) c = parent[c] = parent[parent[c]]; return c; }
        if (u.Inner != null)
        {
            // gateway in the main part: exactly its two exits to the neighbors, no cross roads
            bool AlongInner((Cell A, Cell B, int Side) e) => (e.A == u.Inner && (u.InnerMask & (1 << e.Side)) != 0) || (e.B == u.Inner && (u.InnerMask & (1 << ((e.Side + 2) % 4))) != 0);
            foreach (var e in edges.Where(AlongInner).ToList())
            {
                if (Root(e.A) != Root(e.B)) parent[Root(e.A)] = Root(e.B);
                e.A.Mask |= 1 << e.Side;
                e.B.Mask |= 1 << ((e.Side + 2) % 4);
            }
            edges.RemoveAll(e => e.A == u.Inner || e.B == u.Inner);
        }
        foreach (var (a, b, side) in edges.OrderBy(_ => rnd.Next()))
        {
            bool tree = Root(a) != Root(b);
            if (tree) parent[Root(a)] = Root(b);
            if (tree || rnd.NextDouble() < extra)
            {
                a.Mask |= 1 << side;
                b.Mask |= 1 << ((side + 2) % 4);
            }
        }
    }

    /// description of parts (part_highway_transition); null = no taper at the gateway
    public Func<string, PrefabDef> PartDefOf { get; set; }

    /// taper 16 → 8 m (part_highway_transition, 18 × 8 m) at the side side of a gateway tile: at its marker
    /// (highwayN/highwayS), placed like parts of buildings (position rotated, rotation tile + marker + 2, y from the ground height);
    /// the narrow end lies at the tile edge. null = no matching marker
    PlacedPrefab Transition(PlacedPrefab tile, int side)
    {
        const string name = "part_highway_transition";
        var def = PartDefOf?.Invoke(name);
        if (def == null || tile.Def?.Parts == null) return null;
        foreach (var m in tile.Def.Parts.Where(m => m.PartNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            var (ax, az) = PlacedPrefab.Rotate(m.X, m.Z, tile.Rotation, tile.Def.SizeX, tile.Def.SizeZ);
            var (bx, bz) = PlacedPrefab.Rotate(m.X + m.SizeX, m.Z + m.SizeZ, tile.Rotation, tile.Def.SizeX, tile.Def.SizeZ);
            int x0 = tile.X + (int)Math.Round(Math.Min(ax, bx)), z0 = tile.Z + (int)Math.Round(Math.Min(az, bz));
            int x1 = tile.X + (int)Math.Round(Math.Max(ax, bx)), z1 = tile.Z + (int)Math.Round(Math.Max(az, bz));
            bool touches = side switch { 0 => z0 == tile.Z, 1 => x1 == tile.X + tile.SizeX, 2 => z1 == tile.Z + tile.SizeZ, _ => x0 == tile.X };
            if (!touches) continue;
            return new PlacedPrefab
            {
                Name = name, Def = def, X = x0, Z = z0,
                Rotation = ((tile.Rotation + m.Rotation + 2) % 4 + 4) % 4,
                Y = tile.Y - tile.Def.YOffset + def.YOffset,
            };
        }
        return null;
    }

    /// room in front of an open exit up to the map edge (the user's request 2026-11-07: open connections do not point to the
    /// map edge – the road to other cities would not get away there)
    const int EdgeRoom = 500;
    bool FacesEdge(double px, double pz, int side) => side switch
    {
        0 => pz + w.Half, 1 => w.Half - px, 2 => w.Half - pz, _ => px + w.Half,
    } < EdgeRoom;
    bool FacesEdge(Unit u, Cell c, int side) { var (px, pz) = SideMid(u, c, side); return FacesEdge(px, pz, side); }

    /// area in front of an open exit that must stay free (road to other cities, step 3): 100 m out, 32 m wide
    static (int X0, int Z0, int X1, int Z1) Corridor(Unit u, Cell c, int side)
    {
        var (px, pz) = SideMid(u, c, side);
        double ax = px + Dir[side].DI * 4, az = pz + Dir[side].DJ * 4, bx = px + Dir[side].DI * 100, bz = pz + Dir[side].DJ * 100;
        int x0 = (int)Math.Floor(Math.Min(ax, bx)), x1 = (int)Math.Ceiling(Math.Max(ax, bx)), z0 = (int)Math.Floor(Math.Min(az, bz)), z1 = (int)Math.Ceiling(Math.Max(az, bz));
        if (side % 2 == 0) { x0 -= 16; x1 += 16; } else { z0 -= 16; z1 += 16; }
        return (x0, z0, x1, z1);
    }

    // corridor free of all parts of this city, other cities/buildings and their corridors?
    bool CorridorFree((int X0, int Z0, int X1, int Z1) r, IEnumerable<Unit> all) =>
        !all.Any(o => RectGap(r, o.Rect) < 1) && !Near(r.X0, r.Z0, r.X1, r.Z1, 1) && !plannedCorridors.Any(o => RectGap(r, o) < 1);

    /// plan tiles, exits and roads; failed = part to which no road was found (then null)
    CityPlan Build(TownType type, Unit main, List<Unit> units, out Unit failed)
    {
        failed = null;
        var all = new List<Unit> { main };
        all.AddRange(units);
        foreach (var u in all) foreach (var c in u.Cells.Values) { c.Mask = 0; c.Kind = null; c.Prefix = null; }

        // road network in every part: random spanning tree plus further connections
        double extra = type.Name.Contains("country") || type.Name == "oldwest" ? 0.45 : 0.7;
        foreach (var u in all) Streets(u, u == main ? extra : 0.5);
        // connections of the parts to the main part (gateway: second side opposite stays open for the road to other cities)
        var links = new List<Link>();
        var open = new List<(Unit U, Cell C, int Side)>();
        foreach (var u in units.OrderBy(u => u.AttachCell != null ? 0 : 1))
        {
            // districts hang on the main part, the gateway on the nearest part (otherwise a detour around a district)
            double score = 0;
            Link link = u.AttachCell != null ? new Link(main, u.AttachCell, u.AttachSide, u, u.Cells.Values.First(), (u.AttachSide + 2) % 4, true)
                : Connect(main, u, out score);
            if (u.Gateway && u.AttachCell == null)
                foreach (var other in units.Where(o => !o.Gateway))
                {
                    var l2 = Connect(other, u, out double s2);
                    if (l2 != null && s2 < score) { link = l2; score = s2; }
                }
            if (link == null) { failed = u; return null; }
            link.CA.Mask |= 1 << link.SA;
            link.CB.Mask |= 1 << link.SB;
            links.Add(link);
            if (u.Gateway)
            {
                int outer = (link.SB + 2) % 4;
                if (FacesEdge(u, link.CB, outer) || !CorridorFree(Corridor(u, link.CB, outer), all.Where(o => o != u))) { failed = u; return null; }   // exit points to the city, another road or the map edge
                link.CB.Mask |= 1 << outer;
                open.Add((u, link.CB, outer));
            }
        }
        // further open exits (the user's request 2026-11-07: every city 2–4 open connections for roads to other cities;
        // small cities 2, from 6 tiles 3, from 12 tiles 4 – the outer exit of the gateway counts). First at the main part, otherwise
        // at districts; outwards and away from the other parts, preferably in different directions, never to the map edge
        int tilesTotal = all.Where(u => !u.Gateway).Sum(u => u.Cells.Count) - (main.Inner != null ? 1 : 0);
        int wantOpen = tilesTotal >= 12 ? 4 : tilesTotal >= 6 ? 3 : 2;
        var (mcx, mcz) = main.Center;
        var candidates = all.Where(u => !u.Gateway).SelectMany(u => u.Cells.Values.SelectMany(c => Enumerable.Range(0, 4)
            .Where(s => c != main.Inner && Free(u, c, s) && Bits(c.Mask) < 3 && !FacesEdge(u, c, s) && CorridorFree(Corridor(u, c, s), all.Where(o => o != u)) && CorridorFree(Corridor(u, c, s), new[] { u }))
            .Select(s =>
            {
                var (px, pz) = SideMid(u, c, s);
                var others = all.Where(o => o != u).ToList();
                double away = others.Count == 0 ? 0 : others.Average(o => { var (ux, uz) = o.Center; double dx = ux - px, dz = uz - pz, l = Math.Max(1, Math.Sqrt(dx * dx + dz * dz)); return (Dir[s].DI * dx + Dir[s].DJ * dz) / l; });
                double outwards = ((px - mcx) * Dir[s].DI + (pz - mcz) * Dir[s].DJ) / Tile;
                return (U: u, C: c, Side: s, Score: outwards - 2 * away + (u == main ? 1 : 0) + rnd.NextDouble());
            }))).ToList();
        while (open.Count < wantOpen && candidates.Count > 0)
        {
            // different directions: already open directions cost, corridors must not overlap
            var best = candidates.OrderByDescending(o => o.Score - 1.5 * open.Count(p => p.Side == o.Side)).First();
            candidates.Remove(best);
            var r = Corridor(best.U, best.C, best.Side);
            if (Bits(best.C.Mask) >= 4 || (best.C.Mask & (1 << best.Side)) != 0 || open.Any(p => RectGap(r, Corridor(p.U, p.C, p.Side)) < 1)) continue;
            best.C.Mask |= 1 << best.Side;
            open.Add((best.U, best.C, best.Side));
        }

        // districts: in the main part around random center points, edge tiles partly rural; districts one district each
        var list = main.Cells.Values.ToList();
        int seeds = Math.Clamp((int)Math.Round(list.Count / 6.0), 1, Math.Max(1, type.Districts.Count));
        var centers = list.OrderBy(_ => rnd.Next()).Take(seeds).Select(c => (c, Prefix: Weighted(type.Districts))).ToList();
        foreach (var c in list)
            c.Prefix = centers.OrderBy(s => Math.Abs(s.c.I - c.I) + Math.Abs(s.c.J - c.J) + rnd.NextDouble() * 0.9).First().Prefix;
        if (type.Outskirt != null && list.Count > 3)
            foreach (var c in list.Where(c => Dir.Any(d => !main.Cells.ContainsKey((c.I + d.DI, c.J + d.DJ)))).OrderBy(_ => rnd.Next()))
            {
                if (rnd.NextDouble() >= type.OutskirtShare * 0.6) continue;
                bool avoided = Dir.Any(d => main.Cells.TryGetValue((c.I + d.DI, c.J + d.DJ), out var nb) && Avoids(nb.Prefix, type.Outskirt));
                if (!avoided && list.Count(x => x.Prefix == type.Outskirt) < list.Count / 2) c.Prefix = type.Outskirt;
            }
        foreach (var u in units.Where(u => !u.Gateway))
        {
            string prefix = type.Outskirt != null && rnd.NextDouble() < type.OutskirtShare * 0.5 ? type.Outskirt : Weighted(type.Districts);
            foreach (var c in u.Cells.Values) c.Prefix = prefix;
        }

        // tile per cell: same exits in one of the four rotations
        var localUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool Choose(Cell c, IEnumerable<TileKind> pool, IEnumerable<string> prefixes)
        {
            foreach (var prefix in prefixes)
            {
                var options = new List<(TileKind K, int R)>();
                foreach (var k in pool.Where(k => prefix == null || k.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    if (rules.MaxTiles.TryGetValue(k.Name, out int max) && used.GetValueOrDefault(k.Name) + localUse.GetValueOrDefault(k.Name) >= max) continue;
                    for (int r = 0; r < 4; r++) if (k.Mask[r] == c.Mask) options.Add((k, r));
                }
                if (options.Count == 0) continue;
                // variety: tiles already used often less frequently
                var pick = options.OrderBy(o => localUse.GetValueOrDefault(o.K.Name) * 2 + rnd.NextDouble() * 2.5).First();
                c.Kind = pick.K; c.Rot = pick.R;
                if (prefix != null) c.Prefix = prefix;
                localUse[pick.K.Name] = localUse.GetValueOrDefault(pick.K.Name) + 1;
                return true;
            }
            return false;
        }
        foreach (var u in all)
            foreach (var c in u.Cells.Values)
            {
                bool ok = u.Gateway || c == main.Inner ? Choose(c, gateways, new string[] { null })
                    : Choose(c, kinds, new[] { c.Prefix }.Concat(type.Districts.Select(d => d.Prefix)).Append(type.Outskirt).Where(p => p != null).Distinct());
                if (!ok) { failed = u == main ? null : u; return null; }   // no fitting tile (shape missing in the district)
            }

        // place tiles
        var plan = new CityPlan { Type = type, MainFloor = main.Floor };
        var tileOf = new Dictionary<Cell, PlacedPrefab>();
        foreach (var u in all)
            foreach (var c in u.Cells.Values)
            {
                var t = new PlacedPrefab { Name = c.Kind.Name, Def = c.Kind.Def, X = u.X(c), Z = u.Z(c), Rotation = c.Rot, Y = u.Floor + c.Kind.Def.YOffset };
                plan.Tiles.Add(t);
                tileOf[c] = t;
                if (u.Gateway || c == main.Inner) plan.Gateway = t;
            }
        plan.PartFloors = units.Where(u => !u.Gateway).Select(u => u.Floor).ToList();
        plan.X0 = plan.Tiles.Min(t => t.X); plan.Z0 = plan.Tiles.Min(t => t.Z);
        plan.X1 = plan.Tiles.Max(t => t.X + Tile); plan.Z1 = plan.Tiles.Max(t => t.Z + Tile);
        (plan.CenterX, plan.CenterZ) = main.Center;
        plan.Elevated = elevatedTown;
        foreach (var c in main.Cells.Values)
            for (int z = main.Z(c) + 2; z < main.Z(c) + Tile; z += 5)
                for (int x = main.X(c) + 2; x < main.X(c) + Tile; x += 5) plan.MaxCut = Math.Max(plan.MaxCut, w.HeightAt(x, z) - (main.Floor - 0.85));
        plan.Biome = cb[CoarseIndex(plan.CenterX, plan.CenterZ)];

        var corridors = new List<(int X0, int Z0, int X1, int Z1)>(plannedCorridors);
        foreach (var (u, c, s) in open) plan.Corridors.Add(Corridor(u, c, s));
        corridors.AddRange(plan.Corridors);
        // roads between the parts: curved, following the terrain (first the districts, the gateway last)
        var roads = new List<List<Point>>(plannedRoads);
        foreach (var link in links.Where(l => !l.Direct).OrderBy(l => l.B.Gateway ? 1 : 0))
        {
            var ta = tileOf[link.CA]; var tb = tileOf[link.CB];
            var ea = Exit(ta, link.SA); var eb = Exit(tb, link.SB);
            if (ea == null || eb == null) { failed = link.B; return null; }
            var points = RouteBetween(plan.Tiles, roads, corridors, ta, tb, ea.Value, link.SA, eb.Value, link.SB);
            if (points == null) { failed = link.B; return null; }
            roads.Add(points);
            // gateway: taper the 16 m roadway at the connection to the city to 8 m with part_highway_transition (the user's hint)
            double widthB = eb.Value.Width;
            if (link.B.Gateway && Transition(tb, link.SB) is PlacedPrefab tr) { plan.Parts.Add(tr); widthB = Math.Min(widthB, ea.Value.Width); }
            plan.Roads.Add(new CityRoad(points, ea.Value.Width, widthB, ea.Value.Kind == WorldData.Gravel ? Surface.Gravel : Surface.Asphalt));
        }
        // gateway in the middle of the main part: taper on both sides
        if (main.Inner != null)
            for (int s = 0; s < 4; s++)
                if ((main.InnerMask & (1 << s)) != 0 && Transition(tileOf[main.Inner], s) is PlacedPrefab tr) plan.Parts.Add(tr);
        // gateway directly at the main part: taper likewise
        foreach (var link in links.Where(l => l.Direct && l.B.Gateway))
            if (Transition(tileOf[link.CB], link.SB) is PlacedPrefab tr) plan.Parts.Add(tr);
        foreach (var (u, c, s) in open)
        {
            var e = Exit(tileOf[c], s);
            if (e != null) plan.OpenExits.Add(new Point(e.Value.X, e.Value.Z));
        }
        foreach (var (k, v) in localUse) used[k] = used.GetValueOrDefault(k) + v;
        return plan;
    }

    /// path from exit A (side sa) to exit B (side sb): 1 m in tile A, 12 m straight out, path search, 12 m straight in
    List<Point> RouteBetween(List<PlacedPrefab> townTiles, List<List<Point>> roads, List<(int X0, int Z0, int X1, int Z1)> corridors, PlacedPrefab ta, PlacedPrefab tb, (double X, double Z, double Width, byte Kind) ea, int sa,
        (double X, double Z, double Width, byte Kind) eb, int sb)
    {
        var (ax, az) = (Dir[sa].DI, Dir[sa].DJ);
        var (bx, bz) = (Dir[sb].DI, Dir[sb].DJ);
        var start = new Point(ea.X + ax * 12, ea.Z + az * 12);
        var goal = new Point(eb.X + bx * 12, eb.Z + bz * 12);
        // blocked: tiles (+6 m), buildings and other cities, corridors to keep free in front of open exits and everything up to 20 m
        // beside already planned roads – roads must neither cross nor touch (Ninezo-Land 2026-11-07: crossing and
        // touching with height difference, gateway exit blocked by another road)
        var near = new HashSet<(int, int)>();
        foreach (var r in roads)
            for (int i = 0; i + 1 < r.Count; i++)
            {
                double len = (r[i + 1] - r[i]).Length;
                for (double t = 0; t <= len; t += 4)
                {
                    double x = r[i].X + (r[i + 1].X - r[i].X) * t / Math.Max(len, 1e-9), z = r[i].Y + (r[i + 1].Y - r[i].Y) * t / Math.Max(len, 1e-9);
                    for (int dz = -5; dz <= 5; dz++) for (int dx = -5; dx <= 5; dx++) if (dx * dx + dz * dz <= 25) near.Add(((int)Math.Floor(x / 4) + dx, (int)Math.Floor(z / 4) + dz));
                }
            }
        bool Blocked(double x, double z)
        {
            foreach (var t in townTiles)
                if (x > t.X - 6 && x < t.X + Tile + 6 && z > t.Z - 6 && z < t.Z + Tile + 6) return true;
            foreach (var (x0, z0, x1, z1, maxGap) in blocked)
                if (maxGap == int.MaxValue && x > x0 - 6 && x < x1 + 6 && z > z0 - 6 && z < z1 + 6) return true;
            foreach (var (x0, z0, x1, z1) in corridors)
                if (x > x0 && x < x1 && z > z0 && z < z1) return true;
            return near.Contains(((int)Math.Floor(x / 4), (int)Math.Floor(z / 4)));
        }
        // near the tiles the terrain lies at their ground height after shaping (transition about 40 m): so the path search sees the
        // real height difference between the parts. Between two parts weighted mixed – with the nearest tile alone
        // the height jumped in the middle of the gap, and the path search drove a loop that crossed itself (Ninezo-Land)
        double Adjust(double x, double z, double terrain)
        {
            double dmin = 40, sw = 0, sh = 0;
            foreach (var t in townTiles)
            {
                double dx = Math.Max(0, Math.Max(t.X - x, x - (t.X + Tile))), dz = Math.Max(0, Math.Max(t.Z - z, z - (t.Z + Tile)));
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d >= 60) continue;
                double wgt = 1 / ((d + 5) * (d + 5));
                sw += wgt; sh += wgt * t.TerrainHeight;
                dmin = Math.Min(dmin, d);
            }
            if (sw == 0 || dmin >= 40) return terrain;
            double floor = sh / sw, s = dmin / 40;
            s = s * s * (3 - 2 * s);
            return floor + (terrain - floor) * s;
        }
        var path = RoadRouter.Route(w, start, RoadRouter.DirIndex(ax, az), goal, RoadRouter.DirIndex(-bx, -bz), spec.RoadGrade, Blocked, null, Adjust);
        if (path == null) return null;
        // plausible? Long enough for the height difference (otherwise too steep), not absurdly long (detour around half cities)
        double length = 24;
        for (int i = 0; i + 1 < path.Count; i++) length += (path[i + 1] - path[i]).Length;
        double drop = Math.Abs(ta.TerrainHeight - tb.TerrainHeight), direct = (goal - start).Length + 24;
        if (length < drop / spec.RoadGrade + 8) return null;
        if (length > Math.Max(3 * direct, drop / spec.RoadGrade * 2.5) + 150) return null;
        // the path must not cross or touch itself (two pieces closer than 22 m that lie > 50 m apart along the road)
        var dense = new List<(Point P, double D)>();
        double run = 0;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            double len = (path[i + 1] - path[i]).Length;
            for (double t = 0; t < len; t += 2) dense.Add((path[i] + (path[i + 1] - path[i]) * (t / len), run + t));
            run += len;
        }
        for (int i = 0; i < dense.Count; i++)
            for (int j = i + 1; j < dense.Count; j++)
                if (dense[j].D - dense[i].D > 50 && (dense[j].P - dense[i].P).LengthSquared < 22 * 22) return null;
        var points = new List<Point> { new(ea.X - ax, ea.Z - az) };
        points.AddRange(path);
        points.Add(new Point(eb.X - bx, eb.Z - bz));
        return points;
    }

    bool WaterAround(PlacedPrefab t)
    {
        for (int z = t.Z - 20; z <= t.Z + Tile + 20; z += 8)
            for (int x = t.X - 20; x <= t.X + Tile + 20; x += 8)
                if (w.WaterAt(x, z)) return true;
        return false;
    }

    /// earthwork under and beside a tile within limits? (height difference to the terrain: on average at most 6 m, nowhere
    /// more than 16 m in the tile and 22 m within 20 m – otherwise steep walls arise at the tile edge);
    /// up to 45 m beside it at most 22 m + 0.4 per meter distance above 20 m (no tile at the foot of a rock wall – the road out
    /// would have to cut deep, Fidife-Land 2026-11-07: 16 m cut, steps up to 9 m)
    /// big = leveled main part on a hill: on average up to 10 m, in the tile up to 24 m (removal), beside it 30 m + 0.6 per
    /// meter above 20 m (the slope falls away outwards)
    /// level 0 normal, 1 hill (leveled), 2 terrace in the mountains (cut into the slope)
    bool EarthworkOk(PlacedPrefab t, int level = 0)
    {
        double g = t.TerrainHeight, sum = 0;
        double inMax = level switch { 2 => 34, 1 => 24, _ => 16 }, ringMax = level switch { 2 => 42, 1 => 30, _ => 22 };
        double ringSlope = level switch { 2 => 0.8, 1 => 0.6, _ => 0.4 }, meanMax = level switch { 2 => 14, 1 => 10, _ => 6 };
        int m = 0;
        for (int z = t.Z - 45; z <= t.Z + Tile + 45; z += 5)
            for (int x = t.X - 45; x <= t.X + Tile + 45; x += 5)
            {
                double d = Math.Abs(w.HeightAt(x, z) - g);
                if (t.Contains(x, z))
                {
                    if (d > inMax) return false;
                    sum += d; m++;
                    continue;
                }
                double ex = Math.Max(0, Math.Max(t.X - x, x - (t.X + Tile))), ez = Math.Max(0, Math.Max(t.Z - z, z - (t.Z + Tile)));
                double dist = Math.Sqrt(ex * ex + ez * ez);
                if (dist <= 45 && d > ringMax + Math.Max(0, dist - 20) * ringSlope) return false;
            }
        return sum / m <= meanMax;
    }

    static int Bits(int m) => ((m & 1) + ((m >> 1) & 1) + ((m >> 2) & 1) + ((m >> 3) & 1));

    bool Avoids(string a, string b) => rules.Avoided.Any(p => p.A.Equals(a, StringComparison.OrdinalIgnoreCase) && p.B.Equals(b, StringComparison.OrdinalIgnoreCase)
                                                          || p.A.Equals(b, StringComparison.OrdinalIgnoreCase) && p.B.Equals(a, StringComparison.OrdinalIgnoreCase));

    string Weighted(List<(string Prefix, double Weight)> list)
    {
        double sum = list.Sum(d => d.Weight), v = rnd.NextDouble() * sum;
        foreach (var (p, wt) in list) { v -= wt; if (v <= 0) return p; }
        return list[^1].Prefix;
    }

    double MeanHeight(int x0, int z0)
    {
        double s = 0;
        int m = 0;
        for (int z = z0 + 2; z < z0 + Tile; z += 5)
            for (int x = x0 + 2; x < x0 + Tile; x += 5) { s += w.HeightAt(x, z); m++; }
        return s / m;
    }

    /// exit of a tile at one side: middle of the tile road exactly at the edge, width and surface
    (double X, double Z, double Width, byte Kind)? Exit(PlacedPrefab t, int side)
    {
        var local = w.TileSurface(t, out _);
        if (local == null) return null;
        int sw = t.SizeX, sd = t.SizeZ;
        int len = side % 2 == 0 ? sw : sd;
        byte At(int k) => side switch { 0 => local[k], 2 => local[k + (sd - 1) * sw], 3 => local[k * sw], _ => local[(sw - 1) + k * sw] };
        int c = len / 2, a = At(c) != 0 ? c : c - 1;
        if (a < 0 || At(a) == 0) return null;
        byte kind = At(a);
        int lo = a, hi = a;
        while (lo > 0 && At(lo - 1) == kind) lo--;
        while (hi < len - 1 && At(hi + 1) == kind) hi++;
        double mid = (lo + hi + 1) / 2.0;
        var (x, z) = side switch
        {
            0 => (t.X + mid, (double)t.Z),
            2 => (t.X + mid, (double)(t.Z + sd)),
            3 => ((double)t.X, t.Z + mid),
            _ => ((double)(t.X + sw), t.Z + mid),
        };
        return (x, z, hi - lo + 1, kind);
    }

    // ---------- Terrain ----------

    /// shape the terrain of a city: tiles level at their ground height, in between and outside softly into the terrain. What is calculated is the
    /// change (Laplace fill on a 4 m grid: fixed in the tiles, 0 from distance D and at roads/buildings/water) so that the
    /// terrain keeps its form; the last 6 m in front of a tile run exactly to its height. Returns changed height points.
    public static int ShapeTerrain(WorldData w, WorldEdits e, CityPlan plan)
    {
        var tiles = plan.Tiles;
        // largest height difference at the edge determines the width of the transition outwards
        double maxDiff = 0;
        foreach (var t in tiles)
            for (int k = 0; k <= Tile; k += 5)
                foreach (var (x, z) in new[] { (t.X + k, t.Z - 1), (t.X + k, t.Z + Tile), (t.X - 1, t.Z + k), (t.X + Tile, t.Z + k) })
                    if (!tiles.Any(o => o.Contains(x, z))) maxDiff = Math.Max(maxDiff, Math.Abs(w.HeightAt(x, z) - t.TerrainHeight));
        double reach = Math.Clamp(2.2 * maxDiff + 8, 16, 90);
        const int c = 4;
        int bx0 = plan.X0 - (int)reach - 2 * c, bz0 = plan.Z0 - (int)reach - 2 * c;
        int gw = (plan.X1 - plan.X0 + 2 * ((int)reach + 2 * c)) / c + 1, gh = (plan.Z1 - plan.Z0 + 2 * ((int)reach + 2 * c)) / c + 1;
        var others = w.Prefabs.Where(p => p.Def != null && !tiles.Contains(p) && p.X < bx0 + gw * c && p.X + p.SizeX > bx0 && p.Z < bz0 + gh * c && p.Z + p.SizeZ > bz0).ToList();
        PlacedPrefab TileAt(double x, double z) => tiles.FirstOrDefault(t => t.Contains(x, z));
        (double D, PlacedPrefab T) Nearest(double x, double z)
        {
            double best = double.MaxValue;
            PlacedPrefab bt = null;
            foreach (var t in tiles)
            {
                double dx = Math.Max(0, Math.Max(t.X - x, x - (t.X + Tile))), dz = Math.Max(0, Math.Max(t.Z - z, z - (t.Z + Tile)));
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d < best) { best = d; bt = t; }
            }
            return (best, bt);
        }
        bool Protected(int x, int z) => !w.Contains(x, z) || w.RoadAt(x, z) != 0 || w.WaterAt(x, z) || others.Any(o => o.Contains(x + 0.5, z + 0.5));

        var delta = new float[gw * gh];
        var free = new bool[gw * gh];
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx;
                double x = bx0 + gx * c + 0.5, z = bz0 + gz * c + 0.5;
                var t = TileAt(x, z);
                if (t != null) { delta[g] = (float)(t.TerrainHeight - w.HeightAt((int)x, (int)z)); continue; }
                if (gx == 0 || gz == 0 || gx == gw - 1 || gz == gh - 1) continue;
                if (Nearest(x, z).D >= reach || Protected((int)x, (int)z)) continue;
                free[g] = true;
            }
        var cells = Enumerable.Range(0, gw * gh).Where(g => free[g]).ToArray();
        for (int it = 0; it < 3000; it++)
        {
            float maxChange = 0;
            foreach (int g in cells)
            {
                float v = (delta[g - 1] + delta[g + 1] + delta[g - gw] + delta[g + gw]) / 4;
                float nv = delta[g] + 1.9f * (v - delta[g]);
                maxChange = Math.Max(maxChange, Math.Abs(nv - delta[g]));
                delta[g] = nv;
            }
            if (maxChange < 0.002f) break;
        }

        int changed = 0;
        int x0 = bx0 + c, z0 = bz0 + c, x1 = bx0 + (gw - 1) * c, z1 = bz0 + (gh - 1) * c;
        for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++)
            {
                if (!w.Contains(x, z)) continue;
                double px = x + 0.5, pz = z + 0.5;
                int i = w.Index(x, z);
                double hNew;
                var t = TileAt(px, pz);
                if (t != null) hNew = t.GroundHeight;   // like when placing a tile (the game levels the area itself)
                else
                {
                    if (Protected(x, z)) continue;
                    var (d, nt) = Nearest(px, pz);
                    if (d >= reach) continue;
                    // change bilinear from the 4 m grid
                    double fx = (px - 0.5 - bx0) / c, fz = (pz - 0.5 - bz0) / c;
                    int ix = Math.Clamp((int)Math.Floor(fx), 0, gw - 2), iz = Math.Clamp((int)Math.Floor(fz), 0, gh - 2);
                    double ax = fx - ix, az = fz - iz;
                    double dv = (delta[iz * gw + ix] * (1 - ax) + delta[iz * gw + ix + 1] * ax) * (1 - az) + (delta[(iz + 1) * gw + ix] * (1 - ax) + delta[(iz + 1) * gw + ix + 1] * ax) * az;
                    double fade = Math.Clamp((reach - d) / 12, 0, 1);
                    dv *= fade * fade * (3 - 2 * fade);   // softly to 0 towards the outer edge
                    if (Math.Abs(dv) < 0.01 && d > 6) continue;
                    hNew = w.Heights[i] / 256.0 + dv;
                    if (d < 6)
                    {
                        double s = d / 6;
                        s = s * s * (3 - 2 * s);
                        hNew = nt.TerrainHeight + (hNew - nt.TerrainHeight) * s;
                    }
                }
                var hv = (ushort)Math.Clamp(Math.Round(hNew * 256), 0, 65535);
                if (hv != w.Heights[i]) { e.SetHeight(i, hv); changed++; }
            }
        return changed;
    }

    /// even out steps beside the city roads: if two pieces of a road (hairpin on the slope) or two roads with
    /// different height run close beside each other, the slope of one meets that of the other, and a
    /// step remains in between (Zumoza-Land: up to 6 m). All cells up to 30 m beside a road with more than 1.5 m height difference to the neighbor
    /// (and 5 m around that) are filled from their surroundings (Laplace); roads, tiles, buildings and water stay fixed.
    public static int SmoothSteps(WorldData w, WorldEdits e, CityPlan plan) => SmoothSteps(w, e, plan.X0, plan.Z0, plan.X1, plan.Z1, 110);

    /// the same for a rectangle (x0..x1, z0..z1) plus margin pad (e.g. around a gravel path of the wilderness POIs)
    public static int SmoothSteps(WorldData w, WorldEdits e, int x0, int z0, int x1, int z1, int pad)
    {
        int gx0 = x0 - pad, gz0 = z0 - pad, gw = x1 - x0 + 2 * pad, gh = z1 - z0 + 2 * pad;
        int n = gw * gh;
        var fixedCell = new bool[n];
        var road = new bool[n];
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int x = gx0 + gx, z = gz0 + gz, g = gz * gw + gx;
                bool edge = gx == 0 || gz == 0 || gx == gw - 1 || gz == gh - 1;
                road[g] = w.RoadAt(x, z) != 0;
                fixedCell[g] = edge || !w.Contains(x, z) || road[g] || w.WaterAt(x, z);
            }
        foreach (var p in w.Prefabs.Where(p => p.Def != null && p.X < gx0 + gw && p.X + p.SizeX > gx0 && p.Z < gz0 + gh && p.Z + p.SizeZ > gz0))
            for (int z = Math.Max(p.Z - 2, gz0); z < Math.Min(p.Z + p.SizeZ + 2, gz0 + gh); z++)
                for (int x = Math.Max(p.X - 2, gx0); x < Math.Min(p.X + p.SizeX + 2, gx0 + gw); x++) fixedCell[(z - gz0) * gw + (x - gx0)] = true;
        var nearRoad = GroundShaper.Distance(road, gw, gh);
        var mark = new bool[n];
        int count = 0;
        for (int gz = 0; gz < gh - 1; gz++)
            for (int gx = 0; gx < gw - 1; gx++)
            {
                int g = gz * gw + gx;
                if (fixedCell[g] || nearRoad[g] > 30) continue;
                double h = w.HeightAt(gx0 + gx, gz0 + gz);
                if ((!fixedCell[g + 1] && Math.Abs(h - w.HeightAt(gx0 + gx + 1, gz0 + gz)) > 1.5) || (!fixedCell[g + gw] && Math.Abs(h - w.HeightAt(gx0 + gx, gz0 + gz + 1)) > 1.5))
                { mark[g] = true; count++; }
            }
        if (count == 0) return 0;
        var dist = GroundShaper.Distance(mark, gw, gh);
        var free = new bool[n];
        for (int g = 0; g < n; g++) free[g] = dist[g] <= 5 && !fixedCell[g];
        return GroundShaper.FillNatural(w, e, gx0, gz0, gw, gh, free, 0, (_, _) => false).Changed;
    }

    /// plan a road between two parts as a road-brush road (after the tiles stand and the terrain is shaped):
    /// follows the terrain with at most grade gradient, 4 m horizontal at every tile edge (automatic), width like the exits
    public static RoadResult PlanRoad(WorldData w, CityRoad road, double grade)
    {
        var s = road.Surface == Surface.Gravel ? RoadSettings.Gravel() : RoadSettings.Asphalt();
        s.Width = Math.Max(road.WidthA, road.WidthB);
        s.Shoulder = road.Surface == Surface.Asphalt ? 1 : 0;
        s.MaxGrade = grade;
        s.Smooth = 40;
        s.Blend = 10;
        var widths = road.WidthA == road.WidthB ? null : road.Points.Select((_, i) => i == 0 ? road.WidthA : i == road.Points.Count - 1 ? road.WidthB : (double?)null).ToList();
        if (widths != null) s.Width = road.WidthA;   // width of the intermediate points: transition from A to B
        return RoadPlanner.Compute(w, road.Points, s, widths: widths);
    }
}
