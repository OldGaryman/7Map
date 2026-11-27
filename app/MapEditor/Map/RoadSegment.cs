using System.Windows;

namespace MapEditor.Map;

/// a line along the edge of the remaining road through the old mouth, from the station on one arm to the station on the other.
/// Where the two edges meet at an angle (the remaining arms form a corner), the line runs exactly along each measured edge and only the
/// corner is rounded – a single smooth curve cut into the arms (the user's test 2026-11-23, Kuwoto-Land). Straight on: a cubic curve.
public sealed class EdgeCurve
{
    public Point B0, B3;                       // start (station on the first arm) and end (station on the second arm)
    public double Length;                      // length between the stations (m)
    readonly List<(double T, Point P)> poly = new();

    /// ta = direction at a towards the junction, tb = direction at b away from it; corner = size of the rounding (m)
    public EdgeCurve(Point a, Vector ta, Point b, Vector tb, double corner)
    {
        B0 = a; B3 = b;
        ta.Normalize(); tb.Normalize();
        var pts = new List<Point>();
        // where the two edge lines meet: a + ta·u = b − tb·v
        double den = ta.X * -tb.Y - ta.Y * -tb.X;
        double u = double.NaN, v = double.NaN;
        if (Math.Abs(den) > 0.25)   // more than about 15° between them
        {
            var w = b - a;
            u = (w.X * -tb.Y - w.Y * -tb.X) / den;
            v = (ta.X * w.Y - ta.Y * w.X) / den;
        }
        if (u > 1 && v > 1)
        {
            var x = a + ta * u;
            double c = Math.Min(corner, Math.Min(u, v) * 0.9);
            Point p1 = x - ta * c, p2 = x + tb * c;
            pts.Add(a);
            for (int k = 0; k <= 20; k++)
            {
                double t = k / 20.0, s = 1 - t;
                pts.Add(new Point(s * s * p1.X + 2 * s * t * x.X + t * t * p2.X, s * s * p1.Y + 2 * s * t * x.Y + t * t * p2.Y));
            }
            pts.Add(b);
        }
        else
        {
            double d = (b - a).Length / 3;
            Point c1 = a + ta * d, c2 = b - tb * d;
            for (int k = 0; k <= 60; k++)
            {
                double t = k / 60.0, s = 1 - t;
                pts.Add(new Point(s * s * s * a.X + 3 * s * s * t * c1.X + 3 * s * t * t * c2.X + t * t * t * b.X,
                                  s * s * s * a.Y + 3 * s * s * t * c1.Y + 3 * s * t * t * c2.Y + t * t * t * b.Y));
            }
        }
        // parameter 0…1 by length; beyond the stations the line continues along their edges
        double len = 0;
        var at = new List<double> { 0 };
        for (int k = 1; k < pts.Count; k++) { len += (pts[k] - pts[k - 1]).Length; at.Add(len); }
        len = Math.Max(len, 1e-6);
        Length = len;
        poly.Add((-40 / len, a - ta * 40));
        for (int k = 0; k < pts.Count; k++) poly.Add((at[k] / len, pts[k]));
        poly.Add((1 + 40 / len, b + tb * 40));
    }

    public Point At(double t)
    {
        for (int k = 0; k + 1 < poly.Count; k++)
        {
            if (t > poly[k + 1].T && k + 2 < poly.Count) continue;
            double span = poly[k + 1].T - poly[k].T, f = span < 1e-12 ? 0 : (t - poly[k].T) / span;
            return poly[k].P + (poly[k + 1].P - poly[k].P) * f;
        }
        return poly[^1].P;
    }

    /// nearest place: parameter (below 0 / above 1 = before / behind the stations) and signed distance (positive = left of B0 → B3)
    public (double T, double Off) Project(Point p)
    {
        double best = double.MaxValue, bt = 0, off = 0;
        for (int k = 0; k + 1 < poly.Count; k++)
        {
            var a = poly[k].P; var d = poly[k + 1].P - a;
            double l2 = d.LengthSquared, u = l2 < 1e-9 ? 0 : Math.Clamp(((p - a) * d) / l2, 0, 1);
            double dist = (p - (a + d * u)).Length;
            if (dist >= best) continue;
            best = dist;
            bt = poly[k].T + (poly[k + 1].T - poly[k].T) * u;
            off = (d.X * (p.Y - a.Y) - d.Y * (p.X - a.X)) >= 0 ? dist : -dist;
        }
        return (bt, off);
    }
}

/// Where the deleted piece meets a road that stays (the user's request 2026-11-23): the edge of that road on the side of the deleted
/// piece is rebuilt – asphalt up to its edge, then its gravel edge with the width measured on that road. The edges are measured at a
/// "clean" place on each arm of the remaining road (where the mouth no longer widens it) and joined through the old mouth.
public sealed class SegmentJoint
{
    public Point Center;              // middle of the junction
    public Point End;                 // last point of the deleted piece before the junction
    public Vector Dir;                // direction of the deleted piece towards the junction
    public EdgeCurve Asphalt;         // edge of the asphalt (null: the remaining road is a gravel road)
    public EdgeCurve Gravel;          // outer edge of the gravel (asphalt roads: of their gravel edge)
    public int Side = 1;              // sign so that a positive distance from the curves is outside the remaining road
    public double GravelWidth;        // measured width of the gravel edge (asphalt roads), for the hint
    public double Gravel0, Gravel2;   // gravel edge width at the two stations: the gravel edge runs parallel to the asphalt edge
    public string Why;                // why the edge is not rebuilt (null = it is)
    public Point Arm0, Arm2;          // centers of the remaining road on its two arms (where the edges were measured)
    public int Window;                // half size of the square around Center that is repaired (m)
    public byte Kind;                 // surface of the remaining road
}

/// a piece of road between two junctions (or tile, road end), found from one click
public sealed class SegmentResult
{
    public List<Point> Points = new();   // center line, about every 2 m
    public double Width;                 // measured width including edges (m)
    public byte Kind;                    // asphalt or gravel
    public double Length;
    public string StartStop, EndStop;    // "junction", "tile", "end", or null (at most the search length)
    public List<SegmentJoint> Joints = new();
}

/// Delete a road between two junctions (the user's request 2026-11-23): a click on a road marks the piece from the clicked place in
/// both directions up to the next junction, city tile or road end. Deleting removes surface and road bed (natural ground, like re-laying);
/// at the junctions the road that stays gets its edge back, its heights stay exactly as they were.
public static class RoadSegment
{
    const double MaxLength = 8000;
    const double MouthReach = 16;   // corners of a mouth reach up to this far beside the axis of the deleted piece (slanted mouths are wide)
    const double CornerRadius = 10;  // rounding where the remaining arms meet at an angle (outer edge of the asphalt, m)

    public static SegmentResult Find(WorldData w, Point click, out string error)
    {
        error = null;
        var scan = new RoadScan(w);
        var spot = RoadFollower.Center(scan, click);
        if (spot == null || scan.InTile(spot.Center.X, spot.Center.Y))
        {
            error = Loc.T("Please click on an existing road (outside the town tiles).");
            return null;
        }
        var fwd = RoadFollower.Extend(scan, spot.Center, spot.Dir, MaxLength, spot.Width, true);
        var back = RoadFollower.Extend(scan, spot.Center, -spot.Dir, MaxLength, spot.Width, true);
        var res = new SegmentResult { Kind = spot.Kind, StartStop = back.Stop, EndStop = fwd.Stop };
        res.Points.AddRange(Enumerable.Reverse(back.Points));
        res.Points.Add(spot.Center);
        res.Points.AddRange(fwd.Points);
        var widths = new List<double> { spot.Width };
        if (fwd.Points.Count > 0) widths.Add(fwd.Width);
        if (back.Points.Count > 0) widths.Add(back.Width);
        widths.Sort();
        res.Width = Math.Clamp(widths[widths.Count / 2], 3, 40);
        for (int k = 1; k < res.Points.Count; k++) res.Length += (res.Points[k] - res.Points[k - 1]).Length;
        if (res.Points.Count < 3 || res.Length < 6)
        {
            error = Loc.T("This piece of road is too short – it lies between two junctions that are very close together.");
            return null;
        }
        if (res.EndStop == "junction") AddJoint(scan, res, res.Points[^1], res.Points[^1] - res.Points[^3]);
        if (res.StartStop == "junction") AddJoint(scan, res, res.Points[0], res.Points[0] - res.Points[2]);
        return res;
    }

    /// arms of a junction on a ring around its center: middle angles (radians) of the connected road runs
    static List<double> Arms(RoadScan s, Point c, double r)
    {
        const int N = 360;
        var on = new bool[N];
        for (int a = 0; a < N; a++)
        {
            double ang = a * Math.PI * 2 / N;
            on[a] = s.Road(c.X + Math.Cos(ang) * r, c.Y + Math.Sin(ang) * r);
        }
        var res = new List<double>();
        if (on.All(v => v) || on.All(v => !v)) return res;
        int start = Array.FindIndex(on, v => !v);   // begin in a gap so no run wraps around the start
        for (int k = 1; k <= N; k++)
        {
            int a = (start + k) % N;
            if (!on[a]) continue;
            int len = 0;
            while (len < N && on[(a + len) % N]) len++;
            if (len >= 3) res.Add((a + (len - 1) / 2.0) * Math.PI * 2 / N);
            k += len;
        }
        return res;
    }

    static double AngleDiff(double a, double b)
    {
        double d = (a - b) % (2 * Math.PI);
        if (d < 0) d += 2 * Math.PI;
        return d;   // 0 … 2π counter-clockwise from b to a
    }

    /// across the road from p: asphalt from p on (if it starts on asphalt), then gravel – distances of the last asphalt and gravel pixels
    static (double A, double G) Walk(RoadScan s, Point p, Vector d, bool asphaltStart)
    {
        double t = 0, aEnd = 0, gEnd = 0;
        bool inAsphalt = asphaltStart;
        while (t < 30)
        {
            t += 0.5;
            var q = p + d * t;
            if (s.InTile(q.X, q.Y)) break;
            byte kind = s.W.RoadAt((int)Math.Floor(q.X), (int)Math.Floor(q.Y));
            if (inAsphalt && kind == WorldData.Asphalt) { aEnd = t; continue; }
            inAsphalt = false;
            if (kind == WorldData.Gravel) { gEnd = t; continue; }
            break;
        }
        return (aEnd, Math.Max(gEnd, aEnd));
    }

    /// a clean place on one arm (outside the widened mouth): edge points of asphalt and gravel on our side (side = +1 left, −1 right of
    /// the arm going outward) and the direction outward
    sealed record Station(Point Asphalt, Point Gravel, Vector Out, byte Kind, double GravelWidth, Point Center);

    static Station ArmStation(RoadScan s, Point junction, double ringAngle, double r, int side)
    {
        var ring = new Point(junction.X + Math.Cos(ringAngle) * r, junction.Y + Math.Sin(ringAngle) * r);
        var spot = RoadFollower.Center(s, ring);
        if (spot == null) return null;
        var dOut = spot.Dir;
        if (dOut * (spot.Center - junction) < 0) dOut = -dOut;
        // near a wide junction the road axis can come out across the arm: then start outward from the junction (Extend re-centres)
        var radial = new Vector(Math.Cos(ringAngle), Math.Sin(ringAngle));
        if (dOut * radial < Math.Cos(Math.PI / 4)) dOut = radial;
        var follow = RoadFollower.Extend(s, spot.Center, dOut, 60, spot.Width, false);
        // also from the ring inwards up to where the mouth widens the road: the edges are measured as close to the mouth as possible
        // (an arm that bends towards the junction would otherwise be continued straight from far away and cut into)
        var inward = RoadFollower.Extend(s, spot.Center, -dOut, r, spot.Width, true);
        var pts = new List<Point>();
        pts.AddRange(Enumerable.Reverse(inward.Points));
        pts.Add(spot.Center);
        pts.AddRange(follow.Points);
        if (pts.Count < 4) return null;
        var m = new List<(Point C, Vector T, Vector N, double Lo, double Hi, double G, bool Asphalt)>();
        for (int k = 0; k < pts.Count; k++)
        {
            var t = pts[Math.Min(k + 1, pts.Count - 1)] - pts[Math.Max(k - 1, 0)];
            if (t.Length < 1e-6) continue;
            t.Normalize();
            var n = new Vector(-t.Y, t.X) * side;
            byte k0 = s.W.RoadAt((int)Math.Floor(pts[k].X), (int)Math.Floor(pts[k].Y));
            if (k0 == 0) continue;
            bool asp = k0 == WorldData.Asphalt;
            var hi = Walk(s, pts[k], n, asp);
            var lo = Walk(s, pts[k], -n, asp);
            m.Add((pts[k], t, n, asp ? lo.A : lo.G, asp ? hi.A : hi.G, hi.G, asp));
        }
        if (m.Count < 3) return null;
        // reference from the whole stretch: width of the roadway and of the gravel edge on our side
        bool asphaltRoad = m.Count(v => v.Asphalt) * 2 >= m.Count;
        var halfs = m.Select(v => (v.Lo + v.Hi) / 2).OrderBy(v => v).ToList();
        var gws = m.Select(v => v.G - v.Hi).OrderBy(v => v).ToList();
        double refHalf = halfs[halfs.Count / 2], refG = gws[gws.Count / 2];
        bool Clean(int k) => k < m.Count && m[k].Asphalt == asphaltRoad && Math.Abs(m[k].Hi - m[k].Lo) <= 1.0
            && Math.Abs((m[k].Lo + m[k].Hi) / 2 - refHalf) <= 1.0 && Math.Abs(m[k].G - m[k].Hi - refG) <= 0.6;
        int at = -1;
        for (int k = 0; k < m.Count && at < 0; k++)
            if (Clean(k) && (Clean(k + 1) || k + 1 >= m.Count) && (Clean(k + 2) || k + 2 >= m.Count)) at = k;
        if (at < 0) return null;
        var v0 = m[at];
        // direction of the edge: from the edge points of the clean places over the next 10 m (one place alone is too unsteady)
        Point EdgeAt(int k) => m[k].C + m[k].N * (m[k].G + 0.25);
        int last = at;
        for (int k = at + 1; k < m.Count && k <= at + 5 && Clean(k); k++) last = k;
        var tan = last > at ? EdgeAt(last) - EdgeAt(at) : v0.T;
        tan.Normalize();
        if (tan * dOut < 0) tan = -tan;
        // half width and gravel edge: medians over the clean places of the next 10 m (one place alone is measured in 0.5 m steps);
        // the center from this place; the pixel edges lie a quarter metre beyond the last pixel reached
        double Med(IEnumerable<double> v) { var l = v.OrderBy(x => x).ToList(); return l[l.Count / 2]; }
        var run = Enumerable.Range(at, last - at + 1).Where(Clean).ToList();
        double halfW = Med(run.Select(k => (m[k].Lo + m[k].Hi) / 2)), gravelW = Med(run.Select(k => m[k].G - m[k].Hi));
        var center = v0.C + v0.N * ((v0.Hi - v0.Lo) / 2);
        var asphaltEdge = center + v0.N * (halfW + 0.25);
        var gravelEdge = asphaltEdge + v0.N * gravelW;
        return new Station(asphaltEdge, gravelEdge, tan, asphaltRoad ? WorldData.Asphalt : WorldData.Gravel, asphaltRoad ? gravelW : 0, center);
    }

    static void AddJoint(RoadScan s, SegmentResult seg, Point end, Vector dir)
    {
        if (dir.Length < 1e-6) return;
        dir.Normalize();
        // across the remaining road along our direction: from the end up to where the surface stops
        double t = 0, last = 0;
        while (t < 60)
        {
            t += 0.5;
            var q = end + dir * t;
            if (s.InTile(q.X, q.Y)) return;   // the junction is a tile exit: nothing to repair (the tile has its own road)
            if (s.Road(q.X, q.Y)) last = t; else if (t - last >= 1.0) break;
        }
        var center = end + dir * Math.Max(2, (last + 2) / 2);
        double r = Math.Max(last, seg.Width) / 2 + 12;
        // arms that meet at an angle touch each other near the junction (paved corner between them): a larger ring separates them
        var arms = Arms(s, center, r);
        for (double rr = r + 8; arms.Count < 3 && rr <= r + 40; rr += 8)
        {
            var more = Arms(s, center, rr);
            if (more.Count >= 3) { arms = more; r = rr; }
        }
        double own = Math.Atan2(-dir.Y, -dir.X);
        // without a rebuilt edge the junction is still protected: the remaining roads keep surface and heights exactly (Repair)
        var j = new SegmentJoint { Center = center, End = end, Dir = dir, Window = (int)Math.Ceiling(r + 8) };
        seg.Joints.Add(j);
        if (arms.Count < 3) { j.Why = $"{arms.Count} arm(s) at {r:0} m"; return; }   // only our piece and one continuation: no real junction
        int oi = 0;
        double Dist(double a) => Math.Min(AngleDiff(a, own), AngleDiff(own, a));
        for (int k = 1; k < arms.Count; k++) if (Dist(arms[k]) < Dist(arms[oi])) oi = k;
        double ownA = arms[oi];
        // neighbours of our arm: the next one clockwise and the next one counter-clockwise
        double right = arms.Where((_, k) => k != oi).OrderBy(a => AngleDiff(ownA, a)).First();
        double left = arms.Where((_, k) => k != oi).OrderBy(a => AngleDiff(a, ownA)).First();
        if (Math.Abs(left - right) < 1e-9) { j.Why = "one remaining arm"; return; }
        // the remaining arms may also meet at an angle (the deleted road continued one of them, the other joins at the side; the user's
        // test 2026-11-23, Kuwoto-Land 181/761): then the edge becomes a bend around the corner. Only a very sharp fork stays as it is.
        if (Math.Min(AngleDiff(left, right), AngleDiff(right, left)) < Math.PI / 4) { j.Why = "sharp fork"; return; }
        // our side of each arm (outward along it): the deleted arm lies counter-clockwise of the clockwise neighbour = on its left (+1),
        // clockwise of the counter-clockwise neighbour = on its right (−1)
        var a0 = ArmStation(s, center, right, r, 1);
        var a2 = ArmStation(s, center, left, r, -1);
        if (a0 == null || a2 == null) { j.Why = a0 == null ? "no clean place on the clockwise arm" : "no clean place on the counter-clockwise arm"; return; }
        j.Kind = a0.Kind == WorldData.Asphalt && a2.Kind == WorldData.Asphalt ? WorldData.Asphalt : WorldData.Gravel;
        // through the mouth: from the station on one arm (towards the junction) to the station on the other (away from it)
        if (j.Kind == WorldData.Asphalt) j.Asphalt = new EdgeCurve(a0.Asphalt, -a0.Out, a2.Asphalt, a2.Out, CornerRadius);
        j.Gravel = new EdgeCurve(a0.Gravel, -a0.Out, a2.Gravel, a2.Out, CornerRadius + (a0.Gravel - a0.Asphalt).Length);
        j.GravelWidth = (a0.GravelWidth + a2.GravelWidth) / 2;
        j.Gravel0 = a0.GravelWidth; j.Gravel2 = a2.GravelWidth;
        j.Arm0 = a0.Center; j.Arm2 = a2.Center;
        // outside the road = the side away from the center of the remaining road
        j.Side = (j.Asphalt ?? j.Gravel).Project(a0.Center).Off > 0 ? -1 : 1;
        double reach = new[] { a0.Gravel, a2.Gravel }.Max(p => (p - center).Length);
        j.Window = (int)Math.Ceiling(Math.Min(Math.Max(reach + 8, j.Window), 120));
    }

    /// delete the piece as part of an edit step: road bed natural (removal), then repair the remaining roads at the junctions
    public static void Delete(WorldData w, WorldEdits e, SegmentResult seg, IProgress<string> progress)
    {
        e.GuardBanks();
        var quiet = new Progress<string>(_ => { });
        // before: surface and heights around each junction
        var snaps = new List<(SegmentJoint J, byte[] Mask, ushort[] Heights, uint[] Splat)>();
        foreach (var j in seg.Joints)
        {
            int r = j.Window, sz = 2 * r + 1, x0 = (int)Math.Floor(j.Center.X) - r, z0 = (int)Math.Floor(j.Center.Y) - r;
            var mask = new byte[sz * sz]; var hs = new ushort[sz * sz]; var sp = new uint[sz * sz];
            for (int g = 0; g < sz * sz; g++)
            {
                int x = x0 + g % sz, z = z0 + g / sz;
                if (!w.Contains(x, z)) continue;
                int i = w.Index(x, z);
                mask[g] = w.RoadMask[i]; hs[g] = w.Heights[i];
                if (mask[g] != 0) sp[g] = e.SplatAt(x, z);
            }
            snaps.Add((j, mask, hs, sp));
        }

        progress?.Report(Loc.T("Removing the road …"), 0.05);
        var s = RoadSettings.Remove();
        s.Width = seg.Width + 2;
        s.KeepCrossings = true;
        s.Reshape = true;
        var rr = RoadPlanner.Compute(w, NetworkBuilder.Even(seg.Points, 12), s);
        if (rr != null)
            for (int from = 0; from < rr.Path.Count; from += 1600)
                RoadApplier.Apply(w, e, rr, quiet, from, Math.Min(rr.Path.Count, from + 1600), 4);

        progress?.Report(Loc.T("Repairing the junctions …"), 0.9);
        foreach (var (j, mask, hs, sp) in snaps) Repair(w, e, seg, j, mask, hs, sp);
    }

    /// distance from p to a polyline
    static double ToLine(List<Point> line, Point p)
    {
        double best = double.MaxValue;
        for (int k = 0; k + 1 < line.Count; k++)
        {
            var a = line[k]; var d = line[k + 1] - a;
            double l2 = d.LengthSquared, u = l2 < 1e-9 ? 0 : Math.Clamp(((p - a) * d) / l2, 0, 1);
            best = Math.Min(best, (p - (a + d * u)).Length);
        }
        return best;
    }

    static void Repair(WorldData w, WorldEdits e, SegmentResult seg, SegmentJoint j, byte[] mask, ushort[] hs, uint[] sp)
    {
        int r = j.Window, sz = 2 * r + 1, x0 = (int)Math.Floor(j.Center.X) - r, z0 = (int)Math.Floor(j.Center.Y) - r;
        // the piece near the junction, and its axis continued up to the edge of the remaining road (the mouth lies around both)
        var near = seg.Points.Where(p => (p - j.Center).Length < r + seg.Width).ToList();
        if (near.Count < 2) near = new List<Point> { j.End - j.Dir * 4, j.End };
        var link = j.Gravel == null ? null : new List<Point> { j.End, j.Gravel.At(Math.Clamp(j.Gravel.Project(j.End).T, 0, 1)) };
        double halfPiece = seg.Width / 2;
        var oldRoad = new byte[sz * sz];   // what stays road: for the seam
        var paved = new List<(int X, int Z)>();   // asphalt repainted in the mouth
        for (int g = 0; g < sz * sz; g++)
        {
            int x = x0 + g % sz, z = z0 + g / sz;
            if (!w.Contains(x, z) || mask[g] == 0) continue;   // nothing is painted where there was no road
            int i = w.Index(x, z);
            var p = new Point(x + 0.5, z + 0.5);
            double toPiece = ToLine(near, p);
            if (j.Gravel == null)
            {
                // no edge to rebuild: our piece up to its last point goes, every other road stays exactly as it was
                if (toPiece <= halfPiece + 1 && (p - j.End) * j.Dir <= 0) continue;
                if (w.Heights[i] != hs[g]) e.SetHeight(i, hs[g]);
                if (w.RoadMask[i] != mask[g]) { e.SetSplat(x, z, sp[g]); e.SetRoad(i, mask[g]); }
                oldRoad[g] = mask[g];
                continue;
            }
            // asphalt roads: the gravel edge lies exactly the measured width outside the asphalt edge (two curves of their own could
            // differ by half a metre between the stations)
            double tg, og, oa;
            if (j.Asphalt != null)
            {
                (tg, oa) = j.Asphalt.Project(p);
                oa *= j.Side;
                og = oa - (j.Gravel0 + (j.Gravel2 - j.Gravel0) * Math.Clamp(tg, 0, 1));
            }
            else { (tg, og) = j.Gravel.Project(p); og *= j.Side; oa = og; }
            // up to 6 m beyond the stations too (along the measured edges): the widened corners of a mouth can reach past them
            double beyond = 6 / (j.Asphalt ?? j.Gravel).Length;
            bool mouth = tg > -beyond && tg < 1 + beyond && oa > -3 && og <= halfPiece + MouthReach && Math.Min(toPiece, ToLine(link, p)) <= halfPiece + MouthReach;
            if (mouth)
            {
                if (og <= 0.15)   // a little tolerance: a narrow gravel edge running slantwise would otherwise miss single pixels
                {
                    // the edge of the remaining road comes back: asphalt up to its edge, then the gravel edge (gravel roads: gravel)
                    byte kind = j.Asphalt != null && oa <= 0 ? WorldData.Asphalt : WorldData.Gravel;
                    uint px = kind == WorldData.Asphalt ? WorldEdits.AsphaltPixel : WorldEdits.GravelPixel;
                    if (e.SplatAt(x, z) != px) e.SetSplat(x, z, px);
                    e.SetRoad(i, kind);
                    if (w.Heights[i] != hs[g]) e.SetHeight(i, hs[g]);
                    oldRoad[g] = kind;
                    if (kind == WorldData.Asphalt) paved.Add((x, z));
                }
                else if (w.RoadMask[i] != 0 || e.SplatAt(x, z) != WorldEdits.EmptyPixel)
                {
                    // corners of the old mouth and the rest of our piece in front of the edge: surface away
                    e.SetSplat(x, z, WorldEdits.EmptyPixel);
                    e.SetRoad(i, 0);
                }
                continue;
            }
            if (toPiece <= halfPiece + 1 && og > 0) continue;   // our piece: the removal has taken it
            // any other road in the window (the remaining road itself, other roads): exactly as before
            if (w.Heights[i] != hs[g]) e.SetHeight(i, hs[g]);
            if (w.RoadMask[i] != mask[g]) { e.SetSplat(x, z, sp[g]); e.SetRoad(i, mask[g]); }
            oldRoad[g] = mask[g];
        }
        // asphalt that stays never borders ground where the deletion took surface away (corners of the old mouth beside the stations;
        // a narrow gravel edge running steeply slantwise that misses single pixels): such a pixel becomes gravel edge
        var pavedSet = new HashSet<(int, int)>(paved);
        for (int g = 0; g < sz * sz; g++)
        {
            int x = x0 + g % sz, z = z0 + g / sz;
            if (!w.Contains(x, z) || w.RoadMask[w.Index(x, z)] != WorldData.Asphalt) continue;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, nz = z + dz;
                if (nx < x0 || nz < z0 || nx >= x0 + sz || nz >= z0 + sz || !w.Contains(nx, nz)) continue;
                int ng = (nz - z0) * sz + (nx - x0);
                if (w.RoadMask[w.Index(nx, nz)] != 0 || (mask[ng] == 0 && !pavedSet.Contains((x, z)))) continue;
                e.SetSplat(nx, nz, WorldEdits.GravelPixel);
                e.SetRoad(w.Index(nx, nz), WorldData.Gravel);
            }
        }
        // the natural ground of the deleted piece runs out onto the repaired road (no step at its edge)
        RouteBuilder.Seam(w, e, j.Center, (oldRoad, hs), r);
    }
}
