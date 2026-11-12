namespace MapEditor.Map;

/// Parts of a building (mainly driveways) as the game sets them when generating the world from the "PartSpawn" markers
/// (the user's request: driveways, so that roads can connect). Checked on the original world (2026-10-29, 2,905 parts):
/// position = rectangle of the marker rotated like the building, smallest corner (2,464 exact); rotation = building + marker + 2
/// (2,485); height = ground height of the building + YOffset of the part, i.e. y = building y − building YOffset + part YOffset (2,619).
/// Several markers of the same group are alternatives: one per group is set (chosen deterministically from the position).
public static class PrefabParts
{
    /// parts for building p at its position; defOf returns the description of a part (null = unknown, is skipped)
    public static List<PlacedPrefab> For(PlacedPrefab p, Func<string, PrefabDef> defOf)
    {
        var list = new List<PlacedPrefab>();
        if (p.Def?.Parts == null || p.Def.Parts.Count == 0) return list;
        int index = 0;
        var groups = p.Def.Parts.Select(m => (m, key: string.IsNullOrEmpty(m.Group) ? "#" + index++ : m.Group)).GroupBy(t => t.key);
        foreach (var g in groups)
        {
            var options = g.Select(t => t.m).ToList();
            var m = options[Pick(p, g.Key, options.Count)];
            // probability of the marker (POIMarkerPartSpawnChance, e.g. four concert signs at 25 % each): rolled deterministically
            // from the position – before, all four signs stood on top of each other (2026-11-09)
            // (only for groups with one marker; with alternatives one is chosen anyway)
            if (options.Count == 1 && m.Chance < 1 && Pick(p, g.Key + "/c", 1000) >= m.Chance * 1000) continue;
            string name = m.PartNames[Pick(p, g.Key + "/n", m.PartNames.Length)];
            var def = defOf(name);
            if (def == null) continue;
            var (ax, az) = PlacedPrefab.Rotate(m.X, m.Z, p.Rotation, p.Def.SizeX, p.Def.SizeZ);
            var (bx, bz) = PlacedPrefab.Rotate(m.X + m.SizeX, m.Z + m.SizeZ, p.Rotation, p.Def.SizeX, p.Def.SizeZ);
            list.Add(new PlacedPrefab
            {
                Name = name,
                Def = def,
                X = p.X + (int)Math.Round(Math.Min(ax, bx)),
                Z = p.Z + (int)Math.Round(Math.Min(az, bz)),
                Rotation = ((p.Rotation + m.Rotation + 2) % 4 + 4) % 4,
                // parts without their own YOffset (0) lie in the game like YOffset -1 (original world, 2,700 driveways: all the same,
                // before, e.g. part_driveway_countrytown_12 and _13 stood 1 m too high, 2026-11-11)
                Y = p.Y - p.Def.YOffset + Math.Min(def.YOffset, -1),
            });
        }
        return list;
    }

    /// parts that belong to a building: on the footprint or at most 1 m beside it and named in its markers
    /// (in city tiles there are also parts of the tile beside it such as lamps and backyards – those stay)
    public static List<PlacedPrefab> Attached(WorldData w, PlacedPrefab p)
    {
        if (p.Def == null || p.Def.Kind == PrefabKind.Part) return new List<PlacedPrefab>();
        var own = new HashSet<string>(p.Def.Parts.SelectMany(m => m.PartNames), StringComparer.OrdinalIgnoreCase);
        return w.Prefabs.Where(q => q != p && q.Def?.Kind == PrefabKind.Part && own.Contains(q.Name)
            && q.X < p.X + p.SizeX + 1 && q.X + q.SizeX > p.X - 1 && q.Z < p.Z + p.SizeZ + 1 && q.Z + q.SizeZ > p.Z - 1).ToList();
    }

    /// driveway (part with "driveway" in its name)
    public static bool IsDriveway(PlacedPrefab q) => q.Def?.Kind == PrefabKind.Part && q.Name.Contains("driveway", StringComparison.OrdinalIgnoreCase);

    /// snap point of a driveway: center of the edge that lies farthest from the building. In the original world (2026-10-29)
    /// the road ends at most 2 m beside this point for 477 of 479 driveways (buildings like diner_01 have no RoadExit themselves).
    public static (double X, double Z) DrivewayExit(PlacedPrefab q, PlacedPrefab owner)
    {
        double mx = q.X + q.SizeX / 2.0, mz = q.Z + q.SizeZ / 2.0;
        // side on which the driveway protrudes farthest beyond the building (south, north, west, east)
        var sides = new[]
        {
            (Out: owner.Z - q.Z, P: (mx, (double)q.Z)),
            (Out: q.Z + q.SizeZ - (owner.Z + owner.SizeZ), P: (mx, (double)(q.Z + q.SizeZ))),
            (Out: owner.X - q.X, P: ((double)q.X, mz)),
            (Out: q.X + q.SizeX - (owner.X + owner.SizeX), P: ((double)(q.X + q.SizeX), mz)),
        };
        var best = sides.MaxBy(s => s.Out);
        if (best.Out > 0) return best.P;
        // fully inside: at the long edge of the driveway, away from the building center
        double cx = owner.X + owner.SizeX / 2.0, cz = owner.Z + owner.SizeZ / 2.0;
        if (q.SizeZ <= q.SizeX) return mz < cz ? sides[0].P : sides[1].P;
        return mx < cx ? sides[2].P : sides[3].P;
    }

    /// snap point with the width of the connection in m (0 = unknown)
    public readonly record struct ExitPoint(double X, double Z, PlacedPrefab Owner, int Width);

    /// width of a driveway at the snap point: length of the edge on which the point lies
    public static int DrivewayWidth(PlacedPrefab q, PlacedPrefab owner)
    {
        var (_, z) = DrivewayExit(q, owner);
        bool horizontal = Math.Abs(z - q.Z) < 0.01 || Math.Abs(z - (q.Z + q.SizeZ)) < 0.01;   // south/north edge
        return horizontal ? q.SizeX : q.SizeZ;
    }

    /// All snap points of a building with width: RoadExit markers (width = longer side of the marker), outer edges of the
    /// driveways (width = edge length) and connections from the building's own surface (PrefabSurface.EdgeExits, the user's
    /// request 2026-11-03), unless a snap point already lies there (5 m). used: driveways already counted (one can belong to two
    /// buildings).
    public static List<ExitPoint> ExitsFor(WorldData w, PlacedPrefab pf, HashSet<PlacedPrefab> used = null, bool withDriveways = true)
    {
        var list = new List<ExitPoint>();
        if (pf.Def == null) return list;
        for (int i = 0; i < pf.Def.Exits.Count; i++)
        {
            var (rx, rz) = PlacedPrefab.Rotate(pf.Def.Exits[i].X, pf.Def.Exits[i].Z, pf.Rotation, pf.Def.SizeX, pf.Def.SizeZ);
            var (sx, sz) = i < pf.Def.ExitSizes.Count ? pf.Def.ExitSizes[i] : (0, 0);
            list.Add(new ExitPoint(pf.X + rx, pf.Z + rz, pf, Math.Max(sx, sz)));
        }
        if (pf.IsTile || pf.Def.Kind == PrefabKind.Part) return list;
        if (withDriveways && pf.Def.Parts.Count > 0)
            foreach (var q in Attached(w, pf))
                if (IsDriveway(q) && (used == null || used.Add(q)))
                {
                    var (x, z) = DrivewayExit(q, pf);
                    list.Add(new ExitPoint(x, z, pf, DrivewayWidth(q, pf)));
                }
        if (pf.Def.Kind == PrefabKind.Poi)
        {
            // until reading all buildings is finished (EdgeExitsWarmUp) only those already read count; afterwards they are read when needed
            var edge = w.EdgeExitsOf(pf.Name, pf.Def, cachedOnly: !w.EdgeExitsReady);
            if (edge != null)
                foreach (var e in edge)
                {
                    var (rx, rz) = PlacedPrefab.Rotate(e.LX, e.LZ, pf.Rotation, pf.Def.SizeX, pf.Def.SizeZ);
                    double x = pf.X + rx, z = pf.Z + rz;
                    if (list.Any(l => Math.Abs(l.X - x) < 5 && Math.Abs(l.Z - z) < 5)) continue;
                    list.Add(new ExitPoint(x, z, pf, e.Width));
                }
        }
        return list;
    }

    /// all snap points of the world with width: road exits of the city tiles (from their blocks, WorldData.TileExits) and
    /// the snap points of every building (ExitsFor)
    public static List<ExitPoint> AllExitsWithWidth(WorldData w, Func<PlacedPrefab, bool> near = null)
    {
        var list = new List<ExitPoint>();
        var used = new HashSet<PlacedPrefab>();
        foreach (var e in w.TileExits)
            if (near == null || near(e.Tile)) list.Add(new ExitPoint(e.X, e.Z, e.Tile, w.TileExitWidth(e.Tile, e.X, e.Z).Width));
        foreach (var pf in w.Prefabs)
        {
            if (near != null && !near(pf)) continue;
            list.AddRange(ExitsFor(w, pf, used));
        }
        return list;
    }

    /// all snap points of the world: entrances/exits (RoadExit) of every building, the outer edges of the driveways (belong to the building,
    /// so that the road takes over its ground height there) and the connections from the surface of the building
    public static List<(double X, double Z, PlacedPrefab Owner)> AllExits(WorldData w, Func<PlacedPrefab, bool> near = null)
        => AllExitsWithWidth(w, near).Select(e => (e.X, e.Z, e.Owner)).ToList();
    // fixed choice from position and group (string.GetHashCode changes on every program start, therefore calculated by hand)
    static int Pick(PlacedPrefab p, string key, int count)
    {
        if (count <= 1) return 0;
        unchecked
        {
            uint h = (uint)(p.X * 73856093) ^ (uint)(p.Z * 19349663) ^ (uint)(p.Rotation * 83492791);
            foreach (char c in key) h = (h ^ c) * 16777619;
            return (int)(h % (uint)count);
        }
    }
}
