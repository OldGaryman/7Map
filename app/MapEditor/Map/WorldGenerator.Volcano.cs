using System.Windows;

namespace MapEditor.Map;

/// Burnt-forest volcanic crater (user request 2026-11-22): always placed in the burnt forest (biome 1) whenever its share > 0,
/// independent of the Anomalies slider. Towers above all other burnt-forest mountains (up to ~215 m, 40 m below the 255 m game ceiling).
/// Uses strong domain-warped caldera geometry, collapsed wall blocks, varying cliff/talus steepness, mini-plateaus and micro-anomalies
/// so the crater looks genuinely fractured and irregular (floor 10–16 m, optional inner crater lake).
/// Prepares a lively 12–20 % contour-hugging spiral 5 m gravel ramp with extra downhill edge clearance, two organically excavated medium
/// POI shelves (Pad 0 on the crater rim, Pad 1 halfway up beside the spiral) with jagged stepped rock cut-walls, and 1–2 landslide sites
/// stored in yamee_volcano.json so WildernessGenerator places the POIs, builds the 5 m gravel road and applies realistic landslides.
public sealed partial class WorldGenerator
{
    public VolcanoSite Volcano { get; private set; }
    /// Diagnostic: (arcDist, plannedH, preCarveGround) along the volcano spiral before carving (used by tests).
    public List<(double Dist, double H, double PreGround)> VolcanoRampFills { get; private set; }
    /// Diagnostic: steepest slope (degrees) on downhill cells 6–20 m beside the road that were raised by the road.
    public double VolcanoMaxFillSlopeDeg { get; private set; }

    VolcanoPlan volcanoPlan;

    sealed record CalderaBlock(double Angle, double HalfWidth, double T0, double T1, double Drop, int Seed);
    sealed record CraterLedge(double X, double Z, double Rx, double Rz, double Axis, double Height, int Seed);
    sealed record CraterMicro(int Kind, double X, double Z, double Len, double Wid, double Axis, double Amp, int Seed);

    sealed class VolcanoPlan
    {
        public double Cx, Cz;                 // grid coordinates (0 .. size)
        public double RimRadius, OuterRadius;
        public double Stretch, Axis;
        public double[] RimAmp = new double[6], RimPhase = new double[6];
        public double[] OuterAmp = new double[6], OuterPhase = new double[6];
        public double[] HeightAmp = new double[4], HeightPhase = new double[4];
        public double BaseAvg, BaseRimH, FloorH, FlankExp, FloorFrac, RibAmp, RibPhase, MidFrac;
        public int RibCount, InnerRibCount, Seed;
        public double InnerRibPhase, FloorPhase1, FloorPhase2;
        public bool HasBench, HasLake;
        public double BenchFrac, BenchWidth;
        public int LakeLevel;
        public double PadRadius;
        public double A0, AJ, A1, Pad1ShoulderU; // angles of rim Pad 0, top spiral junction J, and Pad 1 natural flank shoulder
        public int Dir;                       // +1 or -1
        public double Pad0X, Pad0Z, Pad0H, Pad0Exit;
        public double Pad1X, Pad1Z, Pad1H, Pad1Exit;
        public int Pad0Seed, Pad1Seed;
        public double Jx, Jz, Jh;
        public List<(double X, double Z, double H)> Ramp = new();
        public List<(double X, double Z, double H)> MidBranch = new();
        public List<CalderaBlock> Blocks = new();
        public List<CraterLedge> Ledges = new();
        public List<CraterMicro> Micros = new();
        public List<VolcanoLandslide> Landslides = new();

        public double RimR(double a)
        {
            double t = a - Axis, ra = RimRadius * Math.Sqrt(Stretch), rb = RimRadius / Math.Sqrt(Stretch);
            double c = Math.Cos(t) / ra, s = Math.Sin(t) / rb;
            double e = 1.0 / Math.Sqrt(c * c + s * s);
            double w = 1.0;
            for (int k = 2; k <= 5; k++) w += RimAmp[k] * Math.Sin(k * a + RimPhase[k]);
            return e * Math.Clamp(w, 0.72, 1.30);
        }

        public double OuterR(double a)
        {
            double t = a - Axis, ra = OuterRadius * Math.Sqrt(Stretch), rb = OuterRadius / Math.Sqrt(Stretch);
            double c = Math.Cos(t) / ra, s = Math.Sin(t) / rb;
            double e = 1.0 / Math.Sqrt(c * c + s * s);
            double w = 1.0;
            for (int k = 2; k <= 5; k++) w += OuterAmp[k] * Math.Sin(k * a + OuterPhase[k]);
            return e * Math.Clamp(w, 0.78, 1.26);
        }

        public double RimElevation(double a)
        {
            double h = BaseRimH;
            for (int k = 1; k <= 3; k++) h += HeightAmp[k] * Math.Sin(k * a + HeightPhase[k]);
            return Math.Clamp(h, 80, 215);
        }

        /// Angular weight (0..1) of the naturally grown broad summit shoulder around Pad 0 and top spiral arrival J.
        public double Pad0ShoulderWeight(double a)
        {
            double aMid = A0 - Dir * 0.10;
            double da = Math.Abs(Math.IEEERemainder(a - aMid, Math.PI * 2));
            return 1.0 - SmoothStep(0.18, 0.74, da);
        }

        /// Angular weight (0..1) of the naturally grown mid-flank lava shoulder / spur for Pad 1.
        public double Pad1ShoulderWeight(double a)
        {
            double da = Math.Abs(Math.IEEERemainder(a - A1, Math.PI * 2));
            return 1.0 - SmoothStep(0.14, 0.56, da);
        }

        /// Extra radial rim-crest width grown organically into the volcano summit so Pad 0 sits on a natural broad shoulder.
        public double RimShoulderExtra(double a) => Pad0ShoulderWeight(a) * (PadRadius * 2.15 + 14.0);

        /// Outer volcano radius including the naturally grown outer slope extensions for Pad 0 and Pad 1 shoulders.
        public double OuterRWithShoulders(double a) =>
            OuterR(a) + Pad0ShoulderWeight(a) * (PadRadius * 2.6 + 38.0) + Pad1ShoulderWeight(a) * (PadRadius * 1.4 + 24.0);
    }

    /// Organic, ragged outer radius of a POI shelf while keeping >= 30.5 m inner building core.
    double OrganicPadRadius(VolcanoPlan vp, double a, int padSeed)
    {
        double mod = 0.94
            + 0.14 * Math.Sin(2 * a + padSeed * 0.71)
            + 0.10 * Math.Sin(3 * a + padSeed * 1.13)
            + 0.09 * AngNoise(a, 4.8, padSeed);
        return Math.Max(30.5, vp.PadRadius * Math.Clamp(mod, 0.84, 1.22));
    }

    /// Domain-warped displacement inside and around the crater to break radial funnel symmetry completely.
    (double Wx, double Wz) WarpCrater(VolcanoPlan vp, double gx, double gz)
    {
        double dx = gx - vp.Cx, dz = gz - vp.Cz;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        // full warp inside the caldera and around the rim, fading smoothly to 0 across the outer flank
        double fade = 1.0 - SmoothStep(vp.RimRadius * 1.15, vp.OuterRadius * 0.85, dist);
        if (fade <= 0.001) return (gx, gz);
        double scale = Math.Max(48.0, vp.RimRadius * 0.44);
        double amp = vp.RimRadius * 0.25 * fade;
        double wx = gx + amp * detail.Fbm(gx / scale + vp.Seed * 0.13, gz / scale + 4.7, 3);
        double wz = gz + amp * detail.Fbm(gx / scale + 19.3, gz / scale + vp.Seed * 0.17, 3);
        return (wx, wz);
    }

    /// Phase 1 (before HydraulicErode): plan volcano from resolution-independent RawHeight and shape the cone + irregular deep caldera.
    void PlaceVolcanoBody()
    {
        Volcano = null;
        volcanoPlan = null;
        VolcanoRampFills = null;
        VolcanoMaxFillSlopeDeg = 0;
        if (spec.Shares[1] <= 0 || spec.SkipVolcano) return;

        var r = new Random(spec.SeedValue + 1337);
        // Map-size scale (2k: 0.8, 4k: 0.9, 6k: 1.0, 8k: 1.1, 10k: 1.2, 12k: 1.3; capped at 1.4 for 16k)
        double sizeScale = Math.Clamp(0.7 + 0.1 * (size / 2048.0), 0.8, 1.4);
        // crater diameter (~290–410 m at sizeScale 1.0, scaled smoothly from 2k to 12k+)
        double rimRadius = (145 + r.NextDouble() * 60) * Math.Sqrt(sizeScale);
        double outerRadius = rimRadius * (2.5 + r.NextDouble() * 0.65);
        double margin = Math.Min(Math.Max(outerRadius + 80, 240), size * 0.22);

        // measure highest existing mountain in burnt forest (resolution-independent) so the volcano stands taller than all of them
        double maxBurntH = 75;
        const int Scan = 64;
        for (int sj = 1; sj < Scan; sj++)
            for (int si = 1; si < Scan; si++)
            {
                double sx = (si + 0.5) * size / Scan, sz = (sj + 0.5) * size / Scan;
                if (BiomeAt(sx, sz) != 1) continue;
                double rh = RawHeight(sx, sz, out _);
                if (rh > maxBurntH) maxBurntH = rh;
            }

        // find a good spot inside the burnt forest (biome 1) using resolution-independent RawHeight
        double bestX = -1, bestZ = -1, bestScore = double.NegativeInfinity;
        Span<double> w = stackalloc double[5];
        for (int pass = 0; pass < 2 && bestX < 0; pass++)
        {
            double minWeight = pass == 0 ? 0.72 : 0.35;
            for (int attempt = 0; attempt < 260; attempt++)
            {
                double x = margin + r.NextDouble() * (size - 2 * margin);
                double z = margin + r.NextDouble() * (size - 2 * margin);
                if (BiomeAt(x, z) != 1) continue;
                Weights(x, z, w);
                if (w[1] < minWeight) continue;

                int burntRim = 0;
                double sumH = 0, maxH = 0;
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4;
                    double rx = x + Math.Cos(a) * rimRadius * 1.35, rz = z + Math.Sin(a) * rimRadius * 1.35;
                    if (rx < 60 || rz < 60 || rx > size - 60 || rz > size - 60) { burntRim = -10; break; }
                    if (BiomeAt(rx, rz) == 1) burntRim++;
                    double rh = RawHeight(rx, rz, out _);
                    sumH += rh;
                    if (rh > maxH) maxH = rh;
                }
                if (burntRim < (pass == 0 ? 6 : 3)) continue;
                double avgH = sumH / 8.0;
                if (avgH > 145) continue;

                double score = burntRim * 15 + w[1] * 25 - Math.Abs(avgH - 68) * 0.45 - Math.Max(0, maxH - 115) * 0.6 + r.NextDouble() * 28;
                if (score > bestScore) { bestScore = score; bestX = x; bestZ = z; }
            }
        }
        if (bestX < 0) return;

        var vp = new VolcanoPlan
        {
            Cx = bestX,
            Cz = bestZ,
            RimRadius = rimRadius,
            OuterRadius = outerRadius,
            Stretch = 1.0 + r.NextDouble() * 0.28,
            Axis = r.NextDouble() * Math.PI,
            FlankExp = 1.25 + r.NextDouble() * 0.55,
            FloorFrac = 0.21 + r.NextDouble() * 0.14,
            RibCount = 7 + r.Next(7),
            RibAmp = 4.0 + r.NextDouble() * 5.5,
            RibPhase = r.NextDouble() * Math.PI * 2,
            InnerRibCount = 7 + r.Next(8),
            InnerRibPhase = r.NextDouble() * Math.PI * 2,
            FloorPhase1 = r.NextDouble() * Math.PI * 2,
            FloorPhase2 = r.NextDouble() * Math.PI * 2,
            MidFrac = 0.44 + r.NextDouble() * 0.14,
            Seed = r.Next(10000),
            Pad0Seed = r.Next(10000),
            Pad1Seed = r.Next(10000),
            HasBench = r.NextDouble() < 0.65,
            BenchFrac = 0.40 + r.NextDouble() * 0.26,
            BenchWidth = 0.08 + r.NextDouble() * 0.06,
            HasLake = spec.Lakes > 0 && r.NextDouble() < 0.55,
            PadRadius = 34 + r.NextDouble() * 3.0,   // flat core ~29 m fits medium POIs cleanly without cut-wall drift
            Dir = r.Next(2) == 0 ? 1 : -1
        };

        for (int k = 2; k <= 5; k++)
        {
            vp.RimAmp[k] = (r.NextDouble() * 0.11) / Math.Sqrt(k);
            vp.RimPhase[k] = r.NextDouble() * Math.PI * 2;
            vp.OuterAmp[k] = (r.NextDouble() * 0.12) / Math.Sqrt(k);
            vp.OuterPhase[k] = r.NextDouble() * Math.PI * 2;
        }
        for (int k = 1; k <= 3; k++)
        {
            vp.HeightAmp[k] = (5.5 + r.NextDouble() * 7.5) / k;
            vp.HeightPhase[k] = r.NextDouble() * Math.PI * 2;
        }

        // base terrain elevation around the volcano foot
        double baseSum = 0;
        for (int k = 0; k < 16; k++)
        {
            double a = k * Math.PI * 2 / 16;
            double orad = vp.OuterR(a);
            baseSum += RawHeight(vp.Cx + Math.Cos(a) * orad, vp.Cz + Math.Sin(a) * orad, out _);
        }
        vp.BaseAvg = baseSum / 16.0;

        // Volcano towers above the rest of burnt forest mountains, up to 215 m (40 m below game ceiling 255 m)
        double prominence = (90 + r.NextDouble() * 45) * Math.Sqrt(sizeScale);
        double targetRim = Math.Max(maxBurntH + 16 + r.NextDouble() * 14, vp.BaseAvg + prominence);
        vp.BaseRimH = Math.Clamp(targetRim, 115, 212);

        // deep vent floor: 10–16 m above game 0-point
        if (vp.HasLake)
        {
            vp.FloorH = 10.0 + r.NextDouble() * 3.5;                                  // 10.0 .. 13.5 m
            vp.LakeLevel = (int)Math.Round(vp.FloorH + 3.0 + r.NextDouble() * 2.5);   // 13 .. 19 m
        }
        else
        {
            vp.FloorH = 10.0 + r.NextDouble() * 6.0;                                  // 10.0 .. 16.0 m
            vp.LakeLevel = 0;
        }

        // Pad 0 on the naturally grown broad crater-rim shoulder + top junction J of the wrapping spiral road
        vp.A0 = r.NextDouble() * Math.PI * 2;
        double padOff = vp.PadRadius * 1.15; // keep safely outside the warped inner crater lip
        double r0 = vp.RimR(vp.A0) + padOff;
        vp.Pad0X = vp.Cx + Math.Cos(vp.A0) * r0;
        vp.Pad0Z = vp.Cz + Math.Sin(vp.A0) * r0;
        vp.Pad0H = vp.RimElevation(vp.A0);

        double jAngOff = (vp.PadRadius + 15) / Math.Max(60, r0);
        vp.AJ = vp.A0 - vp.Dir * jAngOff;
        double rJ = vp.RimR(vp.AJ) + padOff;
        vp.Jx = vp.Cx + Math.Cos(vp.AJ) * rJ;
        vp.Jz = vp.Cz + Math.Sin(vp.AJ) * rJ;
        vp.Jh = vp.Pad0H;
        vp.Pad0Exit = Math.Atan2(vp.Jz - vp.Pad0Z, vp.Jx - vp.Pad0X);

        // Plan azimuth & flank position of Pad 1's naturally grown lava shoulder halfway down the spiral
        double estDrop = Math.Max(45.0, vp.Pad0H - vp.BaseAvg);
        double estMidArc = estDrop * (1.0 - vp.MidFrac) / 0.165;
        double estMidR = (vp.RimRadius + vp.OuterRadius) * 0.48;
        double dAng1 = Math.Clamp(estMidArc / Math.Max(120.0, estMidR), 0.72, 1.45);
        vp.A1 = vp.AJ - vp.Dir * dAng1;
        vp.Pad1ShoulderU = Math.Clamp(1.0 - Math.Pow(vp.MidFrac, 1.0 / Math.Max(0.8, vp.FlankExp)), 0.30, 0.62);

        // 3–5 large collapsed caldera wall blocks (huge stepped slump terraces / bays breaking the funnel symmetry!)
        int blockCount = 3 + r.Next(3);
        for (int k = 0; k < blockCount; k++)
        {
            double ba = r.NextDouble() * Math.PI * 2;
            // keep away from Pad 0's rim shoulder
            if (Math.Abs(Math.IEEERemainder(ba - vp.A0, Math.PI * 2)) < 0.45) ba += 0.8;
            double bHalf = 0.28 + r.NextDouble() * 0.36;
            double t0 = 0.30 + r.NextDouble() * 0.25;
            double t1 = Math.Min(0.95, t0 + 0.22 + r.NextDouble() * 0.24);
            double drop = 18 + r.NextDouble() * 28; // dramatic 18–46 m slumped block terrace!
            vp.Blocks.Add(new CalderaBlock(ba, bHalf, t0, t1, drop, r.Next(10000)));
        }

        // 7–11 small inner crater plateaus (distinct jagged rock ledges jutting from the caldera walls)
        int ledgeCount = 7 + r.Next(5);
        for (int k = 0; k < ledgeCount; k++)
        {
            double la = r.NextDouble() * Math.PI * 2;
            double lt = 0.30 + r.NextDouble() * 0.52;
            double lr = vp.RimR(la) * lt;
            double lx = vp.Cx + Math.Cos(la) * lr, lz = vp.Cz + Math.Sin(la) * lr;
            double lrx = (12 + r.NextDouble() * 16) * Math.Sqrt(sizeScale);
            double lrz = (8 + r.NextDouble() * 11) * Math.Sqrt(sizeScale);
            double lh = vp.FloorH + (vp.RimElevation(la) - vp.FloorH) * Math.Clamp((lt - vp.FloorFrac) / (1.0 - vp.FloorFrac), 0.12, 0.88)
                + (r.NextDouble() - 0.5) * 14.0;
            vp.Ledges.Add(new CraterLedge(lx, lz, lrx, lrz, la + Math.PI * 0.5 + (r.NextDouble() - 0.5) * 0.7, Math.Clamp(lh, 16, 200), r.Next(10000)));
        }

        // 11–16 small inner crater anomalies (sharp rock spires, protruding basalt dikes, deep gullies/fissures)
        int microCount = 11 + r.Next(6);
        for (int k = 0; k < microCount; k++)
        {
            int kind = k % 3; // 0 = rock spire/lava tooth, 1 = basalt spur/dike, 2 = deep fissure/chute
            double ma = r.NextDouble() * Math.PI * 2;
            double mt = kind == 0 ? 0.22 + r.NextDouble() * 0.65 : 0.28 + r.NextDouble() * 0.60;
            double mr = vp.RimR(ma) * mt;
            double mx = vp.Cx + Math.Cos(ma) * mr, mz = vp.Cz + Math.Sin(ma) * mr;
            double mLen = kind == 0 ? 6 + r.NextDouble() * 6 : (18 + r.NextDouble() * 24) * Math.Sqrt(sizeScale);
            double mWid = kind == 0 ? 6 + r.NextDouble() * 6 : 5 + r.NextDouble() * 6;
            double mAmp = kind == 0 ? 9 + r.NextDouble() * 12 : kind == 1 ? 9 + r.NextDouble() * 10 : 9 + r.NextDouble() * 10;
            vp.Micros.Add(new CraterMicro(kind, mx, mz, mLen, mWid, ma + (r.NextDouble() - 0.5) * 0.65, mAmp, r.Next(10000)));
        }

        volcanoPlan = vp;

        // Shape the volcano body (outer cone with naturally grown shoulders for BOTH POIs + strongly domain-warped deep caldera)
        double reach = vp.OuterRadius * 1.38 + vp.PadRadius * 3.0 + 48;
        ForCells(vp.Cx, vp.Cz, reach, (idx, gx, gz) =>
        {
            var (wgx, wgz) = WarpCrater(vp, gx, gz);
            double dx = wgx - vp.Cx, dz = wgz - vp.Cz;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            double a = Math.Atan2(dz, dx);
            double rimR = vp.RimR(a);
            double shoulderExtra = vp.RimShoulderExtra(a);
            double effRimR = rimR + shoulderExtra;
            double outerR = vp.OuterRWithShoulders(a);

            // outer cutoff uses unwarped distance so outer blending stays cleanly inside reach
            double udx = gx - vp.Cx, udz = gz - vp.Cz;
            double uDist = Math.Sqrt(udx * udx + udz * udz);
            double uOuterR = vp.OuterRWithShoulders(Math.Atan2(udz, udx));
            if (uDist >= uOuterR && dist >= outerR) return;

            double w0 = vp.Pad0ShoulderWeight(a);
            double rimH = vp.RimElevation(a) + (vp.Pad0H - vp.RimElevation(a)) * w0 * 0.85;
            double baseH = coarse[idx];

            if (dist >= rimR)
            {
                if (shoulderExtra > 1.0 && dist < effRimR)
                {
                    // Naturally grown broad summit shoulder for Pad 0: gently rolling volcanic plateau instead of a sharp cliff edge
                    double st = (dist - rimR) / shoulderExtra;
                    double roll = -2.0 * st * st + 1.4 * Math.Sin(st * Math.PI) * detail.Noise(gx / 28.0 + vp.Seed, gz / 28.0);
                    double target = rimH + roll;
                    coarse[idx] = (float)Math.Clamp(target, 4, 215);
                }
                else
                {
                    // outer flank: adapts smoothly to surrounding terrain with strong volcanic lava ribs & natural Pad 1 flank bench
                    double u = Math.Clamp((dist - effRimR) / Math.Max(1.0, outerR - effRimR), 0, 1);
                    double coneW = Math.Pow(1.0 - SmoothStep(0.0, 1.0, u), vp.FlankExp);
                    double w1 = vp.Pad1ShoulderWeight(a);
                    if (w1 > 0.005)
                    {
                        double du = (u - vp.Pad1ShoulderU) / 0.16;
                        double benchEnv = w1 * Math.Exp(-du * du);
                        double benchConeW = Math.Pow(1.0 - SmoothStep(0.0, 1.0, vp.Pad1ShoulderU), vp.FlankExp);
                        coneW = coneW + (benchConeW - coneW) * (0.58 * benchEnv);
                    }
                    double rib = vp.RibAmp * coneW * (1.0 - coneW) * 4.0 * (1.0 - 0.55 * w0)
                        * (0.55 * Math.Sin(vp.RibCount * a + vp.RibPhase) + 0.45 * AngNoise(a, 5.0, vp.Seed + 17));
                    double target = baseH + (rimH - baseH) * coneW + rib;
                    if (u < 0.10) target += 3.0 * (1.0 - u / 0.10) * (1.0 - 0.7 * w0) * detail.Noise(gx / 22.0 + vp.Seed, gz / 22.0);
                    coarse[idx] = (float)Math.Clamp(target, 4, 215);
                    if (u < 0.08 && w0 < 0.45) hard[idx] = 1;
                }
            }
            else
            {
                // irregular, domain-warped inner caldera: collapsed blocks, wide steepness contrast, deep chutes & rock ridges
                double t = dist / Math.Max(1.0, rimR);
                double floorFracHere = Math.Clamp(vp.FloorFrac * (1.0
                    + 0.34 * Math.Sin(2 * a + vp.FloorPhase1)
                    + 0.24 * Math.Sin(3 * a + vp.FloorPhase2)
                    + 0.22 * AngNoise(a, 3.6, vp.Seed + 51)), 0.11, 0.48);

                double floorNoise = 2.6 * detail.Fbm(gx / 28.0 + vp.Seed * 0.3, gz / 28.0, 3)
                    + 1.4 * detail.Noise(gx / 9.0 + vp.Seed, gz / 9.0 + 4.2);
                double floorTarget = vp.HasLake
                    ? Math.Max(10.0, vp.FloorH - 1.5 * (1.0 - Math.Min(1.0, t / Math.Max(0.05, floorFracHere))) + floorNoise * 0.75)
                    : Math.Clamp(vp.FloorH + floorNoise, 10.0, 16.0);

                double h;
                if (t <= floorFracHere)
                {
                    h = floorTarget;
                }
                else
                {
                    double s = (t - floorFracHere) / Math.Max(0.01, 1.0 - floorFracHere);
                    // strong per-sector steepness contrast: sheer 65° cliff plunge vs stepped 30° talus slope
                    double sectorN = 0.5 + 0.5 * AngNoise(a, 2.2, vp.Seed + 41);
                    double sectorExp = 0.38 + 1.15 * sectorN;
                    double wall = 0.35 * SmoothStep(0.0, 1.0, s) + 0.65 * Math.Pow(s, sectorExp);

                    // irregular broken rock strata terraces (non-concentric thanks to domain warp + angular phase shift)
                    double strataPhase = 2.6 * AngNoise(a, 3.2, vp.Seed + 89) + 1.4 * detail.Noise(gx / 60.0, gz / 60.0);
                    double strata = 0.042 * Math.Sin(s * Math.PI * 6.0 + strataPhase) * Math.Sin(s * Math.PI);
                    wall += strata;

                    if (vp.HasBench)
                    {
                        double bShift = 0.08 * AngNoise(a, 2.5, vp.Seed + 101);
                        double db = (s - (vp.BenchFrac + bShift)) / vp.BenchWidth;
                        wall -= 0.11 * Math.Exp(-db * db);
                    }

                    // bold radial rock buttresses & deep erosion ravines running down the inner wall
                    double ribStrength = 9.0 + 9.5 * (0.5 + 0.5 * AngNoise(a, 2.0, vp.Seed + 63));
                    double innerRib = ribStrength * Math.Sin(s * Math.PI)
                        * (0.50 * Math.Sin(vp.InnerRibCount * a + vp.InnerRibPhase + 0.8 * s)
                           + 0.50 * detail.Fbm(gx / 42.0 + vp.Seed * 0.4, gz / 42.0 + 7.1, 3) * 1.8);
                    double crag = (6.5 * detail.Noise(gx / 22.0 + vp.Seed, gz / 22.0 + 3.1)
                        + 3.2 * detail.Noise(gx / 9.5 + vp.Seed * 0.7, gz / 9.5 + 8.3)) * Math.Sin(s * Math.PI);

                    h = floorTarget + (rimH - floorTarget) * Math.Clamp(wall, 0.0, 1.0) + innerRib + crag;

                    // collapsed caldera wall blocks (big jagged slump benches breaking up the wall)
                    foreach (var blk in vp.Blocks)
                    {
                        double da = Math.Abs(Math.IEEERemainder(a - blk.Angle, Math.PI * 2));
                        if (da >= blk.HalfWidth * 1.35 || t <= blk.T0 - 0.12 || t >= blk.T1 + 0.12) continue;
                        double wa = 1.0 - SmoothStep(blk.HalfWidth * 0.55, blk.HalfWidth * 1.35, da);
                        double wt = SmoothStep(blk.T0 - 0.10, blk.T0 + 0.06, t) * (1.0 - SmoothStep(blk.T1 - 0.06, blk.T1 + 0.10, t));
                        double bn = 0.75 + 0.5 * detail.Noise(gx / 18.0 + blk.Seed, gz / 18.0);
                        h -= blk.Drop * wa * wt * bn;
                    }
                }
                coarse[idx] = (float)Math.Clamp(h, 10.0, 215.0);
                if (t > floorFracHere * 0.55) hard[idx] = 1;
            }
        });

        int half = size / 2;
        Anomalies.Add(("Volcanic crater", (int)Math.Round(vp.Cx) - half, (int)Math.Round(vp.Cz) - half));
    }

    /// Is the ground low enough for the spiral to end here? The volcano is prepared for its road (the user's decision
    /// 2026-11-24): the foot of the spiral always lies down on the plain around the cone, at most FootAbovePlain above the
    /// ground just outside the cone in the same direction – so the wilderness path never has to come down a flank from a
    /// high connection. Before, the spiral stopped on a narrow flank bench (Wupevi-Land: foot at 88 m, plain at 48 m,
    /// behind it 30 m drop over 30 m) and no path could reach a road.
    const double FootAbovePlain = 5.0;
    bool FootIsLow(VolcanoPlan vp, double x, double z, double h) => h <= PlainHeight(vp, x, z) + FootAbovePlain;

    /// ground of the plain just outside the cone in the direction of (x, z): median of 12 samples (4 radii × 3 angles)
    double PlainHeight(VolcanoPlan vp, double x, double z)
    {
        double a0 = Math.Atan2(z - vp.Cz, x - vp.Cx);
        var hs = new List<double>(12);
        foreach (double da in new[] { -0.06, 0.0, 0.06 })
        {
            double a = a0 + da, rOut = vp.OuterRWithShoulders(a);
            foreach (double f in new[] { 1.05, 1.12, 1.2, 1.3 })
                hs.Add(SampleCoarse(Math.Clamp(vp.Cx + Math.Cos(a) * rOut * f, 30, size - 30), Math.Clamp(vp.Cz + Math.Sin(a) * rOut * f, 30, size - 30)));
        }
        hs.Sort();
        return (hs[5] + hs[6]) / 2;
    }

    /// tests: height of the prepared spiral foot above the plain outside the cone (NaN without volcano)
    internal double VolcanoFootAbovePlain()
    {
        var vp = volcanoPlan;
        if (vp == null || vp.Ramp.Count == 0) return double.NaN;
        var (x, z, h) = vp.Ramp[^1];
        return h - PlainHeight(vp, x, z);
    }

    /// Find the radius along ray (angle a) on the outer volcano flank where actual terrain height crosses targetH.
    double FindContourRadius(VolcanoPlan vp, double a, double targetH, double rMin, double rMax)
    {
        const int Samples = 28;
        double prevR = rMin;
        double prevH = SampleCoarse(vp.Cx + Math.Cos(a) * rMin, vp.Cz + Math.Sin(a) * rMin);
        if (prevH <= targetH) return rMin;
        for (int i = 1; i <= Samples; i++)
        {
            double r = rMin + (rMax - rMin) * i / Samples;
            double h = SampleCoarse(vp.Cx + Math.Cos(a) * r, vp.Cz + Math.Sin(a) * r);
            if (h <= targetH)
            {
                double denom = prevH - h;
                double frac = denom > 1e-4 ? (prevH - targetH) / denom : 0.5;
                return prevR + (r - prevR) * Math.Clamp(frac, 0, 1);
            }
            prevR = r;
            prevH = h;
        }
        return rMax;
    }

    /// Phase 2 (after HydraulicErode, before Erode(4)): carve inner crater mini-plateaus & micro-anomalies,
    /// trace a steeper (12–20 %) lively contour-hugging spiral ramp with extra downhill edge clearance,
    /// place Pad 1 halfway up beside the spiral, carve domain-warped tiered rocky POI shelves + road shelf,
    /// gently settle only extreme > 62° glitches (preserving crisp cliffs & crags!), and pick 1–2 landslide spots.
    void PlaceVolcanoPreparations()
    {
        var vp = volcanoPlan;
        if (vp == null) return;

        var pr = new Random(vp.Seed + 4444);

        // 0. Carve inner crater mini-plateaus (ledges) and micro-anomalies (spires, basalt spurs, deep wall fissures)
        foreach (var l in vp.Ledges)
        {
            double reachL = Math.Max(l.Rx, l.Rz) * 1.9 + 8;
            double ca = Math.Cos(l.Axis), sa = Math.Sin(l.Axis);
            ForCells(l.X, l.Z, reachL, (idx, gx, gz) =>
            {
                double dx = gx - l.X, dz = gz - l.Z;
                double u = (dx * ca + dz * sa) / l.Rx, v = (-dx * sa + dz * ca) / l.Rz;
                double d = Math.Sqrt(u * u + v * v);
                double ragged = 1.0 + 0.24 * AngNoise(Math.Atan2(v, u), 3.4, l.Seed);
                double td = d / ragged;
                if (td >= 1.65) return;
                var (wgx, wgz) = WarpCrater(vp, gx, gz);
                double distC = Math.Sqrt((wgx - vp.Cx) * (wgx - vp.Cx) + (wgz - vp.Cz) * (wgz - vp.Cz));
                double rimR = vp.RimR(Math.Atan2(wgz - vp.Cz, wgx - vp.Cx));
                if (distC > rimR * 0.94 || distC < rimR * 0.14) return;
                double wL = 1.0 - SmoothStep(0.72, 1.65, td);
                double shelfH = l.Height + 1.3 * detail.Noise(gx / 7.5 + l.Seed, gz / 7.5);
                coarse[idx] = (float)Math.Clamp(coarse[idx] + (shelfH - coarse[idx]) * wL, 10.0, 215.0);
                if (wL > 0.2) hard[idx] = 1;
            });
        }
        foreach (var m in vp.Micros)
        {
            double reachM = Math.Max(m.Len, m.Wid) * 1.65 + 6;
            double ca = Math.Cos(m.Axis), sa = Math.Sin(m.Axis);
            ForCells(m.X, m.Z, reachM, (idx, gx, gz) =>
            {
                double dx = gx - m.X, dz = gz - m.Z;
                var (wgx, wgz) = WarpCrater(vp, gx, gz);
                double distC = Math.Sqrt((wgx - vp.Cx) * (wgx - vp.Cx) + (wgz - vp.Cz) * (wgz - vp.Cz));
                double rimR = vp.RimR(Math.Atan2(wgz - vp.Cz, wgx - vp.Cx));
                if (distC > rimR * 0.96 || distC < rimR * 0.10) return;
                double u = (dx * ca + dz * sa) / m.Len, v = (-dx * sa + dz * ca) / m.Wid;
                double d = Math.Sqrt(u * u + v * v);
                if (d >= 1.45) return;
                double f = 1.0 - SmoothStep(0.0, 1.45, d);
                double n = 0.78 + 0.45 * detail.Noise(gx / 5.5 + m.Seed, gz / 5.5);
                double delta = m.Kind == 2 ? -m.Amp * f * f * n : m.Amp * Math.Pow(f, 1.3) * n;
                coarse[idx] = (float)Math.Clamp(coarse[idx] + delta, 10.0, 215.0);
                if (f > 0.25) hard[idx] = 1;
            });
        }

        // 1. Trace lively, strictly 12–20 % monotonic contour-hugging spiral cut INTO the mountain flank (no tall embankment!)
        double rimShoulderH = SampleCoarse(vp.Pad0X, vp.Pad0Z);
        vp.Pad0H = Math.Clamp(Math.Max(rimShoulderH + 1.0, vp.Pad0H - 12.0), 85, 215);
        vp.Jh = vp.Pad0H;

        vp.Ramp.Clear();
        double curA = vp.AJ;
        double curR = Math.Sqrt((vp.Jx - vp.Cx) * (vp.Jx - vp.Cx) + (vp.Jz - vp.Cz) * (vp.Jz - vp.Cz));
        double curH = vp.Jh;
        vp.Ramp.Add((vp.Jx, vp.Jz, curH));

        const double StepLen = 12.0;
        const double MinGrade = 0.138;      // 13.8–18.5 % target band -> built 20 m sections stay cleanly in 12–20 %
        const double MaxGrade = 0.185;
        const int MaxSteps = 280;
        double baseGrade = 0.154 + 0.014 * pr.NextDouble();   // ~15.4–16.8 % mean
        double gradePhase = pr.NextDouble() * Math.PI * 2;
        double wigglePhase = pr.NextDouble() * Math.PI * 2;
        double GradeAtStep(int s) => Math.Clamp(baseGrade
            + 0.020 * Math.Sin(s * 0.22 + gradePhase)
            + 0.007 * Math.Cos(s * 0.53 + gradePhase * 1.7), MinGrade, MaxGrade);

        const int ExtraFootSteps = 20;
        int footSteps = -1;
        for (int step = 0; step < MaxSteps; step++)
        {
            double dAngle = StepLen / Math.Max(70.0, curR);
            double nextA = curA - vp.Dir * dAngle;
            double rRimHere = vp.RimR(nextA);
            double rOuterHere = vp.OuterRWithShoulders(nextA);
            // smooth radial freedom (±0.60·Step) so the spiral flows in wide mountain bends instead of sharp zigzags
            double rMin = Math.Max(rRimHere * 1.08, curR - StepLen * 0.60);
            // never below the inner bound (where the rim reaches out far, e.g. at the summit shoulder of Pad 0: Math.Clamp threw)
            double rMax = Math.Max(rOuterHere * 1.14, rRimHere * 1.08);

            double stepGrade = GradeAtStep(step);
            // Passing underneath Pad 0: climb/descend steeper (up to 18.5 %) so the spiral naturally runs lower on the contour instead of pushing out into thin air
            if (step > 10)
            {
                double dAng0 = Math.Abs(Math.IEEERemainder(nextA - vp.A0, Math.PI * 2));
                if (dAng0 < 0.72) stepGrade = Math.Max(stepGrade, 0.182);
            }

            double estH = curH - StepLen * stepGrade;
            double rContour = FindContourRadius(vp, nextA, estH, rRimHere * 1.08, rMax);

            // gentle long-wavelength uphill wiggle only (never sharp jitter, never downhill over the contour line)
            double wiggle = Math.Abs(1.2 * Math.Sin(step * 0.16 + wigglePhase));
            double rTarget = Math.Clamp(rContour - 3.2 - wiggle * 0.5, rRimHere * 1.08, rMax);
            double nextR = Math.Clamp(curR + (rTarget - curR) * 0.75, rMin, Math.Max(rMin, Math.Min(rMax, curR + StepLen * 0.60)));

            double px = Math.Clamp(vp.Cx + Math.Cos(nextA) * nextR, 30, size - 30);
            double pz = Math.Clamp(vp.Cz + Math.Sin(nextA) * nextR, 30, size - 30);
            double gHere = SampleCoarse(px, pz);

            double segLen = Math.Sqrt((px - vp.Ramp[^1].X) * (px - vp.Ramp[^1].X) + (pz - vp.Ramp[^1].Z) * (pz - vp.Ramp[^1].Z));
            double nextH = curH - segLen * stepGrade;

            vp.Ramp.Add((px, pz, nextH));
            curA = nextA;
            curR = nextR;
            curH = nextH;

            // foot reached (spiral meets the ground on the outer flank and the ground lies on the plain): trace some steps more –
            // smoothing and the landing podest of Pad 1 make the spiral shorter and end higher, IntegrateRamp cuts it at the real foot
            if (footSteps < 0 && step > 12 && nextH <= gHere + 1.0 && (nextR >= rOuterHere * 0.92 || nextH <= vp.BaseAvg + 10.0)
                && FootIsLow(vp, px, pz, gHere)) footSteps = ExtraFootSteps;
            if (footSteps >= 0 && footSteps-- == 0) break;
            if (nextH <= 18.0) break;
        }

        // Helper: re-integrate exact monotonic H(s) along (X, Z), optionally embedding a gentle 0–4 % landing podest around podestDist
        List<(double X, double Z, double H)> IntegrateRamp(List<(double X, double Z, double H)> src, double? podestDist)
        {
            var exact = new List<(double X, double Z, double H)>(src.Count) { (src[0].X, src[0].Z, vp.Jh) };
            double hAcc = vp.Jh, sAcc = 0;
            for (int i = 1; i < src.Count; i++)
            {
                var prev = exact[^1];
                var cur = src[i];
                double seg = Math.Sqrt((cur.X - prev.X) * (cur.X - prev.X) + (cur.Z - prev.Z) * (cur.Z - prev.Z));
                if (seg < 1.0) continue;
                double g = GradeAtStep(i - 1);
                double sMid = sAcc + seg * 0.5;
                if (podestDist is double sPod)
                {
                    double dPod = Math.Abs(sMid - sPod);
                    const double PodHalf = 18.0, PodRamp = 10.0;
                    if (dPod <= PodHalf) g = 0.025; // flat 2.5 % landing podest covering the entire Pad 1 Y-fork
                    else if (dPod < PodHalf + PodRamp) g = 0.025 + (g - 0.025) * SmoothStep(PodHalf, PodHalf + PodRamp, dPod);
                    else if (dPod < PodHalf + PodRamp + 45.0) g = Math.Min(MaxGrade, g + 0.010); // gentle compensation outside podest
                }
                double nh = hAcc - seg * g;
                double ground = SampleCoarse(cur.X, cur.Z);
                double rCur = Math.Sqrt((cur.X - vp.Cx) * (cur.X - vp.Cx) + (cur.Z - vp.Cz) * (cur.Z - vp.Cz));
                double rOut = vp.OuterRWithShoulders(Math.Atan2(cur.Z - vp.Cz, cur.X - vp.Cx));
                bool nearFoot = (rCur >= rOut * 0.88 || nh <= vp.BaseAvg + 10.0) && FootIsLow(vp, cur.X, cur.Z, ground);
                if (i > 12 && nearFoot && nh <= ground && hAcc > SampleCoarse(prev.X, prev.Z))
                {
                    double prevDiff = hAcc - SampleCoarse(prev.X, prev.Z);
                    double currDiff = ground - nh;
                    double frac = Math.Clamp(prevDiff / Math.Max(1e-4, prevDiff + currDiff), 0.15, 1.0);
                    double fx = prev.X + (cur.X - prev.X) * frac;
                    double fz = prev.Z + (cur.Z - prev.Z) * frac;
                    exact.Add((fx, fz, hAcc - seg * frac * g));
                    break;
                }
                exact.Add((cur.X, cur.Z, nh));
                hAcc = nh;
                sAcc += seg;
                if (nearFoot && nh <= ground + 0.5) break;
            }
            return exact;
        }

        // Helper: smooth XZ path with gentle Laplacian averaging + explicit turn-angle relaxation so no sharp kinks remain
        List<(double X, double Z, double H)> SmoothRampXZ(List<(double X, double Z, double H)> src, int lapPasses, int curvPasses, double maxTurnDeg)
        {
            var cur = new List<(double X, double Z, double H)>(src);
            for (int p = 0; p < lapPasses && cur.Count >= 4; p++)
            {
                var sm = new List<(double X, double Z, double H)>(cur.Count) { cur[0] };
                for (int i = 1; i < cur.Count - 1; i++)
                    sm.Add((0.22 * cur[i - 1].X + 0.56 * cur[i].X + 0.22 * cur[i + 1].X,
                            0.22 * cur[i - 1].Z + 0.56 * cur[i].Z + 0.22 * cur[i + 1].Z, cur[i].H));
                sm.Add(cur[^1]);
                cur = sm;
            }
            double minCos = Math.Cos(maxTurnDeg * Math.PI / 180.0);
            for (int p = 0; p < curvPasses && cur.Count >= 4; p++)
            {
                var sm = new List<(double X, double Z, double H)>(cur);
                bool any = false;
                for (int i = 1; i < cur.Count - 1; i++)
                {
                    double ax = cur[i].X - cur[i - 1].X, az = cur[i].Z - cur[i - 1].Z;
                    double bx = cur[i + 1].X - cur[i].X, bz = cur[i + 1].Z - cur[i].Z;
                    double la = Math.Sqrt(ax * ax + az * az), lb = Math.Sqrt(bx * bx + bz * bz);
                    if (la < 1e-4 || lb < 1e-4) continue;
                    double cosA = (ax * bx + az * bz) / (la * lb);
                    if (cosA >= minCos) continue;
                    any = true;
                    double mx = 0.5 * (cur[i - 1].X + cur[i + 1].X), mz = 0.5 * (cur[i - 1].Z + cur[i + 1].Z);
                    double blend = cosA < 0.80 ? 0.50 : 0.35;
                    sm[i] = (cur[i].X + (mx - cur[i].X) * blend, cur[i].Z + (mz - cur[i].Z) * blend, cur[i].H);
                }
                cur = sm;
                if (!any) break;
            }
            return cur;
        }

        // Helper: iteratively pull any point with fill > 0.55 m back onto the uphill contour using a 5-point Gaussian spread
        // (so neighboring vertices curve smoothly together instead of snapping a single point into a sharp kink!), smooth XZ, and re-integrate H(s)
        List<(double X, double Z, double H)> RefineContourFill(List<(double X, double Z, double H)> pts, double? podestDist, int passes)
        {
            pts = IntegrateRamp(pts, podestDist);
            for (int pass = 0; pass < passes && pts.Count >= 5; pass++)
            {
                var rawDr = new double[pts.Count];
                for (int i = 1; i < pts.Count - 1; i++)
                {
                    double gHere = SampleCoarse(pts[i].X, pts[i].Z);
                    double fill = pts[i].H - gHere;
                    if (fill <= 0.50) continue;
                    double a = Math.Atan2(pts[i].Z - vp.Cz, pts[i].X - vp.Cx);
                    double rRimHere = vp.RimR(a);
                    double rOuterHere = vp.OuterRWithShoulders(a);
                    double rCur = Math.Sqrt((pts[i].X - vp.Cx) * (pts[i].X - vp.Cx) + (pts[i].Z - vp.Cz) * (pts[i].Z - vp.Cz));
                    double rC = FindContourRadius(vp, a, pts[i].H, rRimHere * 1.05, rOuterHere * 1.14);
                    double rWant = Math.Clamp(rC - 3.6, rRimHere * 1.05, Math.Max(rRimHere * 1.05, rOuterHere * 1.14));
                    double pull = fill > 1.4 ? 0.85 : 0.65;
                    rawDr[i] = Math.Min(0.0, (rWant - rCur) * pull);
                }
                // Spread inward radial adjustment across a smooth 5-point window so the curve bends gradually
                var moved = new List<(double X, double Z, double H)>(pts);
                for (int i = 1; i < pts.Count - 1; i++)
                {
                    double dr = rawDr[i] * 0.44
                        + (i - 1 >= 1 ? rawDr[i - 1] * 0.20 : 0) + (i + 1 < pts.Count - 1 ? rawDr[i + 1] * 0.20 : 0)
                        + (i - 2 >= 1 ? rawDr[i - 2] * 0.08 : 0) + (i + 2 < pts.Count - 1 ? rawDr[i + 2] * 0.08 : 0);
                    // also keep the local inward requirement intact when smoothing would under-correct a deep fill
                    dr = Math.Min(dr, rawDr[i] * 0.72);
                    if (Math.Abs(dr) < 0.05) continue;
                    double a = Math.Atan2(pts[i].Z - vp.Cz, pts[i].X - vp.Cx);
                    double rRimHere = vp.RimR(a);
                    double rOuterHere = vp.OuterRWithShoulders(a);
                    double rCur = Math.Sqrt((pts[i].X - vp.Cx) * (pts[i].X - vp.Cx) + (pts[i].Z - vp.Cz) * (pts[i].Z - vp.Cz));
                    double rNew = Math.Clamp(rCur + dr, rRimHere * 1.05, Math.Max(rRimHere * 1.05, rOuterHere * 1.14));
                    moved[i] = (Math.Clamp(vp.Cx + Math.Cos(a) * rNew, 30, size - 30), Math.Clamp(vp.Cz + Math.Sin(a) * rNew, 30, size - 30), pts[i].H);
                }
                // ALWAYS smooth XZ curvature after every pass (including the final pass!) so no sharp zigzag corners remain
                moved = SmoothRampXZ(moved, pass < passes - 1 ? 3 : 2, 10, 14.0);
                pts = IntegrateRamp(moved, podestDist);
            }
            return pts;
        }

        // Initial smooth + 3 contour-fill refinement passes
        vp.Ramp = SmoothRampXZ(vp.Ramp, 4, 10, 14.0);
        vp.Ramp = RefineContourFill(vp.Ramp, null, 3);

        // 2. Place Pad 1 halfway up beside a gentle landing podest on the spiral, with an oblique (~40°) curved driveway
        // halfway up the cone flank: reference = where the spiral reaches the outer flank (as before the spiral went down
        // to the plain; the stretch over the lowest flank must not pull Pad 1 down)
        double footH = vp.Ramp[^1].H;
        for (int i = 13; i < vp.Ramp.Count; i++)
        {
            var q = vp.Ramp[i];
            double rQ = Math.Sqrt((q.X - vp.Cx) * (q.X - vp.Cx) + (q.Z - vp.Cz) * (q.Z - vp.Cz));
            if (rQ >= vp.OuterRWithShoulders(Math.Atan2(q.Z - vp.Cz, q.X - vp.Cx)) * 0.88 || q.H <= vp.BaseAvg + 10.0) { footH = q.H; break; }
        }
        double targetMidH = footH + (vp.Pad0H - footH) * vp.MidFrac;
        int midIdx = Math.Clamp(vp.Ramp.Count / 2, 5, Math.Max(5, vp.Ramp.Count - 6));
        double bestMidScore = double.MaxValue;
        for (int i = 5; i < vp.Ramp.Count - 5; i++)
        {
            var q = vp.Ramp[i];
            double distPad0 = Math.Sqrt((q.X - vp.Pad0X) * (q.X - vp.Pad0X) + (q.Z - vp.Pad0Z) * (q.Z - vp.Pad0Z));
            if (distPad0 < vp.PadRadius * 2 + 55) continue;
            double rQ = Math.Sqrt((q.X - vp.Cx) * (q.X - vp.Cx) + (q.Z - vp.Cz) * (q.Z - vp.Cz));
            double aQ = Math.Atan2(q.Z - vp.Cz, q.X - vp.Cx);
            double candR = rQ - (vp.PadRadius + 14);
            if (candR < vp.RimR(aQ) + vp.PadRadius * 1.12) continue;
            double candX = vp.Cx + Math.Cos(aQ) * candR, candZ = vp.Cz + Math.Sin(aQ) * candR;
            bool hitsOtherTurn = false;
            for (int j = 0; j < vp.Ramp.Count; j++)
            {
                if (Math.Abs(j - i) <= 7) continue;
                double d2 = (vp.Ramp[j].X - candX) * (vp.Ramp[j].X - candX) + (vp.Ramp[j].Z - candZ) * (vp.Ramp[j].Z - candZ);
                if (d2 < (vp.PadRadius + 18) * (vp.PadRadius + 18)) { hitsOtherTurn = true; break; }
            }
            if (hitsOtherTurn) continue;
            // Prefer the naturally grown flank lava shoulder around vp.A1 while staying near target mid-height
            double score = Math.Abs(q.H - targetMidH) - 9.0 * vp.Pad1ShoulderWeight(aQ);
            if (score < bestMidScore) { bestMidScore = score; midIdx = i; }
        }

        // Compute arc distance of midIdx and embed the 0–4 % landing podest into the spiral, then refine contour fill again!
        double podestDist = 0;
        for (int i = 1; i <= midIdx && i < vp.Ramp.Count; i++)
            podestDist += Math.Sqrt((vp.Ramp[i].X - vp.Ramp[i - 1].X) * (vp.Ramp[i].X - vp.Ramp[i - 1].X)
                + (vp.Ramp[i].Z - vp.Ramp[i - 1].Z) * (vp.Ramp[i].Z - vp.Ramp[i - 1].Z));
        vp.Ramp = RefineContourFill(vp.Ramp, podestDist, 2);

        // Re-locate midIdx at podestDist after podest re-integration
        {
            double sWalk = 0;
            midIdx = Math.Clamp(vp.Ramp.Count / 2, 4, Math.Max(4, vp.Ramp.Count - 5));
            double bestD = double.MaxValue;
            for (int i = 1; i < vp.Ramp.Count - 1; i++)
            {
                sWalk += Math.Sqrt((vp.Ramp[i].X - vp.Ramp[i - 1].X) * (vp.Ramp[i].X - vp.Ramp[i - 1].X)
                    + (vp.Ramp[i].Z - vp.Ramp[i - 1].Z) * (vp.Ramp[i].Z - vp.Ramp[i - 1].Z));
                if (Math.Abs(sWalk - podestDist) < bestD) { bestD = Math.Abs(sWalk - podestDist); midIdx = i; }
            }
            podestDist = 0;
            for (int i = 1; i <= midIdx; i++)
                podestDist += Math.Sqrt((vp.Ramp[i].X - vp.Ramp[i - 1].X) * (vp.Ramp[i].X - vp.Ramp[i - 1].X)
                    + (vp.Ramp[i].Z - vp.Ramp[i - 1].Z) * (vp.Ramp[i].Z - vp.Ramp[i - 1].Z));
        }

        // Place Pad 1 uphill and slightly shifted along the spiral so the curved driveway meets the podest at ~40° (oblique, smooth!)
        var midPt = vp.Ramp[midIdx];
        var mPrev = vp.Ramp[Math.Max(0, midIdx - 1)];
        var mNext = vp.Ramp[Math.Min(vp.Ramp.Count - 1, midIdx + 1)];
        double mTx = mNext.X - mPrev.X, mTz = mNext.Z - mPrev.Z;
        double mTl = Math.Sqrt(mTx * mTx + mTz * mTz);
        if (mTl < 1e-4) { mTx = 1; mTz = 0; } else { mTx /= mTl; mTz /= mTl; }
        // inward (uphill) normal perpendicular to spiral tangent
        double inX = -mTz, inZ = mTx;
        if (inX * (vp.Cx - midPt.X) + inZ * (vp.Cz - midPt.Z) < 0) { inX = -inX; inZ = -inZ; }

        double midSideSign = bestMidScore < double.MaxValue ? 1.0 : -1.0;
        double lateralOff = vp.PadRadius + 16.0;
        double alongOff = 15.0; // gentle offset along the spiral so the curved driveway meets the podest cleanly at ~50° (ArrivalOk >= 45°!)
        // Pick whichever tangent direction (-mTx or +mTx) stays furthest from all other spiral turns!
        double MinSpiralDist(double cx, double cz)
        {
            double minD2 = double.MaxValue;
            for (int j = 0; j < vp.Ramp.Count; j++)
            {
                double dx = vp.Ramp[j].X - cx, dz = vp.Ramp[j].Z - cz;
                double d2 = dx * dx + dz * dz;
                if (d2 < minD2) minD2 = d2;
            }
            return Math.Sqrt(minD2);
        }
        double dirSign = -1.0;
        double xNeg = midPt.X + inX * lateralOff * midSideSign - mTx * alongOff;
        double zNeg = midPt.Z + inZ * lateralOff * midSideSign - mTz * alongOff;
        double xPos = midPt.X + inX * lateralOff * midSideSign + mTx * alongOff;
        double zPos = midPt.Z + inZ * lateralOff * midSideSign + mTz * alongOff;
        if (MinSpiralDist(xPos, zPos) > MinSpiralDist(xNeg, zNeg)) dirSign = 1.0;

        double candP1X = midPt.X + inX * lateralOff * midSideSign + dirSign * mTx * alongOff;
        double candP1Z = midPt.Z + inZ * lateralOff * midSideSign + dirSign * mTz * alongOff;
        // verify uphill offset stays outside warped crater rim; ensure core stays clear of the spiral road!
        double candA1 = Math.Atan2(candP1Z - vp.Cz, candP1X - vp.Cx);
        double candR1 = Math.Sqrt((candP1X - vp.Cx) * (candP1X - vp.Cx) + (candP1Z - vp.Cz) * (candP1Z - vp.Cz));
        if (midSideSign > 0 && candR1 < vp.RimR(candA1) + vp.PadRadius * 1.05)
        {
            candP1X = midPt.X + inX * (vp.PadRadius + 12.0) + dirSign * mTx * 12.0;
            candP1Z = midPt.Z + inZ * (vp.PadRadius + 12.0) + dirSign * mTz * 12.0;
        }
        // if still slightly close to a spiral curve, push a few meters further laterally so Pad 1 core stays 100% flat
        for (int push = 0; push < 4 && MinSpiralDist(candP1X, candP1Z) < vp.PadRadius + 8.0; push++)
        {
            candP1X += inX * 4.0 * midSideSign;
            candP1Z += inZ * 4.0 * midSideSign;
        }
        vp.Pad1X = Math.Clamp(candP1X, 50, size - 50);
        vp.Pad1Z = Math.Clamp(candP1Z, 50, size - 50);
        vp.Pad1H = midPt.H;

        // Smooth curved driveway (quadratic Bezier, 9 waypoints, ALL at exact podest height vp.Pad1H!)
        // Meets the spiral podest at ~50° (strictly >= 45° per YAMEE's ArrivalOk rule so the two roadways separate immediately!)
        vp.MidBranch.Clear();
        double ctrlX = midPt.X + dirSign * mTx * 9.5 + inX * 11.5 * midSideSign;
        double ctrlZ = midPt.Z + dirSign * mTz * 9.5 + inZ * 11.5 * midSideSign;
        double dirEx = ctrlX - vp.Pad1X, dirEz = ctrlZ - vp.Pad1Z;
        double dirEl = Math.Sqrt(dirEx * dirEx + dirEz * dirEz);
        if (dirEl < 1e-4) { dirEx = midPt.X - vp.Pad1X; dirEz = midPt.Z - vp.Pad1Z; dirEl = Math.Max(1.0, Math.Sqrt(dirEx * dirEx + dirEz * dirEz)); }
        dirEx /= dirEl; dirEz /= dirEl;
        vp.Pad1Exit = Math.Atan2(dirEz, dirEx);

        double bx0 = vp.Pad1X + dirEx * (vp.PadRadius - 4.0);
        double bz0 = vp.Pad1Z + dirEz * (vp.PadRadius - 4.0);
        const int BranchSteps = 8;
        for (int b = 0; b <= BranchSteps; b++)
        {
            double t = (double)b / BranchSteps, om = 1.0 - t;
            double qx = om * om * bx0 + 2 * om * t * ctrlX + t * t * midPt.X;
            double qz = om * om * bz0 + 2 * om * t * ctrlZ + t * t * midPt.Z;
            vp.MidBranch.Add((qx, qz, vp.Pad1H));
        }
        // Y-fork inner wedge (V-Zwickel) triangle strictly inside the flat ±18 m podest (8 + 10 m <= 18 m) so it never undercuts the climbing spiral!
        var forkA = (X: midPt.X, Z: midPt.Z);
        var forkB = (X: midPt.X + dirSign * mTx * 8.0, Z: midPt.Z + dirSign * mTz * 8.0);
        var forkC = vp.MidBranch[BranchSteps / 2];

        // 2b. Choose always 2 larger landslide spots immediately before and immediately after Pad 1 (the middle POI)
        //     along the spiral road (1 downhill chute + 1 uphill debris slide) so debris stays high on the cone flank
        //     and city tiles near the volcano foot can never cut off the rubble.
        vp.Landslides.Clear();
        int half = size / 2;
        var rampArc = new double[vp.Ramp.Count];
        for (int i = 1; i < vp.Ramp.Count; i++)
            rampArc[i] = rampArc[i - 1] + Math.Sqrt((vp.Ramp[i].X - vp.Ramp[i - 1].X) * (vp.Ramp[i].X - vp.Ramp[i - 1].X)
                + (vp.Ramp[i].Z - vp.Ramp[i - 1].Z) * (vp.Ramp[i].Z - vp.Ramp[i - 1].Z));
        double midArcRamp = rampArc[Math.Clamp(midIdx, 0, vp.Ramp.Count - 1)];

        var beforeSlides = new List<int>();
        var afterSlides = new List<int>();
        foreach (double maxMid in new[] { 110.0, 170.0, double.MaxValue })
        {
            for (int i = 4; i < vp.Ramp.Count - 4; i++)
            {
                double dMid = Math.Abs(rampArc[i] - midArcRamp);
                if (dMid < 44.0 || dMid > maxMid) continue;
                var q = vp.Ramp[i];
                double d0 = Math.Sqrt((q.X - vp.Pad0X) * (q.X - vp.Pad0X) + (q.Z - vp.Pad0Z) * (q.Z - vp.Pad0Z));
                double d1 = Math.Sqrt((q.X - vp.Pad1X) * (q.X - vp.Pad1X) + (q.Z - vp.Pad1Z) * (q.Z - vp.Pad1Z));
                if (d0 < vp.PadRadius + 24 || d1 < vp.PadRadius + 18) continue;
                if (i < midIdx) { if (!beforeSlides.Contains(i)) beforeSlides.Add(i); }
                else { if (!afterSlides.Contains(i)) afterSlides.Add(i); }
            }
            if (beforeSlides.Count > 0 && afterSlides.Count > 0) break;
        }
        beforeSlides.Sort((a, b) => Math.Abs(rampArc[a] - midArcRamp).CompareTo(Math.Abs(rampArc[b] - midArcRamp)));
        afterSlides.Sort((a, b) => Math.Abs(rampArc[a] - midArcRamp).CompareTo(Math.Abs(rampArc[b] - midArcRamp)));

        var chosenSlides = new List<int>();
        if (beforeSlides.Count > 0) chosenSlides.Add(beforeSlides[pr.Next(Math.Min(3, beforeSlides.Count))]);
        if (afterSlides.Count > 0) chosenSlides.Add(afterSlides[pr.Next(Math.Min(3, afterSlides.Count))]);
        if (chosenSlides.Count < 2)
        {
            var fallback = beforeSlides.Concat(afterSlides).Where(i => chosenSlides.All(c =>
            {
                double dx = vp.Ramp[i].X - vp.Ramp[c].X, dz = vp.Ramp[i].Z - vp.Ramp[c].Z;
                return dx * dx + dz * dz >= 45.0 * 45.0;
            })).ToList();
            if (fallback.Count > 0) chosenSlides.Add(fallback[0]);
            else if (vp.Ramp.Count >= 7)
            {
                for (int i = 2; i < vp.Ramp.Count - 2 && chosenSlides.Count < 2; i++)
                    if (Math.Abs(i - midIdx) >= 2 && !chosenSlides.Contains(i)) chosenSlides.Add(i);
            }
        }

        bool firstBury = pr.Next(2) == 0;
        for (int s = 0; s < chosenSlides.Count; s++)
        {
            int pick = chosenSlides[s];
            var prev = vp.Ramp[Math.Max(0, pick - 1)];
            var curr = vp.Ramp[pick];
            var next = vp.Ramp[Math.Min(vp.Ramp.Count - 1, pick + 1)];
            double tx = next.X - prev.X, tz = next.Z - prev.Z;
            double tl = Math.Sqrt(tx * tx + tz * tz);
            if (tl < 1e-4) { tx = 1; tz = 0; } else { tx /= tl; tz /= tl; }
            double nx = -tz, nz = tx;
            if (nx * (curr.X - vp.Cx) + nz * (curr.Z - vp.Cz) < 0) { nx = -nx; nz = -nz; }
            // always 1 of each type: 1× BuryFromAbove (uphill debris slide) + 1× !BuryFromAbove (downhill chute)
            bool bury = s == 0 ? firstBury : !firstBury;
            vp.Landslides.Add(new VolcanoLandslide
            {
                X = Math.Round(curr.X - half, 1),
                Z = Math.Round(curr.Z - half, 1),
                DirX = Math.Round(tx, 4),
                DirZ = Math.Round(tz, 4),
                DownX = Math.Round(nx, 4),
                DownZ = Math.Round(nz, 4),
                Length = Math.Round(30 + pr.NextDouble() * 14, 1),
                Strength = Math.Round(7.6 + pr.NextDouble() * 3.2, 2),
                BuryFromAbove = bury,
                Seed = pr.Next(100000)
            });
        }

        // 3. Carve the contour-cut spiral shelf (+1 m downhill clearance, natural proportional fill slopes) and gentle POI refinements
        double reach = vp.OuterRadius * 1.38 + vp.PadRadius * 3.0 + 48;
        const double ShelfHalf = 5.8;
        const double DownhillExtra = 1.6;
        const double CutBlend = 15.0;
        const double PadBlendBase = 28.0;
        const double CoreFlatR = 29.0; // larger flat core fits medium POIs cleanly without cut-wall height drift

        // Record pre-carve terrain along the spiral for fill diagnostics
        VolcanoRampFills = new List<(double Dist, double H, double PreGround)>(vp.Ramp.Count);
        {
            double sRec = 0;
            for (int i = 0; i < vp.Ramp.Count; i++)
            {
                if (i > 0)
                    sRec += Math.Sqrt((vp.Ramp[i].X - vp.Ramp[i - 1].X) * (vp.Ramp[i].X - vp.Ramp[i - 1].X)
                        + (vp.Ramp[i].Z - vp.Ramp[i - 1].Z) * (vp.Ramp[i].Z - vp.Ramp[i - 1].Z));
                VolcanoRampFills.Add((sRec, vp.Ramp[i].H, SampleCoarse(vp.Ramp[i].X, vp.Ramp[i].Z)));
            }
        }

        int i0 = Math.Max(1, (int)((vp.Cx - reach) / Cell)), i1 = Math.Min(N - 2, (int)((vp.Cx + reach) / Cell));
        int j0 = Math.Max(1, (int)((vp.Cz - reach) / Cell)), j1 = Math.Min(N - 2, (int)((vp.Cz + reach) / Cell));
        int bw = i1 - i0 + 1, bh = j1 - j0 + 1;
        var pin = bw > 0 && bh > 0 ? new bool[bw * bh] : null;
        var preBox = bw > 0 && bh > 0 ? new float[bw * bh] : null;
        var roadFillBox = bw > 0 && bh > 0 ? new bool[bw * bh] : null;
        if (preBox != null)
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    preBox[(j - j0) * bw + (i - i0)] = coarse[j * N + i];
        double pinR2 = (CoreFlatR + 1.5) * (CoreFlatR + 1.5);

        ForCells(vp.Cx, vp.Cz, reach, (idx, gx, gz) =>
        {
            var (wgx, wgz) = WarpCrater(vp, gx, gz);
            double dx = wgx - vp.Cx, dz = wgz - vp.Cz;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            double a = Math.Atan2(dz, dx);
            double rimR = vp.RimR(a);

            double innerGuard = SmoothStep(rimR * 0.80, rimR * 1.00, dist);

            // Re-assert deep crater floor stays in [10, 16] m
            if (dist < rimR * vp.FloorFrac * 1.05)
            {
                double maxFloor = vp.HasLake ? Math.Min(15.5, vp.FloorH + 0.8) : 16.0;
                if (coarse[idx] > maxFloor)
                {
                    double fw = 1.0 - SmoothStep(rimR * vp.FloorFrac * 0.72, rimR * vp.FloorFrac * 1.05, dist);
                    coarse[idx] = (float)(coarse[idx] + (vp.FloorH - coarse[idx]) * fw);
                }
                if (coarse[idx] < 10.0f) coarse[idx] = 10.0f;
            }

            if (innerGuard <= 0.001) return;

            double cur = coarse[idx];

            // 1. Wrapping spiral ramp & oblique curved mid-height branch shelf (proportional irregular fill slopes!)
            double wRoadSum = 0, whRoadSum = 0, wRoadMax = 0, dRoad = double.MaxValue;
            double fillRoughSum = 0;
            bool isRoadFill = false;
            AccumulateRoad(vp.Ramp, vp.Cx, vp.Cz, gx, gz, cur, ShelfHalf, DownhillExtra, CutBlend, ref wRoadSum, ref whRoadSum, ref wRoadMax, ref dRoad, ref isRoadFill, ref fillRoughSum);
            // funnel widening & smooth Y-fork wedge (V-Zwickel) grading so no sharp rocky hump stands inside the fork!
            double dMid = Math.Sqrt((gx - midPt.X) * (gx - midPt.X) + (gz - midPt.Z) * (gz - midPt.Z));
            double midApron = dMid < 24.0 ? 3.2 * (1.0 - SmoothStep(10.0, 24.0, dMid)) + 0.6 : 0.6;
            AccumulateRoad(vp.MidBranch, vp.Cx, vp.Cz, gx, gz, cur, ShelfHalf + midApron * 0.5, 0.8, CutBlend, ref wRoadSum, ref whRoadSum, ref wRoadMax, ref dRoad, ref isRoadFill, ref fillRoughSum);
            double dFork = DistToTriangle(gx, gz, forkA.X, forkA.Z, forkB.X, forkB.Z, forkC.X, forkC.Z);
            if (dFork < 10.0)
            {
                double wFork = (1.0 - SmoothStep(1.5, 10.0, dFork)) * 0.92;
                double wPow = wFork * wFork * wFork + 1e-4 * wFork;
                wRoadSum += wPow;
                whRoadSum += wPow * vp.Pad1H;
                if (wFork > wRoadMax) wRoadMax = wFork;
                if (dFork < dRoad) dRoad = dFork;
            }
            double wRoad = wRoadMax * innerGuard;
            double hRoad = wRoadSum > 1e-6 ? whRoadSum / wRoadSum : cur;

            // 2. Domain-warped, stepped rocky excavation for Pad 0 and Pad 1 (no smooth geometric scoop!)
            // Suppress Pad 1's rocky cliff crags right inside the Y-fork so no rock tooth protrudes between spiral & driveway
            double cragAllow = SmoothStep(ShelfHalf, ShelfHalf + 10.0, dRoad) * SmoothStep(6.0, 16.0, dFork);
            (double wP0, double hP0, bool cliff0) = EvalRuggedPad(vp, gx, gz, vp.Pad0X, vp.Pad0Z, vp.Pad0H, vp.Pad0Seed, cur, CoreFlatR, PadBlendBase, innerGuard, cragAllow);
            (double wP1, double hP1, bool cliff1) = EvalRuggedPad(vp, gx, gz, vp.Pad1X, vp.Pad1Z, vp.Pad1H, vp.Pad1Seed, cur, CoreFlatR, PadBlendBase, innerGuard, cragAllow);
            if (cliff0 || cliff1) hard[idx] = 1;
            if (dFork < 10.0) hard[idx] = 0; // keep Y-fork wedge smooth earthy ground, never overridden by cliff1!

            // Keep both POI inner building cores cleanly level so no passing road blend tilts a building pad
            double dCore0 = Math.Sqrt((gx - vp.Pad0X) * (gx - vp.Pad0X) + (gz - vp.Pad0Z) * (gz - vp.Pad0Z));
            double dCore1 = Math.Sqrt((gx - vp.Pad1X) * (gx - vp.Pad1X) + (gz - vp.Pad1Z) * (gz - vp.Pad1Z));
            if (dCore0 < CoreFlatR + 1.5 || dCore1 < CoreFlatR + 1.5)
                wRoad *= SmoothStep(CoreFlatR - 2.0, CoreFlatR + 2.0, Math.Min(dCore0, dCore1));

            double wSum = wRoad + wP0 + wP1;
            if (wSum > 0)
            {
                double hTarget = (wRoad * hRoad + wP0 * hP0 + wP1 * hP1) / wSum;
                double wTotal = Math.Max(wRoad, Math.Max(wP0, wP1));
                double nh = cur + (hTarget - cur) * wTotal;
                // Natural earthy/scree roughness on road fill slopes (never rock texture, never smooth artificial ramp!)
                if (isRoadFill && wRoad >= wP0 && wRoad >= wP1 && nh > cur + 0.12 && dRoad > ShelfHalf + 1.5)
                {
                    hard[idx] = 0;
                    if (wRoadSum > 1e-6) nh += (fillRoughSum / wRoadSum) * innerGuard;
                    if (roadFillBox != null)
                    {
                        int ci = idx % N - i0, cj = idx / N - j0;
                        if (ci >= 0 && cj >= 0 && ci < bw && cj < bh) roadFillBox[cj * bw + ci] = true;
                    }
                }
                coarse[idx] = (float)nh;
            }

            if (pin != null)
            {
                int ci = idx % N - i0, cj = idx / N - j0;
                if (ci >= 0 && cj >= 0 && ci < bw && cj < bh)
                {
                    double d0 = (gx - vp.Pad0X) * (gx - vp.Pad0X) + (gz - vp.Pad0Z) * (gz - vp.Pad0Z);
                    double d1 = (gx - vp.Pad1X) * (gx - vp.Pad1X) + (gz - vp.Pad1Z) * (gz - vp.Pad1Z);
                    if (d0 <= pinR2 || d1 <= pinR2 || dRoad <= 5.0) pin[cj * bw + ci] = true;
                }
            }
        });

        // Light slope cap ONLY for extreme vertical glitches (> 64°) on rock, plus true downhill talus transfer on road fill slopes!
        if (pin != null)
        {
            float maxRockStep = (float)(Math.Tan(64.0 * Math.PI / 180.0) * Cell);
            float maxFillStep = (float)(Math.Tan(28.0 * Math.PI / 180.0) * Cell);
            float maxFillDiag = maxFillStep * 1.41421356f;
            var dBuf = new float[bw * bh];
            ReadOnlySpan<(int Di, int Dj, float Cap, float W)> nbs = stackalloc[]
            {
                (-1, 0, maxFillStep, 0.22f), (1, 0, maxFillStep, 0.22f),
                (0, -1, maxFillStep, 0.22f), (0, 1, maxFillStep, 0.22f),
                (-1, -1, maxFillDiag, 0.11f), (1, -1, maxFillDiag, 0.11f),
                (-1, 1, maxFillDiag, 0.11f), (1, 1, maxFillDiag, 0.11f)
            };
            for (int it = 0; it < 28; it++)
            {
                Array.Clear(dBuf, 0, dBuf.Length);
                for (int j = j0 + 1; j < j1; j++)
                    for (int i = i0 + 1; i < i1; i++)
                    {
                        int bi = (j - j0) * bw + (i - i0);
                        int idx = j * N + i;
                        float h = coarse[idx];
                        bool isFill = roadFillBox != null && (roadFillBox[bi] || h > preBox[bi] + 0.30f) && hard[idx] == 0;
                        if (isFill)
                        {
                            // Thermal talus erosion: shed excess slope downward onto lower non-pinned neighbors (never cancels out on slopes!)
                            foreach (var (di, dj, capF, wF) in nbs)
                            {
                                int nIdx = idx + dj * N + di;
                                float diff = h - coarse[nIdx];
                                if (diff <= capF) continue;
                                int nBi = bi + dj * bw + di;
                                float move = (diff - capF) * wF;
                                if (!pin[bi]) dBuf[bi] -= move;
                                if (!pin[nBi] && hard[nIdx] == 0)
                                {
                                    dBuf[nBi] += move;
                                    roadFillBox[nBi] = true;
                                }
                            }
                        }
                        else if (!pin[bi] && it < 12)
                        {
                            dBuf[bi] +=
                                Flow(coarse[idx - 1], h, maxRockStep) + Flow(coarse[idx + 1], h, maxRockStep) +
                                Flow(coarse[idx - N], h, maxRockStep) + Flow(coarse[idx + N], h, maxRockStep);
                        }
                    }
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                        coarse[j * N + i] += dBuf[(j - j0) * bw + (i - i0)];
            }

            // Diagnostic: measure max slope (degrees) on downhill cells 6–20 m beside the road edge that were raised by the road
            double maxFillSlope = 0;
            for (int j = j0 + 1; j < j1; j++)
                for (int i = i0 + 1; i < i1; i++)
                {
                    int bi = (j - j0) * bw + (i - i0);
                    int idx = j * N + i;
                    if (roadFillBox == null || !roadFillBox[bi] || coarse[idx] <= preBox[bi] + 0.35f) continue;
                    double gx = (i + 0.5) * Cell, gz = (j + 0.5) * Cell;
                    var (dr, _) = DistToPolyline(vp.Ramp, gx, gz);
                    double edgeD = dr - (ShelfHalf + DownhillExtra);
                    if (edgeD < 6.0 || edgeD > 20.0) continue;
                    double hx = (coarse[idx + 1] - coarse[idx - 1]) / (2.0 * Cell);
                    double hz = (coarse[idx + N] - coarse[idx - N]) / (2.0 * Cell);
                    double deg = Math.Atan(Math.Sqrt(hx * hx + hz * hz)) * 180.0 / Math.PI;
                    if (deg > maxFillSlope) maxFillSlope = deg;
                }
            VolcanoMaxFillSlopeDeg = maxFillSlope;
        }

        // Export VolcanoSite metadata in world coordinates (-half .. +half)
        Volcano = new VolcanoSite
        {
            CenterX = Math.Round(vp.Cx - half, 1),
            CenterZ = Math.Round(vp.Cz - half, 1),
            OuterRadius = Math.Round(vp.OuterRadius, 1),
            RimRadius = Math.Round(vp.RimRadius, 1),
            RimHeight = Math.Round(vp.BaseRimH, 1),
            FloorHeight = Math.Round(vp.FloorH, 1),
            HasLake = vp.HasLake,
            LakeLevel = vp.LakeLevel,
            NeedsWildernessPois = true,
            PodestDist = Math.Round(podestDist, 1),
            Pads = new List<VolcanoPad>
            {
                new() { X = Math.Round(vp.Pad0X - half, 1), Z = Math.Round(vp.Pad0Z - half, 1), Radius = Math.Round(vp.PadRadius, 1), Height = Math.Round(vp.Pad0H, 1), ExitAngle = Math.Round(vp.Pad0Exit, 4) },
                new() { X = Math.Round(vp.Pad1X - half, 1), Z = Math.Round(vp.Pad1Z - half, 1), Radius = Math.Round(vp.PadRadius, 1), Height = Math.Round(vp.Pad1H, 1), ExitAngle = Math.Round(vp.Pad1Exit, 4) }
            },
            AccessPath = vp.Ramp.Select(q => new VolcanoPoint(Math.Round(q.X - half, 1), Math.Round(q.Z - half, 1), Math.Round(q.H, 2))).ToList(),
            RimConnector = vp.MidBranch.Select(q => new VolcanoPoint(Math.Round(q.X - half, 1), Math.Round(q.Z - half, 1), Math.Round(q.H, 2))).ToList(),
            Landslides = vp.Landslides
        };
    }

    /// Evaluate a naturally fractured, domain-warped POI mountainside excavation:
    /// flat building core (<= coreFlatR), uneven outer yard, and a jagged, multi-tiered rock quarry cut-wall
    /// (alternating steep rock faces, broken strata benches, scree tongues and protruding crags — never a smooth round scoop!).
    (double Weight, double Height, bool HardCliff) EvalRuggedPad(
        VolcanoPlan vp, double gx, double gz, double px, double pz, double padH, int padSeed,
        double cur, double coreFlatR, double padBlendBase, double innerGuard, double cragAllow)
    {
        double rawDx = gx - px, rawDz = gz - pz;
        double rawD = Math.Sqrt(rawDx * rawDx + rawDz * rawDz);
        double maxR = vp.PadRadius * 1.35 + padBlendBase * 1.9 + 18;
        if (rawD >= maxR) return (0, padH, false);

        // Domain-warp coordinates outside the inner building core so the cut-back into the mountainside is jagged & angular
        double warpFade = SmoothStep(coreFlatR - 2.0, coreFlatR + 14.0, rawD) * cragAllow;
        double wDx = rawDx + 13.0 * warpFade * detail.Fbm(gx / 26.0 + padSeed * 0.19, gz / 26.0 + 3.1, 3);
        double wDz = rawDz + 13.0 * warpFade * detail.Fbm(gx / 26.0 + 14.7, gz / 26.0 + padSeed * 0.23, 3);
        double d = Math.Sqrt(wDx * wDx + wDz * wDz);
        double a = Math.Atan2(wDz, wDx);

        double padR = OrganicPadRadius(vp, a, padSeed);
        // vary slope reach strongly with angle: steep 14 m blasted rock faces alternate with wide 44 m broken scree slopes
        double slopeVar = 0.52 + 1.05 * (0.5 + 0.5 * AngNoise(a, 2.8, padSeed + 31));
        double blendR = padBlendBase * slopeVar;
        if (d >= padR + blendR) return (0, padH, false);

        // Inside the actual building core (unwarped rawD <= coreFlatR), guarantee full flat weight
        double wP = rawD <= coreFlatR
            ? innerGuard
            : (1.0 - SmoothStep(padR, padR + blendR, d)) * innerGuard;
        if (rawD > coreFlatR + 2.0) wP *= cragAllow;

        double hP = padH;
        bool hardCliff = false;

        // uneven rocky yard edge outside the building core
        if (rawD > coreFlatR && d < padR + blendR * 0.45)
        {
            double yardT = SmoothStep(coreFlatR, padR, rawD) * (1.0 - SmoothStep(padR, padR + blendR * 0.45, d));
            hP += yardT * cragAllow * (1.1 * detail.Noise(gx / 9.5 + padSeed, gz / 9.5) + 0.6 * detail.Noise(gx / 4.8 + padSeed, gz / 4.8 + 2.1));
        }

        // fractured, stepped rock cut-wall behind the POI only where deep cutting (> 4.5 m) actually happens
        if (rawD > coreFlatR + 3.0 && d > padR * 0.88 && cur > padH + 4.5 && cragAllow > 0.01)
        {
            double bt = Math.Clamp((d - padR * 0.88) / Math.Max(5.0, blendR * 1.12), 0, 1);
            double rise = cur - padH;
            // non-uniform stepped profile: rock benches / broken strata instead of one smooth curve
            double benchWave = Math.Sin(bt * Math.PI * 3.0 + 2.0 * AngNoise(a, 3.5, padSeed + 53)) * Math.Sin(bt * Math.PI);
            double benchOffset = rise * 0.16 * benchWave;
            // bold vertical crags, jutting rock ribs and scree pockets across the excavated cliff face
            double cragAmp = Math.Min(11.0, rise * 0.34) * Math.Sin(bt * Math.PI);
            double crag = cragAmp * (0.55 * detail.Fbm(gx / 16.0 + padSeed * 0.31, gz / 16.0 + 5.7, 3) * 1.7
                + 0.45 * AngNoise(a, 10.0, padSeed + 73));
            hP += (benchOffset + crag) * cragAllow;
            hardCliff = bt > 0.10 && bt < 0.88;
        }
        return (wP, hP, hardCliff);
    }

    static double DistToTriangle(double px, double pz, double ax, double az, double bx, double bz, double cx, double cz)
    {
        // signed area test for inside triangle
        double d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
        double d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
        double d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        if (!(hasNeg && hasPos)) return 0.0;
        static double Seg(double x, double z, double x0, double z0, double x1, double z1)
        {
            double vx = x1 - x0, vz = z1 - z0, l2 = vx * vx + vz * vz;
            double t = l2 < 1e-6 ? 0 : Math.Clamp(((x - x0) * vx + (z - z0) * vz) / l2, 0, 1);
            double dx = x - (x0 + vx * t), dz = z - (z0 + vz * t);
            return Math.Sqrt(dx * dx + dz * dz);
        }
        return Math.Min(Seg(px, pz, ax, az, bx, bz), Math.Min(Seg(px, pz, bx, bz, cx, cz), Seg(px, pz, cx, cz, ax, az)));
    }

    /// Phase 3 (after PlaceRivers): calm fine roughness only on the inner building cores and roadbed,
    /// and register optional inner crater lake for FillWater.
    void PlaceVolcanoLake()
    {
        var vp = volcanoPlan;
        if (vp == null) return;

        double reach = vp.OuterRadius * 1.38 + vp.PadRadius * 3.0 + 48;
        ForCells(vp.Cx, vp.Cz, reach, (idx, gx, gz) =>
        {
            double c = calm[idx];
            double dP0 = Math.Sqrt((gx - vp.Pad0X) * (gx - vp.Pad0X) + (gz - vp.Pad0Z) * (gz - vp.Pad0Z));
            if (dP0 < 36) c = Math.Min(c, SmoothStep(28, 36, dP0));
            double dP1 = Math.Sqrt((gx - vp.Pad1X) * (gx - vp.Pad1X) + (gz - vp.Pad1Z) * (gz - vp.Pad1Z));
            if (dP1 < 36) c = Math.Min(c, SmoothStep(28, 36, dP1));
            var (dRamp, _) = DistToPolyline(vp.Ramp, gx, gz);
            var (dMid, _) = DistToPolyline(vp.MidBranch, gx, gz);
            double dRoad = Math.Min(dRamp, dMid);
            if (dRoad < 13) c = Math.Min(c, SmoothStep(5.8, 13, dRoad));
            calm[idx] = (float)c;
        });

        if (!vp.HasLake || vp.LakeLevel < 12) return;

        double lakeRad = vp.RimRadius * (vp.FloorFrac + 0.16);
        int id = LakeList.Count + 1;
        LakeList.Add(new LakeInfo(vp.Cx, vp.Cz, lakeRad, vp.LakeLevel, Math.Max(2.0, vp.LakeLevel - vp.FloorH)));
        ForCells(vp.Cx, vp.Cz, vp.RimRadius * 0.95, (idx, gx, gz) =>
        {
            var (wgx, wgz) = WarpCrater(vp, gx, gz);
            double dx = wgx - vp.Cx, dz = wgz - vp.Cz;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            double rimR = vp.RimR(Math.Atan2(dz, dx));
            if (dist < rimR * 0.85 && coarse[idx] < vp.LakeLevel + 6)
            {
                lakeOf[idx] = id;
                if (coarse[idx] < vp.LakeLevel + 2) calm[idx] = 0f;
            }
        });
    }

    /// helper for PlaceLakes: returns true if a random lake at (cx, cz) with radius would overlap the volcano cone
    bool OverlapsVolcano(double cx, double cz, double radius)
    {
        if (volcanoPlan == null) return false;
        double dx = cx - volcanoPlan.Cx, dz = cz - volcanoPlan.Cz;
        double keep = volcanoPlan.OuterRadius + radius + 80;
        return dx * dx + dz * dz < keep * keep;
    }

    /// Smooth multi-segment road shelf accumulator: uphill cuts stay steep & rocky, while any downhill fill gets
    /// a natural height-proportional repose slope (fill * 1.9, ~28–30°) with irregular toe width (±30 %) and scree roughness.
    void AccumulateRoad(List<(double X, double Z, double H)> line, double vcx, double vcz, double gx, double gz, double cur,
        double shelfHalf, double downhillExtra, double cutBlend,
        ref double wSum, ref double whSum, ref double wMax, ref double minDist, ref bool isFill, ref double fillRoughSum)
    {
        const double MaxFillBlend = 32.0;
        double maxReach = shelfHalf + downhillExtra + Math.Max(cutBlend, MaxFillBlend * 1.35);
        double maxReach2 = maxReach * maxReach;
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var b = line[i];
            double vx = b.X - a.X, vz = b.Z - a.Z;
            double l2 = vx * vx + vz * vz;
            double t = l2 < 1e-6 ? 0 : Math.Clamp(((gx - a.X) * vx + (gz - a.Z) * vz) / l2, 0, 1);
            double px = a.X + vx * t, pz = a.Z + vz * t;
            double d2 = (gx - px) * (gx - px) + (gz - pz) * (gz - pz);
            if (d2 >= maxReach2) continue;
            double d = Math.Sqrt(d2);
            if (d < minDist) minDist = d;
            double h = a.H + (b.H - a.H) * t;
            double outDot = (gx - px) * (px - vcx) + (gz - pz) * (pz - vcz);
            double effShelf = outDot > 0 ? shelfHalf + downhillExtra : shelfHalf;
            bool segFill = cur < h;
            double blend;
            if (!segFill)
            {
                blend = cutBlend;
            }
            else
            {
                double fillH = h - cur;
                double baseFill = Math.Clamp(Math.Max(7.0, fillH * 2.15), 7.0, 32.0);
                // irregular toe width (±25–30 %) along the road so embankment foot is never a straight artificial line
                double toeNoise = 1.0 + 0.22 * detail.Noise(gx / 15.0 + 3.1, gz / 15.0 + 7.7)
                                      + 0.10 * detail.Noise(gx / 6.0 + 11.4, gz / 6.0 + 2.3);
                blend = baseFill * Math.Clamp(toeNoise, 0.78, 1.32);
            }
            if (d >= effShelf + blend) continue;
            double w = 1.0 - SmoothStep(effShelf, effShelf + blend, d);
            double wPow = w * w * w + 1e-4 * w;
            wSum += wPow;
            whSum += wPow * h;
            if (segFill && d > effShelf + 2.5)
            {
                isFill = true;
                double ft = Math.Clamp((d - effShelf) / Math.Max(1.0, blend), 0, 1);
                double scree = Math.Sin(ft * Math.PI) * (0.45 * detail.Noise(gx / 7.0 + 5.2, gz / 7.0 + 1.9)
                                                        + 0.25 * detail.Noise(gx / 3.2 + 13.7, gz / 3.2 + 8.4));
                fillRoughSum += wPow * scree;
            }
            if (w > wMax) wMax = w;
        }
    }

    static (double Dist, double H) DistToPolyline(List<(double X, double Z, double H)> line, double gx, double gz)
    {
        double bestD2 = double.MaxValue, bestH = 0;
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var b = line[i];
            double vx = b.X - a.X, vz = b.Z - a.Z;
            double l2 = vx * vx + vz * vz;
            double t = l2 < 1e-6 ? 0 : Math.Clamp(((gx - a.X) * vx + (gz - a.Z) * vz) / l2, 0, 1);
            double px = a.X + vx * t, pz = a.Z + vz * t;
            double d2 = (gx - px) * (gx - px) + (gz - pz) * (gz - pz);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestH = a.H + (b.H - a.H) * t;
            }
        }
        return (Math.Sqrt(bestD2), bestH);
    }
}
