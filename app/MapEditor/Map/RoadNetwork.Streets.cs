using System.Windows;

namespace MapEditor.Map;

/// Crossing existing town streets (the user's request 2026-11-17, Darobe-Land 1050 W / 2800 N: a free exit of a T gateway only
/// found a way by crossing two 8 m streets between the parts of a town – drawn by hand, the planner treated every existing road as
/// an obstacle). At straight, flat places of existing roads outside the tiles the search may now jump across at a right angle, like
/// across a planned road. Nothing is stored in the plan: when building, NetworkBuilder finds the crossings with existing roads on
/// the line itself (StreetCrossings) and lays the new road there at the height of the street, which stays unchanged.
public sealed partial class RoadNetwork
{
    /// street crossings: from cell, to cell, cost, middle on the street, driving direction of the jump
    readonly List<(int From, int To, float Cost, Point Mid, int Dir)> streetJumps = new();

    /// street crossings only where the street is at most this steep along (otherwise a step at the edge of the crossing)
    const double StreetCrossGrade = 0.06;
    /// a crossing of a town street costs a little more than one of a planned road (better around a town than through it)
    const float StreetCrossPenalty = CrossPenalty + 60;

    void FindStreetCrossings()
    {
        streetJumps.Clear();
        var scan = new RoadScan(w);
        var tiles = w.Prefabs.Where(p => p.IsTile).ToList();
        const int TileBucket = 150;
        int tbw = (w.Size + TileBucket - 1) / TileBucket;
        var tileNear = new List<PlacedPrefab>[tbw * tbw];
        foreach (var t in tiles)
        {
            int i0 = Math.Clamp((int)Math.Floor((t.X - 30 + half) / (double)TileBucket), 0, tbw - 1);
            int i1 = Math.Clamp((int)Math.Floor((t.X + t.SizeX + 30 + half) / (double)TileBucket), 0, tbw - 1);
            int j0 = Math.Clamp((int)Math.Floor((t.Z - 30 + half) / (double)TileBucket), 0, tbw - 1);
            int j1 = Math.Clamp((int)Math.Floor((t.Z + t.SizeZ + 30 + half) / (double)TileBucket), 0, tbw - 1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    (tileNear[j * tbw + i] ??= new()).Add(t);
        }
        bool TooCloseToTile(Point p)
        {
            int i = (int)Math.Floor((p.X + half) / TileBucket), j = (int)Math.Floor((p.Y + half) / TileBucket);
            if ((uint)i >= (uint)tbw || (uint)j >= (uint)tbw) return false;
            var l = tileNear[j * tbw + i];
            if (l == null) return false;
            for (int k = 0; k < l.Count; k++)
            {
                var t = l[k];
                double dx = Math.Max(0, Math.Max(t.X - p.X, p.X - (t.X + t.SizeX))), dz = Math.Max(0, Math.Max(t.Z - p.Y, p.Y - (t.Z + t.SizeZ)));
                if (dx * dx + dz * dz < 30 * 30) return true;
            }
            return false;
        }
        const int SpotBucket = 40;
        int sbw = (w.Size + SpotBucket - 1) / SpotBucket;
        var spotGrid = new List<Point>[sbw * sbw];
        bool NearSpot(Point p)
        {
            int ci = Math.Clamp((int)Math.Floor((p.X + half) / SpotBucket), 0, sbw - 1);
            int cj = Math.Clamp((int)Math.Floor((p.Y + half) / SpotBucket), 0, sbw - 1);
            int j0 = Math.Max(0, cj - 1), j1 = Math.Min(sbw - 1, cj + 1);
            int i0 = Math.Max(0, ci - 1), i1 = Math.Min(sbw - 1, ci + 1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var l = spotGrid[j * sbw + i];
                    if (l == null) continue;
                    for (int k = 0; k < l.Count; k++)
                    {
                        double dx = l[k].X - p.X, dz = l[k].Y - p.Y;
                        if (dx * dx + dz * dz < 40 * 40) return true;
                    }
                }
            return false;
        }
        double H(Point p) => w.HeightAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
        var candidates = new List<(int G, Point Center, int C0, int C1, Vector Nrm)>();
        Parallel.For(0, gw, () => new List<(int G, Point Center, int C0, int C1, Vector Nrm)>(), (row, _, local) =>
        {
            int g0 = row * gw, g1 = g0 + gw;
            for (int g = g0; g < g1; g++)
            {
                if (!roadCell[g] || tileCell[g]) continue;
                var spot = RoadFollower.Center(scan, Center(g));
                if (spot == null || spot.Width < 3 || spot.Width > 14) continue;
                if (TooCloseToTile(spot.Center)) continue;
                // straight stretch (no curve, no junction of the street)
                var a1 = RoadFollower.Axis(scan, spot.Center + spot.Dir * 15, 7);
                var a2 = RoadFollower.Axis(scan, spot.Center - spot.Dir * 15, 7);
                if (a1 == null || a2 == null || Math.Abs(Vector.Multiply(a1.Value, spot.Dir)) < 0.94 || Math.Abs(Vector.Multiply(a2.Value, spot.Dir)) < 0.94) continue;
                if (Math.Abs(H(spot.Center + spot.Dir * 12) - H(spot.Center - spot.Dir * 12)) > StreetCrossGrade * 24) continue;
                var nrm = new Vector(-spot.Dir.Y, spot.Dir.X);
                double hs = H(spot.Center);
                // approach cells on both sides: the first free cell 44–68 m away (the street blocks about two cells around itself)
                int Side(int sg)
                {
                    for (double d = ApproachLength; d <= ApproachLength + 24; d += 8)
                    {
                        var ap = spot.Center + nrm * (sg * d);
                        int c = CellOf(ap.X, ap.Y);
                        if (c < 0 || blocked[c] || roadCell[c] || edge[c]) continue;
                        if (Math.Abs(Sample(hb, ap.X, ap.Y) - hs) > d * spec.Grade * 1.5) return -1;
                        // nothing else in between: no water, no other road beside the street
                        for (double t = spot.Width / 2 + 3; t < d - 8; t += 2)
                        {
                            var q = spot.Center + nrm * (sg * t);
                            int ix = (int)Math.Floor(q.X), iz = (int)Math.Floor(q.Y);
                            if (w.WaterAt(ix, iz) || w.RoadAt(ix, iz) != 0) return -1;
                        }
                        return c;
                    }
                    return -1;
                }
                int c0 = Side(1), c1 = Side(-1);
                if (c0 < 0 || c1 < 0) continue;
                local.Add((g, spot.Center, c0, c1, nrm));
            }
            return local;
        }, local => { if (local.Count > 0) lock (candidates) candidates.AddRange(local); });
        candidates.Sort((a, b) => a.G.CompareTo(b.G));
        int spotCount = 0;
        foreach (var (_, center, c0, c1, nrm) in candidates)
        {
            if (NearSpot(center)) continue;
            int si = Math.Clamp((int)Math.Floor((center.X + half) / SpotBucket), 0, sbw - 1);
            int sj = Math.Clamp((int)Math.Floor((center.Y + half) / SpotBucket), 0, sbw - 1);
            (spotGrid[sj * sbw + si] ??= new()).Add(center);
            spotCount++;
            float cost = (float)((Center(c0) - Center(c1)).Length + StreetCrossPenalty);
            streetJumps.Add((c0, c1, cost, center, DirOf(-nrm.X, -nrm.Y)));
            streetJumps.Add((c1, c0, cost, center, DirOf(nrm.X, nrm.Y)));
        }
        Log?.Invoke($"street crossings: {spotCount} places");
        AddStreetJumps();
    }

    /// enter the street crossings into the jumps of the search (Road = null marks a crossing of an existing street)
    void AddStreetJumps()
    {
        foreach (var (from, to, cost, mid, dir) in streetJumps)
        {
            if (!jumps.TryGetValue(from, out var l)) jumps[from] = l = new();
            l.Add((to, cost, mid, null, -1, dir));
        }
    }
}
