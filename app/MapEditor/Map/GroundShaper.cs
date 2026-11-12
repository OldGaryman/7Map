namespace MapEditor.Map;

/// Shape the terrain: fill naturally (eraser, deleted buildings) and level under new buildings.
public static class GroundShaper
{
    const int Ring = 8;   // strip outside in which the unevenness of the terrain is measured (m)

    /// result of leveling or making natural: changed height points, road area (m², left out when placing, smoothed
    /// when deleting) and other buildings around which the edge was led
    public sealed record ShapeInfo(int Changed, int RoadCells, IReadOnlyList<PlacedPrefab> Spared);

    // strength of the unevenness per level (0 none … 3 strong)
    static readonly double[] BumpFactor = { 0, 1, 3, 5 };
    static double Factor(double bumps) => BumpFactor[Math.Clamp((int)Math.Round(bumps), 0, 3)];

    /// Make free cells (free) natural: Laplace fill from the terrain all around and unevenness like beside it, faded out towards
    /// the edge. bumps = level (0 none … 3 strong), skipRoughness = cells that do not count when measuring the unevenness
    /// (e.g. tiles), smooth = cells without unevenness (roads stay smooth). Edge cells of the grid must not be
    /// free. Returns changed height points and the strength.
    public static (int Changed, double Amp) FillNatural(WorldData w, WorldEdits e, int gx0, int gz0, int gw, int gh, bool[] free,
        double bumps, Func<int, int, bool> skipRoughness, bool[] smooth = null)
    {
        int n = gw * gh;
        var h = new float[n];
        var cells = new List<int>();
        for (int g = 0; g < n; g++)
        {
            int gx = g % gw, gz = g / gw;
            h[g] = (float)w.HeightAt(gx0 + gx, gz0 + gz);
            if (free[g]) cells.Add(g);
        }
        if (cells.Count == 0) return (0, 0);

        // Laplace fill (SOR): every point = mean of its neighbors, everything outside fixed
        for (int it = 0; it < 2000; it++)
        {
            float maxChange = 0;
            foreach (int g in cells)
            {
                float v = (h[g - 1] + h[g + 1] + h[g - gw] + h[g + gw]) / 4;
                float nv = h[g] + 1.85f * (v - h[g]);
                maxChange = Math.Max(maxChange, Math.Abs(nv - h[g]));
                h[g] = nv;
            }
            if (maxChange < 0.002f) break;
        }

        // unevenness of the terrain beside it: deviation from the mean of the points 3 m around, in the strip outside
        var dFree = Distance(free, gw, gh);
        double sum = 0;
        int m = 0;
        for (int gz = 3; gz < gh - 3; gz++)
            for (int gx = 3; gx < gw - 3; gx++)
            {
                int g = gz * gw + gx;
                if (free[g] || dFree[g] < 1 || dFree[g] > Ring || skipRoughness(gx0 + gx, gz0 + gz)) continue;
                double avg = (h[g - 3] + h[g + 3] + h[g - 3 * gw] + h[g + 3 * gw]) / 4;
                sum += (h[g] - avg) * (h[g] - avg);
                m++;
            }
        // base strength from the terrain beside it (at least 0.25 m), times the chosen level (the user's request: more unevenness),
        // at most 1.5 m. On level terrain: mean about 0.2 m, bumps up to about 0.6 m; strong up to about 1 m
        double amp = Math.Min(1.5, Math.Clamp(m > 0 ? Math.Sqrt(sum / m) * 2 : 0.2, 0.25, 0.6) * Factor(bumps));

        // fade the noise out towards the edge of the free area so that it connects seamlessly
        var fixedCells = new bool[n];
        for (int g = 0; g < n; g++) fixedCells[g] = !free[g];
        var dIn = Distance(fixedCells, gw, gh);
        int changed = 0;
        foreach (int g in cells)
        {
            int x = gx0 + g % gw, z = gz0 + g / gw;
            // fade out towards the edge over 2.5 m (before 4 m: narrow strips stayed almost smooth); plus small bumps (2 m)
            double v = Math.Clamp(dIn[g] / 2.5, 0, 1);
            if (smooth == null || !smooth[g])
                h[g] += (float)(amp * v * v * (3 - 2 * v) * (RoadApplier.Noise(x, z) + 0.35 * RoadApplier.Octave(x, z, 2, 53)));
            int hi = w.Index(x, z);
            var hv = (ushort)Math.Clamp(Math.Round(h[g] * 256), 0, 65535);
            if (hv != w.Heights[hi]) { e.SetHeight(hi, hv); changed++; }
        }
        return (changed, amp);
    }

    /// adapt the ground under deleted buildings to the surroundings (the user's request): footprint and an irregular edge of
    /// 6–16 m beyond it (this also removes the slopes with which the game leveled the lot). Other buildings and tiles stay (edge runs out in front of them); roads in it are adjusted too,
    /// but without unevenness (decision of the user). Call after the buildings have been removed from w.Prefabs.
    public static ShapeInfo Naturalize(WorldData w, WorldEdits e, IReadOnlyCollection<PlacedPrefab> removed, double bumps)
    {
        if (removed.Count == 0) return new ShapeInfo(0, 0, Array.Empty<PlacedPrefab>());
        const double extraMin = 6, extraMax = 16;
        int margin = Ring + 5 + (int)extraMax;
        int gx0 = removed.Min(p => p.X) - margin, gz0 = removed.Min(p => p.Z) - margin;
        int gw = removed.Max(p => p.X + p.SizeX) + margin - gx0, gh = removed.Max(p => p.Z + p.SizeZ) + margin - gz0;
        var others = Near(w, gx0, gz0, gw, gh, null);
        var tiles = others.Where(p => p.IsTile).ToList();
        var free = new bool[gw * gh];
        var smooth = new bool[gw * gh];
        var spared = new HashSet<PlacedPrefab>();
        int roads = 0;
        for (int gz = 1; gz < gh - 1; gz++)
            for (int gx = 1; gx < gw - 1; gx++)
            {
                int x = gx0 + gx, z = gz0 + gz;
                double cx = x + 0.5, cz = z + 0.5;
                if (!w.Contains(x, z)) continue;
                // distance to the nearest deleted footprint; allowed edge per place 6–16 m (noise, about 20 m wavelength)
                double d = removed.Min(p => RectDistance(p, cx, cz));
                double extra = extraMin + (extraMax - extraMin) * (0.5 + 0.5 * RoadApplier.Octave(x, z, 20, 97));
                if (d > extra) continue;
                var o = others.FirstOrDefault(p => p.Contains(cx, cz));
                if (o != null) { spared.Add(o); continue; }
                int g = gz * gw + gx;
                free[g] = true;
                if (w.RoadMask[w.Index(x, z)] != 0) { smooth[g] = true; roads++; }
            }
        // When a tile is removed, the neighboring tiles in the game lie at their tile height (dtm.raw is uneven there): set their edges
        // to this height so that the new ground connects seamlessly and no step appears
        if (removed.Any(r => r.IsTile))
            foreach (var t in tiles)
            {
                var level = (ushort)Math.Clamp(Math.Round(t.GroundHeight * 256), 0, 65535);
                for (int gz = 0; gz < gh; gz++)
                    for (int gx = 0; gx < gw; gx++)
                    {
                        int x = gx0 + gx, z = gz0 + gz;
                        if (w.Contains(x, z) && t.Contains(x + 0.5, z + 0.5)) e.SetHeight(w.Index(x, z), level);
                    }
            }        var (changed, _) = FillNatural(w, e, gx0, gz0, gw, gh, free, bumps, (x, z) => tiles.Any(t => t.Contains(x + 0.5, z + 0.5)), smooth);
        return new ShapeInfo(changed, roads, spared.ToList());
    }

    // distance of a point to the footprint (0 = inside)
    static double RectDistance(PlacedPrefab p, double cx, double cz)
    {
        double dx = Math.Max(Math.Max(p.X - cx, cx - (p.X + p.SizeX)), 0), dz = Math.Max(Math.Max(p.Z - cz, cz - (p.Z + p.SizeZ)), 0);
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// terrain for a new building: footprint at ground (ground height of the building), outside softly into the terrain. The edge is
    /// natural instead of uniform (the user's request): width = double the height difference at the edge (8–60 m), per place of the
    /// outline 45–155 % of that (noise), plus slight unevenness in the transition (level like with the eraser).
    /// Other buildings, tiles and roads stay unchanged (the edge runs out in front of them) and are reported.
    public static ShapeInfo Flatten(WorldData w, WorldEdits e, PlacedPrefab p, double ground, double bumps, IReadOnlyCollection<PlacedPrefab> ignore = null, bool footprintRoads = false)
    {
        // largest height difference 1 m outside the footprint determines the width of the transition
        double maxDiff = 0;
        for (int x = p.X - 1; x <= p.X + p.SizeX; x++)
            foreach (int z in new[] { p.Z - 1, p.Z + p.SizeZ })
                maxDiff = Math.Max(maxDiff, Math.Abs(w.HeightAt(x, z) - ground));
        for (int z = p.Z; z < p.Z + p.SizeZ; z++)
            foreach (int x in new[] { p.X - 1, p.X + p.SizeX })
                maxDiff = Math.Max(maxDiff, Math.Abs(w.HeightAt(x, z) - ground));
        double baseBlend = Math.Clamp(maxDiff * 2, 8, 60);
        double amp = 0.15 * Factor(bumps);   // unevenness in the transition: 0 / 0.15 / 0.45 / 0.75 m

        int margin = (int)Math.Ceiling(baseBlend * 1.55) + 2;
        int gx0 = p.X - margin, gz0 = p.Z - margin, gw = p.SizeX + 2 * margin, gh = p.SizeZ + 2 * margin;
        var others = Near(w, gx0, gz0, gw, gh, p);
        if (ignore != null) others.RemoveAll(ignore.Contains);   // e.g. the buildings of a tile that are leveled along with it
        var spared = new HashSet<PlacedPrefab>();
        int changed = 0, roads = 0;
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int x = gx0 + gx, z = gz0 + gz;
                if (!w.Contains(x, z)) continue;
                double cx = x + 0.5, cz = z + 0.5;
                double d = RectDistance(p, cx, cz);
                // width of the transition per place of the outline (nearest point on it): 45–155 % of the base width
                int qx = (int)Math.Floor(Math.Clamp(cx, p.X, p.X + p.SizeX)), qz = (int)Math.Floor(Math.Clamp(cz, p.Z, p.Z + p.SizeZ));
                double blend = baseBlend * (1 + 0.4 * RoadApplier.Octave(qx, qz, 25, 71) + 0.15 * RoadApplier.Octave(qx, qz, 9, 89));
                if (d >= blend) continue;
                var o = others.FirstOrDefault(q => q.Contains(cx, cz));
                if (o != null) { spared.Add(o); continue; }
                int i = w.Index(x, z);
                if (w.RoadMask[i] != 0 && !(footprintRoads && d == 0)) { roads++; continue; }   // roads keep their height (decision of the user); in the footprint of a tile they do not (the game levels it)
                double terrain = w.Heights[i] / 256.0;
                double t = d / blend, s = t * t * (3 - 2 * t);
                // unevenness only in the transition (strongest in the middle), the footprint stays level
                double hNew = ground + (terrain - ground) * s + (d > 0 ? amp * 4 * t * (1 - t) * RoadApplier.Noise(x, z) : 0);
                var hv = (ushort)Math.Clamp(Math.Round(hNew * 256), 0, 65535);
                if (hv != w.Heights[i]) { e.SetHeight(i, hv); changed++; }
            }
        return new ShapeInfo(changed, roads, spared.ToList());
    }

    /// mean terrain height under the footprint (for the build height when placing)
    public static double AverageHeight(WorldData w, int x0, int z0, int sx, int sz)
    {
        double sum = 0;
        int n = 0;
        for (int z = z0; z < z0 + sz; z++)
            for (int x = x0; x < x0 + sx; x++) { sum += w.HeightAt(x, z); n++; }
        return n > 0 ? sum / n : 0;
    }

    static List<PlacedPrefab> Near(WorldData w, int gx0, int gz0, int gw, int gh, PlacedPrefab except) =>
        w.Prefabs.Where(o => o != except && o.X < gx0 + gw && o.X + o.SizeX > gx0 && o.Z < gz0 + gh && o.Z + o.SizeZ > gz0).ToList();

    // distance (m) of every cell to the nearest cell with seed = true (chamfer, two passes)
    internal static float[] Distance(bool[] seed, int gw, int gh)
    {
        const float D = 1.41421f;
        var d = new float[gw * gh];
        for (int g = 0; g < d.Length; g++) d[g] = seed[g] ? 0 : 1e9f;
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx;
                float v = d[g];
                if (gx > 0) v = Math.Min(v, d[g - 1] + 1);
                if (gz > 0)
                {
                    v = Math.Min(v, d[g - gw] + 1);
                    if (gx > 0) v = Math.Min(v, d[g - gw - 1] + D);
                    if (gx < gw - 1) v = Math.Min(v, d[g - gw + 1] + D);
                }
                d[g] = v;
            }
        for (int gz = gh - 1; gz >= 0; gz--)
            for (int gx = gw - 1; gx >= 0; gx--)
            {
                int g = gz * gw + gx;
                float v = d[g];
                if (gx < gw - 1) v = Math.Min(v, d[g + 1] + 1);
                if (gz < gh - 1)
                {
                    v = Math.Min(v, d[g + gw] + 1);
                    if (gx < gw - 1) v = Math.Min(v, d[g + gw + 1] + D);
                    if (gx > 0) v = Math.Min(v, d[g + gw - 1] + D);
                }
                d[g] = v;
            }
        return d;
    }
}
