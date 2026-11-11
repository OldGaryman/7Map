using System.Windows;

namespace MapEditor.Map;

/// Anschluss einer neuen Straße an eine bestehende (Wunsch des Nutzers 28.10.2026): Kreuzungen sollen fast eben sein.
/// Liegt die bestehende Straße an der Kreuzung steiler als die Toleranz (einstellbar je Belag der neuen Straße), wird sie auf ca. Reach Meter je Seite umgebaut:
/// um die Kreuzung eine Fläche mit höchstens dieser Steigung, nach außen weich zurück auf die alte Höhe.
public sealed class Junction
{
    public bool AtStart;
    public Point Center;              // Kreuzung auf der bestehenden Straße
    public double Height;             // Höhe der Kreuzung (die neue Straße endet hier)
    public double ExistingGrade;      // Steigung der bestehenden Straße an der Kreuzung (Anteil, mit Vorzeichen)
    public Surface Kind;
    public double Width;              // Fahrbahn der bestehenden Straße (gemessen)
    public Vector Direction;          // Richtung der bestehenden Straße an der Kreuzung (Länge 1, in Richtung ExistingGrade)
    public double PlateauGrade;       // Steigung der bestehenden Straße an der Kreuzung nach dem Umbau (ohne Umbau = ExistingGrade)
    /// höherrangig (Asphalt bei einem neuen Schotterweg): wird nicht umgebaut, die neue Straße passt sich an
    public bool Protected;
    /// andere Wege, die im umgebauten Stück anschließen oder kreuzen: dort bleibt die Höhe, der Umbau endet davor
    public List<Point> HeldBranches = new();
    /// Umbau der bestehenden Straße (null = nicht nötig, sie ist dort schon flach genug)
    public List<Point> RebuildPoints;
    public List<double?> RebuildHeights;
    public RoadSettings RebuildSettings;
    public RoadResult RebuildResult;  // Vorschau
}

public static class Junctions
{
    public const double DefaultTolerance = 0.02;   // Standard: 2 % Steigung darf an der Kreuzung bleiben
    const double Reach = 70;                // so weit wird die bestehende Straße je Seite verfolgt/umgebaut (m)

    /// end = Anfang/Ende der neuen Straße; null, wenn dort keine bestehende Straße (außerhalb von Kacheln) liegt
    /// along: Richtung, in die die neue Straße von hier aus verläuft (bei Kreuzungen mitten auf der Straße bothWays = true).
    /// Diese Richtung (±35°) wird ausgeschlossen: liegt dort schon eine Straße (z. B. die alte, über die man neu zeichnet),
    /// ist das nicht die Straße, an die angeschlossen wird (28.10.2026: Umbau lief sonst Nord → West statt Nord → Süd)
    /// newKind: Belag der neuen Straße. Rangordnung (Wunsch des Nutzers 28.10.2026): Asphalt vor Schotter – ein neuer
    /// Schotterweg baut eine Asphaltstraße nie um, er passt sich an (Höhe, Querneigung)
    public static Junction Find(WorldData w, Point end, bool atStart, double newHalfWidth, double tolerance, Func<Surface, RoadSettings> settingsFor,
        Vector? along = null, bool bothWays = false, Surface newKind = Surface.Asphalt)
    {
        double alongAngle = along is Vector av && av.Length > 0 ? Math.Atan2(av.Y, av.X) * 180 / Math.PI : double.NaN;
        bool Excluded(double ang)
        {
            if (double.IsNaN(alongAngle)) return false;
            double off = Math.Abs(((ang - alongAngle) % 360 + 540) % 360 - 180);   // 0 = genau in Richtung der neuen Straße
            return off <= 35 || bothWays && off >= 145;
        }
        var tiles = w.Prefabs.Where(p => p.IsTile && p.X < end.X + Reach + 20 && p.X + p.SizeX > end.X - Reach - 20
                                                  && p.Z < end.Y + Reach + 20 && p.Z + p.SizeZ > end.Y - Reach - 20).ToList();
        bool Road(double x, double z) => w.RoadAt((int)Math.Floor(x), (int)Math.Floor(z)) != 0 && !tiles.Any(t => t.Contains(x, z));
        if (tiles.Any(t => t.Contains(end.X, end.Y))) return null;   // Kacheln: eigene Regel (Kachelhöhe)
        bool onRoad = false;
        for (int dz = -2; dz <= 2 && !onRoad; dz++)
            for (int dx = -2; dx <= 2 && !onRoad; dx++)
                onRoad = Road(end.X + dx, end.Y + dz);
        if (!onRoad) return null;

        // Belag und Richtung der bestehenden Straße an der Kreuzung (Hauptachse der Straßenpixel im Umkreis von 10 m)
        int asphalt = 0, gravel = 0;
        double sx = 0, sz = 0, sxx = 0, szz = 0, sxz = 0;
        int cnt = 0;
        for (int dz = -10; dz <= 10; dz++)
            for (int dx = -10; dx <= 10; dx++)
            {
                if (dx * dx + dz * dz > 100 || !Road(end.X + dx, end.Y + dz)) continue;
                if (dx * dx + dz * dz > 9 && Excluded(Math.Atan2(dz, dx) * 180 / Math.PI)) continue;   // Straße unter der neuen
                if (w.RoadAt((int)Math.Floor(end.X + dx), (int)Math.Floor(end.Y + dz)) == WorldData.Asphalt) asphalt++; else gravel++;
                sx += dx; sz += dz; sxx += dx * dx; szz += dz * dz; sxz += dx * dz; cnt++;
            }
        var kind = asphalt >= gravel ? Surface.Asphalt : Surface.Gravel;
        double mx = sx / cnt, mz = sz / cnt;
        double cxx = sxx / cnt - mx * mx, czz = szz / cnt - mz * mz, cxz = sxz / cnt - mx * mz;
        double axis = 0.5 * Math.Atan2(2 * cxz, cxx - czz) * 180 / Math.PI;   // Richtung der Straße (Grad)

        // je Richtung (axis und axis+180) die Straße verfolgen: Ring in Reach Metern, bei Kacheln oder Kurven auch näher;
        // nur Treffer ungefähr in Straßenrichtung und mit demselben Belag (sonst würde ein abzweigender Weg mit umgebaut)
        // Der Weg muss fast ganz (90 %) auf demselben Belag bleiben: sonst biegt er in einen abzweigenden Weg ab
        // (28.10.2026: Asphaltstraße → Schotterweg, der Schotterweg wurde 20 m breit mit umgebaut)
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

        // Mittellinie: Seite A rückwärts bis zur Kreuzung, dann Seite B
        var line = new List<Point>(Enumerable.Reverse(sides[0].Points));
        if (sides.Count > 1) line.AddRange(sides[1].Points.Skip(1));
        // Liegt der Anschluss am Rand einer breiten Straße, laufen beide Seiten erst quer zur Mitte; dieses Querstück ist flach
        // und verfälscht die gemessene Steigung (28.10.2026: −4,2 % gemessen, tatsächlich −8 %). Darum die Mittellinie noch
        // einmal von Ende zu Ende verfolgen und die Kreuzung auf ihr suchen.
        if (sides.Count > 1)
        {
            var through = RoadTracer.Trace(w, sides[0].Points[^1], sides[1].Points[^1], out _);
            if (through != null && through.Points.Count >= 2 && Pure(through) && through.Length < line.Count * 4 + 200) line = through.Points;
        }
        var pts = Resample(line, 2);
        int ci = NearestIndex(pts, end);
        var dist = Distances(pts);
        double dJ = dist[ci];
        double H(double d) => Avg(pts, dist, d, w);   // Höhe der bestehenden Straße (Mittel über ±3 m)
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
        // tolerance: was an der Kreuzung bleiben darf (Kreuzungs-Toleranz bzw. erlaubte Querneigung der neuen Straße)
        if (Math.Abs(j.ExistingGrade) <= tolerance + 0.005 || sides.Count < 2 && dist[^1] < 20) return j;   // flach genug (0,5 % Spiel)
        if (Rank(kind) > Rank(newKind)) { j.Protected = true; return j; }   // höherrangige Straße bleibt, wie sie ist
        if (Math.Abs(j.ExistingGrade) > 0.25) return j;   // über 25 %: kein befahrbarer Weg (Belagreste am Hang, Messfehler) – nicht umbauen

        // Umbau als weiche Verformung (28.10.2026; vorher harte Platte mit Knicken von 13 % an ihren Rändern): neue Höhe =
        // alte Höhe + Korrektur; die Korrektur bringt die Straße um die Kreuzung (±R) auf eine Linie mit höchstens der Toleranz
        // als Steigung und läuft nach außen mit einer S-Kurve bis zum Ende des umgebauten Stücks auf 0 aus. Alle Punkte fest.
        double R = Math.Max(6, newHalfWidth + 4);   // Fläche um die Kreuzung: Breite der neuen Straße plus etwas Rand
        double g = Math.Sign(j.ExistingGrade) * tolerance;
        j.PlateauGrade = g;
        // andere Wege, die im umgebauten Stück anschließen oder kreuzen (Belag gleich neben dem Rand der Straße, auf mind.
        // 4 m Breite): dort bleibt die Höhe, damit ihr Anschluss nicht verbogen wird – der Umbau läuft schon davor aus
        // (28.10.2026: zwei Schotterwege an derselben Asphaltstraße verbogen sich gegenseitig die Straße)
        double leftEnd = 0, rightEnd = dist[^1];
        for (int i = 1; i < pts.Count - 1; i++)
        {
            double off = dist[i] - dJ;
            if (Math.Abs(off) < R + 6) continue;   // an der Kreuzung selbst liegt die neue Straße
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
        // Abstand vom Rand der Kreuzungsfläche bis dorthin, wo die Korrektur 0 sein muss (Anschluss mit 6 m Abstand)
        double left = Math.Max(4, dJ - R - (leftEnd > 0 ? leftEnd + 6 : 0)), right = Math.Max(4, (rightEnd < dist[^1] ? rightEnd - 6 : dist[^1]) - dJ - R);
        double Corr(double d)
        {
            double off = d - dJ, plateau = j.Height + g * off, full = plateau - H(d);
            double a = Math.Abs(off);
            if (a <= R) return full;
            double t = Math.Clamp((a - R) / (off < 0 ? left : right), 0, 1);
            double keep = 1 - t * t * (3 - 2 * t);
            // außerhalb: die Abweichung am Rand der Fläche weich auslaufen lassen
            double edge = j.Height + g * Math.Sign(off) * R - H(dJ + Math.Sign(off) * R);
            return edge * keep;
        }
        var ctrl = new List<Point>();
        var heights = new List<double?>();
        for (int i = 0; i < pts.Count; i += 2)   // alle 4 m ein fester Punkt
        {
            ctrl.Add(pts[i]);
            heights.Add(H(dist[i]) + Corr(dist[i]));
        }
        if ((ctrl[^1] - pts[^1]).Length > 1) { ctrl.Add(pts[^1]); heights.Add(H(dist[^1]) + Corr(dist[^1])); }

        var rs = settingsFor(j.Kind).Clone();
        rs.Width = j.Width;
        rs.Shoulder = 0;
        rs.HeightOffset = 0;
        rs.PaintSurface = false;     // nur die Höhen ändern, der Belag der bestehenden Straße bleibt, wie er ist
        rs.MaxGrade = Math.Max(rs.MaxGrade, Math.Abs(j.ExistingGrade) * 1.5);   // außerhalb der Kreuzung etwas steiler erlaubt
        j.RebuildPoints = ctrl;
        j.RebuildHeights = heights;
        j.RebuildSettings = rs;
        j.RebuildResult = RoadPlanner.Compute(w, ctrl, rs, heights);
        return j;
    }

    /// Rang einer Straße: Asphalt vor Schotter
    public static int Rank(Surface s) => s == Surface.Asphalt ? 2 : 1;

    // zusammenhängende Winkel (auch über 359/0 hinweg) zu ihrer Mitte zusammenfassen
    static List<double> Clusters(List<int> hits)
    {
        var res = new List<double>();
        if (hits.Count == 0) return res;
        if (hits.Count == 360) return res;   // ganz von Straße umgeben: keine Richtung erkennbar
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
