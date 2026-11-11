using System.Windows;

namespace MapEditor.Map;

/// Remove = vorhandenen Belag entfernen (Radierer) und das Straßenbett an die Umgebung angleichen
public enum Surface { Asphalt, Gravel, Remove }

/// Einstellungen einer Straße. Standardwerte = im Spiel getestete Werte (Markierung 3 bzw. Schotterweg-Anpassung).
public sealed class RoadSettings
{
    public Surface Surface;
    public double Width;        // Fahrbahn (m)
    public double Shoulder;     // Schotterrand je Seite (m), nur Asphalt
    public double GrassEdge;    // Rasenkante je Seite (m): zusätzlich waagerecht auf Straßenhöhe eingeebnet, ohne Belag (Auffahrten in Berge/in den Boden)
    public double MaxGrade;     // höchste Steigung (0,08 = 0,8 m auf 10 m)
    public double Smooth;       // Länge des Glättungsfensters entlang der Straße (m)
    public double Blend;        // Böschung neben der Straße, mindestens (m)
    public double CutSlope;     // > 0: Böschung = Höhenunterschied × CutSlope (höchstens MaxSlopeWidth)
    public bool Reshape = true; // nur Entfernen: altes Straßenbett aus dem Gelände ringsum auffüllen
    public bool KeepCrossings = true;   // nur Entfernen: Wege, die quer über die Straße laufen, bleiben erhalten
    public double JunctionTolerance = 0.02;   // Kreuzungen gelten bis zu dieser Steigung als eben (sonst wird angepasst)
    public double MaxCrossSlope;              // so weit darf die neue Straße an Kreuzungen quer geneigt sein (Anteil)
    public bool PaintSurface = true;   // false: nur Gelände anpassen, Belag nicht malen (Umbau einer bestehenden Straße an einer Kreuzung)
    public double HeightOffset; // Höhenversatz der ganzen Straße gegenüber dem Gelände (m, + = höher); Anschlüsse an Kacheln/Gebäude bleiben
    public double OffsetRamp;   // > 0: Versatz wächst von beiden Enden über so viele Meter an (statt bis zum zweiten/vorletzten Stützpunkt)
    public double[] OffsetAlong;  // zusätzlicher Versatz je 4 m entlang der Straße (Index = Strecke / 4, m; Wildnis-Wege: Einschnitt am Hang)
    public double FlatStart = -1;    // < 0 = automatisch (4 m an Kacheln/Gebäuden, sonst 0); so viele Meter am Anfang bleibt die Straße waagerecht auf der Höhe des Anfangs (Wunsch des Nutzers 4.11.2026)
    public double FlatEnd = -1;      // dasselbe am Ende
    public bool UniformGrade;   // gleichmäßige Steigung von Anfang bis Ende (zwischen festen Punkten), unabhängig vom Gelände
    /// Röhre (Wunsch des Nutzers 10.11.2026: Straßen lagen über lange Strecken auf Dämmen): >= 0 = das Längsprofil ist die straffste
    /// Linie innerhalb einer Röhre um das Gelände – höchstens TubeFill m darüber (Damm), höchstens TubeCut m darunter (Einschnitt),
    /// an den Fahrbahnrändern höchstens TubeEdgeFill m über dem tieferen Rand. < 0 = aus (gleitender Mittelwert über Smooth).
    public double TubeFill = -1, TubeCut = 1.8, TubeEdgeFill = 2.0;

    // Querneigung (Wunsch des Nutzers 28.10.2026): Asphalt vorsichtiger (17 m breit: 4 % = 0,7 m von Kante zu Kante)
    public static RoadSettings Asphalt() => new() { Surface = Surface.Asphalt, Width = 16, Shoulder = 1, MaxGrade = 0.08, Smooth = 100, Blend = 12, CutSlope = 1.5, MaxCrossSlope = 0.04 };
    public static RoadSettings Gravel() => new() { Surface = Surface.Gravel, Width = 5, Shoulder = 0, MaxGrade = 0.10, Smooth = 20, Blend = 8, CutSlope = 1.5, MaxCrossSlope = 0.06 };
    public static RoadSettings Remove() => new() { Surface = Surface.Remove, Width = 20, Shoulder = 0, MaxGrade = 0.10, Smooth = 30, Blend = 10, CutSlope = 0 };
    public static RoadSettings For(Surface s) => s switch { Surface.Asphalt => Asphalt(), Surface.Gravel => Gravel(), _ => Remove() };
    public RoadSettings Clone() => (RoadSettings)MemberwiseClone();
}

/// Punkt der Mittellinie: Lage, Abstand vom Start, Nummer des Abschnitts zwischen zwei Stützpunkten
public readonly record struct PathPoint(double X, double Z, double Dist, int Segment);

/// Stelle, an der die Mittellinie über eine vorhandene Straße läuft
public sealed record Crossing(double Dist, double X, double Z, byte Kind, double RoadHeight, double TerrainHeight, double Length, bool AtEnd)
{
    public double Diff => RoadHeight - TerrainHeight;
    /// Höhenunterschied zu groß: der gekreuzte Weg muss angepasst werden (Anschlüsse an den Enden ausgenommen)
    public bool NeedsWork => !AtEnd && Math.Abs(Diff) > 1;
    public string KindName => Kind == WorldData.Asphalt ? "Asphalt" : "Gravel";
}

/// Entfernen: Weg, der den Bereich kreuzt (Through: auf beiden Seiten vorhanden, A–B = seine Achse quer durch den Bereich)
/// oder nur auf einer Seite einmündet (A = Mündung am Rand, B = Punkt auf der Linie)
public sealed record CrossRoad(Point A, Point B, double HalfWidth, bool Through, double Dist, byte Kind);

/// Gebäude, das näher als die Böschung an der Straße liegt (Edge = Abstand zum Fahrbahnrand, negativ = auf der Straße)
public sealed record Obstacle(PlacedPrefab Prefab, double Edge, double Dist);

public sealed class RoadResult
{
    public RoadSettings Settings;
    public List<PathPoint> Path;
    public double FlatStartUsed, FlatEndUsed;   // tatsächlich waagerechte Meter am Anfang/Ende (Automatik oder Eingabe)
    public double[] Profile;      // Höhe der Straße je Wegpunkt (m)
    public double[] Terrain;      // Gelände unter der Mittellinie (m)
    public double[] SlopeWidth;   // Böschungsbreite je Wegpunkt (m)
    /// Fahrbahnbreite je Wegpunkt (m) bei veränderlicher Breite (Verjüngen); null = überall Settings.Width, sonst ist Settings.Width die größte Breite
    public double[] Widths;
    public double WidthAt(int k) => Widths?[k] ?? Settings.Width;
    public int[] ControlIndex;    // Wegpunkt je Stützpunkt (für Höhe und Anzeige der einzelnen Punkte)
    /// Querneigung je Wegpunkt: Höhenänderung je Meter seitlich in Richtung Normal() (0 = quer waagerecht)
    public double[] CrossSlope;
    /// Kreuzungen (Mitte, Halbmesser): Pixel bestehender Straßen darin behalten ihre Höhe – die neue Straße schreibt dort nichts
    public List<(Point Center, double Radius)> ProtectZones = new();
    public double Length, MaxGrade, MaxCut, CutAt, MaxFill, FillAt;
    public double KinkLength;   // m mit Knick im Längsprofil (> 4 % Steigungswechsel auf 16 m)
    public double FillLength, LongestFill;  // m mit mehr als 1,5 m Damm (gesamt / längste Strecke)
    /// mindestens nötige Steigung zwischen festen Punkten/Enden; größer als Settings.MaxGrade = Strecke zu steil (Warnung)
    public double NeededGrade;
    public int WaterArea;         // m² Wasser im Fahrbahnbereich
    /// Anschluss an eine Stadt-Kachel am Anfang/Ende: die Straße beginnt/endet auf der Höhe der Kachel (sonst null)
    public PlacedPrefab StartTile, EndTile;
    /// Anfang/Ende an der Ein-/Ausfahrt eines Gebäudes: die Straße beginnt/endet auf dessen Bodenhöhe (sonst null)
    public PlacedPrefab StartPoi, EndPoi;
    /// Stadt-Kacheln im Bereich der Straße: bleiben unverändert (ihre Straßen und ihr Boden gehören zur Kachel)
    public List<PlacedPrefab> Tiles = new();
    /// Entfernen: Geländehöhe an den beiden Rändern des Bereichs (links/rechts in Zeichenrichtung), geglättet
    public double[] SideLeft, SideRight;
    /// Straße bearbeiten: Karte zeichnet blau, außerhalb von CoreFrom..CoreTo (Auslauf) violett; HeldPoints = festgehaltene Kreuzungen
    public bool EditLook;
    public int CoreFrom = -1, CoreTo = -1;
    public List<Point> HeldPoints = new();
    public int RemoveAsphalt, RemoveGravel;   // m² Belag im Bereich (ohne Kacheln)
    public List<CrossRoad> CrossRoads = new();
    /// Entfernen: Abstand des Punkts zur Achse des nächsten erhaltenen Wegs minus dessen halbe Breite (≤ 0 = auf dem Weg)
    public double KeptRoadDistance(double x, double z) => KeptRoadDistance(x, z, out _);

    /// wie oben, kind = Belag des nächsten erhaltenen Wegs
    public double KeptRoadDistance(double x, double z, out byte kind)
    {
        double best = double.MaxValue;
        kind = 0;
        if (!Settings.KeepCrossings) return best;
        foreach (var c in CrossRoads)
        {
            if (!c.Through) continue;
            double dx = c.B.X - c.A.X, dz = c.B.Y - c.A.Y, l2 = dx * dx + dz * dz;
            double t = l2 == 0 ? 0 : Math.Clamp(((x - c.A.X) * dx + (z - c.A.Y) * dz) / l2, 0, 1);
            double ex = c.A.X + dx * t - x, ez = c.A.Y + dz * t - z;
            double d = Math.Sqrt(ex * ex + ez * ez) - c.HalfWidth;
            if (d < best) { best = d; kind = c.Kind; }
        }
        return best;
    }
    public bool IsRemoval => Settings.Surface == Surface.Remove;
    public bool InTile(double x, double z) => Tiles.Any(t => t.Contains(x, z));
    public List<Crossing> Crossings = new();
    public List<Obstacle> Obstacles = new();

    /// Wegpunkt zum Abstand vom Start
    public int IndexAt(double dist)
    {
        int lo = 0, hi = Path.Count - 1;
        while (lo < hi) { int m = (lo + hi) / 2; if (Path[m].Dist < dist) lo = m + 1; else hi = m; }
        return lo;
    }
}

/// Berechnet Verlauf, Längsprofil, Böschungen und Prüfbericht einer Straße (wie tools/road.ps1).
public static class RoadPlanner
{
    public const double MaxSlopeWidth = 40;
    public const double CrossFade = 25;   // so lang läuft die Querneigung nach einer Kreuzung aus (m)
    const double SamplesPerMeter = 4;

    /// heights: von Hand gesetzte Höhe je Stützpunkt (null = automatisch); gilt vor Kachel- und Gebäudeanschluss
    /// fixedAt: feste Höhen an Stellen der Straße (Abstand vom Anfang, Höhe), z. B. die ebene Strecke an einer Kreuzung
    /// crossAt: Querneigung an Stellen der Straße (Abstand, Neigung, halbe Länge mit voller Neigung); läuft danach über
    /// CrossFade Meter auf 0 aus
    public static RoadResult Compute(WorldData w, IReadOnlyList<Point> points, RoadSettings s, IReadOnlyList<double?> heights = null,
        IReadOnlyList<(double Dist, double Height)> fixedAt = null, IReadOnlyList<(double Dist, double Slope, double Half)> crossAt = null,
        IReadOnlyList<double?> widths = null)
    {
        var path = BuildPath(points);
        if (path.Count < 2) return null;
        var r = new RoadResult { Settings = s.Clone(), Path = path };
        int n = path.Count;
        r.Length = path[n - 1].Dist;
        r.Terrain = new double[n];
        for (int i = 0; i < n; i++) r.Terrain[i] = w.HeightAt((int)Math.Floor(path[i].X), (int)Math.Floor(path[i].Z));
        // Stützpunkt k liegt am Ende von Abschnitt k-1 (Punkt 0 am Anfang)
        r.ControlIndex = new int[points.Count];
        for (int i = 0; i < n; i++)
            if (path[i].Segment + 1 < points.Count) r.ControlIndex[path[i].Segment + 1] = i;
        for (int k = 1; k < points.Count; k++) r.ControlIndex[k] = Math.Max(r.ControlIndex[k], r.ControlIndex[k - 1]);
        // Verjüngen (Wunsch des Nutzers 3.11.2026): Breite je Stützpunkt (null = Standardbreite), dazwischen weicher Übergang
        // (Smoothstep) über den ganzen Abschnitt zwischen zwei Stützpunkten; davor und danach bleibt die Breite des ersten/letzten
        if (!r.IsRemoval && widths != null && widths.Any(v => v != null))
        {
            var ctl = new List<(double Dist, double W)>();
            for (int k = 0; k < points.Count; k++) ctl.Add((path[r.ControlIndex[k]].Dist, (k < widths.Count ? widths[k] : null) ?? s.Width));
            r.Widths = new double[n];
            int seg = 0;
            for (int i = 0; i < n; i++)
            {
                double d = path[i].Dist;
                while (seg < ctl.Count - 2 && d > ctl[seg + 1].Dist) seg++;
                var (d0, w0) = ctl[seg];
                var (d1, w1) = ctl[Math.Min(seg + 1, ctl.Count - 1)];
                double t = d1 > d0 ? Math.Clamp((d - d0) / (d1 - d0), 0, 1) : 1;
                r.Widths[i] = w0 + (w1 - w0) * (t * t * (3 - 2 * t));
            }
            r.Settings.Width = r.Widths.Max();
        }
        double reach = r.Settings.Width / 2 + s.Shoulder + s.GrassEdge + MaxSlopeWidth + 2;
        double x0 = path.Min(p => p.X) - reach, x1 = path.Max(p => p.X) + reach, z0 = path.Min(p => p.Z) - reach, z1 = path.Max(p => p.Z) + reach;
        r.Tiles = w.Prefabs.Where(pf => pf.IsTile && pf.X <= x1 && pf.X + pf.SizeX >= x0 && pf.Z <= z1 && pf.Z + pf.SizeZ >= z0).ToList();
        if (r.IsRemoval)
        {
            Removal(w, r);
            Stats(r);
            Obstacles(w, r);
            return r;
        }
        // Höhenversatz (Wunsch des Nutzers): die Straße folgt dem um den Versatz verschobenen Gelände – aber nicht an Anfang
        // und Ende (dort schließt sie an Gelände, Straße, Kachel oder Gebäude an): voller Versatz erst vom zweiten bis zum
        // vorletzten Stützpunkt, dazwischen linear; bei nur zwei Punkten im mittleren Drittel
        var raw = new double[n];
        int c1 = points.Count >= 3 ? r.ControlIndex[1] : -1, c2 = points.Count >= 3 ? r.ControlIndex[points.Count - 2] : -1;
        double len = path[n - 1].Dist;
        for (int i = 0; i < n; i++)
        {
            double d = path[i].Dist, wgt;
            if (s.OffsetRamp > 0)
                wgt = Math.Min(d, len - d) / s.OffsetRamp;
            else if (c1 > 0 && c2 < n - 1)
                wgt = d < path[c1].Dist ? d / path[c1].Dist : d > path[c2].Dist ? (len - d) / (len - path[c2].Dist) : 1;
            else wgt = Math.Min(1, Math.Min(d, len - d) / (len / 3));
            double off = s.HeightOffset;
            if (s.OffsetAlong is { Length: > 0 } oa)
            {
                double f = Math.Clamp(d / 4, 0, oa.Length - 1);
                int j = Math.Min((int)f, oa.Length - 2 < 0 ? 0 : oa.Length - 2);
                off += oa.Length == 1 ? oa[0] : oa[j] + (oa[j + 1] - oa[j]) * Math.Clamp(f - j, 0, 1);
            }
            raw[i] = r.Terrain[i] + off * Math.Clamp(wgt, 0, 1);
        }
        // Enden an einer Stadt-Kachel: Höhe der Kachel übernehmen, damit am Kachelrand keine Stufe bleibt
        double h0 = raw[0], h1 = raw[n - 1];
        r.StartTile = TileAt(r.Tiles, path[0].X, path[0].Z);
        r.EndTile = TileAt(r.Tiles, path[n - 1].X, path[n - 1].Z);
        if (r.StartTile != null) h0 = w.TileHeight(r.StartTile, path[0].X, path[0].Z);
        if (r.EndTile != null) h1 = w.TileHeight(r.EndTile, path[n - 1].X, path[n - 1].Z);
        // Stück der Straße innerhalb der Anfangs-/End-Kachel: genau auf Kachelhöhe (dort schreibt das Spiel die Kachel hin)
        var fix = new double?[n];
        for (int i = 0; r.StartTile != null && i < n && r.StartTile.Contains(path[i].X, path[i].Z); i++) fix[i] = h0;
        for (int i = n - 1; r.EndTile != null && i >= 0 && r.EndTile.Contains(path[i].X, path[i].Z); i--) fix[i] = h1;
        // Ende an der Ein-/Ausfahrt eines Gebäudes: Bodenhöhe des Gebäudes im Spiel (nicht das Gelände davor; bei
        // survivor_site_06 lag es 2,3 m tiefer, die Straße begann zu niedrig – Hinweis des Nutzers 28.10.2026)
        if (r.StartTile == null && (r.StartPoi = PoiAtExit(w, path[0])) != null) fix[0] = h0 = r.StartPoi.TerrainHeight;
        if (r.EndTile == null && (r.EndPoi = PoiAtExit(w, path[n - 1])) != null) fix[n - 1] = h1 = r.EndPoi.TerrainHeight;
        // von Hand gesetzte Höhen einzelner Punkte (Wunsch des Nutzers) sind fest
        for (int k = 0; heights != null && k < heights.Count && k < points.Count; k++)
        {
            if (heights[k] is not double hk) continue;
            int i = r.ControlIndex[k];
            fix[i] = hk;
            if (i == 0) h0 = hk;
            if (i == n - 1) h1 = hk;
        }
        foreach (var (fd, fh) in fixedAt ?? Array.Empty<(double, double)>())
        {
            int i = r.IndexAt(Math.Clamp(fd, 0, r.Length));
            if (i > 0 && i < n - 1) fix[i] ??= fh;   // Enden und schon feste Punkte behalten ihren Wert
        }
        // waagerechtes Stück am Anfang/Ende (z. B. 2 m hinter einer Kachel): Punkte auf der Höhe des Anfangs bzw. Endes fest
        // Automatik (Wert < 0): an Stadt-Kacheln und Gebäude-Einfahrten 4 m waagerecht – sonst liegt die Straße schon 2 m vor der
        // Kachelkante einen Block tiefer (Stufe an der Betonkante, Wilibii County 4.11.2026), weil sie dort mit voller Steigung ankommt
        double flatA = s.FlatStart >= 0 ? s.FlatStart : (r.StartTile != null || r.StartPoi != null ? 4 : 0);
        double flatB = s.FlatEnd >= 0 ? s.FlatEnd : (r.EndTile != null || r.EndPoi != null ? 4 : 0);
        r.FlatStartUsed = flatA; r.FlatEndUsed = flatB;
        if (flatA + flatB < r.Length)
        {
            for (int i = 0; flatA > 0 && i < n && path[i].Dist <= flatA; i++) fix[i] ??= h0;
            for (int i = n - 1; flatB > 0 && i >= 0 && r.Length - path[i].Dist <= flatB; i--) fix[i] ??= h1;
        }
        // Gleichmäßige Steigung (Wunsch des Nutzers 28.10.2026: nach vielem Löschen und Neuzeichnen war der Untergrund
        // unruhig): statt des Geländes eine Gerade von Anfang bis Ende entlang der Strecke; feste Punkte (Kacheln, Gebäude,
        // Kreuzungen, von Hand) teilen sie in Abschnitte mit je gleichmäßiger Steigung (Verteilung in Profile); Versatz bleibt
        if (s.UniformGrade)
            for (int i = 0; i < n; i++)
                raw[i] = h0 + (h1 - h0) * path[i].Dist / r.Length + (raw[i] - r.Terrain[i]);
        // die Gerade nicht glätten (Glättung würde sie an den Enden verbiegen)
        double[] tubeLo = null, tubeHi = null;
        if (s.TubeFill >= 0 && !s.UniformGrade)
        {
            tubeLo = new double[n]; tubeHi = new double[n];
            for (int i = 0; i < n; i++)
            {
                var (nx, nz) = Normal(path, i);
                double edge = r.WidthAt(i) / 2 + s.Shoulder;
                double a = w.HeightAt((int)Math.Floor(path[i].X + nx * edge), (int)Math.Floor(path[i].Z + nz * edge));
                double b = w.HeightAt((int)Math.Floor(path[i].X - nx * edge), (int)Math.Floor(path[i].Z - nz * edge));
                double off = raw[i] - r.Terrain[i];
                tubeLo[i] = raw[i] - s.TubeCut;
                // Damm gegenüber dem Gelände, aber auch gegenüber dem tieferen Fahrbahnrand begrenzt (Hang: Straße nicht auf der Talseite in der Luft)
                tubeHi[i] = Math.Min(raw[i] + s.TubeFill, Math.Max(Math.Min(r.Terrain[i], Math.Min(a, b)) + off + s.TubeEdgeFill, tubeLo[i] + 0.3));
            }
        }
        r.Profile = Profile(path, raw, h0, h1, s.UniformGrade ? 0 : s.Smooth, s.MaxGrade, fix, out r.NeededGrade, tubeLo, tubeHi);
        r.CrossSlope = new double[n];
        foreach (var (cd, cs, ch) in crossAt ?? Array.Empty<(double, double, double)>())
            for (int i = 0; i < n; i++)
            {
                double off = Math.Abs(path[i].Dist - cd), wgt = off <= ch ? 1 : Math.Max(0, 1 - (off - ch) / CrossFade);
                wgt = wgt * wgt * (3 - 2 * wgt);
                if (wgt * Math.Abs(cs) > Math.Abs(r.CrossSlope[i])) r.CrossSlope[i] = cs * wgt;   // die stärkere Kreuzung gilt
            }
        r.SlopeWidth = Slopes(w, path, r.Profile, s, r);
        Stats(r);
        Crossings(w, r);
        Water(w, r);
        Obstacles(w, r);
        return r;
    }

    // Catmull-Rom-Kurve durch die Stützpunkte, Abtastung alle 0,25 m
    public static List<PathPoint> BuildPath(IReadOnlyList<Point> p)
    {
        var path = new List<PathPoint>();
        double dist = 0, px = 0, pz = 0;
        for (int i = 0; i < p.Count - 1; i++)
        {
            Point p0 = p[Math.Max(0, i - 1)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(p.Count - 1, i + 2)];
            int steps = (int)Math.Ceiling((p2 - p1).Length * SamplesPerMeter);
            if (steps == 0) continue;
            for (int k = 0; k <= steps; k++)
            {
                if (path.Count > 0 && k == 0) continue;
                double t = (double)k / steps, t2 = t * t, t3 = t2 * t;
                double x = 0.5 * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
                double z = 0.5 * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
                if (path.Count > 0) dist += Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                path.Add(new PathPoint(x, z, dist, i));
                px = x; pz = z;
            }
        }
        return path;
    }

    // Längsprofil: Gelände unter der Mittellinie, gleitend gemittelt, Enden fest (h0/h1: vorhandene Höhe bzw. Höhe der Kachel),
    // Steigung begrenzt. fix: Punkte mit fester Höhe (in einer Kachel). Ist die Steigung zu steil für beide Enden, gibt das Ende
    // ohne Kachel nach (Durchgang zuletzt von der Kachel aus), damit am Kachelrand keine Stufe entsteht (Bodenschwelle im Spiel)
    static double[] Profile(List<PathPoint> path, double[] raw, double h0, double h1, double window, double maxGrade, double?[] fix)
        => Profile(path, raw, h0, h1, window, maxGrade, fix, out _);

    /// needed: höchste Steigung, die zwischen festen Punkten (und den Enden) mindestens nötig ist. Ist sie größer als
    /// maxGrade, wird die Steigung dafür angehoben (höchstens 2,5-fach) und gleichmäßig verteilt, statt ein Ende metertief in
    /// den Hang zu graben (28.10.2026: Schotterweg mit 22 % Gefälle wurde am Anfang 9 m abgesenkt → Sprung zum alten Weg)
    static double[] Profile(List<PathPoint> path, double[] raw, double h0, double h1, double window, double maxGrade, double?[] fix, out double needed,
        double[] tubeLo = null, double[] tubeHi = null)
    {
        int n = path.Count;
        needed = 0;
        int lastI = 0;
        double lastH = fix[0] ?? h0;
        for (int i = 1; i < n; i++)
        {
            double? hi = i == n - 1 ? fix[i] ?? h1 : fix[i];
            if (hi is not double hv) continue;
            double span = path[i].Dist - path[lastI].Dist;
            if (span > 2) needed = Math.Max(needed, Math.Abs(hv - lastH) / span);
            lastI = i; lastH = hv;
        }
        if (needed > maxGrade) maxGrade = Math.Min(needed * 1.05, maxGrade * 2.5);
        var prof = tubeLo != null ? TubeProfile(path, tubeLo, tubeHi, fix, h0, h1, window) : MovingAverage(path, raw, window);
        // Korrektur zwischen den festen Punkten (Enden, Kachelstücke, von Hand gesetzte Höhen) linear verteilen,
        // damit die Straße sanft zu einem gesetzten Punkt ansteigt statt erst kurz davor mit der Höchststeigung
        var anchors = new List<(int i, double d)> { (0, h0 - prof[0]) };
        for (int i = 1; i < n - 1; i++) if (fix[i] is double f) anchors.Add((i, f - prof[i]));
        anchors.Add((n - 1, h1 - prof[n - 1]));
        for (int k = 0; k + 1 < anchors.Count; k++)
        {
            var (ia, da) = anchors[k];
            var (ib, db) = anchors[k + 1];
            double span = path[ib].Dist - path[ia].Dist;
            for (int i = ia; i < ib || (i == ib && k + 2 == anchors.Count); i++)   // jeden Punkt nur einmal
            {
                double t = span > 0 ? (path[i].Dist - path[ia].Dist) / span : 0;
                prof[i] += da * (1 - t) + db * t;
            }
        }
        // Enden ohne festen Wert sind „weich“: die Korrektur oben legt sie auf h0/h1, aber wenn die Steigung zu einem festen
        // Punkt (Kachel, Gebäude, von Hand gesetzte Höhe) es verlangt, geben sie nach – sonst entstünde vor dem festen Punkt
        // eine Steilwand (Test 28.10.2026: Punkt 22 m über dem Gelände ergab 137 m auf 10 m)
        void Forward()
        {
            if (fix[0] is double f0) prof[0] = f0;
            for (int i = 1; i < n; i++) { double lim = maxGrade * (path[i].Dist - path[i - 1].Dist); prof[i] = fix[i] ?? Math.Clamp(prof[i], prof[i - 1] - lim, prof[i - 1] + lim); }
        }
        void Backward()
        {
            if (fix[n - 1] is double f1) prof[n - 1] = f1;
            for (int i = n - 2; i >= 0; i--) { double lim = maxGrade * (path[i + 1].Dist - path[i].Dist); prof[i] = fix[i] ?? Math.Clamp(prof[i], prof[i + 1] - lim, prof[i + 1] + lim); }
        }
        bool startWins = fix[0] != null && fix[n - 1] == null;
        for (int pass = 0; pass < 6; pass++)
            if (startWins) { Backward(); Forward(); }
            else { Forward(); Backward(); }
        return prof;
    }

    /// Straffste Linie (Taut String) innerhalb der Röhre lo..hi: gerade, wo das Gelände es zulässt, sonst am Rand der Röhre
    /// entlang; keine Dämme über Mulden, kein Überschwingen. Feste Punkte und Enden haben lo = hi. Danach leichte Rundung der
    /// Knicke (Fenster 30 % von window, höchstens 30 m) und Zurücksetzen in die Röhre.
    static double[] TubeProfile(List<PathPoint> path, double[] lo, double[] hi, double?[] fix, double h0, double h1, double window)
    {
        int n = path.Count;
        var L = (double[])lo.Clone(); var H = (double[])hi.Clone();
        for (int i = 0; i < n; i++) if (fix[i] is double f) L[i] = H[i] = f;
        L[0] = H[0] = fix[0] ?? h0;
        L[n - 1] = H[n - 1] = fix[n - 1] ?? h1;
        // Stützstellen: jeder 4. Wegpunkt (1 m) und alle festen Punkte
        var ids = new List<int>();
        for (int i = 0; i < n; i++) if (i % 4 == 0 || i == n - 1 || L[i] == H[i]) ids.Add(i);
        int m = ids.Count;
        var X = new double[m]; var lo2 = new double[m]; var hi2 = new double[m];
        for (int k = 0; k < m; k++) { X[k] = path[ids[k]].Dist; lo2[k] = L[ids[k]]; hi2[k] = H[ids[k]]; }
        var y = new double[m];
        int a = 0; double ay = lo2[0]; y[0] = ay;
        while (a < m - 1)
        {
            double sLo = double.NegativeInfinity, sHi = double.PositiveInfinity;
            int iLo = a, iHi = a, next = -1; double ny = 0;
            for (int j = a + 1; j < m; j++)
            {
                double dx = X[j] - X[a];
                if (dx < 1e-9) continue;
                double sl = (lo2[j] - ay) / dx, sh = (hi2[j] - ay) / dx;
                if (sl > sLo) { sLo = sl; iLo = j; }
                if (sh < sHi) { sHi = sh; iHi = j; }
                if (sLo > sHi + 1e-12)
                {
                    if (iLo == j) { next = iHi; ny = hi2[iHi]; }   // Linie müsste über den oberen Punkt hinaus steigen: Knick am oberen Punkt
                    else { next = iLo; ny = lo2[iLo]; }            // Linie müsste unter den unteren Punkt fallen: Knick am unteren Punkt
                    break;
                }
            }
            if (next < 0) { next = m - 1; ny = lo2[m - 1]; }
            if (next <= a) { next = a + 1; ny = Math.Clamp(ay, lo2[next], hi2[next]); }   // Schutz gegen Stillstand bei doppelten Abständen
            for (int k = a + 1; k <= next; k++) y[k] = ay + (ny - ay) * (X[k] - X[a]) / Math.Max(1e-9, X[next] - X[a]);
            a = next; ay = ny;
        }
        var prof = new double[n];
        for (int k = 0; k + 1 < m; k++)
            for (int i = ids[k]; i <= ids[k + 1]; i++)
            {
                double t = ids[k + 1] > ids[k] ? (double)(i - ids[k]) / (ids[k + 1] - ids[k]) : 0;
                prof[i] = y[k] + (y[k + 1] - y[k]) * t;
            }
        // Knicke des Taut Strings (Hinweis des Nutzers 11.11.2026: leichte Stufen im Spiel) ausrunden: mehrmals glätten und
        // jedes Mal zurück in die Röhre – ergibt weiche Übergänge, die höchstens an den Rändern der Röhre anliegen
        double round = Math.Min(60, window * 0.6);
        if (round > 2)
        {
            for (int pass = 0; pass < 8; pass++)
            {
                for (int i = 0; i < n; i++) prof[i] = Math.Clamp(prof[i], L[i], H[i]);
                prof = MovingAverage(path, prof, round);   // zuletzt glätten, nicht zurückklemmen: kein neuer Knick am Röhrenrand
            }
            for (int i = 0; i < n; i++) if (L[i] == H[i]) prof[i] = L[i];   // feste Punkte und Enden bleiben genau
        }
        return prof;
    }

    // Böschungsbreite je Wegpunkt: bei tiefem Einschnitt/hohem Damm breiter (Höhenunterschied in der Mitte und an beiden Rändern
    // × CutSlope, mindestens Blend, höchstens MaxSlopeWidth), dann gleitendes Maximum über ±25 m, damit der Rand ruhig verläuft
    static double[] Slopes(WorldData w, List<PathPoint> path, double[] prof, RoadSettings s, RoadResult r)
    {
        int n = path.Count;
        var raw = new double[n];
        // Breite je Wegpunkt (Verjüngen)
        for (int k = 0; k < n; k++)
        {
            var (nx, nz) = Normal(path, k);
            double edge = r.WidthAt(k) / 2 + s.Shoulder + s.GrassEdge;
            double dh = 0;
            for (int side = -1; side <= 1; side++)
            {
                double x = path[k].X + nx * edge * side, z = path[k].Z + nz * edge * side;
                dh = Math.Max(dh, Math.Abs(prof[k] - w.HeightAt((int)Math.Floor(x), (int)Math.Floor(z))));
            }
            raw[k] = s.CutSlope > 0 ? Math.Max(s.Blend, Math.Min(MaxSlopeWidth, dh * s.CutSlope)) : s.Blend;
        }
        var res = new double[n];
        // gleitendes Maximum mit Fenster ±25 m (monotone Warteschlange)
        var q = new LinkedList<int>();
        int right = 0;
        for (int k = 0; k < n; k++)
        {
            while (right < n && path[right].Dist <= path[k].Dist + 25)
            {
                while (q.Count > 0 && raw[q.Last.Value] <= raw[right]) q.RemoveLast();
                q.AddLast(right++);
            }
            while (path[q.First.Value].Dist < path[k].Dist - 25) q.RemoveFirst();
            res[k] = raw[q.First.Value];
        }
        return res;
    }

    /// Stadt-Kachel, in der der Punkt liegt oder deren Rand höchstens 3 m entfernt ist
    /// Gebäude (keine Kachel), dessen Ein-/Ausfahrt höchstens 2 m vom Punkt entfernt ist (eingerasteter Anfang/Ende)
    static PlacedPrefab PoiAtExit(WorldData w, PathPoint p)
    {
        // auch die Außenkanten der Zufahrten (gehören zum Gebäude)
        bool Near(PlacedPrefab pf) => !pf.IsTile && pf.Def != null
            && p.X >= pf.X - 30 && p.X <= pf.X + pf.SizeX + 30 && p.Z >= pf.Z - 30 && p.Z <= pf.Z + pf.SizeZ + 30;
        foreach (var (ex, ez, pf) in PrefabParts.AllExits(w, Near))
            if ((ex - p.X) * (ex - p.X) + (ez - p.Z) * (ez - p.Z) <= 4) return pf;
        return null;
    }

    static PlacedPrefab TileAt(List<PlacedPrefab> tiles, double x, double z)
    {
        foreach (var pf in tiles)
        {
            double dx = Math.Max(Math.Max(pf.X - x, 0), x - (pf.X + pf.SizeX));
            double dz = Math.Max(Math.Max(pf.Z - z, 0), z - (pf.Z + pf.SizeZ));
            if (dx * dx + dz * dz <= 9) return pf;
        }
        return null;
    }

    // Entfernen: Gelände an beiden Rändern des Bereichs (Breite/2 + Böschung) abtasten und entlang der Linie glätten;
    // die neue Höhe in der Mitte ist der Mittelwert beider Ränder (ohne „Gelände einebnen“ bleibt das Gelände unverändert)
    static void Removal(WorldData w, RoadResult r)
    {
        var s = r.Settings;
        var path = r.Path;
        int n = path.Count;
        double reach = s.Width / 2 + s.Blend;
        var left = new double[n];
        var right = new double[n];
        for (int k = 0; k < n; k++)
        {
            var (nx, nz) = Normal(path, k);
            left[k] = w.HeightAt((int)Math.Floor(path[k].X + nx * reach), (int)Math.Floor(path[k].Z + nz * reach));
            right[k] = w.HeightAt((int)Math.Floor(path[k].X - nx * reach), (int)Math.Floor(path[k].Z - nz * reach));
        }
        r.SideLeft = MovingAverage(path, left, s.Smooth);
        r.SideRight = MovingAverage(path, right, s.Smooth);
        r.Profile = new double[n];
        for (int k = 0; k < n; k++) r.Profile[k] = s.Reshape ? (r.SideLeft[k] + r.SideRight[k]) / 2 : r.Terrain[k];
        r.SlopeWidth = Enumerable.Repeat(s.Blend, n).ToArray();

        // vorhandener Belag im Bereich, je Meter einmal gezählt (Kacheln ausgenommen)
        double half = s.Width / 2;
        var seen = new HashSet<long>();
        for (int k = 0; k < n; k++)
        {
            var (nx, nz) = Normal(path, k);
            for (double o = -half; o <= half; o += 0.5)
            {
                int x = (int)Math.Floor(path[k].X + nx * o), z = (int)Math.Floor(path[k].Z + nz * o);
                if (!w.Contains(x, z) || !seen.Add(((long)x << 32) ^ (uint)z) || r.InTile(x + 0.5, z + 0.5)) continue;
                byte kind = w.RoadAt(x, z);
                if (kind == WorldData.Asphalt) r.RemoveAsphalt++;
                else if (kind == WorldData.Gravel) r.RemoveGravel++;
            }
        }
        CrossRoads(w, r);
    }

    // Wege, die den Bereich kreuzen: knapp außerhalb (2 m neben dem Rand) auf beiden Seiten nach Belag suchen.
    // Zusammenhängende Stücke = ein Weg; links und rechts auf gleicher Höhe der Linie = durchgehende Kreuzung,
    // nur einseitig = Einmündung. Die ersten/letzten 12 m bzw. eine Straßenbreite (Fortsetzung der Straße selbst) und sehr lange Stücke
    // (Straße läuft daneben her) zählen nicht.
    static void CrossRoads(WorldData w, RoadResult r)
    {
        var path = r.Path;
        int n = path.Count;
        double probe = r.Settings.Width / 2 + 2, endGap = Math.Max(12, r.Settings.Width);
        var runs = new List<(int side, int a, int b)>();
        foreach (int side in new[] { 1, -1 })
        {
            int start = -1;
            for (int k = 0; k <= n; k += 4)
            {
                bool on = false;
                if (k < n && path[k].Dist > endGap && r.Length - path[k].Dist > endGap)
                {
                    var (nx, nz) = Normal(path, k);
                    double x = path[k].X + nx * probe * side, z = path[k].Z + nz * probe * side;
                    on = w.RoadAt((int)Math.Floor(x), (int)Math.Floor(z)) != 0 && !r.InTile(x, z);
                }
                if (on && start < 0) start = k;
                if (!on && start >= 0) { runs.Add((side, start, Math.Min(k - 4, n - 1))); start = -1; }
            }
        }
        runs.RemoveAll(u => path[u.b].Dist - path[u.a].Dist > 60);

        Point Mid((int side, int a, int b) u)
        {
            int m = (u.a + u.b) / 2;
            var (nx, nz) = Normal(path, m);
            return new Point(path[m].X + nx * probe * u.side, path[m].Z + nz * probe * u.side);
        }
        double Len((int side, int a, int b) u) => path[u.b].Dist - path[u.a].Dist + 1;
        var used = new HashSet<int>();
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].side != 1) continue;
            int mi = (runs[i].a + runs[i].b) / 2, best = -1;
            double bd = 30;
            for (int j = 0; j < runs.Count; j++)
            {
                if (runs[j].side != -1 || used.Contains(j)) continue;
                double d = Math.Abs(path[(runs[j].a + runs[j].b) / 2].Dist - path[mi].Dist);
                if (d < bd) { bd = d; best = j; }
            }
            if (best < 0) continue;
            used.Add(i); used.Add(best);
            Point a = Mid(runs[i]), b = Mid(runs[best]);
            // Breite des kreuzenden Wegs: Länge des Stücks entlang der Linie × sin(Kreuzungswinkel)
            var (tx, tz) = Normal(path, mi);
            double ax = b.X - a.X, az = b.Y - a.Y, al = Math.Sqrt(ax * ax + az * az);
            double sin = al == 0 ? 1 : Math.Abs(ax * tx + az * tz) / al;   // Normal ⟂ Linie: Anteil quer = sin des Winkels zur Linie
            double hw = Math.Clamp((Len(runs[i]) + Len(runs[best])) / 2 * sin / 2 + 1, 2.5, 12);
            byte kind = Math.Max(w.RoadAt((int)Math.Floor(a.X), (int)Math.Floor(a.Y)), w.RoadAt((int)Math.Floor(b.X), (int)Math.Floor(b.Y)));
            r.CrossRoads.Add(new CrossRoad(a, b, hw, true, path[mi].Dist, kind == 0 ? WorldData.Gravel : kind));
        }
        for (int i = 0; i < runs.Count; i++)
        {
            if (used.Contains(i)) continue;
            int m = (runs[i].a + runs[i].b) / 2;
            r.CrossRoads.Add(new CrossRoad(Mid(runs[i]), new Point(path[m].X, path[m].Z), Len(runs[i]) / 2, false, path[m].Dist, 0));
        }
        r.CrossRoads.Sort((p, q) => p.Dist.CompareTo(q.Dist));
    }

    internal static double[] MovingAverage(List<PathPoint> path, double[] v, double window)
    {
        int n = path.Count, lo = 0, hi = 0;
        var res = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            while (hi < n && path[hi].Dist <= path[i].Dist + window / 2) { sum += v[hi]; hi++; }
            while (path[lo].Dist < path[i].Dist - window / 2) { sum -= v[lo]; lo++; }
            res[i] = sum / (hi - lo);
        }
        return res;
    }

    /// Spawnpunkt am Straßenanfang: etwa 30 m hinter dem Anschluss auf der Fahrbahn, y = Straßenhöhe aufgerundet,
    /// Blick entlang der Straße (0 = Norden, 90 = Osten)
    public static SpawnPoint SpawnAtStart(RoadResult r)
    {
        int k = r.IndexAt(Math.Min(30, r.Length / 3));
        var a = r.Path[Math.Max(0, k - 8)];
        var b = r.Path[Math.Min(r.Path.Count - 1, k + 8)];
        double heading = (Math.Atan2(b.X - a.X, b.Z - a.Z) * 180 / Math.PI + 360) % 360;
        return new SpawnPoint { X = Math.Round(r.Path[k].X), Y = Math.Ceiling(r.Profile[k]), Z = Math.Round(r.Path[k].Z), Heading = Math.Round(heading) };
    }

    /// Einheitsvektor quer zur Straße (nach links) am Wegpunkt k
    public static (double nx, double nz) Normal(List<PathPoint> path, int k)
    {
        var a = path[Math.Max(0, k - 2)]; var b = path[Math.Min(path.Count - 1, k + 2)];
        double dx = b.X - a.X, dz = b.Z - a.Z, l = Math.Sqrt(dx * dx + dz * dz);
        if (l == 0) return (0, 0);
        return (-dz / l, dx / l);
    }

    static void Stats(RoadResult r)
    {
        var path = r.Path;
        int n = path.Count;
        for (int i = 4; i < n; i += 4)
        {
            double g = Math.Abs(r.Profile[i] - r.Profile[i - 4]) / (path[i].Dist - path[i - 4].Dist);
            if (g > r.MaxGrade) r.MaxGrade = g;
        }
        for (int i = 0; i < n; i++)
        {
            double d = r.Profile[i] - r.Terrain[i];
            if (d > r.MaxFill) { r.MaxFill = d; r.FillAt = path[i].Dist; }
            if (-d > r.MaxCut) { r.MaxCut = -d; r.CutAt = path[i].Dist; }
        }
        // Knicke: Steigungswechsel von mehr als 4 % innerhalb von 16 m (sichtbare Stufe/Kante in der Fahrbahn)
        for (int i = 32; i + 32 < n; i += 4)
        {
            double s1 = (r.Profile[i] - r.Profile[i - 32]) / Math.Max(1, path[i].Dist - path[i - 32].Dist);
            double s2 = (r.Profile[i + 32] - r.Profile[i]) / Math.Max(1, path[i + 32].Dist - path[i].Dist);
            if (Math.Abs(s2 - s1) > 0.04) r.KinkLength += path[i + 4].Dist - path[i].Dist;
        }
        // Dammstrecken: Länge, auf der die Straße mehr als 1,5 m über dem Gelände liegt, und die längste zusammenhängende
        double run = 0;
        for (int i = 1; i < n; i++)
        {
            double step = path[i].Dist - path[i - 1].Dist;
            if (r.Profile[i] - r.Terrain[i] > 1.5) { run += step; r.FillLength += step; if (run > r.LongestFill) r.LongestFill = run; }
            else run = 0;
        }
    }

    // Kreuzungen: zusammenhängende Stücke der Mittellinie auf vorhandenen Straßen
    static void Crossings(WorldData w, RoadResult r)
    {
        var path = r.Path;
        int n = path.Count, start = -1;
        byte kind = 0;
        for (int i = 0; i <= n; i++)
        {
            byte k = i < n ? w.RoadAt((int)Math.Floor(path[i].X), (int)Math.Floor(path[i].Z)) : (byte)0;
            if (k != 0 && start < 0) { start = i; kind = k; }
            else if (k != 0 && k > kind) kind = k;
            if (k == 0 && start >= 0)
            {
                int m = (start + i - 1) / 2;
                double len = path[i - 1].Dist - path[start].Dist;
                bool atEnd = path[start].Dist < 15 || r.Length - path[i - 1].Dist < 15;
                r.Crossings.Add(new Crossing(path[m].Dist, path[m].X, path[m].Z, kind, r.Profile[m], r.Terrain[m], len, atEnd));
                start = -1;
            }
        }
    }

    // Wasser im Fahrbahnbereich: je Meter entlang der Straße quer abtasten
    static void Water(WorldData w, RoadResult r)
    {
        double half = r.Settings.Width / 2 + r.Settings.Shoulder;
        int count = 0;
        for (int k = 0; k < r.Path.Count; k += (int)SamplesPerMeter)
        {
            var (nx, nz) = Normal(r.Path, k);
            for (double o = -(r.WidthAt(k) / 2 + r.Settings.Shoulder); o <= r.WidthAt(k) / 2 + r.Settings.Shoulder; o += 1)
                if (w.WaterAt((int)Math.Floor(r.Path[k].X + nx * o), (int)Math.Floor(r.Path[k].Z + nz * o))) count++;
        }
        r.WaterArea = count;
    }

    // Gebäude, Kacheln und Teile näher als die Böschung an der Straße
    static void Obstacles(WorldData w, RoadResult r)
    {
        var path = r.Path;
        double half = r.Settings.Width / 2 + r.Settings.Shoulder + r.Settings.GrassEdge, reach = half + MaxSlopeWidth;
        double x0 = path.Min(p => p.X) - reach, x1 = path.Max(p => p.X) + reach, z0 = path.Min(p => p.Z) - reach, z1 = path.Max(p => p.Z) + reach;
        foreach (var pf in w.Prefabs)
        {
            if (pf.X > x1 || pf.X + pf.SizeX < x0 || pf.Z > z1 || pf.Z + pf.SizeZ < z0) continue;
            // angeschlossene Kacheln bleiben unverändert; beim Entfernen werden Kacheln gar nicht angetastet
            if (pf == r.StartTile || pf == r.EndTile || r.IsRemoval && pf.IsTile) continue;
            double best = double.MaxValue;
            int bi = 0;
            for (int k = 0; k < path.Count; k += 2)
            {
                double dx = Math.Max(Math.Max(pf.X - path[k].X, 0), path[k].X - (pf.X + pf.SizeX));
                double dz = Math.Max(Math.Max(pf.Z - path[k].Z, 0), path[k].Z - (pf.Z + pf.SizeZ));
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d < best) { best = d; bi = k; }
            }
            double edge = best - (r.WidthAt(bi) / 2 + r.Settings.Shoulder + r.Settings.GrassEdge);
            if (edge < r.SlopeWidth[bi]) r.Obstacles.Add(new Obstacle(pf, edge, path[bi].Dist));
        }
        r.Obstacles.Sort((a, b) => a.Edge.CompareTo(b.Edge));
    }
}
