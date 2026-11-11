using System.Windows;

namespace MapEditor.Map;

/// Einstellungen für einen See (Wunsch des Nutzers 5.11.2026; Messungen in WASSER_ANALYSE.md)
public sealed class LakeSpec
{
    /// Wasserspiegel in Metern (Blauwert in splat4.png); RWG-Welten benutzen immer 30
    public int Level = 30;
    /// größte Tiefe unter dem Spiegel (m)
    public double Depth = 6;
    /// Breite des Ufers (m): so weit vom Rand nach innen wird der Boden bis zur größten Tiefe abgesenkt
    public double Shore = 14;
    /// Unebenheit des Seebodens (0 keine … 3 stark)
    public int Bumps = 1;
    public int Seed;
    /// Biom unter dem See (Index in BiomePalette.Names), -1 = Biom bleibt
    public int Biome = -1;
    /// Umriss in Weltkoordinaten
    public List<Point> Outline = new();

    public const double MinArea = 400;
    public const int MinLevel = 2, MaxLevel = 250;
}

/// Ergebnis: Zellen mit Wasser (m²), geänderte Höhenpunkte, ausgesparte Zellen (Straßen, Gebäude, Kacheln), mittleres Gelände
/// im Bereich vorher, größte Anhebung (Damm) und Absenkung, größte Tiefe
/// Ergebnis „Krater füllen“: Wasserzellen (m²), Zahl der Becken, niedrigster und höchster Spiegel, zu groß zum Berechnen
public sealed record CraterStats(int Water, int Craters, int MinLevel, int MaxLevel, bool TooHuge);

public sealed record LakeStats(int Water, int Changed, int Spared, double MeanGround, double MaxRaise, double MaxCut, double MaxDepth);

/// See setzen oder Wasser entfernen. Wasser steht nur als Blauwert in splat4.png (= Höhe des Spiegels); das Gelände ist das Becken.
/// Ein See braucht also beides: Boden unter dem Spiegel (Profil wie bei den Seen der Weltgenerierung: am Ufer flach, nach ca. 15–20 m
/// am tiefsten) und blaue Pixel. Außerhalb läuft das Ufer weich in das vorhandene Gelände aus; ist das Land niedriger als der
/// Spiegel, entsteht ein Damm. Straßen, Gebäude und Kacheln bleiben unberührt (der See hat dort ein Loch).
public static class Lake
{
    const int Bank = 60;   // größte Breite des Ufers außerhalb (m)
    // RWG: Wasserpixel liegen höher als Spiegel − 0,4 m nie, das Land am Ufer mindestens bei Spiegel − 0,4 m
    /// Wie weit über dem Spiegel Boden noch Wasser bekommt (m). Das Spiel rundet die Bodenhöhe auf ganze Blöcke (Block = Höhe + 0,5 abgerundet);
    /// der oberste Wasserblock liegt eine Stufe über dem Spiegel und fließt in jede trockene Nachbarzelle, deren oberster Block auf Spiegelhöhe
    /// oder darunter liegt, also bei Höhe < Spiegel + 0,5. Solche Zellen bekamen als „trocken“ das Biom und die Pflanzen des Landes (Gras, Büsche,
    /// Waldboden) mitten im Wasser. Deshalb bekommen sie selbst Wasser (Unterwasser-Biom, oberster Block Erde, keine Landpflanzen).
    /// Fest 1 m (Wunsch des Nutzers 6.11.2026, vorher 0,45 und einstellbar): so weit über dem Spiegel bekommt Boden noch Wasser.
    public const double WetMargin = 1.0;
    const double Rim = 0;

    /// Bereich der Zellen: Rahmen um den Umriss (mit Rand) und Maske der Zellen innerhalb
    static (int gx0, int gz0, int gw, int gh, bool[] inside) Grid(WorldData w, IReadOnlyList<Point> outline, int margin)
    {
        double ax = outline.Min(p => p.X), bx = outline.Max(p => p.X), az = outline.Min(p => p.Y), bz = outline.Max(p => p.Y);
        int gx0 = Math.Max(-w.Half + 2, (int)Math.Floor(ax) - margin), gz0 = Math.Max(-w.Half + 2, (int)Math.Floor(az) - margin);
        int gx1 = Math.Min(w.Half - 3, (int)Math.Ceiling(bx) + margin), gz1 = Math.Min(w.Half - 3, (int)Math.Ceiling(bz) + margin);
        int gw = gx1 - gx0 + 1, gh = gz1 - gz0 + 1;
        var inside = new bool[Math.Max(0, gw * gh)];
        if (gw < 3 || gh < 3) return (gx0, gz0, 0, 0, inside);
        var xs = new List<double>();
        for (int gz = 0; gz < gh; gz++)
        {
            double z = gz0 + gz + 0.5;
            xs.Clear();
            for (int i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                if ((a.Y <= z && b.Y > z) || (b.Y <= z && a.Y > z)) xs.Add(a.X + (z - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int x0 = Math.Max(0, (int)Math.Ceiling(xs[k] - gx0 - 0.5)), x1 = Math.Min(gw - 1, (int)Math.Floor(xs[k + 1] - gx0 - 0.5));
                for (int gx = x0; gx <= x1; gx++) inside[gz * gw + gx] = true;
            }
        }
        return (gx0, gz0, gw, gh, inside);
    }

    /// Wasserstand eines vorhandenen Gewässers im oder höchstens 3 m neben dem Bereich (häufigster Blauwert); null = keins.
    /// Beim Erweitern eines Sees oder beim Anschluss eines Flusses übernimmt das neue Wasser diesen Stand, sonst gäbe es Stufen im Wasser.
    public static int? ExistingLevel(WorldData w, IReadOnlyList<Point> outline)
    {
        var (gx0, gz0, gw, gh, inside) = Grid(w, outline, 4);
        if (gw == 0) return null;
        var near = GroundShaper.Distance(inside, gw, gh);   // Abstand zum Bereich
        var count = new Dictionary<int, int>();
        for (int g = 0; g < gw * gh; g++)
        {
            if (!inside[g] && near[g] > 3) continue;
            byte v = w.WaterMask[w.Index(gx0 + g % gw, gz0 + g / gw)];
            if (v != 0) count[v] = count.GetValueOrDefault(v) + 1;
        }
        return count.Count == 0 ? null : count.OrderByDescending(k => k.Value).First().Key;
    }

    /// Flussband um eine Mittellinie: Catmull-Rom, alle ca. 3 m ein Punkt, links und rechts je halbe Breite mit leichtem Rauschen
    public static List<Point> Corridor(IReadOnlyList<Point> line, double width, int seed, double step = 3, double irregular = 0)
    {
        var c = new List<Point>();
        for (int i = 0; i < line.Count - 1; i++)
        {
            Point p0 = line[Math.Max(0, i - 1)], p1 = line[i], p2 = line[i + 1], p3 = line[Math.Min(line.Count - 1, i + 2)];
            int steps = Math.Max(1, (int)Math.Ceiling((p2 - p1).Length / step));
            for (int s = 0; s < steps; s++)
            {
                double t = (double)s / steps, t2 = t * t, t3 = t2 * t;
                c.Add(new Point(
                    0.5 * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                    0.5 * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)));
            }
        }
        c.Add(line[^1]);
        var left = new List<Point>(); var right = new List<Point>();
        var rnd = new Random(seed);
        // irregular = Anteil der Breite: jeder Uferpunkt wandert höchstens so weit nach links oder rechts (12 m Breite, 10 % = 1,2 m)
        double Jit() => irregular * width * (rnd.NextDouble() * 2 - 1);
        for (int i = 0; i < c.Count; i++)
        {
            var d = c[Math.Min(c.Count - 1, i + 1)] - c[Math.Max(0, i - 1)];
            if (d.Length < 1e-6) continue;
            d.Normalize();
            var nrm = new Vector(-d.Y, d.X);
            double half = width / 2;
            bool end = i == 0 || i == c.Count - 1;   // Anfang und Ende bleiben gerade
            left.Add(c[i] + nrm * Math.Max(1.5, half + (end ? 0 : Jit())));
            right.Add(c[i] - nrm * Math.Max(1.5, half + (end ? 0 : Jit())));
        }
        right.Reverse();
        left.AddRange(right);
        return left;
    }

    /// Zellen, die unberührt bleiben: Straße oder Belag, Grundfläche von Gebäuden, Teilen und Kacheln (+ 1 m)
    static bool[] Protected(WorldData w, int gx0, int gz0, int gw, int gh)
    {
        var p = new bool[gw * gh];
        for (int g = 0; g < p.Length; g++)
            if (w.RoadMask[w.Index(gx0 + g % gw, gz0 + g / gw)] != 0) p[g] = true;
        foreach (var b in w.Prefabs)
        {
            if (b.X - 1 >= gx0 + gw || b.X + b.SizeX + 1 <= gx0 || b.Z - 1 >= gz0 + gh || b.Z + b.SizeZ + 1 <= gz0) continue;
            for (int z = Math.Max(b.Z - 1, gz0); z <= Math.Min(b.Z + b.SizeZ, gz0 + gh - 1); z++)
                for (int x = Math.Max(b.X - 1, gx0); x <= Math.Min(b.X + b.SizeX, gx0 + gw - 1); x++)
                    p[(z - gz0) * gw + x - gx0] = true;
        }
        return p;
    }

    static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// See setzen: Becken, Ufer, Wasserpixel
    public static LakeStats Place(WorldData w, WorldEdits e, LakeSpec spec)
    {
        e.SkipWaterSync();
        var (gx0, gz0, gw, gh, inside) = Grid(w, spec.Outline, Bank + 2);
        if (gw == 0) return new LakeStats(0, 0, 0, 0, 0, 0, 0);
        var prot = Protected(w, gx0, gz0, gw, gh);
        int n = gw * gh;
        var notInside = new bool[n];
        for (int g = 0; g < n; g++) notInside[g] = !inside[g];
        var dIn = GroundShaper.Distance(notInside, gw, gh);   // innen: Abstand zum Rand
        var dOut = GroundShaper.Distance(inside, gw, gh);     // außen: Abstand zum See
        double level = spec.Level, shoreH = level + WetMargin + 0.15;   // Ufer liegt über der Wassergrenze (Spiegel + 1 m): alles darunter bekommt Wasser
        // Schroff (Wunsch des Nutzers 5.11.2026): Unebenheit in Metern, drei Rauschgrößen; Ufer und Boden fallen steiler ab
        double rough = 0.35 + 0.55 * spec.Bumps;   // m: 0,35 … 2,0
        double Rugged(int x, int z, int seed) =>
            RoadApplier.Octave(x, z, 22, seed) * 0.6 + RoadApplier.Octave(x, z, 8, seed + 11) * 0.8 + RoadApplier.Octave(x, z, 3, seed + 23) * 0.5;
        int water = 0, changed = 0, spared = 0;
        double sumGround = 0, maxRaise = 0, maxCut = 0, maxDepth = 0;
        int counted = 0;
        for (int g = 0; g < n; g++)
        {
            int x = gx0 + g % gw, z = gz0 + g / gw;
            int hi = w.Index(x, z);
            double ex = w.Heights[hi] / 256.0;
            double target;
            if (inside[g])
            {
                if (prot[g]) { spared++; continue; }
                sumGround += ex; counted++;
                double d = Math.Max(0, dIn[g] - 0.5);
                double t = Math.Min(1, d / Math.Max(1, spec.Shore));
                // steiler Abfall am Ufer: kubisch statt quadratisch, dazu Felsstufen und Rauschen in Metern
                // spec.Depth = Wassertiefe: Höhe des Wassers über dem tiefsten Punkt des Bodens
                double depth = Math.Max(0.1, spec.Depth - Rim) * (1 - (1 - t) * (1 - t) * (1 - t));
                depth += rough * Rugged(x, z, spec.Seed + 7) * (0.25 + 0.75 * Math.Min(1, t * 3)) * (spec.Bumps > 0 ? 1 : 0);
                depth = Math.Max(0, depth);
                target = Math.Min(ex, level - Rim - depth);
                maxDepth = Math.Max(maxDepth, level - target);
                e.SetWater(hi, (byte)spec.Level);
                water++;
            }
            else
            {
                if (dOut[g] > Bank || prot[g] || w.WaterMask[hi] != 0) continue;
                // Ufer: vom Rand (Höhe knapp über dem Spiegel) weich in das vorhandene Gelände; je größer der Unterschied,
                // desto breiter der Übergang (hier wird auch ein Damm hochgezogen, wenn das Land niedriger ist)
                double diff = ex - shoreH;
                // schroffer: kürzerer Übergang, Breite und Höhe mit Rauschen, Kante springt unregelmäßig
                double wn = 1 + 0.45 * RoadApplier.Octave(x, z, 30, spec.Seed + 3);
                double width = Math.Clamp(Math.Max(spec.Shore, 1.1 * Math.Abs(diff)) * wn, 4, Bank);   // mindestens die eingestellte Uferbreite
                if (dOut[g] >= width) continue;
                double u = dOut[g] / width;
                target = shoreH + diff * Math.Pow(Smooth(u), 0.8);
                if (spec.Bumps > 0) target += rough * 0.9 * Rugged(x, z, spec.Seed + 5) * Math.Sin(Math.PI * Math.Min(1, u * 1.2));
                if (dOut[g] < 4) target = Math.Max(target, level + WetMargin + 0.1);   // direkt am Wasser nie unter der Wassergrenze (sonst bliebe überflutetes Land mit Gras und Büschen)
            }
            var hv = (ushort)Math.Clamp(Math.Round(target * 256), 0, 65535);
            if (hv != w.Heights[hi])
            {
                e.SetHeight(hi, hv);
                changed++;
                double dz = target - ex;
                maxRaise = Math.Max(maxRaise, dz);
                maxCut = Math.Max(maxCut, -dz);
            }
        }
        e.PaintWaterBiome(spec.Biome);
        return new LakeStats(water, changed, spared, counted > 0 ? sumGround / counted : 0, maxRaise, maxCut, maxDepth);
    }

    const int CraterHardMax = 20_000_000;

    /// Geplante Füllung: je Startpunkt ein Spiegel und die Zellen (Weltindex), die Wasser bekommen
    public sealed class CraterPlan
    {
        public List<(int level, int[] cells)> Groups = new();
        public long Total;        // Zellen (m²) insgesamt
        public bool TooHuge;      // Fläche größer als CraterHardMax: nicht berechnet
    }

    /// Krater planen. Der Klickpunkt hat Bodenhöhe g; der Spiegel liegt „height“ Meter darüber (g + height, auf ganze Meter gerundet).
    /// Von dort wird alles Zusammenhängende gesucht, dessen Boden unter dem Spiegel liegt: das Wasser läuft über jede solche Zelle und füllt
    /// dahinter weiter, bis es auf Boden trifft, der höher ist als der Spiegel. Wasser bekommen alle Zellen mit Boden < Spiegel + 1 m (WetMargin): das Spiel macht jedes Wasserpixel zu Unterwasser-Boden
    /// (oberster Block Erde, Biom „underwater“). Straßen und Gebäude zählen nicht als Hindernis: das
    /// Wasser steigt dort auch, genau wie im Spiel.
    public static CraterPlan PlanCraters(WorldData w, IReadOnlyList<Point> seeds, double height)
    {
        var plan = new CraterPlan();
        int size = w.Size, half = w.Half;
        var mark = new byte[size * size];   // 1 = in dieser Flutung besucht, 2 = schon Wasser aus diesem Plan
        double minDepth = -WetMargin;   // Zellen bis 1 m über dem Spiegel bekommen auch Wasser (siehe WetMargin)
        int strokeLevel = 0;
        foreach (var s in seeds)
        {
            int sx = (int)Math.Floor(s.X), sz = (int)Math.Floor(s.Y);
            if (Math.Abs(sx) >= half - 3 || Math.Abs(sz) >= half - 3) continue;
            int si = w.Index(sx, sz);
            if (mark[si] == 2 || w.WaterMask[si] != 0) continue;
            double g = w.Heights[si] / 256.0;
            // Ein Strich = ein Spiegel (der des ersten Punkts), sonst stünden Wasserflächen mit verschiedenem Stand nebeneinander und liefen ineinander
            if (strokeLevel == 0) strokeLevel = Math.Min(LakeSpec.MaxLevel, Math.Max((int)Math.Floor(g + minDepth) + 1, (int)Math.Round(g + height)));
            int level = strokeLevel;
            if (g >= (level - minDepth)) continue;   // dieser Punkt liegt höher als das Wasser
            double wet = (level - minDepth) * 256.0, pass = wet;   // das Wasser läuft über alles, was selbst Wasser bekommt
            var visited = new List<int> { si };
            var cells = new List<int>();
            mark[si] = 1;
            bool huge = false;
            for (int q = 0; q < visited.Count; q++)
            {
                int c = visited[q];
                if (w.Heights[c] < wet) cells.Add(c);
                int cz = c / size, cx = c % size;
                if (cx < 3 || cz < 3 || cx >= size - 3 || cz >= size - 3) continue;   // Kartenrand
                if (visited.Count > CraterHardMax) { huge = true; break; }
                for (int d = 0; d < 4; d++)
                {
                    int n = c + (d == 0 ? 1 : d == 1 ? -1 : d == 2 ? size : -size);
                    if (mark[n] != 0 || w.Heights[n] >= pass) continue;
                    mark[n] = 1;
                    visited.Add(n);
                }
            }
            foreach (int c in visited) if (mark[c] == 1) mark[c] = 0;
            if (huge) { plan.TooHuge = true; return plan; }
            foreach (int c in cells) mark[c] = 2;
            plan.Groups.Add((level, cells.ToArray()));
            plan.Total += cells.Count;
        }
        return plan;
    }

    /// Krater füllen: planen und die Wasserpixel setzen. Nur splat4.png ändert sich, das Gelände bleibt.
    public static CraterStats FillCraters(WorldData w, WorldEdits e, IReadOnlyList<Point> seeds, double height, int biome = -1)
    {
        var plan = PlanCraters(w, seeds, height);
        if (plan.TooHuge) return new CraterStats(0, 0, 0, 0, true);
        int minL = int.MaxValue, maxL = 0;
        foreach (var (level, cells) in plan.Groups)
        {
            foreach (int c in cells) e.SetWater(c, (byte)level);
            minL = Math.Min(minL, level); maxL = Math.Max(maxL, level);
        }
        e.PaintWaterBiome(biome);
        return new CraterStats((int)plan.Total, plan.Groups.Count, plan.Groups.Count == 0 ? 0 : minL, maxL, false);
    }

    /// Wasser im Bereich entfernen: nur die Wasserpixel (splat4.png) werden gelöscht, das Gelände bleibt genau wie es ist
    public static LakeStats Remove(WorldData w, WorldEdits e, IReadOnlyList<Point> outline, int bumps)
    {
        e.SkipWaterSync();
        var (gx0, gz0, gw, gh, inside) = Grid(w, outline, 2);
        if (gw == 0) return new LakeStats(0, 0, 0, 0, 0, 0, 0);
        int count = 0;
        for (int g = 0; g < gw * gh; g++)
        {
            if (!inside[g]) continue;
            int i = w.Index(gx0 + g % gw, gz0 + g / gw);
            if (w.WaterMask[i] == 0) continue;
            e.SetWater(i, 0);
            count++;
        }
        return new LakeStats(count, 0, 0, 0, 0, 0, 0);
    }
}
