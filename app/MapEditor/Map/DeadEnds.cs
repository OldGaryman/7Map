namespace MapEditor.Map;

/// exit of a city tile that leads nowhere (direction outwards)
public sealed record DeadEnd(double X, double Z, double DX, double DZ, PlacedPrefab Tile);

/// Dead ends on the map (the user's request 2026-11-14: View → Dead ends, always marked, also outside "Generate roads"):
/// road exits of city tiles in front of which there is neither a neighboring tile nor road surface – the same rule with which
/// "Generate roads" finds free town exits (no neighbor 3 m in front, no surface 3–14 m in front across the width of the exit).
public static class DeadEnds
{
    public static List<DeadEnd> Find(WorldData w)
    {
        var res = new List<DeadEnd>();
        if (w == null) return res;
        var tiles = w.PrefabSnapshot().Where(p => p.IsTile).ToList();
        foreach (var (x, z, tile) in w.TileExitSnapshot())
        {
            var (dx, dz) = Outward(tile, x, z);
            if (!w.Contains((int)Math.Floor(x + dx * 3), (int)Math.Floor(z + dz * 3))) continue;   // exit right at the map edge
            if (tiles.Any(o => o != tile && o.Contains(x + dx * 3, z + dz * 3))) continue;
            int total = Math.Max(4, w.TileExitWidth(tile, x, z).Total);
            double px = -dz, pz = dx;
            bool paved = false;
            for (double d = 3; d <= 14 && !paved; d += 1)
                for (double l = -(total / 2.0 - 1); l <= total / 2.0 - 1 && !paved; l += 1)
                    paved = w.RoadAt((int)Math.Floor(x + dx * d + px * l), (int)Math.Floor(z + dz * d + pz * l)) != 0;
            if (!paved) res.Add(new DeadEnd(x, z, dx, dz, tile));
        }
        return res;
    }

    /// direction out of the tile at an exit in the middle of a side
    static (double DX, double DZ) Outward(PlacedPrefab t, double x, double z) =>
        Math.Abs(z - t.Z) < 0.6 ? (0, -1) : Math.Abs(x - (t.X + t.SizeX)) < 0.6 ? (1, 0) : Math.Abs(z - (t.Z + t.SizeZ)) < 0.6 ? (0, 1) : (-1, 0);
}
