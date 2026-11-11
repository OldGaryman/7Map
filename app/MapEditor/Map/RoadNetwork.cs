using System.Windows;

namespace MapEditor.Map;

/// Einstellungen für „Straßen erzeugen“ (Wunsch des Nutzers 7.11.2026, Schritt 3 nach Terrain und Städten)
public sealed class NetworkSpec
{
    public double Grade = 0.12;      // übliche Steigung (darüber wird es teurer)
    public double MaxGrade = 0.18;   // in engen Passagen höchstens (Entscheidung des Nutzers)
    public double RoadFill = 1.2;      // Damm höchstens so hoch über dem Gelände (m); < 0 = alter Mittelwert-Verlauf
    public double RoadCut = 2.2;       // Einschnitt höchstens so tief (m)
    public double RoadEdgeFill = 2.0;  // am Hang: Fahrbahn höchstens so hoch über dem tieferen Rand (m)
    public int Loops = 1;           // Querverbindungen: 0 wenige, 1 normal, 2 viele
    public int Curves = 1;           // Kurvigkeit: 0 wenig, 1 normal, 2 stark
    public int Cuts = 1;             // Schneisen: 0 nie, 1 selten, 2 öfter
    public string Seed = "YAMEE";
    public NetworkSpec Clone() => (NetworkSpec)MemberwiseClone();
}

/// Stadt aus den Kacheln der Karte (zusammenhängende Gruppe inkl. Ortsteile und Gateway)
public sealed class NetTown
{
    public int Id;
    public List<PlacedPrefab> Tiles = new();
    public bool OldWest;
    public int X0, Z0, X1, Z1;
    public List<NetExit> Exits = new();
    public bool Connected;
    public double CX => (X0 + X1) / 2.0;
    public double CZ => (Z0 + Z1) / 2.0;
}

/// Freier Straßenausgang einer Kachel (kein Nachbar, keine Straße davor)
public sealed class NetExit
{
    public NetTown Town;
    public PlacedPrefab Tile;
    public double X, Z, DX, DZ;    // Mitte am Kachelrand, Richtung nach außen
    public int Width;              // Fahrbahn der Kachel am Ausgang (m)
    public bool Gravel, Gateway, Used;
    // Anschluss an eine vorhandene Ortsstraße (Ortsanbindung): X/Z = Mitte der Straße, DX/DZ = Richtung der neuen Straße,
    // Width = Fahrbahnbreite dort, StreetHalf = halbe Breite samt Rand
    public bool Street;
    public double StreetHalf, StreetDX, StreetDZ;
    internal int Cell = -1;        // Rasterzelle 44 m vor dem Ausgang (-1 = nicht erreichbar)
    public bool Reachable => Cell >= 0;
    public Point Approach => new(X + DX * RoadNetwork.ApproachLength, Z + DZ * RoadNetwork.ApproachLength);
}

/// Geplante Straße des Netzes (Mittellinie von From zu To bzw. zur Abzweigung)
public sealed class NetRoad
{
    public List<Point> Points;
    public bool Gravel;
    public double Width;           // Fahrbahn (Asphalt 16 m, Schotter wie der Ausgang, höchstens 12 m)
    public bool Loop;              // Querverbindung (nicht nötig, damit jede Stadt erreichbar ist)
    public bool Feeder;            // Zubringer (zweiter Durchlauf): freier Stadtausgang an eine nahe Straße
    public bool TownLink;          // Ortsanbindung: kurze Verbindung von einer vorbeilaufenden Straße an eine Ortsstraße
    public bool Cut;               // darf Rücken mit einer Schneise durchschneiden
    public NetExit From, To;       // To = null: endet an einer anderen Straße (Junction)
    public Point? Junction;
    public double Length, MaxGrade, SteepShare, MaxCut;
    public List<Point> Crossings = new();
    public List<NetRoad> CrossedRoads = new();   // je Kreuzung die gekreuzte Straße (gleiche Reihenfolge wie Crossings)
    public NetRoad StartRoad, EndRoad;           // Straße, an der die Straße am Anfang/Ende abzweigt (null = Stadtausgang)
    internal List<Point> Samples;  // alle 16 m
    internal int[] Nodes;          // Knoten im Straßengraphen je Probe
}

public sealed class NetworkPlan
{
    public List<NetTown> Towns = new();
    public List<NetRoad> Roads = new();
    public List<Point> Junctions = new();
    public List<Point> CrossingPoints = new();
    public List<string> Problems = new();
    public double StraightShare;   // Anteil der Länge, der praktisch gerade ist (über 288 m höchstens 2 m Abweichung)
    public double Seconds;
}

/// Straßennetz zwischen den Städten planen (Stufe 1: nur planen, nichts ändern). Ablauf:
/// 1. Städte aus den Kacheln (Gruppen mit höchstens 330 m Lücke), freie Ausgänge (kein Nachbar, keine Straße davor).
/// 2. Kostenraster 16 m: Länge, Steigung (bis Grade billig, bis MaxGrade teuer, darüber sehr teuer), Querneigung, Felswände;
///    Täler billiger, Kuppen teurer (Lage gegenüber der Umgebung, ca. 400 m); großräumiges Rauschen (240/600 m), damit die Straße
///    auch in der Ebene in Bögen schwingt. Gesperrt: Kartenrand 100 m, Wasser, Kacheln (+24 m), Gebäude (+12 m), vorhandene
///    Straßen, schon geplante Straßen (bis 26 m) – außer an Abzweigungen und Kreuzungen (rechtwinklig, Abstand ≥ 200 m).
/// 3. Wie Prim: von der größten Stadt aus wird immer die Stadt angeschlossen, die am günstigsten an das bisherige Netz kommt –
///    an einen freien Ausgang oder seitlich an eine geplante Straße (Abzweigung). Old-West-Orte zuletzt mit Schotterwegen.
/// 4. Querverbindungen, wo der Weg über das Netz viel länger wäre (≥ 1,8 × bzw. 2,2 × zwischen zwei Straßen); sie dürfen
///    andere Straßen kreuzen.
/// Ein Teil der Straßen darf Rücken mit einer Schneise durchschneiden (Gelände für die Wegsuche bis 20 m abgesenkt).
public sealed class RoadNetwork
{
    const int C = 16;                           // Raster (m)
    public const double ApproachLength = 44;    // gerades Stück vor dem Ausgang bzw. vor der Abzweigung (m)
    /// Straßen (Fahrbahn samt Rand) bleiben mindestens so weit vom Kartenrand weg (Wunsch des Nutzers 8.11.2026: nicht unter
    /// 64 m, besser 80 m); die Mittellinie also EdgeKeep + 9 m (halbe 16-m-Fahrbahn + 1 m Rand)
    public const double EdgeKeep = 80, EdgeCenter = EdgeKeep + 9;
    const double RoadBuffer = 26;               // so nah dürfen Mittellinien fremder Straßen nicht kommen (Zellmitte)
    const double NodeSpacing = 250;             // Abstand zwischen Abzweigungen/Kreuzungen/Straßenenden (m; 200 m wirkte gedrängt)
    const double TownGap = 150;                 // keine Abzweigung näher an einer Stadt (m)
    const float JunctionPenalty = 80, CrossPenalty = 120, ThroughTown = 60;

    readonly WorldData w;
    readonly NetworkSpec spec;
    readonly Random rnd;
    readonly int gw, n, half;
    float[] hb, hcut, gradX, gradZ, mult;
    int[] reserveOwner;                        // Zelle vor einem freien Ausgang (Nummer in exitList), -1 = keiner
    bool[] nearTown;                           // bis 100 m neben einer Stadt: teurer (Straßen sollen nicht an Städten entlangschrammen)
    readonly List<NetExit> exitList = new();
    HashSet<NetExit> allowed = new();          // Ausgänge, deren Vorplatz die aktuelle Suche benutzen darf
    bool[] roadCell, tileCell;                 // vorhandener Belag bzw. Mitte in einer Kachel (je Zelle)
    bool[] blocked, roadBlock, edge;           // edge = Randstreifen 100 m (gesperrt außer vor Stadtausgängen am Rand)
    // Nähe zu geplanten Straßen (Wunsch des Nutzers 8.11.2026: Fahrbahnen liefen auf langer Strecke dicht nebeneinander):
    // Abstand zur nächsten Mittellinie bis ParallelRange und deren Geländehöhe dort
    float[] nearRoadD, nearRoadH;
    const double ParallelRange = 160, ParallelHeight = 12;   // auf Rifane-Land liefen gebaute Straßen in 100–200 m Abstand nebeneinander
    readonly NetworkPlan plan = new();
    readonly List<Point> nodesUsed = new();
    readonly List<List<(int To, double Cost)>> graph = new();

    static readonly (int DI, int DJ)[] Steps =
    {
        (1, 0), (2, 1), (1, 1), (1, 2), (0, 1), (-1, 2), (-1, 1), (-2, 1),
        (-1, 0), (-2, -1), (-1, -1), (-1, -2), (0, -1), (1, -2), (1, -1), (2, -1),
    };

    public static Action<string> Log;   // Testausgabe

    public RoadNetwork(WorldData world, NetworkSpec s)
    {
        w = world; spec = s;
        rnd = new Random(new GeneratorSpec { Seed = s.Seed + "/roads" }.SeedValue);
        half = w.Half; gw = w.Size / C; n = gw * gw;
    }

    int CellOf(double x, double z)
    {
        int i = (int)Math.Floor((x + half) / C), j = (int)Math.Floor((z + half) / C);
        return i < 0 || j < 0 || i >= gw || j >= gw ? -1 : j * gw + i;
    }
    Point Center(int c) => new(-half + (c % gw) * C + C / 2.0, -half + (c / gw) * C + C / 2.0);
    bool Passable(int c) => c >= 0 && !blocked[c] && !roadBlock[c];

    /// Wert eines Rasterfelds an einer Weltstelle (bilinear)
    double Sample(float[] f, double x, double z)
    {
        double fx = (x + half) / C - 0.5, fz = (z + half) / C - 0.5;
        int i = Math.Clamp((int)Math.Floor(fx), 0, gw - 2), j = Math.Clamp((int)Math.Floor(fz), 0, gw - 2);
        double tx = Math.Clamp(fx - i, 0, 1), tz = Math.Clamp(fz - j, 0, 1);
        int g = j * gw + i;
        return (f[g] * (1 - tx) + f[g + 1] * tx) * (1 - tz) + (f[g + gw] * (1 - tx) + f[g + gw + 1] * tx) * tz;
    }

    public NetworkPlan Plan(IProgress<string> progress)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report(Loc.T("Reading the terrain …"));
        BuildGrid();
        FindTowns();
        var asphalt = plan.Towns.Where(t => !t.OldWest).ToList();
        var oldWest = plan.Towns.Where(t => t.OldWest).ToList();
        foreach (var t in plan.Towns) { graph.Add(new()); }
        foreach (var t in plan.Towns.Where(t => !t.Exits.Any(e => e.Cell >= 0)))
            plan.Problems.Add(Loc.F("Town at {0}: no free road exit at the town edge – it cannot be connected.", Pos(t.CX, t.CZ)));

        // 1. Grundnetz: jede Stadt erreichbar (Prim mit Abzweigungen)
        var start = asphalt.Where(t => t.Exits.Any(e => e.Cell >= 0)).OrderByDescending(t => t.Tiles.Count).FirstOrDefault();
        if (start != null)
        {
            start.Connected = true;
            while (true)
            {
                var left = asphalt.Where(t => !t.Connected && t.Exits.Any(e => e.Cell >= 0 && !e.Used)).ToList();
                if (left.Count == 0) break;
                progress?.Report(Loc.F("Connecting towns ({0} of {1}) …", asphalt.Count(t => t.Connected), asphalt.Count));
                if (!ConnectNext(left, false)) break;
            }
            foreach (var t in asphalt.Where(t => !t.Connected && t.Exits.Any(e => e.Cell >= 0)))
                plan.Problems.Add(Loc.F("Town at {0}: no way to the other towns was found (water, steep walls or other roads in the way).", Pos(t.CX, t.CZ)));
        }

        // 2. Old-West-Orte mit Schotterwegen an das Netz
        foreach (var t in oldWest.Where(t => t.Exits.Any(e => e.Cell >= 0)))
        {
            progress?.Report(Loc.T("Connecting Old West towns …"));
            if (plan.Roads.Count == 0 && !plan.Towns.Any(o => o.Connected && !o.OldWest)) { plan.Problems.Add(Loc.F("Old West town at {0}: no road network to connect to.", Pos(t.CX, t.CZ))); continue; }
            if (!ConnectNext(new List<NetTown> { t }, true))
                plan.Problems.Add(Loc.F("Old West town at {0}: no way to the road network was found.", Pos(t.CX, t.CZ)));
        }

        // 3. Querverbindungen
        progress?.Report(Loc.T("Adding cross connections …"));
        AddLoops(asphalt);

        // 4. Zubringer: freie Stadtausgänge an Straßen, die in der Nähe vorbeilaufen
        progress?.Report(Loc.T("Connecting free town exits to nearby roads …"));
        AddFeeders(asphalt);

        // 5. Ortsanbindungen: Straßen, die dicht an Ortsstraßen vorbeilaufen, kurz anbinden
        progress?.Report(Loc.T("Linking roads that pass close to towns …"));
        AddTownLinks(asphalt);

        foreach (var r in plan.Roads) Measure(r);
        double straight = 0, total = 0;
        foreach (var r in plan.Roads) { var (s, t) = Straightness(r.Samples); straight += s; total += t; }
        plan.StraightShare = total > 0 ? straight / total : 0;
        plan.Seconds = watch.Elapsed.TotalSeconds;
        return plan;
    }

    static string Pos(double x, double z) => Loc.F("{0} {1}, {2} {3}", Math.Abs(Math.Round(x)), x < 0 ? Loc.T("W") : Loc.T("E"), Math.Abs(Math.Round(z)), z < 0 ? Loc.T("S") : Loc.T("N"));

    // ---------------------------------------------------------------- Raster

    void BuildGrid()
    {
        var h = new float[n];
        blocked = new bool[n];
        edge = new bool[n];
        roadBlock = new bool[n];
        nearRoadD = new float[n];
        Array.Fill(nearRoadD, float.MaxValue);
        nearRoadH = new float[n];
        reserveOwner = new int[n];
        Array.Fill(reserveOwner, -1);
        var water = new bool[n];
        var road = roadCell = new bool[n];
        Parallel.For(0, gw, j =>
        {
            for (int i = 0; i < gw; i++)
            {
                int x0 = -half + i * C, z0 = -half + j * C;
                double s = 0;
                bool wet = false, paved = false;
                for (int sz = 0; sz < 4; sz++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        int x = x0 + 2 + sx * 4, z = z0 + 2 + sz * 4;
                        s += w.HeightAt(x, z);
                        wet |= w.WaterAt(x, z);
                        paved |= w.RoadAt(x, z) != 0;
                    }
                int g = j * gw + i;
                h[g] = (float)(s / 16);
                water[g] = wet;
                road[g] = paved;
                double cx = x0 + C / 2.0, cz = z0 + C / 2.0;
                if (cx < -half + 100 || cx > half - 100 || cz < -half + 100 || cz > half - 100) edge[g] = true;
            }
        });
        hb = Blur(h, 1);
        var large = Blur(hb, 6);
        var wide = Blur(hb, 12);
        hcut = new float[n];
        gradX = new float[n];
        gradZ = new float[n];
        mult = new float[n];
        int seed = new GeneratorSpec { Seed = spec.Seed + "/wander" }.SeedValue;
        double amp = spec.Curves switch { 0 => 0.15, 2 => 0.45, _ => 0.3 };
        Parallel.For(0, gw, j =>
        {
            for (int i = 0; i < gw; i++)
            {
                int g = j * gw + i;
                // Schneise: Rücken bis 20 m abgesenkt (Richtung Mittel über ca. 200 m), Täler unverändert
                hcut[g] = hb[g] > large[g] ? Math.Max(hb[g] - 20, large[g]) : hb[g];
                int il = Math.Max(0, i - 1), ir = Math.Min(gw - 1, i + 1), jd = Math.Max(0, j - 1), ju = Math.Min(gw - 1, j + 1);
                gradX[g] = (hb[j * gw + ir] - hb[j * gw + il]) / ((ir - il) * C);
                gradZ[g] = (hb[ju * gw + i] - hb[jd * gw + i]) / ((ju - jd) * C);
                // Täler billiger, Kuppen teurer; großräumiges Rauschen gegen lange Geraden
                double tpi = Math.Clamp((hb[g] - wide[g]) / 25.0, -1, 1);
                double noise = 0.7 * RoadApplier.Octave(i, j, 600.0 / C, seed) + 0.3 * RoadApplier.Octave(i, j, 240.0 / C, seed + 7);
                mult[g] = (float)Math.Max(0.35, (1 + 0.2 * tpi) * (1 + amp * noise));
            }
        });
        // Wasser und vorhandene Straßen außerhalb der Kacheln (+1 Zelle)
        var inTile = tileCell = new bool[n];
        foreach (var p in w.Prefabs.Where(p => p.Def != null && p.IsTile))
            Mark(inTile, p.X, p.Z, p.X + p.SizeX, p.Z + p.SizeZ, 0, centerOnly: true);
        for (int g = 0; g < n; g++)
        {
            bool isRoad = road[g] && !inTile[g];
            if (!water[g] && !isRoad) continue;
            int i = g % gw, j = g / gw, r = 1;
            for (int dj = -r; dj <= r; dj++)
                for (int di = -r; di <= r; di++)
                {
                    int a = i + di, b = j + dj;
                    if (di * di + dj * dj > 5 || a < 0 || b < 0 || a >= gw || b >= gw) continue;
                    if (water[g] && (Math.Abs(di) > 1 || Math.Abs(dj) > 1) && !isRoad) continue;
                    blocked[b * gw + a] = true;
                }
        }
        // vorhandene Straßen außerhalb der Kacheln (Ortsteil- und Gateway-Straßen der Städte) zählen für den Aufpreis „läuft
        // daneben entlang“ wie geplante Straßen
        int pr = (int)Math.Ceiling(ParallelRange / C);
        for (int g = 0; g < n; g++)
        {
            if (!road[g] || inTile[g]) continue;
            int i = g % gw, j = g / gw;
            for (int dj = -pr; dj <= pr; dj++)
                for (int di = -pr; di <= pr; di++)
                {
                    int a = i + di, b = j + dj;
                    if (a < 0 || b < 0 || a >= gw || b >= gw) continue;
                    float d = (float)(C * Math.Sqrt(di * di + dj * dj));
                    int c = b * gw + a;
                    if (d < ParallelRange && d < nearRoadD[c]) { nearRoadD[c] = d; nearRoadH[c] = hb[g]; }
                }
        }
        // Kacheln und Gebäude
        foreach (var p in w.Prefabs.Where(p => p.Def != null))
        {
            int m = p.IsTile ? 24 : p.Def.Kind == PrefabKind.Part ? 4 : 12;
            Mark(blocked, p.X, p.Z, p.X + p.SizeX, p.Z + p.SizeZ, m);
        }
    }

    // Zellen, die das Rechteck (+ m) überdecken bzw. deren Mitte darin liegt
    void Mark(bool[] f, double x0, double z0, double x1, double z1, double m, bool centerOnly = false)
    {
        x0 -= m; z0 -= m; x1 += m; z1 += m;
        double pad = centerOnly ? 0 : C / 2.0 - 0.01;
        int i0 = Math.Max(0, (int)Math.Floor((x0 - pad + half) / C - 0.5) - 1), i1 = Math.Min(gw - 1, (int)Math.Ceiling((x1 + pad + half) / C) + 1);
        int j0 = Math.Max(0, (int)Math.Floor((z0 - pad + half) / C - 0.5) - 1), j1 = Math.Min(gw - 1, (int)Math.Ceiling((z1 + pad + half) / C) + 1);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                double cx = -half + i * C + C / 2.0, cz = -half + j * C + C / 2.0;
                if (cx + pad > x0 && cx - pad < x1 && cz + pad > z0 && cz - pad < z1) f[j * gw + i] = true;
            }
    }

    float[] Blur(float[] src, int r)
    {
        var tmp = new float[n];
        var dst = new float[n];
        Parallel.For(0, gw, j =>
        {
            double s = 0; int c = 0;
            for (int k = 0; k <= Math.Min(gw - 1, r); k++) { s += src[j * gw + k]; c++; }
            for (int i = 0; i < gw; i++)
            {
                tmp[j * gw + i] = (float)(s / c);
                int add = i + r + 1, rem = i - r;
                if (add < gw) { s += src[j * gw + add]; c++; }
                if (rem >= 0) { s -= src[j * gw + rem]; c--; }
            }
        });
        Parallel.For(0, gw, i =>
        {
            double s = 0; int c = 0;
            for (int k = 0; k <= Math.Min(gw - 1, r); k++) { s += tmp[k * gw + i]; c++; }
            for (int j = 0; j < gw; j++)
            {
                dst[j * gw + i] = (float)(s / c);
                int add = j + r + 1, rem = j - r;
                if (add < gw) { s += tmp[add * gw + i]; c++; }
                if (rem >= 0) { s -= tmp[rem * gw + i]; c--; }
            }
        });
        return dst;
    }

    // ---------------------------------------------------------------- Städte und Ausgänge

    void FindTowns()
    {
        var tiles = w.Prefabs.Where(p => p.IsTile && p.Def != null).ToList();
        var parent = Enumerable.Range(0, tiles.Count).ToArray();
        int Root(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        // eine Stadt = aneinandergrenzende Kacheln und Kacheln, die über Straßen verbunden sind (Ortsteile, Gateway); nur der
        // Abstand reicht nicht: zwei Städte liegen manchmal näher beieinander als ein Ortsteil am Hauptteil (Ninezo-Land)
        for (int a = 0; a < tiles.Count; a++)
            for (int b = a + 1; b < tiles.Count; b++)
                if (Gap(tiles[a], tiles[b]) <= 4) parent[Root(a)] = Root(b);
        var index = tiles.Select((t, k) => (t, k)).ToDictionary(p => p.t, p => p.k);
        var near = new Dictionary<long, List<int>>();
        long Key(int x, int z) => ((long)Math.Floor(x / 150.0) << 32) ^ (uint)(int)Math.Floor(z / 150.0);
        for (int k = 0; k < tiles.Count; k++)
            for (int x = tiles[k].X; x < tiles[k].X + tiles[k].SizeX + 150; x += 150)
                for (int z = tiles[k].Z; z < tiles[k].Z + tiles[k].SizeZ + 150; z += 150)
                {
                    long key = Key(Math.Min(x, tiles[k].X + tiles[k].SizeX - 1), Math.Min(z, tiles[k].Z + tiles[k].SizeZ - 1));
                    if (!near.TryGetValue(key, out var l)) near[key] = l = new();
                    if (!l.Contains(k)) l.Add(k);
                }
        int TileAt(int x, int z) => near.TryGetValue(Key(x, z), out var l) ? l.FirstOrDefault(k => tiles[k].Contains(x + 0.5, z + 0.5), -1) : -1;
        foreach (var (x, z, tile) in w.TileExits)
        {
            if (!index.TryGetValue(tile, out int origin)) continue;
            var (dx, dz) = Outward(tile, x, z);
            int sx = (int)Math.Floor(x + dx * 2), sz = (int)Math.Floor(z + dz * 2);
            if (w.RoadAt(sx, sz) == 0) continue;
            // dem Belag folgen (höchstens 900 m weit; Ortsteilstraßen winden sich, Ninezo-Land: 630 m)
            var seen = new HashSet<long> { ((long)sx << 32) ^ (uint)sz };
            var queue = new Queue<(int X, int Z)>();
            queue.Enqueue((sx, sz));
            while (queue.Count > 0 && seen.Count < 250000)
            {
                var (cx, cz) = queue.Dequeue();
                foreach (var (ax, az) in new[] { (cx + 1, cz), (cx - 1, cz), (cx, cz + 1), (cx, cz - 1) })
                {
                    if (Math.Abs(ax - sx) + Math.Abs(az - sz) > 900 || w.RoadAt(ax, az) == 0) continue;
                    if (!seen.Add(((long)ax << 32) ^ (uint)az)) continue;
                    int other = TileAt(ax, az);
                    if (other == origin) continue;
                    if (other >= 0) { parent[Root(origin)] = Root(other); continue; }
                    queue.Enqueue((ax, az));
                }
            }
        }
        foreach (var grp in Enumerable.Range(0, tiles.Count).GroupBy(Root))
        {
            var t = new NetTown { Id = plan.Towns.Count };
            t.Tiles.AddRange(grp.Select(k => tiles[k]));
            t.X0 = t.Tiles.Min(p => p.X); t.Z0 = t.Tiles.Min(p => p.Z);
            t.X1 = t.Tiles.Max(p => p.X + p.SizeX); t.Z1 = t.Tiles.Max(p => p.Z + p.SizeZ);
            t.OldWest = t.Tiles.Count(p => p.Name.Contains("_oldwest_", StringComparison.OrdinalIgnoreCase)) * 2 > t.Tiles.Count;
            plan.Towns.Add(t);
        }
        var townOf = new Dictionary<PlacedPrefab, NetTown>();
        foreach (var t in plan.Towns) foreach (var p in t.Tiles) townOf[p] = t;

        // Randstreifen 100 m sperren – außer bis 160 m vor Stadtausgängen nahe am Rand, damit die Straße dort einen Bogen statt
        // eines rechtwinkligen Knicks machen kann (Stadt am Südrand von Zutico-Land). Auch dort bleibt die Mittellinie mindestens
        // EdgeCenter vom Rand (Wunsch des Nutzers 8.11.2026: Straßen nicht näher als 80 m an den Kartenrand; vorher bis 30 m)
        var exitPoints = w.TileExits.Select(e => new Point(e.X, e.Z)).Where(p => half - Math.Max(Math.Abs(p.X), Math.Abs(p.Y)) < 260).ToList();
        for (int g = 0; g < n; g++)
        {
            if (!edge[g]) continue;
            var c = Center(g);
            bool relax = half - Math.Max(Math.Abs(c.X), Math.Abs(c.Y)) >= EdgeCenter + C / 2.0 && exitPoints.Any(p => (p - c).LengthSquared < 160 * 160);
            if (!relax) blocked[g] = true;
        }

        foreach (var (x, z, tile) in w.TileExits)
        {
            if (!townOf.TryGetValue(tile, out var town)) continue;
            var (dx, dz) = Outward(tile, x, z);
            // Nachbarkachel davor?
            if (tiles.Any(o => o != tile && o.Contains(x + dx * 3, z + dz * 3))) continue;
            var (width, total, surface) = w.TileExitWidth(tile, x, z);
            if (width <= 0) continue;
            // schon eine Straße davor (Ortsteil-, Gateway- oder eigene Straße)?
            double px = -dz, pz = dx;
            bool paved = false;
            for (double d = 3; d <= 14 && !paved; d += 1)
                for (double l = -(total / 2.0 - 1); l <= total / 2.0 - 1 && !paved; l += 1)
                    paved = w.RoadAt((int)Math.Floor(x + dx * d + px * l), (int)Math.Floor(z + dz * d + pz * l)) != 0;
            if (paved) continue;
            var e = new NetExit
            {
                Town = town, Tile = tile, X = x, Z = z, DX = dx, DZ = dz, Width = width, Gravel = surface == WorldData.Gravel,
                Gateway = tile.Name.Contains("gateway", StringComparison.OrdinalIgnoreCase),
            };
            var a = e.Approach;
            int c = CellOf(a.X, a.Y);
            // vor dem Ausgang frei (gerades Stück bis zur Zelle)?
            bool free = Passable(c);
            for (double d = 8; d < ApproachLength && free; d += 4)
            {
                int ix = (int)Math.Floor(x + dx * d), iz = (int)Math.Floor(z + dz * d);
                free = !w.WaterAt(ix, iz) && !tiles.Any(o => o.Contains(ix, iz));
                // keine vorhandene Straße quer vor dem Ausgang (Wahava-Land: Ortsteilstraße 40 m vor dem Ausgang, das gerade
                // Anschlussstück lief ohne Höhenanpassung darüber – Sprung von 4 m)
                for (double l = -10; l <= 10 && free && d >= 15; l += 2)
                    free = w.RoadAt((int)Math.Floor(x + dx * d + px * l), (int)Math.Floor(z + dz * d + pz * l)) == 0;
            }
            if (free) e.Cell = c;
            town.Exits.Add(e);
        }
        nearTown = new bool[n];
        foreach (var t in plan.Towns) Mark(nearTown, t.X0, t.Z0, t.X1, t.Z1, 100);
        // Platz vor den freien Ausgängen möglichst frei lassen (spätere Straßen dorthin): bis 110 m teurer, bis 52 m (vor der
        // Zelle des Anschlussstücks) für fremde Straßen gesperrt – sonst lief eine Straße knapp vor einem Ausgang vorbei und die
        // spätere Straße aus diesem Ausgang überquerte sie mit ihrem geraden Anschlussstück (Sprung bis 4 m, Wahava/Ninezo)
        reserveNear = new bool[n];
        foreach (var e in plan.Towns.SelectMany(t => t.Exits).Where(e => e.Cell >= 0))
        {
            exitList.Add(e);
            double px = -e.DZ, pz = e.DX;
            for (double d = 8; d <= 110; d += 4)
                for (double l = -16; l <= 16; l += 4)
                {
                    int c = CellOf(e.X + e.DX * d + px * l, e.Z + e.DZ * d + pz * l);
                    if (c < 0) continue;
                    reserveOwner[c] = exitList.Count - 1;
                    if (d <= 52 && c != e.Cell) reserveNear[c] = true;
                }
        }
    }

    bool[] reserveNear;

    /// Ausgang noch benutzbar: frei und keine geplante Straße an seinem Anschlussstück
    bool Open(NetExit e) => !e.Used && e.Cell >= 0 && Passable(e.Cell);

    // Richtung nach außen an einem Ausgang (Mitte einer Kachelseite)
    static (double DX, double DZ) Outward(PlacedPrefab tile, double x, double z) =>
        Math.Abs(z - tile.Z) < 0.6 ? (0, -1) : Math.Abs(z - (tile.Z + tile.SizeZ)) < 0.6 ? (0, 1) : Math.Abs(x - tile.X) < 0.6 ? (-1, 0) : (1, 0);

    static double Gap(PlacedPrefab a, PlacedPrefab b)
    {
        double dx = Math.Max(0, Math.Max(a.X - (b.X + b.SizeX), b.X - (a.X + a.SizeX)));
        double dz = Math.Max(0, Math.Max(a.Z - (b.Z + b.SizeZ), b.Z - (a.Z + a.SizeZ)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    double TownDistance(double x, double z) => plan.Towns.Count == 0 ? double.MaxValue : plan.Towns.Min(t =>
    {
        double dx = Math.Max(0, Math.Max(t.X0 - x, x - t.X1)), dz = Math.Max(0, Math.Max(t.Z0 - z, z - t.Z1));
        return Math.Sqrt(dx * dx + dz * dz);
    });

    // ---------------------------------------------------------------- Abzweigungen und Kreuzungen

    /// Stelle an einer geplanten Straße, an der eine neue Straße rechtwinklig abzweigen kann (Cell = Zelle 44 m daneben)
    sealed record Tap(NetRoad Road, int K, Point P, int Cell, int Side, int Dir)
    {
        public int Arrive => (Dir + 8) % Dirs;   // Fahrtrichtung, mit der eine Straße hier ankommt (zur Straße hin)
    }

    List<Tap> taps = new();
    // Kreuzung: von Zelle (Key) in Richtung Dir gerade über die Straße nach To
    Dictionary<int, List<(int To, float Cost, Point Mid, NetRoad Road, int K, int Dir)>> jumps = new();

    const int Dirs = 16;   // Fahrtrichtungen; Richtung k = k · 22,5° (0 = Osten, 4 = Norden), passend zu Steps
    static int DirOf(double dx, double dz) => ((int)Math.Round(Math.Atan2(dz, dx) / (Math.PI / 8)) % Dirs + Dirs) % Dirs;
    static int DirDiff(int a, int b) { int d = Math.Abs(a - b) % Dirs; return Math.Min(d, Dirs - d); }

    void BuildTaps(double townGap = TownGap)
    {
        taps = new List<Tap>();
        jumps = new();
        foreach (var r in plan.Roads)
        {
            var s = r.Samples;
            for (int k = 3; k + 3 < s.Count; k++)
            {
                var p = s[k];
                if (nodesUsed.Any(q => (q - p).Length < NodeSpacing)) continue;
                if (TownDistance(p.X, p.Y) < townGap) continue;
                Vector a = s[k] - s[k - 3], b = s[k + 3] - s[k];
                if (a.Length < 1 || b.Length < 1 || Math.Abs(Vector.AngleBetween(a, b)) > 25) continue;
                var d = s[k + 3] - s[k - 3]; d.Normalize();
                var nrm = new Vector(-d.Y, d.X);
                double hp = Sample(hb, p.X, p.Y);
                int[] side = new int[2];
                for (int q = 0; q < 2; q++)
                {
                    int sg = q == 0 ? 1 : -1;
                    var ap = p + nrm * (sg * ApproachLength);
                    int c = CellOf(ap.X, ap.Y);
                    side[q] = -1;
                    if (!Passable(c) || Math.Abs(Sample(hb, ap.X, ap.Y) - hp) > ApproachLength * spec.Grade * 1.5) continue;
                    side[q] = c;
                    if (!r.Gravel) taps.Add(new Tap(r, k, p, c, sg, DirOf(nrm.X * sg, nrm.Y * sg)));
                }
                // Kreuzung: von einer Seite gerade auf die andere
                if (side[0] >= 0 && side[1] >= 0)
                {
                    float cost = (float)(2 * ApproachLength + CrossPenalty);
                    if (!jumps.TryGetValue(side[0], out var l0)) jumps[side[0]] = l0 = new();
                    if (!jumps.TryGetValue(side[1], out var l1)) jumps[side[1]] = l1 = new();
                    l0.Add((side[1], cost, p, r, k, DirOf(-nrm.X, -nrm.Y)));
                    l1.Add((side[0], cost, p, r, k, DirOf(nrm.X, nrm.Y)));
                }
            }
        }
    }

    // ---------------------------------------------------------------- Wegsuche auf dem Raster

    // Zustand = Zelle · 16 + Fahrtrichtung: Je Schritt darf die Straße höchstens 22,5° abbiegen (Hinweis des Nutzers 7.11.2026:
    // ohne Fahrtrichtung bog die Suche bei jedem 16-m-Schritt beliebig ab, am Hang entstanden dichte Zacken). Kleinster Bogen so
    // ca. 40–90 m Halbmesser, eine Kehre braucht ca. 150 m Breite.
    float[] dist;
    int[] parent;
    int[] source;   // Nummer der Quelle, von der ein Zustand erreicht wurde

    /// Dijkstra von den Quellen (Zelle, Fahrtrichtung) aus; stop = Zustand, bei dem aufgehört wird (true), maxCost = Grenze;
    /// sourceRoads[q] = Straße, an der Quelle q abzweigt (darf nicht gekreuzt werden)
    int Search(List<(int Cell, int Dir, float Cost)> sources, Func<int, bool> stop, bool cut, float maxCost = float.MaxValue, NetRoad[] sourceRoads = null)
    {
        dist ??= new float[n * Dirs];
        parent ??= new int[n * Dirs];
        source ??= new int[n * Dirs];
        Array.Fill(dist, float.MaxValue);
        var h = cut ? hcut : hb;
        var queue = new PriorityQueue<int, float>();
        for (int q = 0; q < sources.Count; q++)
        {
            var (c, dir, cost) = sources[q];
            if (c < 0) continue;
            int s = c * Dirs + dir;
            if (cost >= dist[s]) continue;
            dist[s] = cost; parent[s] = -1; source[s] = q;
            queue.Enqueue(s, cost);
        }
        double G = spec.Grade, Gm = Math.Max(spec.MaxGrade, G + 0.01);
        while (queue.TryDequeue(out int st, out float d))
        {
            if (d > dist[st]) continue;
            if (stop != null && stop(st)) return st;
            if (d > maxCost) break;
            int g = st / Dirs, dir = st % Dirs;
            int i = g % gw, j = g / gw;
            for (int turn = -1; turn <= 1; turn++)
            {
                int ndir = (dir + turn + Dirs) % Dirs;
                var (di, dj) = Steps[ndir];
                int a = i + di, b = j + dj;
                if (a < 0 || b < 0 || a >= gw || b >= gw) continue;
                int ng = b * gw + a;
                if (!Passable(ng)) continue;
                if (Math.Abs(di) == 2 || Math.Abs(dj) == 2)
                {
                    int si = Math.Sign(di), sj = Math.Sign(dj);
                    int m1 = Math.Abs(di) == 2 ? j * gw + i + si : (j + sj) * gw + i;
                    int m2 = Math.Abs(di) == 2 ? (j + dj) * gw + i + si : (j + sj) * gw + i + di;
                    if (!Passable(m1) || !Passable(m2)) continue;
                }
                double len = C * Math.Sqrt(di * di + dj * dj);
                double grade = Math.Abs(h[ng] - h[g]) / len;
                double cost = len * (turn != 0 ? 1.02 : 1);
                if (grade > 0.6 * G) { double t = (Math.Min(grade, G) - 0.6 * G) / (0.4 * G); cost += len * t * t; }
                if (grade > G) { double t = (Math.Min(grade, Gm) - G) / (Gm - G); cost += len * (2 + 6 * t * t); }
                if (grade > Gm) cost += len * (8 + 400 * (grade - Gm));
                // Querneigung (Hangstraßen gehen, kosten aber Erdarbeit) und Felswände
                double ux = di * C / len, uz = dj * C / len;
                double gx = gradX[ng], gz = gradZ[ng];
                double cross = Math.Abs(-gx * uz + gz * ux);
                if (cross > 0.18) cost += len * 6 * (cross - 0.18);
                double slope = Math.Sqrt(gx * gx + gz * gz);
                if (slope > 0.7) cost += len * 10 * (slope - 0.7);
                cost *= 0.5 * (mult[g] + mult[ng]);
                bool own = reserveOwner[ng] >= 0 && allowed.Contains(exitList[reserveOwner[ng]]);
                if (reserveOwner[ng] >= 0 && !own) { if (reserveNear[ng]) continue; cost *= 4; }
                else if (nearTown[ng] && !own) cost *= 1.8;
                // neben einer geplanten Straße entlang (bis 160 m, Höhe ähnlich): teurer, je näher und je ebener, desto mehr; bei
                // großem Höhenunterschied (Hangstraße über der anderen) kein Aufpreis
                if (nearRoadD[ng] < ParallelRange)
                {
                    double closeness = 1 - Math.Clamp((nearRoadD[ng] - RoadBuffer) / (ParallelRange - RoadBuffer), 0, 1);
                    double level = 1 - Math.Clamp(Math.Abs(h[ng] - nearRoadH[ng]) / ParallelHeight, 0, 1);
                    cost *= 1 + 1.5 * closeness * closeness * level;   // 50 m: × 2, 100 m: × 1,3, 140 m: × 1,03
                }
                float nd = (float)(d + cost);
                int ns = ng * Dirs + ndir;
                if (nd < dist[ns]) { dist[ns] = nd; parent[ns] = st; source[ns] = source[st]; queue.Enqueue(ns, nd); }
            }
            // Kreuzung: nur ungefähr rechtwinklig über die Straße
            if (jumps.TryGetValue(g, out var list))
                foreach (var (to, cost, _, jr, _, jd) in list)
                {
                    if (DirDiff(dir, jd) > 1) continue;
                    if (sourceRoads != null && sourceRoads[source[st]] == jr) continue;
                    float nd = d + cost;
                    int ns = to * Dirs + jd;
                    if (nd < dist[ns]) { dist[ns] = nd; parent[ns] = st; source[ns] = source[st]; queue.Enqueue(ns, nd); }
                }
        }
        return -1;
    }

    /// günstigste Ankunft in einer Zelle mit ungefähr dieser Fahrtrichtung: ±22,5° frei, bis ±45° gegen Aufpreis
    /// (rechtwinklig erzwungen gab vor Abzweigungen Kringel): (Kosten inkl. Aufpreis, Zustand)
    (float Cost, int State) Arrival(int cell, int dir)
    {
        float best = float.MaxValue; int state = -1;
        for (int t = -2; t <= 2; t++)
        {
            int s = cell * Dirs + (dir + t + Dirs) % Dirs;
            if (dist[s] == float.MaxValue) continue;
            float c = dist[s] + (Math.Abs(t) == 2 ? 60 : 0);
            if (c < best) { best = c; state = s; }
        }
        return (best, state);
    }

    /// Weg dreht sich ein (Kringel): innerhalb von 24 Schritten mehr als ca. 250° in dieselbe Richtung
    bool Curly(int state)
    {
        var dirs = new List<int>();
        for (int s = state; s >= 0; s = parent[s]) dirs.Add(s % Dirs);
        for (int a = 0; a + 1 < dirs.Count; a++)
        {
            int sum = 0;
            for (int b = a; b + 1 < dirs.Count && b < a + 24; b++)
            {
                sum += ((dirs[b] - dirs[b + 1] + Dirs + Dirs / 2) % Dirs) - Dirs / 2;
                if (Math.Abs(sum) > 11) return true;
            }
        }
        return false;
    }

    /// Zellen von der Quelle bis zum Zustand (Quelle zuerst)
    List<int> Trace(int state)
    {
        var cells = new List<int>();
        for (int s = state; s >= 0; s = parent[s]) cells.Add(s / Dirs);
        cells.Reverse();
        return cells;
    }

    // ---------------------------------------------------------------- Netz aufbauen

    /// eine Stadt aus targets an das bisherige Netz anschließen (die günstigste); gravel = Old-West-Schotterweg
    bool ConnectNext(List<NetTown> targets, bool gravel)
    {
        BuildTaps();
        var exits = targets.SelectMany(t => t.Exits).Where(Open).ToList();
        bool cut = DrawCut();
        Dictionary<int, NetExit> goal = null;
        List<object> what = null;
        int found = -1;
        // erst ohne Knick vor den Ausgängen (Bogen ab 44 m davor); nur wenn das nicht geht (Kartenrand, Nachbarstadt) bis 90°
        // Knick (Hinweis des Nutzers 7.11.2026: der Knick war zu billig und gab unschöne Ecken an Ausgängen mit Platz)
        // dreht sich der gefundene Weg ein (Schleife über sich selbst, weil er genau in Richtung Stadt ankommen muss und für einen
        // Bogen kein Platz war – Wahava-Land/Ninezo-Land: Kreuzung mit sich selbst, Sprünge bis 4 m), nächster Versuch mit schrägerer
        // Einfahrt bzw. Knick; bleibt alles eingedreht, gilt der erste Weg
        (NetExit Exit, List<int> Cells, object From, bool Cut)? first = null;
        foreach (int spread in new[] { 1, 2, 4 })
        {
            var sources = new List<(int Cell, int Dir, float Cost)>();
            what = new List<object>();
            foreach (var e in plan.Towns.Where(t => t.Connected && !t.OldWest).SelectMany(t => t.Exits).Where(Open))
            {
                // Schotterweg lieber an eine Straße als an einen 16-m-Ausgang
                foreach (var (dir, extra) in ExitTurns(DirOf(e.DX, e.DZ), spread)) { sources.Add((e.Cell, dir, (gravel ? 600 : 0) + extra)); what.Add(e); }
            }
            foreach (var t in taps)
                for (int k = -2; k <= 2; k++) { sources.Add((t.Cell, (t.Dir + k + Dirs) % Dirs, JunctionPenalty + (Math.Abs(k) == 2 ? 60 : 0))); what.Add(t); }
            if (sources.Count == 0) return false;
            // Ziel: vor einem Ausgang ankommen, ungefähr in Richtung Stadt fahrend
            goal = new Dictionary<int, NetExit>();
            foreach (var e in exits)
                for (int t = -spread; t <= spread; t++) goal.TryAdd(e.Cell * Dirs + (DirOf(-e.DX, -e.DZ) + t + Dirs) % Dirs, e);
            allowed = new HashSet<NetExit>(what.OfType<NetExit>().Concat(exits));
            // eine Abzweigung darf ihre eigene Straße nicht kreuzen (sonst zweigt sie auf der falschen Seite ab und kreuzt gleich danach)
            var roads = what.Select(o => (o as Tap)?.Road).ToArray();
            found = Search(sources, s => goal.ContainsKey(s), cut, sourceRoads: roads);
            if (found < 0 && cut) { cut = false; found = Search(sources, s => goal.ContainsKey(s), false, sourceRoads: roads); }
            if (found < 0) continue;
            var candidate = (goal[found], Trace(found), what[source[found]], cut);
            if (!Curly(found)) { first = candidate; break; }
            first ??= candidate;
        }
        if (first == null) return false;
        var (exit, cells, from, usedCut) = first.Value;
        AddRoad(exit, cells, from, gravel, usedCut, loop: false);
        exit.Town.Connected = true;
        return true;
    }

    /// Fahrtrichtungen beim Verlassen eines Ausgangs: geradeaus und ±22,5°; mit spread 4 auch stärkerer Knick (bis 90°) gegen
    /// Aufpreis – nur für Ausgänge, vor denen kein Platz für einen weiten Bogen ist (Kartenrand, Nachbarstadt)
    static IEnumerable<(int Dir, float Extra)> ExitTurns(int dir, int spread)
    {
        for (int t = -spread; t <= spread; t++) yield return ((dir + t + Dirs) % Dirs, 60f * Math.Max(0, Math.Abs(t) - 1));
    }

    /// Straßen, die der Weg bis zu diesem Zustand kreuzt (Sprünge über eine Straße)
    List<NetRoad> CrossedRoads(int state)
    {
        var list = new List<NetRoad>();
        for (int s = state; s >= 0 && parent[s] >= 0; s = parent[s])
        {
            int a = parent[s] / Dirs, b = s / Dirs;
            if (IsNeighbour(a, b) || !jumps.TryGetValue(a, out var js)) continue;
            foreach (var j in js) if (j.To == b) { list.Add(j.Road); break; }
        }
        return list;
    }

    bool DrawCut() => rnd.NextDouble() < spec.Cuts switch { 0 => 0, 2 => 0.35, _ => 0.15 };

    /// Straße eintragen: von exit (neue Stadt) über die Zellen (Quelle zuerst) zur Quelle (Ausgang oder Abzweigung)
    void AddRoad(NetExit exit, List<int> cells, object to, bool gravel, bool cut, bool loop, Tap startTap = null)
    {
        // Linie von der Quelle zum Ziel, danach umdrehen (die Straße beginnt am Ausgang der neuen Stadt)
        var pts = new List<Point>();
        var fixedAt = new List<bool>();
        void Add(Point p, bool f) { pts.Add(p); fixedAt.Add(f); }
        Point srcAnchor, srcApproach;
        if (to is NetExit se) { srcAnchor = new Point(se.X, se.Z); srcApproach = se.Approach; }
        else { var t = (Tap)to; srcAnchor = t.P; srcApproach = Center(t.Cell); }
        // gerade fest sind nur die ersten 24 m am Ausgang/an der Abzweigung; dahinter darf die Glättung die Kurve runden
        // (Hinweis des Nutzers 7.11.2026: am Ende des festen 44-m-Stücks entstand eine harte Ecke)
        const double Straight = 24;
        Point Along(Point a, Point b) => a + (b - a) * Math.Min(1, Straight / Math.Max(1e-6, (b - a).Length));
        Add(srcAnchor, true);
        Add(Along(srcAnchor, srcApproach), true);
        Add(srcApproach, false);
        var crossings = new List<(Point P, NetRoad Road, int K)>();
        for (int q = 0; q < cells.Count; q++)
        {
            var c = Center(cells[q]);
            if (q > 0 && jumps.TryGetValue(cells[q - 1], out var list) && list.FirstOrDefault(j => j.To == cells[q]) is var jp && jp.Road != null
                && !IsNeighbour(cells[q - 1], cells[q]))
            {
                // Kreuzung: gerade über die Straße
                fixedAt[^1] = true;
                Add(jp.Mid, true);
                Add(c, true);
                crossings.Add((jp.Mid, jp.Road, jp.K));
                continue;
            }
            if ((c - srcApproach).Length < 14 || (c - exit.Approach).Length < 14) continue;
            Add(c, false);
        }
        var exitPoint = new Point(exit.X, exit.Z);
        Add(exit.Approach, false);
        Add(Along(exitPoint, exit.Approach), true);
        Add(exitPoint, true);
        pts.Reverse(); fixedAt.Reverse();
        var line = KeepOffEdge(Smooth(pts, fixedAt));

        var r = new NetRoad
        {
            Points = line, Gravel = gravel, Loop = loop, Cut = cut, From = exit,
            Width = gravel ? Math.Clamp(exit.Width, 6, 12) : 16,
        };
        if (to is NetExit te) { r.To = te; te.Used = true; }
        else { var t = (Tap)to; r.Junction = t.P; r.EndRoad = t.Road; plan.Junctions.Add(t.P); nodesUsed.Add(t.P); }
        if (startTap != null) { r.StartRoad = startTap.Road; plan.Junctions.Add(startTap.P); nodesUsed.Add(startTap.P); }
        exit.Used = true;
        foreach (var (p, cr, _) in crossings) { r.Crossings.Add(p); r.CrossedRoads.Add(cr); plan.CrossingPoints.Add(p); nodesUsed.Add(p); }
        nodesUsed.Add(line[0]); nodesUsed.Add(line[^1]);
        r.Samples = Resample(line, 16);
        r.Length = r.Samples.Count > 1 ? (r.Samples.Count - 1) * 16.0 : 0;
        plan.Roads.Add(r);
        BlockAlong(line);
        AddToGraph(r, to, crossings, startTap);
    }

    /// Mittellinie mindestens EdgeCenter vom Kartenrand (die Glättung kann sie über die Rasterzellen hinaus ziehen); die festen
    /// Anschlussstücke an den Enden (bis 30 m) bleiben, wie sie sind – ein Stadtausgang lässt sich nicht verschieben
    List<Point> KeepOffEdge(List<Point> line)
    {
        double lim = half - EdgeCenter, total = 0;
        for (int q = 1; q < line.Count; q++) total += (line[q] - line[q - 1]).Length;
        var res = new List<Point>(line.Count);
        double walked = 0;
        for (int q = 0; q < line.Count; q++)
        {
            if (q > 0) walked += (line[q] - line[q - 1]).Length;
            var p = line[q];
            if (walked > 30 && total - walked > 30) p = new Point(Math.Clamp(p.X, -lim, lim), Math.Clamp(p.Y, -lim, lim));
            res.Add(p);
        }
        return res;
    }

    bool IsNeighbour(int a, int b) { int di = Math.Abs(a % gw - b % gw), dj = Math.Abs(a / gw - b / gw); return di <= 2 && dj <= 2; }

    // Zellen nahe der neuen Straße sperren (andere Straßen nur über Abzweigungen und Kreuzungen); bis ParallelRange Abstand und
    // Geländehöhe der Straße merken (Aufpreis für Straßen, die daneben entlanglaufen)
    void BlockAlong(List<Point> line)
    {
        for (int q = 0; q + 1 < line.Count; q++)
        {
            Point a = line[q], b = line[q + 1];
            int i0 = Math.Max(0, (int)((Math.Min(a.X, b.X) - ParallelRange + half) / C) - 1), i1 = Math.Min(gw - 1, (int)((Math.Max(a.X, b.X) + ParallelRange + half) / C) + 1);
            int j0 = Math.Max(0, (int)((Math.Min(a.Y, b.Y) - ParallelRange + half) / C) - 1), j1 = Math.Min(gw - 1, (int)((Math.Max(a.Y, b.Y) + ParallelRange + half) / C) + 1);
            var ab = b - a;
            double l2 = ab.LengthSquared;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int c = j * gw + i;
                    var p = Center(c);
                    double t = l2 < 1e-9 ? 0 : Math.Clamp(((p - a) * ab) / l2, 0, 1);
                    var foot = a + ab * t;
                    double d = (p - foot).Length;
                    if (d <= RoadBuffer) roadBlock[c] = true;
                    if (d < ParallelRange && d < nearRoadD[c]) { nearRoadD[c] = (float)d; nearRoadH[c] = (float)Sample(hb, foot.X, foot.Y); }
                }
        }
    }

    static double SegDist(Point p, Point a, Point b)
    {
        var ab = b - a; double l2 = ab.LengthSquared;
        double t = l2 < 1e-9 ? 0 : Math.Clamp(((p - a) * ab) / l2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    // Linie glätten: auf 4 m verdichten, Mittel über Nachbarn (feste Punkte bleiben), danach ausdünnen
    static List<Point> Smooth(List<Point> pts, List<bool> fixedAt)
    {
        var dense = new List<Point> { pts[0] };
        var fx = new List<bool> { true };
        for (int q = 0; q + 1 < pts.Count; q++)
        {
            double len = (pts[q + 1] - pts[q]).Length;
            int steps = Math.Max(1, (int)Math.Ceiling(len / 4));
            bool both = fixedAt[q] && fixedAt[q + 1];
            for (int k = 1; k <= steps; k++)
            {
                dense.Add(pts[q] + (pts[q + 1] - pts[q]) * (k / (double)steps));
                fx.Add(k == steps ? fixedAt[q + 1] : both);
            }
        }
        var cur = dense.ToArray();
        for (int it = 0; it < 10; it++)
        {
            var next = (Point[])cur.Clone();
            for (int q = 1; q + 1 < cur.Length; q++)
                if (!fx[q]) next[q] = new Point((cur[q - 1].X + 2 * cur[q].X + cur[q + 1].X) / 4, (cur[q - 1].Y + 2 * cur[q].Y + cur[q + 1].Y) / 4);
            cur = next;
        }
        return RoadRouter.Simplify(cur.ToList(), 0.5);
    }

    static List<Point> Resample(List<Point> line, double step)
    {
        var res = new List<Point> { line[0] };
        double carry = 0;
        for (int q = 0; q + 1 < line.Count; q++)
        {
            var a = line[q]; var b = line[q + 1];
            double len = (b - a).Length, pos = step - carry;
            while (pos <= len) { res.Add(a + (b - a) * (pos / len)); pos += step; }
            carry = len - (pos - step);
        }
        if ((res[^1] - line[^1]).Length > 1) res.Add(line[^1]);
        return res;
    }

    // ---------------------------------------------------------------- Straßengraph (für Querverbindungen)

    int NewNode() { graph.Add(new()); return graph.Count - 1; }
    void Link(int a, int b, double cost) { graph[a].Add((b, cost)); graph[b].Add((a, cost)); }

    void AddToGraph(NetRoad r, object to, List<(Point P, NetRoad Road, int K)> crossings, Tap startTap)
    {
        r.Nodes = new int[r.Samples.Count];
        for (int k = 0; k < r.Samples.Count; k++)
        {
            r.Nodes[k] = NewNode();
            if (k > 0) Link(r.Nodes[k - 1], r.Nodes[k], (r.Samples[k] - r.Samples[k - 1]).Length);
        }
        if (startTap != null) Link(r.Nodes[0], startTap.Road.Nodes[startTap.K], 0);
        else Link(r.Nodes[0], r.From.Town.Id, ThroughTown);
        if (to is NetExit te) Link(r.Nodes[^1], te.Town.Id, ThroughTown);
        else { var t = (Tap)to; Link(r.Nodes[^1], t.Road.Nodes[t.K], 0); }
        foreach (var (p, road, k) in crossings)
        {
            int best = 0;
            for (int q = 1; q < r.Samples.Count; q++) if ((r.Samples[q] - p).Length < (r.Samples[best] - p).Length) best = q;
            Link(r.Nodes[best], road.Nodes[k], 0);
        }
    }

    double[] GraphDistances(int from) => GraphDistances(new[] { (from, 0.0) });

    double[] GraphDistances(IEnumerable<(int Node, double D0)> from)
    {
        var d = new double[graph.Count];
        Array.Fill(d, double.MaxValue);
        var q = new PriorityQueue<int, double>();
        foreach (var (f, d0) in from) if (d0 < d[f]) { d[f] = d0; q.Enqueue(f, d0); }
        while (q.TryDequeue(out int a, out double da))
        {
            if (da > d[a]) continue;
            foreach (var (b, c) in graph[a])
                if (da + c < d[b]) { d[b] = da + c; q.Enqueue(b, d[b]); }
        }
        return d;
    }

    /// Weg über das Netz von einem Ausgang aus: durch den Ort (Manhattan-Abstand + 20 m) zu den Ausgängen, an denen schon
    /// Straßen beginnen, und weiter über diese. Der Stadtknoten allein (ThroughTown 60 m) unterschätzt den Umweg bei großen
    /// Orten – ein Ausgang auf der anderen Seite galt dann als „gleich neben“ der eigenen Zufahrt.
    double[] ExitDistances(NetExit e)
    {
        var src = new List<(int, double)>();
        double Manhattan(double x, double z) => Math.Abs(x - e.X) + Math.Abs(z - e.Z) + 20;
        foreach (var r in plan.Roads)
        {
            if (r.From?.Town == e.Town) src.Add((r.Nodes[0], Manhattan(r.From.X, r.From.Z)));
            if (r.To?.Town == e.Town) src.Add((r.Nodes[^1], Manhattan(r.To.X, r.To.Z)));
        }
        return src.Count > 0 ? GraphDistances(src) : GraphDistances(e.Town.Id);
    }

    // ---------------------------------------------------------------- Querverbindungen

    void AddLoops(List<NetTown> asphalt)
    {
        int connected = asphalt.Count(t => t.Connected);
        if (connected < 3 || plan.Roads.Count == 0) return;
        int wanted = (int)Math.Round(connected * (spec.Loops switch { 0 => 0.15, 2 => 0.6, _ => 0.35 }));
        int made = 0;
        // Umweg über das Netz: Netzlänge / Länge der neuen Straße
        double townRatio = spec.Loops switch { 0 => 1.8, 2 => 1.3, _ => 1.5 }, roadRatio = spec.Loops switch { 0 => 2.2, 2 => 1.5, _ => 1.8 };
        // a) von freien Ausgängen angeschlossener Städte
        foreach (var e in asphalt.Where(t => t.Connected).SelectMany(t => t.Exits).Where(Open).OrderBy(_ => rnd.Next()).ToList())
        {
            if (made >= wanted) break;
            if (e.Used) continue;
            if (TryLoop(ExitTurns(DirOf(e.DX, e.DZ), 1).Select(t => (e.Cell, t.Dir, t.Extra)).ToList(), e.Town.Id, townRatio, 250, e, null)) made++;
        }
        // b) zwischen zwei Straßen (Abzweigung → Abzweigung oder freier Ausgang)
        for (int attempt = 0; attempt < 25 && made < wanted; attempt++)
        {
            BuildTaps();
            if (taps.Count == 0) break;
            var t = taps[rnd.Next(taps.Count)];
            if (TryLoop(Enumerable.Range(-2, 5).Select(k => (t.Cell, (t.Dir + k + Dirs) % Dirs, JunctionPenalty + (Math.Abs(k) == 2 ? 60f : 0f))).ToList(), t.Road.Nodes[t.K], roadRatio, 400, null, t)) made++;
        }
    }

    /// Zweiter Durchlauf (Wunsch des Nutzers 8.11.2026): Jeder noch freie Ausgang einer angeschlossenen Stadt bekommt einen
    /// Zubringer mit Abzweigung, wenn eine geplante Straße in der Nähe vorbeiläuft (Weg höchstens FeederLength) und der Weg
    /// über das Netz dorthin mindestens 1,5-mal so lang wäre – sonst ist es die eigene Zufahrt der Stadt und bringt nichts.
    const double FeederLength = 1200;   // 700 m fand auf Rifane-Land nur 1 Zubringer (die meisten nahen Straßen 0,8–1,2 km Weg)

    void AddFeeders(List<NetTown> asphalt)
    {
        if (plan.Roads.Count == 0) return;
        // zuerst zwei freie Ausgänge benachbarter Städte miteinander (sonst stellt ein Zubringer des einen den anderen zu)
        foreach (var e in asphalt.Where(t => t.Connected).SelectMany(t => t.Exits).Where(Open).OrderBy(_ => rnd.Next()).ToList())
        {
            if (!Open(e)) continue;
            if (TryLoop(ExitTurns(DirOf(e.DX, e.DZ), 4).Select(t => (e.Cell, t.Dir, t.Extra)).ToList(), e.Town.Id, DeadEndRatio, 0, e, null,
                    maxLen: DeadEndLength, maxCost: 9000, net: ExitDistances(e), exitsOnly: true, maxEarthwork: DeadEndEarthwork))
                MarkFeeder();
        }
        foreach (var e in asphalt.Where(t => t.Connected).SelectMany(t => t.Exits).Where(Open).OrderBy(_ => rnd.Next()).ToList())
        {
            if (!Open(e)) continue;   // inzwischen von einem früheren Zubringer belegt oder verbaut
            if (TryLoop(ExitTurns(DirOf(e.DX, e.DZ), 1).Select(t => (e.Cell, t.Dir, t.Extra)).ToList(), e.Town.Id, 1.5, 0, e, null,
                    maxLen: FeederLength, tapsOnly: true, townGap: TownGap, maxCost: 5000))
                MarkFeeder();
        }
        // Sackgassen (Hinweis des Nutzers 8.11.2026: 9 offene Ausgänge auf Rifane-Land waren zu viel): Was danach noch offen ist,
        // bekommt einen großzügigeren Versuch – Bogen gleich am Ausgang (bis 90°, vor Felswänden), Abzweigung auch nahe an
        // Städten, Anschluss auch an einen freien Ausgang einer anderen Stadt, schon ab dem 1,15-fachen Umweg über das Netz
        foreach (var e in asphalt.Where(t => t.Connected).SelectMany(t => t.Exits).Where(Open).OrderBy(_ => rnd.Next()).ToList())
        {
            if (!Open(e)) continue;
            var start = ExitTurns(DirOf(e.DX, e.DZ), 4).Select(t => (e.Cell, t.Dir, t.Extra)).ToList();
            if (TryLoop(start, e.Town.Id, DeadEndRatio, 0, e, null, maxLen: DeadEndLength, townGap: DeadEndGap, maxCost: 9000, net: ExitDistances(e), maxEarthwork: DeadEndEarthwork)
                // weiter weg (freies Land vor dem Ausgang): bis 3 km, Umweg mindestens 1,3-fach
                || TryLoop(start, e.Town.Id, 1.3, 0, e, null, maxLen: 3000, townGap: DeadEndGap, maxCost: 16000, net: ExitDistances(e), maxEarthwork: DeadEndEarthwork)
                // zuletzt eine Gabelung: kurz hinter dem Ort in eine nahe Straße (auch die eigene Zufahrt), höchstens 700 m
                || TryLoop(start, e.Town.Id, 0, 0, e, null, maxLen: 700, tapsOnly: true, townGap: DeadEndGap, maxCost: 5000, net: ExitDistances(e), maxEarthwork: DeadEndEarthwork))
                MarkFeeder();
        }
        // Old-West-Orte: weitere Ausgänge mit einem Schotterweg an die nächste Straße
        foreach (var e in plan.Towns.Where(t => t.OldWest && t.Connected).SelectMany(t => t.Exits).Where(Open).ToList())
        {
            if (!Open(e)) continue;
            if (TryLoop(ExitTurns(DirOf(e.DX, e.DZ), 4).Select(t => (e.Cell, t.Dir, t.Extra)).ToList(), e.Town.Id, DeadEndRatio, 0, e, null,
                    maxLen: DeadEndLength, tapsOnly: true, townGap: DeadEndGap, maxCost: 9000, gravel: true, net: ExitDistances(e), maxEarthwork: DeadEndEarthwork))
                MarkFeeder();
        }

        void MarkFeeder()
        {
            plan.Roads[^1].Loop = false;
            plan.Roads[^1].Feeder = true;
        }
    }

    const double DeadEndRatio = 1.15, DeadEndLength = 1600, DeadEndGap = 60, DeadEndEarthwork = 6;

    // ---------------------------------------------------------------- Ortsanbindungen

    /// Ortsanbindungen (Wunsch des Nutzers 8.11.2026, Beispiel Rifane-Land 1124/−1084 → 1192/−1049): Läuft eine geplante Straße
    /// dicht an den Ortsstraßen einer Stadt vorbei (Straßen außerhalb der Kacheln: Ortsteile, Gateway), ohne dort angeschlossen zu
    /// sein, wird sie mit einem kurzen, geraden Stück (40–220 m) an die Ortsstraße angebunden – wenn der Weg über das Netz
    /// mindestens dreimal so lang und 400 m länger wäre. Höchstens zwei je Stadt.
    const double LinkLength = 220;

    void AddTownLinks(List<NetTown> asphalt)
    {
        if (plan.Roads.Count == 0) return;
        var scan = new RoadScan(w);
        var tiles = plan.Towns.SelectMany(t => t.Tiles).ToList();
        double TileGap(Point p) => tiles.Count == 0 ? double.MaxValue : tiles.Min(t =>
        {
            double dx = Math.Max(0, Math.Max(t.X - p.X, p.X - (t.X + t.SizeX))), dz = Math.Max(0, Math.Max(t.Z - p.Y, p.Y - (t.Z + t.SizeZ)));
            return Math.Sqrt(dx * dx + dz * dz);
        });
        // Stellen an vorhandenen Ortsstraßen (Asphalt, nicht in Kacheln), je Stadt höchstens alle 24 m eine
        var ports = new Dictionary<NetTown, List<RoadSpot>>();
        for (int g = 0; g < n; g++)
        {
            if (!roadCell[g] || tileCell[g]) continue;
            var c = Center(g);
            var town = asphalt.Where(t => t.Connected).OrderBy(t => TownDistanceTo(t, c)).FirstOrDefault();
            if (town == null || TownDistanceTo(town, c) > 300) continue;
            var spot = RoadFollower.Center(scan, c);
            if (spot == null || spot.Kind != WorldData.Asphalt || spot.Width < 6 || spot.Width > 24 || TileGap(spot.Center) < 30) continue;
            // gerade Strecke (keine Kurve, keine Einmündung): Richtung 15 m davor und dahinter fast gleich
            var a1 = RoadFollower.Axis(scan, spot.Center + spot.Dir * 15, 7);
            var a2 = RoadFollower.Axis(scan, spot.Center - spot.Dir * 15, 7);
            if (a1 == null || a2 == null || Math.Abs(Vector.Multiply(a1.Value, spot.Dir)) < 0.94 || Math.Abs(Vector.Multiply(a2.Value, spot.Dir)) < 0.94) continue;
            if (!ports.TryGetValue(town, out var list)) ports[town] = list = new();
            if (list.Any(s => (s.Center - spot.Center).Length < 24)) continue;
            list.Add(spot);
        }
        foreach (var (town, list) in ports.OrderBy(kv => kv.Key.Id))
        {
            for (int made = 0; made < 2; made++)
            {
                var net = GraphDistances(town.Id);
                (RoadSpot Spot, Vector Dir, NetRoad Road, int K, double Len, double Saving) best = default;
                foreach (var spot in list)
                {
                    double hs = w.HeightAt((int)Math.Floor(spot.Center.X), (int)Math.Floor(spot.Center.Y));
                    var sn = new Vector(-spot.Dir.Y, spot.Dir.X);
                    foreach (var r in plan.Roads)
                    {
                        if (r.Gravel) continue;
                        var s = r.Samples;
                        for (int k = 3; k + 3 < s.Count; k++)
                        {
                            var v = s[k] - spot.Center;
                            double len = v.Length;
                            if (len < 40 || len > LinkLength) continue;
                            double dn = net[r.Nodes[k]];
                            if (dn == double.MaxValue || dn < 3 * len || dn - len < 400) continue;
                            v /= len;
                            if (Math.Abs(Vector.Multiply(v, sn)) < 0.87) continue;               // ungefähr rechtwinklig von der Ortsstraße (±30°)
                            var rd = s[k + 3] - s[k - 3]; rd.Normalize();
                            if (Math.Abs(Vector.Multiply(v, rd)) > 0.57) continue;                // und auf die Straße (±35°)
                            Vector a = s[k] - s[k - 3], b = s[k + 3] - s[k];
                            if (Math.Abs(Vector.AngleBetween(a, b)) > 40) continue;              // dort keine enge Kurve
                            if (nodesUsed.Any(q => (q - s[k]).Length < 120) || TileGap(s[k]) < 40) continue;
                            double rise = Math.Abs(w.HeightAt((int)Math.Floor(s[k].X), (int)Math.Floor(s[k].Y)) - hs);
                            if (rise > len * spec.MaxGrade) continue;                             // kurz: bis zur Steigung „enge Passage“ (Ortsstraße oft auf einer Stufe)
                            double score = (dn - len) * (1 - 0.5 * rise / (len * spec.MaxGrade));   // flachere Anbindung bevorzugt
                            if (score <= best.Saving) continue;
                            if (!LinkFree(spot, s[k], r)) continue;
                            best = (spot, v, r, k, len, score);
                        }
                    }
                }
                if (best.Road == null) break;
                AddTownLink(town, best.Spot, best.Dir, best.Road, best.K);
                list.RemoveAll(s => (s.Center - best.Spot.Center).Length < NodeSpacing);
            }
        }
    }

    /// Strecke einer Ortsanbindung frei? Kein Wasser, keine Gebäude/Kacheln, keine andere Straße (vorhanden oder geplant)
    /// zwischen dem Rand der Ortsstraße und der geplanten Straße
    bool LinkFree(RoadSpot spot, Point to, NetRoad target)
    {
        var v = to - spot.Center;
        double len = v.Length;
        v /= len;
        double from = spot.Width / 2 + 2;
        double h0 = w.HeightAt((int)Math.Floor(spot.Center.X), (int)Math.Floor(spot.Center.Y)), h1 = w.HeightAt((int)Math.Floor(to.X), (int)Math.Floor(to.Y));
        for (double t = from; t <= len - 10; t += 2)
        {
            var q = spot.Center + v * t;
            int ix = (int)Math.Floor(q.X), iz = (int)Math.Floor(q.Y);
            if (w.WaterAt(ix, iz) || w.RoadAt(ix, iz) != 0) return false;
            // Gelände dazwischen ungefähr auf der geraden Verbindung (höchstens 6 m Einschnitt/Damm, keine Buckel dazwischen)
            if (Math.Abs(w.HeightAt(ix, iz) - (h0 + (h1 - h0) * t / len)) > 6) return false;
        }
        var nv = new Vector(-v.Y, v.X);
        foreach (var p in w.Prefabs)
        {
            if (p.Def == null) continue;
            double m = p.IsTile ? 10 : 4;
            for (double t = from; t <= len; t += 4)
                for (int side = -1; side <= 1; side++)
                {
                    var q = spot.Center + v * t + nv * (side * 9);
                    if (q.X > p.X - m && q.X < p.X + p.SizeX + m && q.Y > p.Z - m && q.Y < p.Z + p.SizeZ + m) return false;
                }
        }
        foreach (var r in plan.Roads)
        {
            if (r == target) continue;
            for (int k = 0; k + 1 < r.Points.Count; k++)
                if (SegDist(r.Points[k], spot.Center, to) < 30 || SegDist(r.Points[k + 1], spot.Center, to) < 30) return false;
        }
        return true;
    }

    void AddTownLink(NetTown town, RoadSpot spot, Vector dir, NetRoad road, int k)
    {
        var p = road.Samples[k];
        double len = (p - spot.Center).Length;
        var line = new List<Point>();
        int steps = Math.Max(2, (int)Math.Ceiling(len / 12));
        for (int q = 0; q <= steps; q++) line.Add(spot.Center + (p - spot.Center) * (q / (double)steps));
        var from = new NetExit
        {
            Town = town, X = spot.Center.X, Z = spot.Center.Y, DX = dir.X, DZ = dir.Y, Street = true,
            Width = (int)Math.Round(Math.Clamp(spot.Width - 2, 6, 16)), StreetHalf = spot.Width / 2, StreetDX = spot.Dir.X, StreetDZ = spot.Dir.Y, Used = true,
        };
        var tap = new Tap(road, k, p, -1, 0, DirOf(-dir.X, -dir.Y));
        var r = new NetRoad { Points = line, Width = 16, From = from, TownLink = true, Junction = p, EndRoad = road };
        plan.Junctions.Add(p);
        plan.Junctions.Add(spot.Center);
        nodesUsed.Add(p); nodesUsed.Add(spot.Center);
        r.Samples = Resample(line, 16);
        r.Length = r.Samples.Count > 1 ? (r.Samples.Count - 1) * 16.0 : 0;
        plan.Roads.Add(r);
        BlockAlong(line);
        AddToGraph(r, tap, new(), null);
    }

    bool TryLoop(List<(int Cell, int Dir, float Cost)> start, int startNode, double ratio, double minCost, NetExit startExit, Tap startTap,
        double maxLen = double.MaxValue, bool tapsOnly = false, double townGap = 300, float maxCost = 9000, bool gravel = false, double[] net = null, bool exitsOnly = false, double maxEarthwork = double.MaxValue)
    {
        BuildTaps(Math.Min(TownGap, townGap));
        if (startTap != null) taps.RemoveAll(t => (t.P - startTap.P).Length < NodeSpacing);
        net ??= GraphDistances(startNode);
        allowed = new HashSet<NetExit>(exitList);
        bool cut = DrawCut();
        Search(start, null, cut, maxCost, startTap == null ? null : Enumerable.Repeat(startTap.Road, start.Count).ToArray());
        double best = 0, maxRatio = 0; object target = null; int targetState = -1;
        void Consider(object o, int cell, int arrive, double extra, double dn)
        {
            if (cell < 0 || dn == double.MaxValue) return;
            var (cost, state) = Arrival(cell, arrive);
            if (cost == float.MaxValue) return;
            // Länge der neuen Straße (Weg zurückverfolgen) statt der Kosten: die enthalten Steigung, Gelände und Kurven
            double c = extra;
            for (int s = state; s >= 0 && parent[s] >= 0; s = parent[s]) c += (Center(s / Dirs) - Center(parent[s] / Dirs)).Length;
            if (c > maxLen || cost > 3 * c + 400 || Curly(state)) return;   // zu lang, sehr steiler/teurer Weg oder Kringel: lieber nicht
            if (o is Tap tt && CrossedRoads(state).Contains(tt.Road)) return;   // kreuzt die Straße, an der sie ankommen soll
            if (ParallelLength(state, c) > 250) return;   // läuft lange dicht neben einer anderen Straße her
            if (maxEarthwork < double.MaxValue && startExit?.Tile != null && Earthwork(startExit, state, o as NetExit) > maxEarthwork) return;
            if (c >= minCost) maxRatio = Math.Max(maxRatio, dn / c);
            if (c < minCost || dn < ratio * c) return;
            double score = dn / c;
            if (score > best) { best = score; target = o; targetState = state; }
        }
        Point origin = startExit != null ? new Point(startExit.X, startExit.Z) : startTap.P;
        foreach (var e in plan.Towns.Where(t => !tapsOnly && t.Connected && !t.OldWest && (startExit == null || t != startExit.Town)).SelectMany(t => t.Exits).Where(Open))
            Consider(e, e.Cell, DirOf(-e.DX, -e.DZ), ApproachLength, net[e.Town.Id]);
        foreach (var t in exitsOnly ? new List<Tap>() : taps)
            if ((t.P - origin).Length >= NodeSpacing && (startExit == null || TownDistanceTo(startExit.Town, t.P) >= townGap))
                Consider(t, t.Cell, t.Arrive, ApproachLength + JunctionPenalty, net[t.Road.Nodes[t.K]]);
        Log?.Invoke($"{(tapsOnly ? "feeder" : "loop")} from {origin.X:0}/{origin.Y:0}: taps in reach {taps.Count(t => Arrival(t.Cell, t.Arrive).Cost < float.MaxValue)}, reached {Enumerable.Range(0, n * Dirs).Count(c => dist[c] < float.MaxValue)} states, taps {taps.Count}, best {best:0.00}, max ratio {maxRatio:0.00}");
        if (target == null) return false;
        var cells = Trace(targetState);   // vom Start zum Ziel
        cells.Reverse();                 // AddRoad erwartet: Quelle (Ziel der Querverbindung) zuerst
        if (startExit != null)
        {
            AddRoad(startExit, cells, target, gravel, cut && !gravel, loop: true);
            return true;
        }
        // Start an einer Straße: als Pseudo-Ausgang an der Abzweigung
        var p = startTap.P;
        var dir = Center(startTap.Cell) - p; dir.Normalize();
        var pseudo = new NetExit { Town = null, X = p.X, Z = p.Y, DX = dir.X, DZ = dir.Y, Width = 16, Cell = startTap.Cell };
        AddRoad(pseudo, cells, target, false, cut, loop: true, startTap: startTap);
        return true;
    }


    /// Erdarbeit vor den Stadtausgängen eines Wegs (wie die Prüfung in Measure): wie weit das Gelände entlang der ersten
    /// 320 m über/unter der Kachel liegt, als selbst die höchste Steigung erlaubt (m). Für die Sackgassen-Versuche: ein
    /// Ausgang, der direkt auf eine Felswand zeigt, soll sich nicht hineinschneiden (Rifane-Land 3050/−2609: Stufen bis 8 m).
    double Earthwork(NetExit start, int state, NetExit end)
    {
        var cells = new List<int>();
        for (int s = state; s >= 0; s = parent[s]) cells.Add(s / Dirs);
        cells.Reverse();
        double worst = 0;
        foreach (var (e, list) in new[] { (start, cells), (end, Enumerable.Reverse(cells).ToList()) })
        {
            if (e?.Tile == null) continue;
            double floor = e.Tile.TerrainHeight, walked = ApproachLength;
            for (int q = 0; q < list.Count && walked <= 320; q++)
            {
                if (q > 0) walked += (Center(list[q]) - Center(list[q - 1])).Length;
                var p = Center(list[q]);
                worst = Math.Max(worst, Math.Abs(Sample(hb, p.X, p.Y) - floor) - walked * spec.MaxGrade);
            }
        }
        return worst;
    }
    /// Länge (m), auf der der Weg bis zu diesem Zustand näher als 120 m und auf ähnlicher Höhe (unter 10 m Unterschied) neben einer
    /// anderen Straße herläuft – ohne die ersten und letzten 150 m (dort schließt er an Ausgang bzw. Abzweigung an)
    double ParallelLength(int state, double total)
    {
        var cells = new List<int>();
        for (int s = state; s >= 0; s = parent[s]) cells.Add(s / Dirs);
        double walked = 0, close = 0;
        for (int q = 1; q < cells.Count; q++)
        {
            double step = (Center(cells[q]) - Center(cells[q - 1])).Length;
            walked += step;
            if (walked < 150 || total - walked < 150) continue;
            int c = cells[q];
            if (nearRoadD[c] < 120 && Math.Abs(hb[c] - nearRoadH[c]) < 10) close += step;
        }
        return close;
    }

    static double TownDistanceTo(NetTown t, Point p)
    {
        double dx = Math.Max(0, Math.Max(t.X0 - p.X, p.X - t.X1)), dz = Math.Max(0, Math.Max(t.Z0 - p.Y, p.Y - t.Z1));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    // ---------------------------------------------------------------- Auswertung

    void Measure(NetRoad r)
    {
        var s = r.Samples;
        var h = r.Cut ? hcut : hb;
        int steep = 0, count = 0;
        // Steigung über 96 m (der Straßenpinsel glättet das Längsprofil über 100 m), ohne die geraden Anschlussstücke an den Enden
        for (int k = 3; k + 9 < s.Count; k++)
        {
            double g = Math.Abs(Sample(h, s[k + 6].X, s[k + 6].Y) - Sample(h, s[k].X, s[k].Y)) / 96;
            if (g > r.MaxGrade) { r.MaxGrade = g; Log?.Invoke($"   max {g * 100:0.0} % at {s[k].X:0}/{s[k].Y:0} ({k * 16} of {(s.Count - 1) * 16} m)"); }
            if (g > spec.Grade + 0.005) steep++;
            count++;
        }
        r.SteepShare = count > 0 ? steep / (double)count : 0;
        // Anschluss an einen Stadtausgang: Gelände entlang der Straße (48–320 m) so weit über/unter der Kachel, dass selbst die
        // höchste Steigung nicht reicht (Hinweis; Stufe 2 muss dort aufschütten bzw. einschneiden)
        foreach (var (e, fromStart) in new[] { (r.From, true), (r.To, false) })
        {
            if (e?.Tile == null) continue;
            double floor = e.Tile.TerrainHeight, worst = 0, at = 0;
            for (int k = 3; k <= 20 && k < s.Count; k++)
            {
                var p = fromStart ? s[k] : s[s.Count - 1 - k];
                double need = Math.Abs(Sample(hb, p.X, p.Y) - floor) - k * 16 * spec.MaxGrade;
                if (need > worst) { worst = need; at = Sample(hb, p.X, p.Y) - floor; }
            }
            if (worst > 3)
                plan.Problems.Add(Loc.F("Town exit at {0}: the ground along the road is up to {1:0} m {2} than the tile – earthworks needed there.",
                    Pos(e.X, e.Z), Math.Abs(at), at > 0 ? Loc.T("higher") : Loc.T("lower")));
        }
        if (r.Cut) foreach (var p in s) r.MaxCut = Math.Max(r.MaxCut, Sample(hb, p.X, p.Y) - Sample(hcut, p.X, p.Y));
    }

    /// (gerade Länge, ganze Länge): Proben, deren Fenster über 288 m höchstens 2 m von der Sehne abweicht
    static (double Straight, double Total) Straightness(List<Point> s)
    {
        if (s.Count < 19) return (0, Math.Max(0, (s.Count - 1) * 16.0));
        var straight = new bool[s.Count];
        for (int k = 0; k + 18 < s.Count; k++)
        {
            Point a = s[k], b = s[k + 18];
            bool ok = true;
            for (int q = k + 1; q < k + 18 && ok; q++) ok = SegDist(s[q], a, b) <= 2;
            if (ok) for (int q = k; q <= k + 18; q++) straight[q] = true;
        }
        return (straight.Count(x => x) * 16.0, (s.Count - 1) * 16.0);
    }
}
