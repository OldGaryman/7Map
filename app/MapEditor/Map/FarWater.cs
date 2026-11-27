namespace MapEditor.Map;

/// Far water of the game (read in the game code 2026-11-26: WorldDecoratorPOIFromImage.InitData fills water16x16Chunks,
/// UnityDistantTerrainWaterPlane.createDynamicWaterPlane_Step1 draws it): far away the game does not show the water pixels but one flat
/// 16 × 16 m plane for every chunk that contains at least one water pixel, at level + 0.77 m. Dry land in such a chunk that lies lower
/// shows water from afar, which vanishes when coming close (real water only stands in the pixels; Testit-Land 2026-11-26: land behind the
/// river embankment 1–3 m below the level). Therefore all dry land in a chunk with water lies clearly above that plane; outside the chunk the
/// raised ground runs out with a gentle slope (12–22 %), like a wide embankment.
public static class FarWater
{
    public const int Chunk = 16;
    /// dry land in a chunk with water lies at least this far above the level (plane at + 0.77 m)
    public const double Clear = Lake.WetMargin + 0.05;   // like the land at the water (WetMargin): intact shores stay as they are
    const double MaxReach = 90;   // m outside the chunk at most

    /// raise dry land in chunks with water. Array coordinates (0 … size − 1, z = row of dtm.raw); x0…x1, z0…z1 = area in which water may have
    /// changed (inclusive). water(x, z) = level or 0, height(x, z) in m, keep(x, z) = cell must not change (roads, buildings), set(x, z, height).
    /// Every target chunk is computed on its own (parallel = Parallel.For over the chunks; set must then only write the cell itself).
    /// Returns the number of raised cells.
    public static int RaiseBanks(int size, int x0, int z0, int x1, int z1, Func<int, int, int> water, Func<int, int, double> height,
        Func<int, int, bool> keep, Action<int, int, double> set, bool parallel = false)
    {
        int nc = size / Chunk;
        int K = (int)Math.Ceiling(MaxReach / Chunk) + 1;
        int wx0 = Math.Max(0, x0 / Chunk), wz0 = Math.Max(0, z0 / Chunk);
        int wx1 = Math.Min(nc - 1, x1 / Chunk), wz1 = Math.Min(nc - 1, z1 / Chunk);
        if (wx1 < wx0 || wz1 < wz0) return 0;
        int ww = wx1 - wx0 + 1, wh = wz1 - wz0 + 1;
        // highest level per chunk of the area (the game takes one level per chunk; with several levels the highest one is safe)
        var level = new int[ww * wh];
        void ScanChunk(int c)
        {
            int cx = wx0 + c % ww, cz = wz0 + c / ww, m = 0;
            for (int z = cz * Chunk; z < cz * Chunk + Chunk; z++)
                for (int x = cx * Chunk; x < cx * Chunk + Chunk; x++)
                    m = Math.Max(m, water(x, z));
            level[c] = m;
        }
        if (parallel) Parallel.For(0, level.Length, ScanChunk); else for (int c = 0; c < level.Length; c++) ScanChunk(c);

        int tx0 = Math.Max(0, wx0 - K), tz0 = Math.Max(0, wz0 - K), tx1 = Math.Min(nc - 1, wx1 + K), tz1 = Math.Min(nc - 1, wz1 + K);
        int tw = tx1 - tx0 + 1, th = tz1 - tz0 + 1;
        int raised = 0;
        void Target(int t)
        {
            int cx = tx0 + t % tw, cz = tz0 + t / tw;
            var near = new List<(int X0, int Z0, int L)>();
            for (int qz = Math.Max(wz0, cz - K); qz <= Math.Min(wz1, cz + K); qz++)
                for (int qx = Math.Max(wx0, cx - K); qx <= Math.Min(wx1, cx + K); qx++)
                {
                    int L = level[(qz - wz0) * ww + qx - wx0];
                    if (L > 0) near.Add((qx * Chunk, qz * Chunk, L));
                }
            if (near.Count == 0) return;
            int count = 0;
            for (int z = cz * Chunk; z < cz * Chunk + Chunk; z++)
                for (int x = cx * Chunk; x < cx * Chunk + Chunk; x++)
                {
                    if (water(x, z) != 0 || keep(x, z)) continue;
                    double fall = 0.17 + 0.05 * RoadApplier.Octave(x, z, 45, 811);   // 12–22 % down outside the chunk
                    double target = double.MinValue;
                    foreach (var q in near)
                    {
                        int dx = Math.Max(0, Math.Max(q.X0 - x, x - (q.X0 + Chunk - 1)));
                        int dz = Math.Max(0, Math.Max(q.Z0 - z, z - (q.Z0 + Chunk - 1)));
                        double d = dx == 0 && dz == 0 ? 0 : Math.Sqrt(dx * dx + dz * dz);
                        if (d > MaxReach) continue;
                        target = Math.Max(target, q.L + Clear - fall * d);
                    }
                    double h = height(x, z);
                    if (target <= h + 0.02) continue;
                    set(x, z, target);
                    count++;
                }
            Interlocked.Add(ref raised, count);
        }
        if (parallel) Parallel.For(0, tw * th, Target); else for (int t = 0; t < tw * th; t++) Target(t);
        return raised;
    }

    /// the same on a loaded world as part of an edit step (river and lake tools): roads and buildings (+ 1 m) stay unchanged.
    /// x0…x1, z0…z1 in world coordinates.
    public static int RaiseBanks(WorldData w, WorldEdits e, int x0, int z0, int x1, int z1)
    {
        int half = w.Half;
        var rects = new List<(int x0, int z0, int x1, int z1)>();
        int m = (int)MaxReach + 2 * Chunk;
        foreach (var b in w.PrefabSnapshot())
            if (b.X - 1 <= x1 + m && b.X + b.SizeX + 1 >= x0 - m && b.Z - 1 <= z1 + m && b.Z + b.SizeZ + 1 >= z0 - m)
                rects.Add((b.X - 1 + half, b.Z - 1 + half, b.X + b.SizeX + half, b.Z + b.SizeZ + half));
        long Idx(int x, int z) => (long)z * w.Size + x;
        return RaiseBanks(w.Size, Math.Max(0, x0 + half), Math.Max(0, z0 + half), Math.Min(w.Size - 1, x1 + half), Math.Min(w.Size - 1, z1 + half),
            (x, z) => w.WaterMask[Idx(x, z)],
            (x, z) => w.Heights[Idx(x, z)] / 256.0,
            (x, z) =>
            {
                if (x < 3 || z < 3 || x >= w.Size - 3 || z >= w.Size - 3 || w.RoadMask[Idx(x, z)] != 0) return true;
                foreach (var r in rects) if (x >= r.x0 && x <= r.x1 && z >= r.z0 && z <= r.z1) return true;
                return false;
            },
            (x, z, h) => e.SetHeight((int)Idx(x, z), (ushort)Math.Clamp(Math.Round(h * 256), 0, 65535)));
    }
}
