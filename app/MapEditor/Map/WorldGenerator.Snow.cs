namespace MapEditor.Map;

/// Terrain styles of the snow (2026-11-17, the user's request: variety like the desert styles, and towns should fit in better).
/// Style 0 = high mountains (state before, unchanged), 1 = glacial valleys, 2 = sharp peaks, 3 = tundra, 4 = mixed.
public sealed partial class WorldGenerator
{
    /// share of the four snow styles at the place: [0] high mountains, [1] glacial valleys, [2] sharp peaks, [3] tundra (sum 1)
    void SnowStyles(double gx, double gz, Span<double> s)
    {
        s.Clear();
        int st = spec.SnowStyle;
        if (st >= 0 && st <= 3) { s[st] = 1; return; }
        // mixed: four noise fields (regions of about 2 km), the strongest one dominates, soft transitions of a few hundred meters
        double sum = 0;
        for (int k = 0; k < 4; k++)
        {
            double v = snow.Fbm(gx / 2000 + 13.7 * k + 3.1, gz / 2000 + 7.3 * k + 1.9, 2) * 2.2
                + 0.08 * snow.Noise(gx / 350 + 5.5 * k, gz / 350 + 2.2 * k + 0.7);
            double e = Math.Exp(v * 7);
            s[k] = e; sum += e;
        }
        for (int k = 0; k < 4; k++) s[k] /= sum;
    }

    double SnowShare(double gx, double gz, int style)
    {
        if (spec.SnowStyle == 0) return style == 0 ? 1 : 0;
        Span<double> s = stackalloc double[4];
        SnowStyles(gx, gz, s);
        return s[style];
    }

    /// width factor of the glacial valleys (slider "valleys": 120 % = 1)
    double GlacialWidth => Math.Clamp(0.5 + 0.5 * spec.Valleys / 1.2, 0.3, 1.4);

    /// distances to the glacial valley lines (noise units): main valleys (about 2.2 km) and hanging side valleys (about 0.9 km)
    (double Main, double Side) GlacialLines(double gx, double gz)
    {
        double vx = gx + 220 * snow.Noise(gx / 700 + 1.1, gz / 700 + 2.2), vz = gz + 220 * snow.Noise(gx / 700 + 3.3, gz / 700 + 4.4);
        return (Math.Abs(snow.Fbm(vx / 2200 + 9.7, vz / 2200 + 5.1, 2)) * 2.4, Math.Abs(snow.Fbm(vx / 900 + 31.3, vz / 900 + 17.2, 2)) * 2.4);
    }

    /// glacial valleys: wide trough with a flat floor (about 250 m), steep walls that flatten upwards (U shape),
    /// higher hanging side valleys; small moraine bumps on the floors (hollows there become ponds). k = share 0 … 1.
    double GlacialValleys(double gx, double gz, double h, double ground, double k)
    {
        double wf = GlacialWidth;
        var (d1, d2) = GlacialLines(gx, gz);
        double moraine = 2.2 * snow.Fbm(gx / 110 + 6.1, gz / 110 + 2.8, 2) + 1.5 * snow.Noise(gx / 45 + 3.9, gz / 45 + 8.1);
        double floor1 = ground + 4 + 6 * snow.Noise(gx / 900 + 7.7, gz / 900 + 0.3) + moraine;
        double res = UTrough(h, floor1, d1, 0.10 * wf, 0.36 * wf);
        double floor2 = floor1 + 20 + 10 * snow.Noise(gx / 600 + 2.9, gz / 600 + 9.9);
        // side valleys only near a main valley (otherwise the noise lines form closed loops in the middle of the mountains)
        double near = 1 - SmoothStep(0.3 * wf, 0.6 * wf, d1);
        if (near > 0) res = UTrough(res, floor2, d2, 0.06 * wf, 0.22 * wf, 0.85 * near);
        return h + (res - h) * k;
    }

    /// U-shaped cut: inside w0 the floor, up to w1 a wall that is steep at the floor and flat at the top
    static double UTrough(double h, double floor, double d, double w0, double w1, double strength = 1)
    {
        if (h <= floor || d >= w1) return h;
        double t = d <= w0 ? 0 : (d - w0) / (w1 - w0);
        double keep = 1 - Math.Pow(1 - t, 2.5);
        double nh = floor + (h - floor) * keep;
        return h + (nh - h) * strength;
    }

    /// tundra: rolling high plain with rounded knolls, kettle hollows (ponds) and single rock knobs (tors). Meters above the ground.
    double Tundra(double gx, double gz)
    {
        double hillK = Math.Min(spec.Hills, 2) / 1.3;
        double knolls = 10 * Math.Pow(0.5 + 0.5 * Math.Clamp(snow.Fbm(gx / 320 + 19.3, gz / 320 + 4.1, 3) * 1.8, -1, 1), 1.6);
        double swells = 5 * snow.Fbm(gx / 900 + 8.2, gz / 900 + 12.6, 2);
        double kettle = -6 * Math.Pow(SmoothStep(0.3, 0.55, snow.Noise(gx / 140 + 27.4, gz / 140 + 15.2)), 1.5);
        double h = hillK * (knolls + swells) + kettle;
        double tors = TorMask(gx, gz);
        if (tors > 0 && spec.Mountains > 0)
        {
            // single round rock knobs (about 20–40 m wide, up to 14 m high) instead of a continuous pattern
            double knob = SmoothStep(0.22, 0.5, snow.Noise(gx / 75 + 3.3, gz / 75 + 21.7) + 0.1 * snow.Noise(gx / 18 + 1.7, gz / 18 + 5.2));
            h += tors * Math.Min(spec.Mountains, 2) / 1.4 * 14 * Math.Pow(knob, 0.7);
        }
        return h;
    }

    double TorMask(double gx, double gz) => SmoothStep(0.25, 0.45, snow.Noise(gx / 600 + 44.4, gz / 600 + 9.1));

    /// rock type in the snow when a style other than 0 is chosen (otherwise -1 = not decided here):
    /// sharp peaks: hard rock in the mountains (steep faces), soft scree below; glacial valleys: firm walls (stay U-shaped);
    /// tundra: tors hard, rest normal
    int SnowHardness(double gx, double gz, double w3)
    {
        if (spec.SnowStyle == 0 || w3 < 0.5) return -1;
        Span<double> s = stackalloc double[4];
        SnowStyles(gx, gz, s);
        if (s[2] > 0.5)
        {
            double m = MountMask(gx, gz);
            if (m > 0.55) return crag.Noise(gx / 300 + 2.7, gz / 300 + 6.3) > -0.05 ? 1 : 2;
            if (m < 0.25) return 3;
            return 0;
        }
        if (s[1] > 0.5)
        {
            var (d1, d2) = GlacialLines(gx, gz);
            double wf = GlacialWidth;
            return d1 < 0.36 * wf || d2 < 0.22 * wf ? 2 : 0;
        }
        if (s[3] > 0.5) return TorMask(gx, gz) > 0.3 ? 1 : 0;
        return -1;
    }

    /// additional small lakes in glacial valley floors and tundra hollows (own random numbers: the other lakes stay as they were)
    void PlaceSnowLakes()
    {
        if (spec.SnowStyle == 0 || spec.Lakes <= 0) return;
        int total = Math.Max(1, spec.Shares.Sum());
        double snowKm2 = (double)size * size / 1e6 * spec.Shares[3] / total;
        double perKm2 = spec.Lakes switch { 1 => 0.08, 2 => 0.18, _ => 0.35 };
        int want = (int)Math.Round(snowKm2 * perKm2);
        if (want <= 0) return;
        var lr = new Random(spec.SeedValue + 91);
        double margin = Math.Min(500, size * 0.1);
        Span<double> w = stackalloc double[5];
        int added = 0;
        for (int attempt = 0; attempt < want * 40 && added < want; attempt++)
        {
            double cx = margin + lr.NextDouble() * (size - 2 * margin), cz = margin + lr.NextDouble() * (size - 2 * margin);
            double radius = 22 + Math.Pow(lr.NextDouble(), 1.5) * 60;
            double depth = 1.5 + lr.NextDouble() * 3;
            var shape = LakeShape.Random(lr, radius);
            if (BiomeAt(cx, cz) != 3) continue;
            Weights(cx, cz, w);
            if (w[3] < 0.7) continue;
            if (lr.NextDouble() > SnowShare(cx, cz, 1) + SnowShare(cx, cz, 3)) continue;
            (cx, cz) = Downhill(cx, cz, margin);
            if (BiomeAt(cx, cz) != 3) continue;
            if (OverlapsVolcano(cx, cz, radius) || OverlapsAnomaly(cx, cz, shape)) continue;
            if (LakeList.Any(l => Math.Sqrt((l.X - cx) * (l.X - cx) + (l.Z - cz) * (l.Z - cz)) < l.Radius * 1.3 + radius * 1.3 + 120)) continue;
            var (min, max) = PlanRim(cx, cz, shape);   // decision on the plan terrain: the same in the preview and in the world
            if (max - min > 8) continue;
            int planLevel0 = (int)Math.Floor(min) - 1;
            if (planLevel0 < 3 || planLevel0 > 235) continue;
            int level = LakeLevel(cx, cz, shape, planLevel0);
            LakeList.Add(new LakeInfo(cx, cz, shape.Radius, level, depth));
            Carve(LakeList.Count, cx, cz, shape, level, depth, lr);
            added++;
        }
    }
}
