namespace MapEditor.Map;

/// what "Replace everything automatically" did
public sealed class ConvertReport
{
    public int Tiles, LotBuildings, WildBuildings, PartsRemoved, Kept;
    public List<string> Notes = new();
    public int Total => Tiles + LotBuildings + WildBuildings + PartsRemoved;
}

/// Convert a project to the installed game (the user's request 2026-11-20: a 3.4 project for 2.6 with Undead Legacy; everything
/// that is missing or does not fit is replaced automatically). Runs inside one editing step and works only with the current state
/// of the world, so redo/replay gives the same result (all choices are seeded by position).
/// Order: (1) missing city tiles → a tile of the installed game with the same district and shape whose road exits fit the neighbors
/// and the roads around (the old tile's size and exits are unknown: tiles are 150 × 150, the exits come from the neighbor tiles and
/// the road surface at the middle of each side); its lots are built up again (old building kept when it fits the same lot).
/// (2) buildings and fillers that are missing or do not fit their lot → on a lot a fitting building like the game (TileLots.Fits),
/// in the wilderness the most similar one (ProjectCheck.Candidates). (3) parts that are missing → removed.
public static class ProjectConvert
{
    public const int TileSize = 150;

    // ---------- file format of the world (main.ttw, map_info.xml, yamee_project.json) ----------

    /// files made by the game from the map (test of the old version): stale after converting, the game makes them again
    public static readonly string[] GameMadeFiles = { "dtm_processed.raw", "splat3_processed.png", "splat4_processed.png", "splat3_half.png", "splat4_half.png", "checksums.txt" };

    /// write main.ttw and map_info.xml in the format of the target version and mark the project for the target profile
    /// (the user's request 2026-11-20: a 3.4 project loaded in 2.6 but behaved oddly). main.ttw = template of the target version
    /// (a world of that version in gameWorlds, otherwise built in) with the old ID; map_info.xml keeps all values.
    /// backupDir = folder for the game-made files of the old version (moved, not deleted); returns what was changed
    public static List<string> ConvertFormat(string folder, GameProfile target, string gameWorlds, string backupDir)
    {
        var done = new List<string>();
        if (target?.Version == null) return done;
        var v = target.Version;
        string ttwFile = Path.Combine(folder, "main.ttw");
        byte[] old = File.Exists(ttwFile) ? File.ReadAllBytes(ttwFile) : null;
        var oldVersion = GameVersion.Parse(GameVersion.TtwVersionText(old));
        if (oldVersion == null || oldVersion.Short != v.Short || oldVersion.Build != v.Build)
        {
            var (ttw, _) = WorldGenerator.TemplateFiles(gameWorlds, v);
            if (old != null && old.Length > 40 && old[^33] == 32) Array.Copy(old, old.Length - 32, ttw, ttw.Length - 32, 32);   // keep the ID
            File.WriteAllBytes(ttwFile, ttw);
            done.Add("main.ttw");
        }
        string infoFile = Path.Combine(folder, "map_info.xml");
        if (File.Exists(infoFile))
        {
            string text = File.ReadAllText(infoFile), now = MapInfoFor(text, v);
            if (now != null && now != text)
            {
                File.WriteAllText(infoFile, now, new System.Text.UTF8Encoding(true));
                done.Add("map_info.xml");
            }
        }
        if (backupDir != null)
            foreach (var f in GameMadeFiles)
            {
                string p = Path.Combine(folder, f);
                if (!File.Exists(p)) continue;
                Directory.CreateDirectory(backupDir);
                File.Move(p, Path.Combine(backupDir, f), true);
                done.Add(f);
            }
        ProjectInfo.Set(folder, target);
        return done;
    }

    /// map_info.xml in the format of a version: 2.x flat ("Generation.Seed"), 3.x with SchemaVersion and a class "Generation";
    /// all values are kept, GameVersion is set. null = not readable
    public static string MapInfoFor(string xml, GameVersion v)
    {
        System.Xml.Linq.XDocument doc;
        try { doc = System.Xml.Linq.XDocument.Parse(xml); } catch (Exception) { return null; }
        if (doc.Root == null) return null;
        var top = new List<(string Name, string Value)>();
        var gen = new List<(string Name, string Value)>();
        foreach (var p in doc.Root.Elements("property"))
        {
            if ((string)p.Attribute("class") == "Generation")
            {
                foreach (var q in p.Elements("property")) gen.Add(((string)q.Attribute("name"), (string)q.Attribute("value") ?? ""));
                continue;
            }
            string n = (string)p.Attribute("name");
            if (n == null) continue;
            if (n.StartsWith("Generation.", StringComparison.Ordinal)) gen.Add((n["Generation.".Length..], (string)p.Attribute("value") ?? ""));
            else if (n != "SchemaVersion") top.Add((n, (string)p.Attribute("value") ?? ""));
        }
        int gi = top.FindIndex(t => t.Name == "GameVersion");
        if (gi >= 0) top[gi] = ("GameVersion", v.MapInfo); else top.Add(("GameVersion", v.MapInfo));
        string P(string n, string val, string indent) => $"{indent}<property name=\"{System.Security.SecurityElement.Escape(n)}\" value=\"{System.Security.SecurityElement.Escape(val)}\" />\r\n";
        bool v3 = v.Major >= 3;
        var sb = new System.Text.StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<MapInfo>\r\n");
        if (v3) sb.Append(P("SchemaVersion", "1", "  "));
        foreach (var (n, val) in top)
        {
            sb.Append(P(n, val, "  "));
            // 2.x: "Generation.Seed" right after GameVersion, like the game writes it
            if (!v3 && n == "GameVersion" && gen.FirstOrDefault(g => g.Name == "Seed") is { Name: not null } seed) sb.Append(P("Generation.Seed", seed.Value, "  "));
        }
        if (v3)
        {
            if (gen.Count > 0)
            {
                sb.Append("  <property class=\"Generation\">\r\n");
                foreach (var (n, val) in gen) sb.Append(P(n, val, "    "));
                sb.Append("  </property>\r\n");
            }
        }
        else
            foreach (var (n, val) in gen.Where(g => g.Name != "Seed")) sb.Append(P("Generation." + n, val, "  "));
        sb.Append("</MapInfo>");
        return sb.ToString();
    }

    /// pois = all buildings and fillers of the installed game; tiles = all city tiles of the installed game;
    /// partDef = description of a part in the installed game (null = missing)
    public static ConvertReport Run(WorldData w, WorldEdits e, IReadOnlyDictionary<string, PrefabDef> pois,
        IReadOnlyList<(string Name, PrefabDef Def)> tiles, Func<string, PrefabDef> partDef, double bumps)
    {
        var report = new ConvertReport();
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // names placed in this run (less repetition)
        foreach (var p in w.Prefabs) used[p.Name] = used.GetValueOrDefault(p.Name) + 1;

        // (1) city tiles
        foreach (var old in w.Prefabs.Where(p => p.IsTile && p.Def is not { Found: true }).ToList())
        {
            var tile = TileFor(w, old, tiles);
            if (tile == null) { report.Kept++; report.Notes.Add(old.Name + ": no city tile of the installed game fits"); continue; }
            ReplaceTile(w, e, old, tile, pois, partDef, used);
            report.Tiles++;
        }

        // (2) buildings and fillers
        foreach (var p in w.Prefabs.Where(p => !p.IsTile && p.Def?.Kind != PrefabKind.Part).ToList())
        {
            if (!w.Prefabs.Contains(p)) continue;
            bool known = p.Def is { Found: true };
            var exact = ProjectCheck.LotOf(w, p);
            var lot = exact ?? LotAround(w, p, known);
            if (lot != null)
            {
                if (known && exact != null && TileLots.Fits(p.Def, lot)) continue;   // fine
                string name = PickForLot(lot, pois, used, p.Name);
                if (name == null) { report.Kept++; report.Notes.Add($"{p.Name} {p.X},{p.Z}: no building fits the lot"); continue; }
                var b = TileLots.Place(lot, name, pois[name]);
                var oldParts = ProjectCheck.OldParts(w, p, lot);
                foreach (var q in oldParts) e.RemovePrefab(q);
                e.ReplacePrefab(p, b);
                foreach (var q in PrefabParts.For(b, partDef, lot.District)) e.AddPrefab(q);
                used[name] = used.GetValueOrDefault(name) + 1;
                report.LotBuildings++;
                continue;
            }
            if (known) continue;
            // wilderness: the most similar building (same name without number first), varied by position among the best ones
            var issue = new CheckIssue { Name = p.Name, Kind = p.Name.Contains("_filler", StringComparison.OrdinalIgnoreCase) ? PrefabKind.Filler : PrefabKind.Poi };
            var cands = ProjectCheck.Candidates(issue, pois);
            if (cands.Count == 0) { report.Kept++; report.Notes.Add($"{p.Name} {p.X},{p.Z}: nothing similar in the installed game"); continue; }
            string baseName = ProjectCheck.Base(p.Name);
            var best = cands.Where(c => ProjectCheck.Base(c) == baseName).ToList();
            if (best.Count == 0) best = cands.Take(5).ToList();
            string pick = best.OrderBy(c => used.GetValueOrDefault(c)).ThenBy(c => Hash(p.X, p.Z, c)).First();
            var nb = ProjectCheck.Replacement(w, p, null, pick, pois[pick]);
            foreach (var q in ProjectCheck.OldParts(w, p, null)) e.RemovePrefab(q);
            e.ReplacePrefab(p, nb);
            var parts = PrefabParts.For(nb, partDef, null);
            foreach (var q in parts) e.AddPrefab(q);
            GroundShaper.Flatten(w, e, nb, nb.GroundHeight, bumps);
            foreach (var q in parts) GroundShaper.Flatten(w, e, q, nb.GroundHeight, bumps);
            used[pick] = used.GetValueOrDefault(pick) + 1;
            report.WildBuildings++;
        }

        // (3) parts that the installed game does not have
        foreach (var q in w.Prefabs.Where(p => !p.IsTile && p.Def is not { Found: true } && IsPartName(p.Name)).ToList())
        {
            e.RemovePrefab(q);
            report.PartsRemoved++;
        }
        return report;
    }

    static bool IsPartName(string name) => name.StartsWith("part_", StringComparison.OrdinalIgnoreCase);

    static int Hash(int x, int z, string s)
    {
        unchecked
        {
            int h = x * 73856093 ^ z * 19349663;
            foreach (char c in s.ToLowerInvariant()) h = h * 31 + c;
            return h & 0x7FFFFFFF;
        }
    }

    // ---------- lots ----------

    /// lot of a city tile with a known description under a building that does not stand exactly on a lot corner: center of its
    /// footprint (known building) or a point just inside its corner (missing building, size unknown)
    static TileLot LotAround(WorldData w, PlacedPrefab p, bool known)
    {
        double cx = known ? p.X + p.SizeX / 2.0 : p.X + 0.5, cz = known ? p.Z + p.SizeZ / 2.0 : p.Z + 0.5;
        foreach (var t in w.Prefabs)
        {
            if (!t.IsTile || t.Def is not { Found: true } || !t.Contains(cx, cz)) continue;
            foreach (var lot in TileLots.Of(t))
                if (cx >= lot.X && cx < lot.X + lot.Width && cz >= lot.Z && cz < lot.Z + lot.Depth) return lot;
        }
        return null;
    }

    /// fitting building for a lot: the old one if it fits, otherwise one with the same name base, otherwise the least used one;
    /// traders only in place of a trader
    static string PickForLot(TileLot lot, IReadOnlyDictionary<string, PrefabDef> pois, Dictionary<string, int> used, string oldName)
    {
        if (oldName != null && pois.TryGetValue(oldName, out var same) && TileLots.Fits(same, lot)) return oldName;
        bool trader = oldName?.StartsWith("trader_", StringComparison.OrdinalIgnoreCase) == true;
        var fits = pois.Where(kv => kv.Key.StartsWith("trader_", StringComparison.OrdinalIgnoreCase) == trader && TileLots.Fits(kv.Value, lot))
            .Select(kv => kv.Key).ToList();
        if (fits.Count == 0 && trader)
            fits = pois.Where(kv => TileLots.Fits(kv.Value, lot)).Select(kv => kv.Key).ToList();
        if (fits.Count == 0) return null;
        string baseName = oldName != null ? ProjectCheck.Base(oldName) : null;
        return fits.OrderBy(n => baseName != null && ProjectCheck.Base(n) == baseName ? 0 : 1).ThenBy(n => used.GetValueOrDefault(n))
            .ThenBy(n => Hash(lot.X, lot.Z, n)).First();
    }

    // ---------- tiles ----------

    /// shape word of a tile name (number of road exits): cap, straight, corner, t, intersection; "" = unknown
    public static string Shape(string name)
    {
        var parts = name.ToLowerInvariant().Split('_');
        foreach (var s in new[] { "intersection", "t", "straight", "corner", "cap" })
            if (parts.Contains(s)) return s;
        return "";
    }

    static string District(string name) => name.Length > 9 ? name[9..].Split('_')[0].ToLowerInvariant() : "";

    /// replacement for a missing tile: candidates of the same size (150 × 150), best district + shape, rotation so that the
    /// exits meet what is around (neighbor tile exits, road surface at the middle of the sides); null = no tile at all
    public static PlacedPrefab TileFor(WorldData w, PlacedPrefab old, IReadOnlyList<(string Name, PrefabDef Def)> tiles)
    {
        var want = WantedSides(w, old);
        string district = District(old.Name), shape = Shape(old.Name);
        bool gateway = district == "gateway";
        (string Name, PrefabDef Def, int Rot, double Score) best = default;
        best.Score = double.MaxValue;
        foreach (var (name, def) in tiles)
        {
            if (def == null || !def.Found || def.SizeX != TileSize || def.SizeZ != TileSize) continue;
            if (name.Equals(old.Name, StringComparison.OrdinalIgnoreCase)) continue;
            string d = District(name), s = Shape(name);
            // other districts only as an emergency; a gateway stays a gateway (trader, highway transition), and vice versa
            if ((d == "gateway") != gateway) continue;
            // the shape in the name is certain (number of exits), the surroundings only a guess: keep district and shape first
            double tier = (d == district ? 0 : 30) + (s == shape || shape == "" ? 0 : 40);
            for (int r = 0; r < 4; r++)
            {
                var probe = new PlacedPrefab { Name = name, Def = def, X = old.X, Z = old.Z, Rotation = r };
                if (w.TileSurface(probe, out var exits) == null) continue;
                var has = new bool[4];
                foreach (var (ex, ez) in exits) { int k = SideOf(old, ex, ez); if (k >= 0) has[k] = true; }
                double score = tier;
                for (int k = 0; k < 4; k++)
                {
                    if (want[k] == 1 && !has[k]) score += 10;        // road ends at the tile without a connection
                    else if (want[k] == -1 && has[k]) score += 10;   // neighbor tile without exit there
                    else if (want[k] == 0 && has[k]) score += 2;     // exit into nothing (dead end)
                }
                score += (Hash(old.X, old.Z, name) % 1000) / 10000.0 + r / 100000.0;   // ties: varied, but fixed by position
                if (score < best.Score) best = (name, def, r, score);
            }
        }
        if (best.Name == null) return null;
        return new PlacedPrefab { Name = best.Name, Def = best.Def, X = old.X, Z = old.Z, Rotation = best.Rot, Y = (int)Math.Round(TileGround(w, old) + 0.85) + best.Def.YOffset };
    }

    /// side of a point at the edge of a 150 m tile: 0 south, 1 north, 2 west, 3 east; −1 = not at the edge
    static int SideOf(PlacedPrefab t, double x, double z)
    {
        if (Math.Abs(z - t.Z) < 1) return 0;
        if (Math.Abs(z - (t.Z + TileSize)) < 1) return 1;
        if (Math.Abs(x - t.X) < 1) return 2;
        if (Math.Abs(x - (t.X + TileSize)) < 1) return 3;
        return -1;
    }

    /// per side (south, north, west, east): 1 = an exit is needed (neighbor tile has one there, or road surface reaches the middle
    /// of the side), −1 = a neighbor tile without exit there, 0 = open land without road
    static int[] WantedSides(WorldData w, PlacedPrefab old)
    {
        var result = new int[4];
        double h = TileSize / 2.0;
        var mids = new (double X, double Z, int DX, int DZ)[]
        {
            (old.X + h, old.Z, 0, -1), (old.X + h, old.Z + TileSize, 0, 1), (old.X, old.Z + h, -1, 0), (old.X + TileSize, old.Z + h, 1, 0),
        };
        for (int k = 0; k < 4; k++)
        {
            var (mx, mz, dx, dz) = mids[k];
            var neighbor = w.Prefabs.FirstOrDefault(t => t != old && t.IsTile && t.Def is { Found: true } && t.Contains(mx + dx * 0.5, mz + dz * 0.5));
            if (neighbor != null)
            {
                w.TileSurface(neighbor, out var exits);
                result[k] = exits.Any(ex => Math.Abs(ex.X - mx) < 1.5 && Math.Abs(ex.Z - mz) < 1.5) ? 1 : -1;
                continue;
            }
            // a road leading away from the middle of the side (outside the tile; inside, sidewalks and parking lots of the old tile
            // also reach the edge): rows 2–12 m out with surface in the middle, but not a road running along the side
            bool Road(double x, double z)
            {
                int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
                return w.Contains(ix, iz) && w.RoadMask[w.Index(ix, iz)] != 0;
            }
            int rows = 0, along = 0;
            for (int d = 2; d <= 12; d++)
            {
                bool hit = false;
                for (int s = -3; s <= 3 && !hit; s++) hit = Road(mx + dx * d + (dx == 0 ? s : 0), mz + dz * d + (dz == 0 ? s : 0));
                if (!hit) continue;
                rows++;
                if (Road(mx + dx * d + (dx == 0 ? 22 : 0), mz + dz * d + (dz == 0 ? 22 : 0)) && Road(mx + dx * d - (dx == 0 ? 22 : 0), mz + dz * d - (dz == 0 ? 22 : 0))) along++;
            }
            result[k] = rows >= 8 && along < 4 ? 1 : 0;
        }
        return result;
    }

    /// ground height of a missing tile (its YOffset is unknown): neighbor tiles of the same town share the floor,
    /// otherwise the median of the terrain inside (YAMEE levels it under its tiles)
    static double TileGround(WorldData w, PlacedPrefab old)
    {
        var near = w.Prefabs.Where(t => t != old && t.IsTile && t.Def is { Found: true }
            && (t.X == old.X + TileSize || t.X + t.SizeX == old.X) && t.Z < old.Z + TileSize && t.Z + t.SizeZ > old.Z
            || t != old && t.IsTile && t.Def is { Found: true } && (t.Z == old.Z + TileSize || t.Z + t.SizeZ == old.Z) && t.X < old.X + TileSize && t.X + t.SizeX > old.X)
            .Select(t => t.GroundHeight).OrderBy(v => v).ToList();
        if (near.Count > 0) return near[near.Count / 2];
        var hs = new List<double>();
        for (int z = old.Z + 4; z < old.Z + TileSize - 4; z += 4)
            for (int x = old.X + 4; x < old.X + TileSize - 4; x += 4)
                if (w.Contains(x, z)) hs.Add(w.HeightAt(x, z));
        hs.Sort();
        return hs.Count > 0 ? hs[hs.Count / 2] : 37.15;
    }

    /// swap a missing tile: everything inside goes, the new tile gets its surface, the lots are built up again
    /// (the old building stays when it stood on the same corner and fits the new lot)
    static void ReplaceTile(WorldData w, WorldEdits e, PlacedPrefab old, PlacedPrefab tile, IReadOnlyDictionary<string, PrefabDef> pois,
        Func<string, PrefabDef> partDef, Dictionary<string, int> used)
    {
        var area = new PlacedPrefab { Name = old.Name, Def = new PrefabDef { SizeX = TileSize, SizeZ = TileSize, Kind = PrefabKind.Tile, Found = true }, X = old.X, Y = old.Y, Z = old.Z };
        var inside = w.Prefabs.Where(q => q != old && !q.IsTile && area.Contains(q.X + (q.Def is { Found: true } ? q.SizeX / 2.0 : 0.5), q.Z + (q.Def is { Found: true } ? q.SizeZ / 2.0 : 0.5))).ToList();
        var oldAt = inside.Where(q => q.Def?.Kind != PrefabKind.Part).GroupBy(q => (q.X, q.Z)).ToDictionary(g => g.Key, g => g.First().Name);
        e.RemoveTileSurface(area);
        foreach (var q in inside) e.RemovePrefab(q);
        e.ReplacePrefab(old, tile);
        e.AddTileSurface(tile);
        foreach (var lot in TileLots.Of(tile))
        {
            string name = PickForLot(lot, pois, used, oldAt.GetValueOrDefault((lot.X, lot.Z)));
            if (name == null) continue;
            var b = TileLots.Place(lot, name, pois[name]);
            e.AddPrefab(b);
            foreach (var q in PrefabParts.For(b, partDef, lot.District)) e.AddPrefab(q);
            used[name] = used.GetValueOrDefault(name) + 1;
        }
    }
}
