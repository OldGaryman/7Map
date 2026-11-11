namespace MapEditor.Map;

/// Spawnpunkte zum Spielen verteilen (Wunsch des Nutzers 10.11.2026: erst am Schluss, nach Welt, Städten, Straßen und
/// Wildnis-POIs; die erzeugte Welt hat vorher nur einen Punkt in der Mitte): nur im Kiefernwald (für die anderen Biome
/// braucht man Ausrüstung), am liebsten in der Nähe von Schotterwegen, mit Abstand zueinander. Flach muss die Stelle nicht
/// sein (auch Berge sind erlaubt), nur trocken und frei von Straße, Gebäuden und Kacheln.
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

        // Schotterstellen im Kiefernwald (alle 8 m) sammeln und mischen
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
                // 6–24 m neben dem Schotterweg, in zufälliger Richtung
                double a = rnd.NextDouble() * Math.PI * 2, d = 6 + rnd.NextDouble() * 18;
                int x = gx + (int)Math.Round(Math.Cos(a) * d), z = gz + (int)Math.Round(Math.Sin(a) * d);
                TryAdd(w, list, x, z, spacing, rnd);
            }
            spacing *= 0.7;
        }
        // Reicht das nicht (keine Schotterwege), irgendwo im Kiefernwald
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
