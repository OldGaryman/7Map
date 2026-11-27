namespace MapEditor.Map;

/// Distribute spawn points for playing (the user's request, 2026-11-10: only at the end, after world, cities, roads and
/// wilderness POIs; the generated world has only one point in the middle before): only in the pine forest (the other biomes
/// need equipment), preferably near gravel roads, with distance to each other. The spot does not have to be flat
/// (mountains are allowed too), only dry and free of roads, buildings and tiles.
public static class SpawnPlacer
{
    public static List<SpawnPoint> Choose(WorldData w, int count, string seed)
    {
        var list = new List<SpawnPoint>();
        int size = w.Size, half = w.Half;
        int h = 17;
        foreach (char c in seed ?? "") h = unchecked(h * 31 + c);
        var rnd = new Random(h + 99);
        double spacing = size / Math.Sqrt(Math.Max(1, count)) * 0.55;

        // collect and shuffle the gravel spots in the pine forest (every 8 m)
        var gravel = new List<(int X, int Z)>();
        int margin = (int)(size * 0.05);
        for (int z = -half + margin; z < half - margin; z += 8)
            for (int x = -half + margin; x < half - margin; x += 8)
                if (w.RoadAt(x, z) == 1 && w.BiomeAt(x, z) == 0) gravel.Add((x, z));
        for (int i = gravel.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); (gravel[i], gravel[j]) = (gravel[j], gravel[i]); }

        for (int pass = 0; pass < 4 && list.Count < count; pass++)
        {
            foreach (var (gx, gz) in gravel)
            {
                if (list.Count >= count) break;
                // 6–24 m beside the gravel road, in a random direction
                double a = rnd.NextDouble() * Math.PI * 2, d = 6 + rnd.NextDouble() * 18;
                int x = gx + (int)Math.Round(Math.Cos(a) * d), z = gz + (int)Math.Round(Math.Sin(a) * d);
                TryAdd(w, list, x, z, spacing, rnd);
            }
            spacing *= 0.7;
        }
        // if that is not enough (no gravel roads), anywhere in the pine forest
        spacing = size / Math.Sqrt(Math.Max(1, count)) * 0.55;
        for (int pass = 0; pass < 4 && list.Count < count; pass++)
        {
            for (int attempt = 0; attempt < 6000 && list.Count < count; attempt++)
                TryAdd(w, list, (int)(size * (0.1 + 0.8 * rnd.NextDouble())) - half, (int)(size * (0.1 + 0.8 * rnd.NextDouble())) - half, spacing, rnd);
            spacing *= 0.7;
        }
        return list;
    }

    static void TryAdd(WorldData w, List<SpawnPoint> list, int x, int z, double spacing, Random rnd)
    {
        if (!w.Contains(x, z) || w.BiomeAt(x, z) != 0) return;
        if (list.Any(s => Math.Abs(s.X - x) + Math.Abs(s.Z - z) < spacing)) return;
        if (!Free(w, x, z)) return;
        list.Add(new SpawnPoint { X = x, Z = z, Y = w.SpawnHeight(x, z), Heading = rnd.Next(0, 360) });
    }

    static bool Free(WorldData w, int x, int z)
    {
        for (int dz = -12; dz <= 12; dz += 3)
            for (int dx = -12; dx <= 12; dx += 3)
                if (w.WaterAt(x + dx, z + dz)) return false;
        for (int dz = -3; dz <= 3; dz++)
            for (int dx = -3; dx <= 3; dx++)
                if (w.RoadAt(x + dx, z + dz) != 0) return false;
        foreach (var p in w.Prefabs)
            if (x >= p.X - 8 && x < p.X + p.SizeX + 8 && z >= p.Z - 8 && z < p.Z + p.SizeZ + 8) return false;
        return true;
    }
}
