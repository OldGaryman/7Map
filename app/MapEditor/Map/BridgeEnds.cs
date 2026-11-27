using System.Windows;

namespace MapEditor.Map;

/// outer end of the straight approach of a built bridge: a snap point for new roads (the user's photo 2026-11-26, Buzovi-Land:
/// a hand-drawn gravel road ended on the approach without its height, gave way because it was too steep and left a 2.8 m wall)
/// At = middle of the outer edge of the approach, Outward = direction away from the bridge, Height = ground of the approach
public sealed record BridgeEnd(Point At, Vector Outward, double Height, int Width, PlacedPrefab Piece, bool Gravel, BridgeSite Site)
{
    /// the point lies at the end (≤ 2.5 m) or on the approach itself (between its outer end and the end piece, within its width)
    public bool Covers(Point p)
    {
        if ((p - At).Length <= 2.5) return true;
        var v = p - At;
        double along = v * Outward, across = Math.Abs(v.X * -Outward.Y + v.Y * Outward.X);
        return along <= 0.5 && along >= -Bridge.Approach && across <= Site.Width / 2.0;
    }
}

public static class BridgeEnds
{
    /// both approach ends of every whole bridge on the map (pieces of one kit in a row, BridgeSwap.FromPieces)
    public static List<BridgeEnd> Find(WorldData w, IEnumerable<BridgeKitDef> kits)
    {
        var res = new List<BridgeEnd>();
        var list = kits?.Where(k => k.Found && !string.IsNullOrEmpty(k.End)).ToList();
        if (w == null || list == null || list.Count == 0) return res;
        var prefabs = w.PrefabSnapshot().Where(p => p.Def != null).ToList();
        var seen = new HashSet<PlacedPrefab>();
        foreach (var p in prefabs)
        {
            if (seen.Contains(p) || !list.Any(k => k.End.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var bb = BridgeSwap.FromPieces(w, p, list, prefabs);
            if (bb == null) continue;
            foreach (var q in bb.Pieces) seen.Add(q);
            var (a, b, h) = NetworkBuilder.ApproachEnds(bb.Site);
            var u = bb.Site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
            int width = Math.Max(3, bb.Site.RoadWidth);
            res.Add(new BridgeEnd(a, -u, h, width, bb.Pieces[0], bb.Site.Gravel, bb.Site));
            res.Add(new BridgeEnd(b, u, h, width, bb.Pieces[^1], bb.Site.Gravel, bb.Site));
        }
        return res;
    }

    /// the bridge end at a start/end point of a road, null = none
    public static BridgeEnd At(IReadOnlyList<BridgeEnd> ends, Point p)
    {
        if (ends == null) return null;
        BridgeEnd best = null;
        double bestD = double.MaxValue;
        foreach (var e in ends)
        {
            if (!e.Covers(p)) continue;
            double d = (p - e.At).Length;
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }
}
