using System.Text.RegularExpressions;

namespace MapEditor.Map;

/// what is wrong with an entry of prefabs.xml in the installed game
public enum CheckProblem
{
    /// the game has no description of this name (e.g. a 3.4 building in a 2.6 game, or a removed one)
    Missing,
    /// the game describes it with another size than the game folder of the project (only known if both are available)
    SizeChanged,
    /// it stands on a lot of a city tile but does not fit it any more (size or district tag)
    LotMismatch,
}

/// one kind of building/tile/part with a problem, with all its copies on the map and the replacements that fit
public sealed class CheckIssue
{
    public string Name;
    public PrefabKind Kind;
    public CheckProblem Problem;
    public List<PlacedPrefab> Instances = new();
    /// description from the game folder of the project (null = unknown there too)
    public PrefabDef Old;
    /// description in the installed game (null = missing)
    public PrefabDef New;
    /// lot of the first copy that stands on a lot of a city tile (null = in the wilderness)
    public TileLot Lot;
    /// names of fitting replacements in the installed game, best first (only for buildings and fillers)
    public List<string> Candidates = new();
    public bool CanReplace => Kind is PrefabKind.Poi or PrefabKind.Filler;
}

public sealed class CheckResult
{
    public GameProfile Project, Target;
    public string TargetFolder;
    public List<CheckIssue> Issues = new();
    public int Count(PrefabKind k) => Issues.Where(i => i.Kind == k).Sum(i => i.Instances.Count);
}

/// Project check (the user's request 2026-11-13): instead of keeping data of other game versions, YAMEE shows what of a project does
/// not exist in the installed game (most players always have the newest version) and offers replacements that may, but need not,
/// be used. Measured: 2.6 (+ Undead Legacy) projects in 3.4: 0–1 missing buildings; the 3.4 map East Nipabu in 2.6 + UL:
/// 204 entries (133 buildings, 12 fillers, 42 parts, 17 tiles), 110 of the buildings on lots of city tiles.
public static class ProjectCheck
{
    /// target = descriptions in the installed game for all names on the map (PrefabCatalog.Load of the installed game folder);
    /// index = all buildings and fillers of the installed game (name → description) for the replacements, asked for only when needed
    public static CheckResult Run(WorldData w, Dictionary<string, PrefabDef> target, Func<Dictionary<string, PrefabDef>> index)
    {
        var result = new CheckResult();
        Dictionary<string, PrefabDef> all = null;   // read only when a building has to be replaced (about 1,000 descriptions)
        foreach (var g in w.Prefabs.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var first = g.First();
            var now = target.GetValueOrDefault(g.Key);
            var old = first.Def is { Found: true } d ? d : null;
            var kind = old?.Kind ?? now?.Kind ?? first.Def?.Kind ?? PrefabKind.Poi;
            CheckIssue issue = null;
            if (now == null || !now.Found)
                issue = new CheckIssue { Problem = CheckProblem.Missing };
            else if (old != null && kind != PrefabKind.Part && (old.SizeX != now.SizeX || old.SizeZ != now.SizeZ))
                issue = new CheckIssue { Problem = CheckProblem.SizeChanged };
            else if (kind is PrefabKind.Poi or PrefabKind.Filler && g.Any(p => LotOf(w, p) is TileLot lot ? !TileLots.Fits(now, lot) : TileLots.Under(w, p) != null))
                // on a lot that it does not fit, or inside a lot without standing on its corner (the tile has other lots in this version)
                issue = new CheckIssue { Problem = CheckProblem.LotMismatch };
            if (issue == null) continue;
            issue.Name = first.Name;
            issue.Kind = kind;
            issue.Instances = g.ToList();
            issue.Old = old;
            issue.New = now is { Found: true } ? now : null;
            issue.Lot = issue.Instances.Select(p => LotOf(w, p) ?? (issue.New != null ? TileLots.Under(w, p) : null)).FirstOrDefault(l => l != null);
            if (issue.CanReplace) issue.Candidates = Candidates(issue, all ??= index());
            result.Issues.Add(issue);
        }
        result.Issues = result.Issues.OrderBy(i => i.Kind switch { PrefabKind.Poi => 0, PrefabKind.Filler => 1, PrefabKind.Tile => 2, _ => 3 })
            .ThenBy(i => i.Problem).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    /// lot of a city tile on which a building stands: the game puts buildings exactly at the smallest corner of the lot,
    /// so this works without the size of the building (which is unknown for a missing one)
    public static TileLot LotOf(WorldData w, PlacedPrefab p)
    {
        if (p.IsTile || p.Def?.Kind == PrefabKind.Part) return null;
        TileLot first = null;
        foreach (var t in w.Prefabs)
        {
            // lots may reach a little over the edge of the tile (Old West), and several lots can share a corner:
            // the one the building fits wins
            if (!t.IsTile || t.Def is not { Found: true } || p.X < t.X - 20 || p.Z < t.Z - 20 || p.X >= t.X + t.SizeX + 20 || p.Z >= t.Z + t.SizeZ + 20) continue;
            foreach (var lot in TileLots.Of(t))
                if (lot.X == p.X && lot.Z == p.Z)
                {
                    if (p.Def is not { Found: true } || TileLots.Fits(p.Def, lot)) return lot;
                    first ??= lot;
                }
        }
        return first;
    }

    /// name without the number at the end: "army_camp_10" → "army_camp", "house_modern_34b" → "house_modern"
    public static string Base(string name) => Regex.Replace(name.ToLowerInvariant(), @"(_\d+[a-z]?)+$", "");

    /// replacements: on a lot every building of the game that fits it exactly (like "Swap"); in the wilderness the most similar ones
    /// (same name without number, same group, similar size and tier) – at most 40
    public static List<string> Candidates(CheckIssue issue, IReadOnlyDictionary<string, PrefabDef> index)
    {
        string baseName = Base(issue.Name), group = PrefabCategories.Of(issue.Name).Key;
        var options = index.Where(kv => !kv.Key.Equals(issue.Name, StringComparison.OrdinalIgnoreCase) && kv.Value.Found
            && kv.Value.Kind is PrefabKind.Poi or PrefabKind.Filler);
        if (issue.Lot != null)
            return options.Where(kv => TileLots.Fits(kv.Value, issue.Lot))
                .OrderBy(kv => Base(kv.Key) == baseName ? 0 : 1).ThenBy(kv => Math.Abs(kv.Value.Tier - (issue.Old?.Tier ?? kv.Value.Tier)))
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key).ToList();
        // same kind (filler for filler); with a known old description: similar size
        bool filler = issue.Kind == PrefabKind.Filler;
        return options.Where(kv => (kv.Value.Kind == PrefabKind.Filler) == filler)
            .Select(kv => (kv.Key, Score: (Base(kv.Key) == baseName ? 0 : 1000) + (PrefabCategories.Of(kv.Key).Key == group ? 0 : 300)
                + (issue.Old != null ? Math.Abs(kv.Value.SizeX - issue.Old.SizeX) + Math.Abs(kv.Value.SizeZ - issue.Old.SizeZ) + 5 * Math.Abs(kv.Value.Tier - issue.Old.Tier) : 0)))
            .OrderBy(c => c.Score).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Take(40).Select(c => c.Key).ToList();
    }

    /// new building in place of an old one: on a lot exactly like the game (TileLots.Place); otherwise same rotation and
    /// the same center (if the old size is known), else the same smallest corner; height from the terrain like when placing
    public static PlacedPrefab Replacement(WorldData w, PlacedPrefab old, TileLot lot, string name, PrefabDef def)
    {
        if (lot != null) return TileLots.Place(lot, name, def);
        var p = new PlacedPrefab { Name = name, Def = def, Rotation = old.Rotation, X = old.X, Z = old.Z };
        if (old.Def is { Found: true })
        {
            p.X = (int)Math.Round(old.X + old.SizeX / 2.0 - p.SizeX / 2.0);
            p.Z = (int)Math.Round(old.Z + old.SizeZ / 2.0 - p.SizeZ / 2.0);
        }
        p.X = Math.Clamp(p.X, -w.Half, w.Half - p.SizeX);
        p.Z = Math.Clamp(p.Z, -w.Half, w.Half - p.SizeZ);
        double mean = GroundShaper.AverageHeight(w, p.X, p.Z, p.SizeX, p.SizeZ);
        p.Y = (int)Math.Round(mean + 0.85) + def.YOffset;
        return p;
    }

    /// parts that belong to an old building: those named in its markers (known description), otherwise driveways and walkways
    /// on its footprint or lot (+1 m) – for a missing building the game folder knows neither size nor markers
    public static List<PlacedPrefab> OldParts(WorldData w, PlacedPrefab old, TileLot lot)
    {
        if (old.Def is { Found: true }) return PrefabParts.Attached(w, old);
        int x0 = lot?.X ?? old.X, z0 = lot?.Z ?? old.Z;
        int x1 = lot != null ? lot.X + lot.Width : old.X + old.SizeX, z1 = lot != null ? lot.Z + lot.Depth : old.Z + old.SizeZ;
        return w.Prefabs.Where(q => q.Def?.Kind == PrefabKind.Part
            && (q.Name.Contains("driveway", StringComparison.OrdinalIgnoreCase) || q.Name.Contains("walkway", StringComparison.OrdinalIgnoreCase))
            && q.X >= x0 - 1 && q.Z >= z0 - 1 && q.X < x1 + 1 && q.Z < z1 + 1).ToList();
    }
}
