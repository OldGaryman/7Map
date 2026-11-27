using System.Windows;

namespace MapEditor.Map;

/// Shapes on the bed of a lake and an island (2026-11-26, the user's request: "small cliff, a small crack, rock formations, an island
/// with a beach and a steep wall – be creative"). They are placed once per lake from its seed (the same in the preview and when
/// applying), only at least 12 m from the shore (the waterline stays clean), and change the bed relative to its planned height.
public static partial class Lake
{
    /// one shape on the bed: 0 = small cliff (step), 1 = crack, 2 = group of rocks
    sealed class BedFeature
    {
        public int Kind;
        public Point A, B;                 // cliff: the edge from A to B
        public double H, W, Side;          // height (cliff, crack depth), width (crack), side of the higher bed (cliff)
        public List<Point> Path;           // crack: zigzag line
        public List<(Point C, double R, double H)> Rocks;
        public Rect Box;                   // area of influence (fast skip)
        public int Seed;

        /// change of the bed height at (px, pz) in m (+ = higher)
        public double Delta(double px, double pz, int x, int z)
        {
            if (!Box.Contains(px, pz)) return 0;
            switch (Kind)
            {
                case 0:
                {
                    var v = B - A; double len = v.Length; v /= len;
                    double t = (px - A.X) * v.X + (pz - A.Y) * v.Y;
                    double lat = -(px - A.X) * v.Y + (pz - A.Y) * v.X;
                    if (t < -4 || t > len + 4) return 0;
                    double ends = Smooth((t + 4) / 18) * Smooth((len + 4 - t) / 18);
                    lat += 8 * RoadApplier.Octave(x, z, 50, Seed + 2) + 3 * RoadApplier.Octave(x, z, 20, Seed + 7) + 1.6 * RoadApplier.Octave(x, z, 9, Seed) + 0.6 * RoadApplier.Octave(x, z, 3, Seed + 1);   // curved, jagged edge
                    double step = Smooth((Side * lat + 1.5) / 3.0) - 0.5;    // −0.5 … +0.5 within 3 m: a wall
                    double from = 6 + 10 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 40, Seed + 9)), over = 8 + 14 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 30, Seed + 11));
                    double reach = 1 - Smooth((Math.Abs(lat) - from) / over);   // the step fades out irregularly 6–38 m beside the edge (no straight borders)
                    return H * step * reach * ends;
                }
                case 1:
                {
                    double best = double.MaxValue, at = 0, run = 0, total = 0;
                    for (int i = 1; i < Path.Count; i++) total += (Path[i] - Path[i - 1]).Length;
                    for (int i = 1; i < Path.Count; i++)
                    {
                        Point a = Path[i - 1], c = Path[i];
                        double dx = c.X - a.X, dz = c.Y - a.Y, l2 = dx * dx + dz * dz, l = Math.Sqrt(l2);
                        double u = l2 > 0 ? Math.Clamp(((px - a.X) * dx + (pz - a.Y) * dz) / l2, 0, 1) : 0;
                        double ex = px - a.X - u * dx, ez = pz - a.Y - u * dz, dd = Math.Sqrt(ex * ex + ez * ez);
                        if (dd < best) { best = dd; at = run + u * l; }
                        run += l;
                    }
                    double hw = W / 2 * (0.7 + 0.6 * (0.5 + 0.5 * RoadApplier.Octave((int)at, 0, 15, Seed)));   // width changes along the crack
                    if (best >= hw + 1.5) return 0;
                    double ends = Smooth(at / 8) * Smooth((total - at) / 8);
                    double q = best / hw;
                    double cut = q < 1 ? Math.Pow(1 - q * q, 0.35) : 0;                  // steep sides, deep inside
                    double lip = q >= 1 ? 0.25 * Math.Max(0, 1 - (best - hw) / 1.5) : 0;  // a small raised lip at the edge
                    return (-H * cut + lip * H * 0.3) * ends;
                }
                case 3:
                {
                    // sinkhole: round deep hole with steep sides and a small rim
                    double dd = (new Point(px, pz) - A).Length + 1.5 * RoadApplier.Octave(x, z, 7, Seed);
                    if (dd < W) return -H * Math.Pow(1 - dd * dd / (W * W), 0.45);
                    return dd < W + 4 ? 0.12 * H * (1 - (dd - W) / 4) : 0;
                }
                case 4:
                {
                    // underwater ridge: a narrow crest with a jagged top, tapered at the ends
                    var v = B - A; double len = v.Length; v /= len;
                    double t = (px - A.X) * v.X + (pz - A.Y) * v.Y;
                    double lat = -(px - A.X) * v.Y + (pz - A.Y) * v.X + 2 * RoadApplier.Octave(x, z, 18, Seed);
                    if (t < 0 || t > len || Math.Abs(lat) >= W) return 0;
                    double ends = Math.Sin(Math.PI * t / len);
                    double q = lat / W;
                    return H * ends * Math.Pow(1 - q * q, 1.5) * (1 + 0.35 * RoadApplier.Octave(x, z, 6, Seed + 1));
                }
                default:
                {
                    double up = 0;
                    foreach (var (c, r, h) in Rocks)
                    {
                        double dx = px - c.X, dz = pz - c.Y, dd = Math.Sqrt(dx * dx + dz * dz);
                        double rr = r * (1 + 0.3 * RoadApplier.Octave(x, z, 4, Seed + 3));   // irregular rocks
                        if (dd >= rr) continue;
                        double t = 1 - dd / rr;
                        up = Math.Max(up, h * Math.Pow(t, 0.8) * (1 + 0.2 * RoadApplier.Octave(x, z, 2, Seed + 5)));
                    }
                    return up;
                }
            }
        }
    }

    /// island: irregular outline, beach on one side (gentle), steep wall on the other, hilly top
    sealed class IslandShape
    {
        public Point C;
        public double R, Top, BeachAngle, TerraceAngle, TerraceH;
        public readonly double[] Amp = new double[7], Phase = new double[7];
        public int Seed;

        double RadiusAt(double ang)
        {
            double f = 1;
            for (int k = 2; k <= 6; k++) f += Amp[k] * Math.Sin(k * ang + Phase[k]);
            return R * Math.Max(0.45, f);
        }

        /// ground height above the waterline line E at (px, pz); NaN = too far away
        public double Height(double px, double pz, int x, int z)
        {
            double dx = px - C.X, dz = pz - C.Y, r = Math.Sqrt(dx * dx + dz * dz);
            if (r > R * 1.5 + 90) return double.NaN;
            double ang = Math.Atan2(dz, dx);
            double e = RadiusAt(ang) - r + 1.2 * RoadApplier.Octave(x, z, 14, Seed);   // > 0 = on the island
            double k = Smooth(0.5 + 0.5 * Math.Cos(ang - BeachAngle));                  // 1 beach … 0 steep wall
            // steep wall up to 250 % (a real rock wall above the water), beach 12 %; the wall takes a clear part of the outline
            double s = 0.12 + (2.5 - 0.12) * (1 - Smooth((k - 0.1) / 0.6)), sNear = Math.Clamp(s, BankMin, 0.6);
            double Prof(double ee)
            {
                double h = ee <= -1.5 ? -1.5 * sNear + Math.Clamp(s, 0.25, 0.8) * (ee + 1.5)   // under water at least 25 %: the slope meets the bed well before the end of the island area (else a hard edge)
                    : ee <= 1.5 ? sNear * ee
                    : 1.5 * sNear + s * (ee - 1.5);
                // smooth cap at the top (no plateau edge)
                const double kk = 0.8;
                return -kk * Math.Log(Math.Exp(-h / kk) + Math.Exp(-Top / kk));
            }
            // near the waterline averaged across the shore like the lake shore (no teeth); higher up the wall stays sharp
            double avg = (Prof(e - 2) + 2 * Prof(e - 1) + 3 * Prof(e) + 2 * Prof(e + 1) + Prof(e + 2)) / 9;
            double near = 1 - Smooth((Math.Abs(e) - 2) / 2);
            double H = near * avg + (1 - near) * Prof(e);
            if (e > 2)
            {
                double up = Smooth((e - 2) / 8) * Math.Min(1, Top / 6);
                // rocky top: ridges and bumps (like the mountains of the world generator, small)
                double rg = 1 - Math.Abs(RoadApplier.Octave(x, z, 22, Seed + 2));
                H += up * (2.2 * rg * rg - 0.8 + 0.7 * RoadApplier.Octave(x, z, 8, Seed + 4));
                // a rock step across the island
                double lat = -(px - C.X) * Math.Sin(TerraceAngle) + (pz - C.Y) * Math.Cos(TerraceAngle) + 2 * RoadApplier.Octave(x, z, 10, Seed + 6);
                H += up * TerraceH * (Smooth((lat + 1) / 2) - 0.5);
            }
            return H;
        }
    }

    /// choose the shapes and the island for a lake (only cells at least minD from the shore are candidates)
    static (List<BedFeature> Features, IslandShape Island, bool TooSmall) PlaceBedFeatures(LakeSpec spec, bool[] inside, float[] dIn, int gx0, int gz0, int gw, double maxIn)
    {
        var list = new List<BedFeature>();
        var rng = new Random(spec.Seed * 7919 + 13);
        var cand = new List<int>();
        int area = 0;
        for (int g = 0; g < inside.Length; g++)
        {
            if (!inside[g]) continue;
            area++;
            if (dIn[g] > 18) cand.Add(g);
        }
        Point? Pick(double minD, double prefer = 0)
        {
            Point? found = null; double bestD = -1;
            for (int t = 0; t < 40 && cand.Count > 0; t++)
            {
                int g = cand[rng.Next(cand.Count)];
                if (dIn[g] < minD) continue;
                var p = new Point(gx0 + g % gw + 0.5, gz0 + g / gw + 0.5);
                if (prefer <= 0) return p;
                if (dIn[g] > bestD) { bestD = dIn[g]; found = p; }   // prefer: the cell furthest from the shore of a few tries
                if (t >= prefer) break;
            }
            return found;
        }
        // sizes follow the water depth (D); "many" = 3 × "some" (2026-11-26, the user: more anomalies, cliffs not to be seen)
        double D = Math.Max(2, spec.Depth);
        double per = spec.BedFeatures <= 0 ? 0 : spec.BedFeatures == 1 ? 1 : 3;
        int Count(double areaPer) => per == 0 || area < 4000 ? 0 : Math.Max(area > 12000 ? 1 : 0, (int)Math.Round(area / areaPer * per * (0.7 + 0.6 * rng.NextDouble())));
        int nCliff = Count(30000), nCrack = Count(40000), nRocks = Count(18000), nHoles = Count(60000), nRidges = Count(50000);
        for (int i = 0; i < nCliff; i++)
        {
            if (Pick(20) is not Point p) break;
            double ang = rng.NextDouble() * Math.PI, len = Math.Min(30 + 90 * rng.NextDouble(), 1.6 * maxIn);
            var dir = new Vector(Math.Cos(ang), Math.Sin(ang));
            var f = new BedFeature { Kind = 0, A = p - dir * len / 2, B = p + dir * len / 2, H = D * (0.5 + 0.5 * rng.NextDouble()) + 1, Side = rng.Next(2) * 2 - 1, Seed = rng.Next(100000) };
            f.Box = new Rect(Math.Min(f.A.X, f.B.X) - 50, Math.Min(f.A.Y, f.B.Y) - 50, Math.Abs(f.A.X - f.B.X) + 100, Math.Abs(f.A.Y - f.B.Y) + 100);   // reach 30 m + curve 7 m beside the edge
            list.Add(f);
        }
        for (int i = 0; i < nCrack; i++)
        {
            if (Pick(20) is not Point p) break;
            double ang = rng.NextDouble() * Math.PI, len = Math.Min(40 + 110 * rng.NextDouble(), 1.8 * maxIn);
            var dir = new Vector(Math.Cos(ang), Math.Sin(ang)); var nrm = new Vector(-dir.Y, dir.X);
            var path = new List<Point>();
            for (int k = 0; k <= 7; k++)
                path.Add(p + dir * (len * (k / 7.0 - 0.5)) + nrm * ((k is 0 or 7) ? 0 : (rng.NextDouble() - 0.5) * 0.15 * len));   // zigzag
            var f = new BedFeature { Kind = 1, Path = path, W = 3 + 4 * rng.NextDouble(), H = D * (0.3 + 0.4 * rng.NextDouble()) + 1.5, Seed = rng.Next(100000) };
            double x0 = path.Min(q => q.X) - 8, x1 = path.Max(q => q.X) + 8, z0 = path.Min(q => q.Y) - 8, z1 = path.Max(q => q.Y) + 8;
            f.Box = new Rect(x0, z0, x1 - x0, z1 - z0);
            list.Add(f);
        }
        for (int i = 0; i < nRocks; i++)
        {
            if (Pick(18) is not Point p) break;
            var rocks = new List<(Point, double, double)>();
            int m = 2 + rng.Next(6);
            for (int k = 0; k < m; k++)
            {
                double a = rng.NextDouble() * Math.PI * 2, dist = k == 0 ? 0 : 3 + 12 * rng.NextDouble();
                bool spire = rng.NextDouble() < 0.3;   // rock needles: narrow and high
                double r = spire ? 2 + 2 * rng.NextDouble() : 3 + 7 * rng.NextDouble();
                double h = (spire ? 0.6 + 0.4 * rng.NextDouble() : k == 0 ? 0.4 + 0.4 * rng.NextDouble() : 0.2 + 0.4 * rng.NextDouble()) * D + 1;
                rocks.Add((p + new Vector(Math.Cos(a), Math.Sin(a)) * dist, r, h));
            }
            list.Add(new BedFeature { Kind = 2, Rocks = rocks, Box = new Rect(p.X - 30, p.Y - 30, 60, 60), Seed = rng.Next(100000) });
        }
        for (int i = 0; i < nHoles; i++)
        {
            if (Pick(24) is not Point p) break;
            double r = Math.Min(6 + 12 * rng.NextDouble(), 0.4 * maxIn);
            list.Add(new BedFeature { Kind = 3, A = p, W = r, H = D * (0.5 + 0.5 * rng.NextDouble()) + 1, Box = new Rect(p.X - r - 8, p.Y - r - 8, 2 * r + 16, 2 * r + 16), Seed = rng.Next(100000) });
        }
        for (int i = 0; i < nRidges; i++)
        {
            if (Pick(24) is not Point p) break;
            double ang = rng.NextDouble() * Math.PI, len = Math.Min(40 + 100 * rng.NextDouble(), 1.6 * maxIn);
            var dir = new Vector(Math.Cos(ang), Math.Sin(ang));
            var f = new BedFeature { Kind = 4, A = p - dir * len / 2, B = p + dir * len / 2, W = 6 + 8 * rng.NextDouble(), H = D * (0.3 + 0.4 * rng.NextDouble()) + 1, Seed = rng.Next(100000) };
            f.Box = new Rect(Math.Min(f.A.X, f.B.X) - 20, Math.Min(f.A.Y, f.B.Y) - 20, Math.Abs(f.A.X - f.B.X) + 40, Math.Abs(f.A.Y - f.B.Y) + 40);
            list.Add(f);
        }
        IslandShape island = null;
        bool tooSmall = false;
        if (spec.Island)
        {
            double r = Math.Clamp(0.2 * maxIn, 8, 60);
            if (maxIn < r + 25) r = maxIn - 25;
            var p = r >= 8 ? Pick(r + 25, 12) : null;
            if (p is Point c)
            {
                island = new IslandShape { C = c, R = r, Top = Math.Clamp(3 + 0.12 * r + 4 * rng.NextDouble(), 3, 12), BeachAngle = rng.NextDouble() * Math.PI * 2, TerraceAngle = rng.NextDouble() * Math.PI, TerraceH = 1.5 + 2 * rng.NextDouble(), Seed = rng.Next(100000) };
                for (int k = 2; k <= 6; k++) { island.Amp[k] = rng.NextDouble() * 0.32 / Math.Pow(k, 0.8); island.Phase[k] = rng.NextDouble() * Math.PI * 2; }
            }
            else tooSmall = true;
        }
        return (list, island, tooSmall);
    }
}
