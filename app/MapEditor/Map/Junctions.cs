using System.Windows;

namespace MapEditor.Map;

/// Connection of a new road to an existing one (the user's request, 2026-10-28): intersections should be almost level.
/// If the existing road at the intersection is steeper than the tolerance (adjustable per surface of the new road), it is rebuilt to about Reach meters per side:
/// around the intersection an area with at most this gradient, outwards softly back to the old height.
public sealed class Junction
{
    public bool AtStart;
    public Point Center;              // intersection on the existing road
    public double Height;             // height of the intersection (the new road ends here)
    public double ExistingGrade;      // gradient of the existing road at the intersection (fraction, signed)
    public Surface Kind;
    public double Width;              // roadway of the existing road (measured)
    public Vector Direction;          // direction of the existing road at the intersection (length 1, in direction of ExistingGrade)
    public double PlateauGrade;       // gradient of the existing road at the intersection after the rebuild (without rebuild = ExistingGrade)
    /// higher-ranking (asphalt with a new gravel path): is not rebuilt, the new road adapts
    public bool Protected;
    /// other paths that connect or cross in the rebuilt piece: the height stays there, the rebuild ends before it
    public List<Point> HeldBranches = new();
    /// rebuild of the existing road (null = not necessary, it is already flat enough there)
    public List<Point> RebuildPoints;
    public List<double?> RebuildHeights;
    public RoadSettings RebuildSettings;
    public RoadResult RebuildResult;  // preview
}

public static class Junctions
{
    public const double DefaultTolerance = 0.02;   // default: 2 % gradient may remain at the intersection
    const double Reach = 70;                // so far (m) the existing road is followed/rebuilt per side

    /// end = start/end of the new road; null if there is no existing road there (outside of tiles)
    /// along: direction in which the new road runs from here (for intersections in the middle of the road bothWays = true).
    /// This direction (±35°) is excluded: if there is already a road there (e.g. the old one that is redrawn),
    /// it is not the road to which to connect (2026-10-28: the rebuild otherwise ran north → west instead of north → south)
    /// newKind: surface of the new road. Ranking (the user's request 2026-10-28): asphalt before gravel – a new
    /// gravel path never rebuilds an asphalt road, it adapts (height, cross slope)
    public static Junction Find(WorldData w, Point end, bool atStart, double newHalfWidth, double tolerance, Func<Surface, RoadSettings> settingsFor,
        Vector? along = null, bool bothWays = false, Surface newKind = Surface.Asphalt)
    {
        double alongAngle = along is Vector av && av.Length > 0 ? Math.Atan2(av.Y, av.X) * 180 / Math.PI : double.NaN;
        bool Excluded(double ang)
        {
            if (double.IsNaN(alongAngle)) return false;
            double off = Math.Abs(((ang - alongAngle) % 360 + 540) % 360 - 180);   // 0 = exactly in the direction of the new road
            return off <= 35 || bothWays && off >= 145;
        }
        var tiles = w.Prefabs.Where(p => p.IsTile && p.X < end.X + Reach + 20 && p.X + p.SizeX > end.X - Reach - 20
                                                  && p.Z < end.Y + Reach + 20 && p.Z + p.SizeZ > end.Y - Reach - 20).ToList();
        bool Road(double x, double z) => w.RoadAt((int)Math.Floor(x), (int)Math.Floor(z)) != 0 && !tiles.Any(t => t.Contains(x, z));
        if (tiles.Any(t => t.Contains(end.X, end.Y))) return null;   // tiles: own rule (tile height)
        bool onRoad = false;
        for (int dz = -2; dz <= 2 && !onRoad; dz++)
            for (int dx = -2; dx <= 2 && !onRoad; dx++)
                onRoad = Road(end.X + dx, end.Y + dz);
        if (!onRoad) return null;

        // surface and direction of the existing road at the intersection (main axis of the road pixels within 10 m)
        int asphalt = 0, gravel = 0;
        double sx = 0, sz = 0, sxx = 0, szz = 0, sxz = 0;
        int cnt = 0;
        for (int dz = -10; dz <= 10; dz++)
            for (int dx = -10; dx <= 10; dx++)
            {
                if (dx * dx + dz * dz > 100 || !Road(end.X + dx, end.Y + dz)) continue;
                if (dx * dx + dz * dz > 9 && Excluded(Math.Atan2(dz, dx) * 180 / Math.PI)) continue;   // road under the new one
                if (w.RoadAt((int)Math.Floor(end.X + dx), (int)Math.Floor(end.Y + dz)) == WorldData.Asphalt) asphalt++; else gravel++;
                sx += dx; sz += dz; sxx += dx * dx; szz += dz * dz; sxz += dx * dz; cnt++;
            }
        var kind = asphalt >= gravel ? Surface.Asphalt : Surface.Gravel;
        double mx = sx / cnt, mz = sz / cnt;
        double cxx = sxx / cnt - mx * mx, czz = szz / cnt - mz * mz, cxz = sxz / cnt - mx * mz;
        double axis = 0.5 * Math.Atan2(2 * cxz, cxx - czz) * 180 / Math.PI;   // direction of the road (degrees)

        // follow the road per direction (axis and axis+180): ring in Reach meters, near tiles or curves also closer;
        // only hits roughly in road direction and with the same surface (otherwise a branching path would be rebuilt too)
        // The path must stay almost completely (90 %) on the same surface: otherwise it turns into a branching path
        // (2026-10-28: asphalt road → gravel path, the gravel path was rebuilt 20 m wide too)
        byte want = kind == Surface.Asphalt ? WorldData.Asphalt : WorldData.Gravel;
        bool Pure(TraceResult t)
        {
            var samples = Resample(t.Points, 2);
            return samples.Count(p => w.RoadAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)) == want) >= 0.9 * samples.Count;
        }
        var sides = new List<TraceResult>();
        foreach (double dir in new[] { axis, axis + 180 })
        {
            bool found = false;
            foreach (double radius in new[] { Reach, 55.0, 40.0, 28.0 })
            {
                var hits = new List<int>();
                for (int a = 0; a < 360; a++)
                    if (Road(end.X + radius * Math.Cos(a * Math.PI / 180), end.Y + radius * Math.Sin(a * Math.PI / 180))) hits.Add(a);
                var cand = Clusters(hits).Select(a => (a, off: Math.Abs(((a - dir) % 360 + 540) % 360 - 180)))
                    .Where(c => c.off <= 50 && !Excluded(c.a)).OrderBy(c => c.off).ToList();
                foreach (var (best, _) in cand)
                {
                    var target = new Point(end.X + radius * Math.Cos(best * Math.PI / 180), end.Y + radius * Math.Sin(best * Math.PI / 180));
                    var t = RoadTracer.Trace(w, end, target, out _);
                    if (t == null || t.Length > radius * 1.5 || t.Points.Count < 2 || !Pure(t)) continue;
                    sides.Add(t);
                    found = true;
                    break;
                }
                if (found) break;
            }
        }
        if (sides.Count == 0) return null;

        // center line: side A backwards to the intersection, then side B
        var line = new List<Point>(Enumerable.Reverse(sides[0].Points));
        if (sides.Count > 1) line.AddRange(sides[1].Points.Skip(1));
        // If the connection lies at the edge of a wide road, both sides first run across to the center; this cross piece is flat
        // and falsifies the measured gradient (2026-10-28: −4.2 % measured, actually −8 %). Therefore follow the center line
        // once more from end to end and look for the intersection on it.
        if (sides.Count > 1)
        {
            var through = RoadTracer.Trace(w, sides[0].Points[^1], sides[1].Points[^1], out _);
            if (through != null && through.Points.Count >= 2 && Pure(through) && through.Length < line.Count * 4 + 200) line = through.Points;
        }
        var pts = Resample(line, 2);
        int ci = NearestIndex(pts, end);
        var dist = Distances(pts);
        double dJ = dist[ci];
        double H(double d) => Avg(pts, dist, d, w);   // height of the existing road (mean over ±3 m)
        var j = new Junction
        {
            AtStart = atStart,
            Center = pts[ci],
            Height = H(dJ),
            Kind = kind,
            Width = Math.Max(4, sides.Average(s => s.Width) - 2),
        };
        double lo = Math.Max(0, dJ - 10), hi = Math.Min(dist[^1], dJ + 10);
        j.ExistingGrade = hi - lo > 4 ? (H(hi) - H(lo)) / (hi - lo) : 0;
        var dv = pts[Math.Min(pts.Count - 1, ci + 3)] - pts[Math.Max(0, ci - 3)];
        j.Direction = dv.Length > 0 ? dv / dv.Length : new Vector(1, 0);
        j.PlateauGrade = j.ExistingGrade;
        // tolerance: what may remain at the intersection (intersection tolerance or allowed cross slope of the new road)
        if (Math.Abs(j.ExistingGrade) <= tolerance + 0.005 || sides.Count < 2 && dist[^1] < 20) return j;   // flat enough (0.5 % margin)
        if (Rank(kind) > Rank(newKind)) { j.Protected = true; return j; }   // higher-ranking road stays as it is
        if (Math.Abs(j.ExistingGrade) > 0.25) return j;   // over 25 %: not a drivable path (surface remnants on a slope, measurement error) – do not rebuild

        // rebuild as a soft deformation (2026-10-28; before, a hard plate with kinks of 13 % at its edges): new height =
        // old height + correction; the correction brings the road around the intersection (±R) onto a line with at most the tolerance
        // as gradient and runs out with an S-curve outwards to the end of the rebuilt piece to 0. All points fixed.
        double R = Math.Max(6, newHalfWidth + 4);   // area around the intersection: width of the new road plus some margin
        double g = Math.Sign(j.ExistingGrade) * tolerance;
        j.PlateauGrade = g;
        // other paths that connect or cross in the rebuilt piece (surface right beside the edge of the road, at least
        // 4 m wide): the height stays there so that their connection is not bent – the rebuild already runs out before it
        // (2026-10-28: two gravel paths at the same asphalt road bent the road for each other)
        double leftEnd = 0, rightEnd = dist[^1];
        for (int i = 1; i < pts.Count - 1; i++)
        {
            double off = dist[i] - dJ;
            if (Math.Abs(off) < R + 6) continue;   // at the intersection itself lies the new road
            var t = pts[i + 1] - pts[i - 1];
            if (t.Length < 1e-6) continue;
            var n = new Vector(-t.Y, t.X) / t.Length;
            foreach (int side in new[] { -1, 1 })
            {
                double o1 = j.Width / 2 + 3, o2 = j.Width / 2 + 6;
                var p1 = pts[i] + n * side * o1;
                var p2 = pts[i] + n * side * o2;
                if (!Road(p1.X, p1.Y) || !Road(p2.X, p2.Y)) continue;
                if (off < 0 && dist[i] > leftEnd) leftEnd = dist[i];
                if (off > 0 && dist[i] < rightEnd) rightEnd = dist[i];
                if (j.HeldBranches.Count == 0 || (j.HeldBranches[^1] - pts[i]).Length > 15) j.HeldBranches.Add(pts[i]);
            }
        }
        // distance from the edge of the intersection area to where the correction must be 0 (connection with 6 m distance)
        double left = Math.Max(4, dJ - R - (leftEnd > 0 ? leftEnd + 6 : 0)), right = Math.Max(4, (rightEnd < dist[^1] ? rightEnd - 6 : dist[^1]) - dJ - R);
        double Corr(double d)
        {
            double off = d - dJ, plateau = j.Height + g * off, full = plateau - H(d);
            double a = Math.Abs(off);
            if (a <= R) return full;
            double t = Math.Clamp((a - R) / (off < 0 ? left : right), 0, 1);
            double keep = 1 - t * t * (3 - 2 * t);
            // outside: let the deviation at the edge of the area run out softly
            double edge = j.Height + g * Math.Sign(off) * R - H(dJ + Math.Sign(off) * R);
            return edge * keep;
        }
        var ctrl = new List<Point>();
        var heights = new List<double?>();
        for (int i = 0; i < pts.Count; i += 2)   // a fixed point every 4 m
        {
            ctrl.Add(pts[i]);
            heights.Add(H(dist[i]) + Corr(dist[i]));
        }
        if ((ctrl[^1] - pts[^1]).Length > 1) { ctrl.Add(pts[^1]); heights.Add(H(dist[^1]) + Corr(dist[^1])); }

        var rs = settingsFor(j.Kind).Clone();
        rs.Width = j.Width;
        rs.Shoulder = 0;
        rs.HeightOffset = 0;
        rs.PaintSurface = false;     // change only the heights, the surface of the existing road stays as it is
        rs.MaxGrade = Math.Max(rs.MaxGrade, Math.Abs(j.ExistingGrade) * 1.5);   // slightly steeper allowed outside the intersection
        j.RebuildPoints = ctrl;
        j.RebuildHeights = heights;
        j.RebuildSettings = rs;
        j.RebuildResult = RoadPlanner.Compute(w, ctrl, rs, heights);
        return j;
    }

    /// rank of a road: asphalt before gravel
    public static int Rank(Surface s) => s == Surface.Asphalt ? 2 : 1;

    // combine connected angles (also across 359/0) to their center
    static List<double> Clusters(List<int> hits)
    {
        var res = new List<double>();
        if (hits.Count == 0) return res;
        if (hits.Count == 360) return res;   // completely surrounded by road: no direction recognizable
        var set = new HashSet<int>(hits);
        int startA = Enumerable.Range(0, 360).First(a => !set.Contains(a));
        var cur = new List<int>();
        for (int k = 1; k <= 360; k++)
        {
            int a = (startA + k) % 360;
            if (set.Contains(a)) { cur.Add(startA + k); continue; }
            if (cur.Count > 0) { res.Add(cur.Average() % 360); cur.Clear(); }
        }
        return res;
    }

    static List<Point> Resample(List<Point> line, double step)
    {
        var res = new List<Point> { line[0] };
        double carry = 0;
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var v = line[i] - a;
            double len = v.Length, t = step - carry;
            while (t <= len) { res.Add(a + v * (t / len)); t += step; }
            carry = len - (t - step);
        }
        if ((res[^1] - line[^1]).Length > 0.5) res.Add(line[^1]);
        return res;
    }

    static double[] Distances(List<Point> p)
    {
        var d = new double[p.Count];
        for (int i = 1; i < p.Count; i++) d[i] = d[i - 1] + (p[i] - p[i - 1]).Length;
        return d;
    }

    static int NearestIndex(List<Point> p, Point q)
    {
        int best = 0;
        for (int i = 1; i < p.Count; i++) if ((p[i] - q).LengthSquared < (p[best] - q).LengthSquared) best = i;
        return best;
    }

    static double Avg(List<Point> p, double[] dist, double d, WorldData w)
    {
        double s = 0; int n = 0;
        for (int i = 0; i < p.Count; i++)
            if (Math.Abs(dist[i] - d) <= 3) { s += w.HeightAt((int)Math.Floor(p[i].X), (int)Math.Floor(p[i].Y)); n++; }
        return n > 0 ? s / n : w.HeightAt((int)Math.Floor(p[0].X), (int)Math.Floor(p[0].Y));
    }
}
