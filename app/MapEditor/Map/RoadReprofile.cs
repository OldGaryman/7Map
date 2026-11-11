using System.Windows;

namespace MapEditor.Map;

/// Einstellungen für „Straße bearbeiten“
public sealed class ReprofileOptions
{
    /// true: Kreuzungen bleiben auf ihrer alten Höhe (die Straße läuft dort durch); false: kreuzende Straßen werden immer mitgezogen
    /// (Wunsch des Nutzers 3.11.2026: keine Beschränkung für Kreuzungen); nur vor einer Stadt-Kachel wird die Änderung kleiner
    public bool KeepJunctions;
    /// true: zwischen den Markierungen gleichmäßige Neigung (Hügel verschwinden); false: Form der Straße bleibt, sie wird nur
    /// höher oder tiefer gelegt
    public bool Even = true;
    /// Querneigung in Kurven automatisch aus dem Verlauf der Straße berechnen (nur zwei Markierungen nötig); von Hand eingegebene
    /// Werte an Markierungen haben Vorrang. AutoCrossMax = höchste Querneigung (Anteil)
    public bool AutoCross;
    public double AutoCrossMax = 0.08;
}

/// Ein Ast einer Kreuzung: die kreuzende Straße von der Kreuzung nach außen
public sealed class BranchArm
{
    public List<Point> Points = new();   // ab der Kreuzung auf der bearbeiteten Straße (alle 2 m)
    public double Width;                 // gemessene Breite (Belag)
    public double Length;                // Länge ab der Kreuzung (m)
    public string Stop;                  // null = volle Länge, "end", "tile", "junction"
    public double Start;                 // bis hierher (ab der Kreuzung) gilt die volle Höhenänderung: Fläche der bearbeiteten Straße
    public double Fade;                  // Länge, auf der die Änderung ausläuft (m)
    /// Platz für den Übergang: vor einer Stadt-Kachel oder einer weiteren Kreuzung 6 m weniger (dort darf nichts mehr ankommen)
    public double Available => Length - Start - (Stop is "tile" or "junction" ? 6 : 0);
}

/// Einfahrt eines Gebäudes oder Straßenausgang einer Kachel neben der bearbeiteten Straße: die Straße behält dort ihre alte Höhe,
/// weil sich das Gebäude/die Kachel nicht mitbewegt (sonst Stufe zwischen Zufahrt und Straße)
public sealed class AnchorPlan
{
    public double Dist;
    public double PinHalf;
    public string Name;
    public bool IsTile;
    public bool Pinnable = true;
}

/// Kreuzung oder Einmündung an der bearbeiteten Strecke
public sealed class JunctionPlan
{
    public double Dist;
    public Point Center;
    public byte Kind;
    public bool Through;
    public List<BranchArm> Arms = new();
    public double Delta;       // Höhenänderung der bearbeiteten Straße an dieser Stelle (m)
    /// null = die Straße folgt der Änderung frei; sonst höchste Änderung an dieser Kreuzung (m, mit Vorzeichen): 0 = Höhe bleibt
    /// (Wunsch des Nutzers), ≠ 0 = verkleinert, weil vor einer Stadt-Kachel kaum Platz für den Übergang ist
    public double? Limit;
    public bool Pinned => Limit == 0;
    public bool Limited => Limit != null && Limit != 0;
    public bool Pinnable = true;
    public string Why;         // Grund, wenn festgehalten oder verkleinert
    public double PinHalf;     // so weit (entlang der Straße) gilt die Begrenzung
    public bool Followed => !Pinned && Math.Abs(Delta) >= 0.03;
}

/// Berechnete Änderung: Verlauf der Straße, alte und neue Höhe, Kreuzungen
public sealed class ReprofilePlan
{
    public string Error;
    public List<Point> Control;                 // Stützpunkte mit Auslauf davor und danach
    public List<PathPoint> Path;
    public int[] MarkerIndex;                   // Wegpunkt je Markierung
    public int CoreFrom, CoreTo;                // Wegpunkte der ersten und letzten Markierung
    public double[] Old, OldSmooth, New, Delta, Blend;
    public double RoadHalf, Edge;               // halbe Breite des Belags (gemessen) bzw. des Bereichs mit voller Änderung
    public byte Kind;
    public double[] MarkerOld, MarkerNew;       // Höhe der Straße an den Markierungen vorher/nachher
    public double[] SegmentLength;              // Länge zwischen Markierung i und i+1
    public double RunIn, RunOut;                // Länge des Auslaufs vor der ersten / nach der letzten Markierung
    public List<JunctionPlan> Junctions = new();
    public List<AnchorPlan> Anchors = new();   // Einfahrten von Gebäuden und Kachelausgänge neben der Straße
    public List<string> Warnings = new();
    public int AutoCurves;                       // Zahl der Kurven mit automatischer Querneigung
    public double MaxGrade, MaxGradeAt;
    /// mindestens eine Markierung hat eine eingegebene Höhe; sonst bleibt die Straße unverändert
    public bool AnyTarget;
    /// Querneigung (Kurvenüberhöhung, Wunsch des Nutzers 3.11.2026): Höhenunterschied je Meter quer zur Straße, + = linke Seite
    /// (in Richtung der Markierungen gesehen) höher. OldCross = vorhandene Querneigung der Straße, CrossDelta = Änderung je Wegpunkt
    public bool AnyCross;
    public double[] OldCross, CrossDelta, MarkerCrossOld, MarkerCrossNew;
    public bool[] MarkerCrossManual;   // Querneigung der Markierung von Hand eingegeben (hat Vorrang vor der Automatik)
    public double MaxCross;   // größte Querneigung nach der Änderung (Betrag)
    public bool Changes => Error == null && (Delta != null && Delta.Any(d => Math.Abs(d) >= 0.02)
                                            || CrossDelta != null && CrossDelta.Any(c => Math.Abs(c) * Edge >= 0.02));
    public double Length => Path[^1].Dist;

    /// Ergebnis für Karte und Höhenprofil (Straße in Blau, Auslauf violett)
    public RoadResult ToResult()
    {
        var s = RoadSettings.For(Kind == WorldData.Asphalt ? Surface.Asphalt : Surface.Gravel);
        s.Width = Edge * 2;
        s.Shoulder = 0;
        var r = new RoadResult
        {
            Settings = s, Path = Path, Profile = New, Terrain = Old, SlopeWidth = Blend, ControlIndex = MarkerIndex,
            Length = Length, EditLook = true, CoreFrom = CoreFrom, CoreTo = CoreTo, MaxGrade = MaxGrade,
        };
        foreach (var j in Junctions)
        {
            int i = Math.Min(Path.Count - 1, IndexAt(j.Dist));
            r.Crossings.Add(new Crossing(j.Dist, j.Center.X, j.Center.Y, j.Kind, New[i], Old[i], 0, true));
            if (j.Pinned || j.Limited) r.HeldPoints.Add(j.Center);
        }
        return r;
    }

    public int IndexAt(double dist)
    {
        int lo = 0, hi = Path.Count - 1;
        while (lo < hi) { int m = (lo + hi) / 2; if (Path[m].Dist < dist) lo = m + 1; else hi = m; }
        return lo;
    }
}

/// „Straße bearbeiten“ (Wunsch des Nutzers 2.11.2026): Der Nutzer setzt Markierungen mitten auf eine vorhandene Straße und legt für sie
/// Höhen oder Neigungen fest. Die Straße zwischen den Markierungen bekommt das neue Längsprofil, davor und danach läuft die Änderung
/// weich aus. Kreuzende Straßen folgen der Änderung (wenn sie das ohne steile Stücke schaffen), sonst bleibt die Kreuzung auf der alten Höhe.
public static class RoadReprofile
{
    /// so viele Meter Auslauf je Meter Höhenänderung (Spitzensteigung des Auslaufs ≈ 1,5 / 37,5 = 4 %)
    const double RunPerMeter = 37.5;
    const double MaxRun = 150, MinRun = 20, MinArm = 12;
    /// abgehende Straßen: so viele Meter Übergang je Meter Höhenänderung (weicher als die Auslaufstrecke der Hauptstraße;
    /// Spitze ≈ 1,5 / 60 = 2,5 % Zusatzsteigung), höchstens MaxArm Meter
    const double ArmPerMeter = 60, MaxArm = 250;
    /// so lang läuft die Querneigung vor der ersten und nach der letzten Markierung aus (m)
    const double CrossRun = 30;

    /// Plan berechnen. targets: gewünschte Höhe je Markierung (null = bleibt). cross: gewünschte Querneigung je Markierung
    /// (Anteil, null = bleibt). cache: gemerkte Verfolgungen zwischen Markierungen.
    public static ReprofilePlan Plan(WorldData w, IReadOnlyList<Point> markers, IReadOnlyList<double?> targets, ReprofileOptions o,
        Dictionary<(double, double, double, double), TraceResult> cache = null, RoadScan scan = null, IReadOnlyList<double?> cross = null)
    {
        var plan = new ReprofilePlan();
        if (markers.Count < 2) { plan.Error = Loc.T("Set at least two markers on the road."); return plan; }
        scan ??= new RoadScan(w);

        // 1. Strecke zwischen den Markierungen (der Straßenmitte folgend)
        var core = new List<Point>();
        var coreIdx = new List<int> { 0 };
        var widths = new List<double>();
        int asphalt = 0, gravel = 0;
        for (int i = 0; i + 1 < markers.Count; i++)
        {
            var key = (markers[i].X, markers[i].Y, markers[i + 1].X, markers[i + 1].Y);
            TraceResult t = null;
            if (cache == null || !cache.TryGetValue(key, out t))
            {
                t = RoadTracer.Trace(w, markers[i], markers[i + 1], out string err);
                if (t == null) { plan.Error = err; return plan; }
                if (cache != null) cache[key] = t;
            }
            if (t.Points.Count < 2 || t.Length < 1) { plan.Error = Loc.T("Two markers are too close together."); return plan; }
            int from = core.Count > 0 && (t.Points[0] - core[^1]).Length < 2.5 ? 1 : 0;
            for (int k = from; k < t.Points.Count; k++) core.Add(t.Points[k]);
            coreIdx.Add(core.Count - 1);
            widths.Add(t.Width);
            if (t.Kind == Surface.Asphalt) asphalt++; else gravel++;
        }
        widths.Sort();
        double width = widths[widths.Count / 2];
        plan.RoadHalf = Math.Max(2, (width - 2) / 2);
        plan.Edge = plan.RoadHalf + 1;
        plan.Kind = asphalt >= gravel ? WorldData.Asphalt : WorldData.Gravel;

        // 2. Auslauf davor und danach: so lang, wie die Änderung an den äußeren Markierungen es verlangt
        var corePath = RoadPlanner.BuildPath(core);
        var coreTerrain = Terrain(w, corePath);
        var coreSmooth = RoadPlanner.MovingAverage(corePath, coreTerrain, 6);
        var ci0 = ControlIndex(corePath, core.Count);
        double OldAt(int m) => coreSmooth[ci0[coreIdx[m]]];
        double d0 = Target(targets, 0, OldAt(0)) - OldAt(0), d1 = Target(targets, markers.Count - 1, OldAt(markers.Count - 1)) - OldAt(markers.Count - 1);
        double RunFor(double d) => Math.Abs(d) < 0.03 ? 0 : Math.Clamp(Math.Abs(d) * RunPerMeter, MinRun, MaxRun);
        // Richtung, in der die Straße vor der ersten bzw. hinter der letzten Markierung weiterläuft (10 m Messstrecke)
        var first = corePath[0];
        var beforeFirst = corePath[Math.Min(corePath.Count - 1, 40)];
        var last = corePath[^1];
        var beforeLast = corePath[Math.Max(0, corePath.Count - 41)];
        var dirStart = new Vector(first.X - beforeFirst.X, first.Z - beforeFirst.Z);
        var dirEnd = new Vector(last.X - beforeLast.X, last.Z - beforeLast.Z);
        // auch die Querneigung läuft vor der ersten / nach der letzten Markierung aus
        var coreCross = OldCross(w, corePath, Math.Max(1, plan.RoadHalf - 0.5));
        double CrossRunFor(int mk)
        {
            double was = coreCross[ci0[coreIdx[mk]]];
            return o.AutoCross || Math.Abs(Target(cross, mk, was) - was) >= 0.003 ? CrossRun : 0;
        }
        var ext0 = RoadFollower.Extend(scan, core[0], dirStart, Math.Max(RunFor(d0), CrossRunFor(0)), width, false);
        var ext1 = RoadFollower.Extend(scan, core[^1], dirEnd, Math.Max(RunFor(d1), CrossRunFor(markers.Count - 1)), width, false);
        var full = new List<Point>(ext0.Points.Count + core.Count + ext1.Points.Count);
        for (int k = ext0.Points.Count - 1; k >= 0; k--) full.Add(ext0.Points[k]);
        int shift = full.Count;
        full.AddRange(core);
        full.AddRange(ext1.Points);
        plan.Control = full;
        var path = plan.Path = RoadPlanner.BuildPath(full);
        int n = path.Count;
        var ci = ControlIndex(path, full.Count);
        plan.MarkerIndex = coreIdx.Select(c => ci[shift + c]).ToArray();
        plan.CoreFrom = plan.MarkerIndex[0];
        plan.CoreTo = plan.MarkerIndex[^1];
        plan.RunIn = path[plan.CoreFrom].Dist;
        plan.RunOut = path[n - 1].Dist - path[plan.CoreTo].Dist;

        // 3. alte Höhen und Zielhöhen der Markierungen
        var old = plan.Old = Terrain(w, path);
        var oldS = plan.OldSmooth = RoadPlanner.MovingAverage(path, old, 6);
        int m = markers.Count;
        plan.AnyTarget = targets != null && targets.Any(t => t != null);
        plan.MarkerOld = new double[m];
        plan.MarkerNew = new double[m];
        plan.SegmentLength = new double[m - 1];
        plan.OldCross = OldCross(w, path, Math.Max(1, plan.RoadHalf - 0.5));
        plan.AnyCross = o.AutoCross || cross != null && cross.Any(t => t != null);
        plan.MarkerCrossOld = new double[m];
        plan.MarkerCrossNew = new double[m];
        plan.MarkerCrossManual = new bool[m];
        for (int k = 0; k < m; k++) plan.MarkerCrossManual[k] = cross != null && k < cross.Count && cross[k] != null;
        for (int k = 0; k < m; k++)
        {
            plan.MarkerOld[k] = oldS[plan.MarkerIndex[k]];
            plan.MarkerNew[k] = Target(targets, k, plan.MarkerOld[k]);
            plan.MarkerCrossOld[k] = plan.OldCross[plan.MarkerIndex[k]];
            plan.MarkerCrossNew[k] = Target(cross, k, plan.MarkerCrossOld[k]);
            if (k > 0) plan.SegmentLength[k - 1] = path[plan.MarkerIndex[k]].Dist - path[plan.MarkerIndex[k - 1]].Dist;
        }
        if (ext0.Length < RunFor(d0) - 4 && RunFor(d0) > 0)
            plan.Warnings.Add(Loc.F("Before the first marker there is only {0:0} m of road for the run-out ({1}).", ext0.Length, StopText(ext0.Stop)));
        if (ext1.Length < RunFor(d1) - 4 && RunFor(d1) > 0)
            plan.Warnings.Add(Loc.F("After the last marker there is only {0:0} m of road for the run-out ({1}).", ext1.Length, StopText(ext1.Stop)));

        // 4. Kreuzungen
        plan.Junctions = FindJunctions(scan, path, plan.RoadHalf);
        foreach (var j in plan.Junctions)
        {
            j.PinHalf = Math.Max(j.Arms.Count > 0 ? j.Arms.Max(a => a.Width) / 2 : 4, 3) + 3;
            // zu nah an einer Markierung: dort lässt sich die Höhe nicht halten
            foreach (int mi in plan.MarkerIndex)
                if (Math.Abs(path[mi].Dist - j.Dist) < j.PinHalf + 2) j.Pinnable = false;
            if (j.Dist < plan.RunIn || j.Dist > path[n - 1].Dist - plan.RunOut) j.Pinnable = false;   // im Auslauf gibt es keinen Haltepunkt
            if (o.KeepJunctions && j.Pinnable) { j.Limit = 0; j.Why = Loc.T("kept as chosen"); }
        }
        // Einfahrten von Gebäuden und Kachelausgänge neben der Straße: dort bleibt die Straße auf ihrer alten Höhe
        plan.Anchors = FindAnchors(w, plan);

        // 5. Profil. Kreuzende Straßen folgen der Änderung immer (keine Beschränkung, Wunsch des Nutzers); nur wenn gleich dahinter
        // eine Stadt-Kachel beginnt und für einen sanften Übergang kein Platz ist, wird die Änderung an dieser Kreuzung kleiner
        for (int iter = 0; iter < 6; iter++)
        {
            Profile(plan, o);
            bool changed = false;
            foreach (var j in plan.Junctions)
            {
                j.Delta = plan.Delta[plan.IndexAt(j.Dist)];
                if (j.Limit != null || !j.Pinnable) continue;
                double? allowed = TileAllowed(j);
                if (allowed is not double al || Math.Abs(j.Delta) <= al + 1e-6) continue;
                j.Limit = Math.Sign(j.Delta) * al;
                j.Why = Loc.T("a city tile is close");
                changed = true;
            }
            if (!changed) break;
        }
        foreach (var j in plan.Junctions)
        {
            if (j.Pinned || Math.Abs(j.Delta) < 0.05) continue;
            foreach (var a in j.Arms)
            {
                double needed = Math.Clamp(Math.Abs(j.Delta) * ArmPerMeter, MinArm, MaxArm);
                a.Fade = Math.Max(1, Math.Min(needed, a.Available));
            }
            double peak = j.Arms.Count == 0 ? 0 : j.Arms.Max(a => 1.5 * Math.Abs(j.Delta) / a.Fade);
            if (peak > 0.10)
                plan.Warnings.Add(Loc.F("Junction at {0:0}, {1:0}: the crossing road has little room, so it climbs steeply there (up to {2:0} %).", j.Center.X, j.Center.Y, peak * 100));
            if (j.Limit == null && !j.Pinnable && TileAllowed(j) is double al2 && Math.Abs(j.Delta) > al2 + 1e-6)
                plan.Warnings.Add(Loc.F("Junction at {0:0}, {1:0}: a city tile is close behind it, the change cannot be reduced here.", j.Center.X, j.Center.Y));
        }
        foreach (var a in plan.Anchors.Where(a => !a.Pinnable))
            if (Math.Abs(plan.Delta[plan.IndexAt(a.Dist)]) > 0.1)
                plan.Warnings.Add(Loc.F("{0} next to the road should keep its height, but a marker or the run-out lies there.", AnchorName(a)));

        // 6. Querneigung, Auswertung: Böschungsbreite, steilste Stelle, Gebäude in der Nähe
        CrossProfile(plan, o);
        plan.Blend = SlopeWidths(plan);
        Grade(plan);
        if (plan.MaxGrade > 0.12)
            plan.Warnings.Add(Loc.F("The road becomes up to {0:0} % steep (at {1:#,0} m).", plan.MaxGrade * 100, plan.MaxGradeAt));
        if (plan.MaxCross > 0.12)
            plan.Warnings.Add(Loc.F("The banking becomes up to {0:0} % (across the road).", plan.MaxCross * 100));
        Obstacles(w, plan);
        return plan;
    }

    static string StopText(string stop) => stop switch
    {
        "end" => Loc.T("the road ends"),
        "tile" => Loc.T("a city tile begins"),
        "junction" => Loc.T("a junction"),
        _ => Loc.T("limit"),
    };

    static double Target(IReadOnlyList<double?> targets, int i, double fallback) =>
        targets != null && i < targets.Count && targets[i] is double t ? t : fallback;

    static double[] Terrain(WorldData w, List<PathPoint> path)
    {
        var t = new double[path.Count];
        for (int i = 0; i < t.Length; i++) t[i] = w.HeightAt((int)Math.Floor(path[i].X), (int)Math.Floor(path[i].Z));
        return t;
    }

    static int[] ControlIndex(List<PathPoint> path, int count)
    {
        var ci = new int[count];
        for (int i = 0; i < path.Count; i++)
            if (path[i].Segment + 1 < count) ci[path[i].Segment + 1] = i;
        for (int k = 1; k < count; k++) ci[k] = Math.Max(ci[k], ci[k - 1]);
        return ci;
    }

    static double Fade(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }

    /// weicheres Auslaufen (glatter in Steigung und Krümmung als Fade): 1 → 0 mit 6t⁵ − 15t⁴ + 10t³
    static double Fade5(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 1 - t * t * t * (t * (t * 6 - 15) + 10);
    }

    /// Höchste Änderung an einer Kreuzung, wenn eine Stadt-Kachel gleich hinter ihr beginnt: der Übergang der kreuzenden Straße darf
    /// dort höchstens 6 % Zusatzsteigung haben (Spitze 1,5 × Änderung / Platz). Null = keine Kachel dahinter, keine Begrenzung
    static double? TileAllowed(JunctionPlan j)
    {
        double? best = null;
        foreach (var a in j.Arms)
        {
            if (a.Stop != "tile") continue;
            double al = 0.06 * Math.Max(a.Available, 0) / 1.5;
            best = best == null ? al : Math.Min(best.Value, al);
        }
        return best;
    }

    static string AnchorName(AnchorPlan a) => a.IsTile ? Loc.F("The road exit of tile {0}", a.Name) : Loc.F("The entrance of {0}", a.Name);

    /// Einfahrten und Kachelausgänge höchstens RoadHalf + 7 m neben der Strecke
    static List<AnchorPlan> FindAnchors(WorldData w, ReprofilePlan p)
    {
        var res = new List<AnchorPlan>();
        var path = p.Path;
        double reach = p.RoadHalf + 7, pad = 40;
        double x0 = path.Min(q => q.X) - pad, x1 = path.Max(q => q.X) + pad, z0 = path.Min(q => q.Z) - pad, z1 = path.Max(q => q.Z) + pad;
        var exits = PrefabParts.AllExitsWithWidth(w, pf => pf.X <= x1 && pf.X + pf.SizeX >= x0 && pf.Z <= z1 && pf.Z + pf.SizeZ >= z0);
        foreach (var e in exits)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int k = 0; k < path.Count; k += 4)
            {
                double d = (path[k].X - e.X) * (path[k].X - e.X) + (path[k].Z - e.Z) * (path[k].Z - e.Z);
                if (d < bd) { bd = d; best = k; }
            }
            if (best < 0 || Math.Sqrt(bd) > reach) continue;
            var a = new AnchorPlan { Dist = path[best].Dist, PinHalf = Math.Max(3, e.Width / 2.0) + 3, Name = e.Owner.Name, IsTile = e.Owner.IsTile };
            if (res.Any(r => Math.Abs(r.Dist - a.Dist) < 6)) continue;
            foreach (int mi in p.MarkerIndex)
                if (Math.Abs(path[mi].Dist - a.Dist) < a.PinHalf + 2) a.Pinnable = false;
            if (a.Dist < p.RunIn || a.Dist > path[^1].Dist - p.RunOut) a.Pinnable = false;
            res.Add(a);
        }
        res.Sort((x, y) => x.Dist.CompareTo(y.Dist));
        return res;
    }

    // ---------- Profil ----------

    /// Neue Höhe und Änderung je Wegpunkt. Zwischen den Markierungen eine glatte Kurve durch die Zielhöhen (und die festgehaltenen
    /// Kreuzungen auf ihrer alten Höhe), davor und danach läuft die Änderung der äußeren Markierung weich aus.
    static void Profile(ReprofilePlan p, ReprofileOptions o)
    {
        var path = p.Path;
        int n = path.Count;
        p.New = new double[n];
        p.Delta = new double[n];
        if (!p.AnyTarget)
        {
            // noch nichts eingegeben: die Straße bleibt, wie sie ist
            Array.Copy(p.Old, p.New, n);
            return;
        }
        double sFirst = path[p.CoreFrom].Dist, sLast = path[p.CoreTo].Dist;
        // Knoten: Markierungen (Zielhöhe bzw. Änderung) und festgehaltene Kreuzungen (alte Höhe bzw. Änderung 0)
        var knots = new List<(double S, double V, bool Marker)>();
        for (int k = 0; k < p.MarkerIndex.Length; k++)
        {
            int mi = p.MarkerIndex[k];
            knots.Add((path[mi].Dist, o.Even ? p.MarkerNew[k] : p.MarkerNew[k] - p.MarkerOld[k], true));
        }
        // begrenzte Kreuzungen (Limit = erlaubte Änderung, 0 = Höhe bleibt) und Einfahrten (immer 0)
        var holds = p.Junctions.Where(j => j.Limit != null && j.Pinnable).Select(j => (j.Dist, j.PinHalf, Delta: j.Limit.Value))
            .Concat(p.Anchors.Where(a => a.Pinnable).Select(a => (a.Dist, a.PinHalf, Delta: 0.0)));
        foreach (var (hd, half, hdelta) in holds)
            foreach (double off in new[] { -half, 0, half })
            {
                double s = hd + off;
                if (s <= sFirst + 1 || s >= sLast - 1) continue;
                knots.Add((s, o.Even ? p.OldSmooth[p.IndexAt(s)] + hdelta : hdelta, false));
            }
        knots.Sort((a, b) => a.S.CompareTo(b.S));
        var clean = new List<(double S, double V)>();
        foreach (var (s, v, marker) in knots)
        {
            if (clean.Count > 0 && s - clean[^1].S < 1.0)
            {
                if (marker) clean[^1] = (s, v);   // Markierung hat Vorrang vor einem Haltepunkt
                continue;
            }
            clean.Add((s, v));
        }
        var curve = new Pchip(clean);
        for (int i = 0; i < n; i++)
        {
            double s = path[i].Dist;
            if (s >= sFirst && s <= sLast)
            {
                double c = curve.At(s);
                p.Delta[i] = o.Even ? c - p.Old[i] : c;
            }
        }
        double endIn = p.Delta[p.CoreFrom], endOut = p.Delta[p.CoreTo];
        for (int i = 0; i < p.CoreFrom; i++) p.Delta[i] = endIn * Fade((sFirst - path[i].Dist) / Math.Max(1e-6, sFirst));
        for (int i = p.CoreTo + 1; i < n; i++) p.Delta[i] = endOut * Fade((path[i].Dist - sLast) / Math.Max(1e-6, path[n - 1].Dist - sLast));
        for (int i = 0; i < n; i++) p.New[i] = p.Old[i] + p.Delta[i];
    }

    /// vorhandene Querneigung je Wegpunkt: Höhe links minus rechts der Mittellinie (halb = Abstand zur Mitte, noch auf dem Belag)
    /// durch den Abstand, über 10 m gemittelt (die Höhen liegen im Meterraster)
    static double[] OldCross(WorldData w, List<PathPoint> path, double half)
    {
        var c = new double[path.Count];
        for (int i = 0; i < c.Length; i++)
        {
            var (nx, nz) = RoadPlanner.Normal(path, i);
            double hl = w.HeightAt((int)Math.Floor(path[i].X + nx * half), (int)Math.Floor(path[i].Z + nz * half));
            double hr = w.HeightAt((int)Math.Floor(path[i].X - nx * half), (int)Math.Floor(path[i].Z - nz * half));
            c[i] = (hl - hr) / (2 * half);
        }
        return RoadPlanner.MovingAverage(path, c, 10);
    }

    /// Querneigung nach der Krümmung der Mittellinie (wie bei einer Rennstrecke): in Kurven kippt die Straße zur Kurveninnenseite
    /// (innen tiefer, außen höher), in Geraden 0. Querneigung = Höchstwert bei Kurvenradius ≤ 80 m, bei größerem Radius
    /// proportional weniger; sehr weite Kurven (Radius > ca. 500 m) bleiben eben. Die Krümmung wird über 40 m gemittelt, dadurch
    /// baut sich die Neigung schon vor der Kurve sanft auf und danach sanft ab. + = linke Seite höher.
    static double[] AutoBanking(ReprofilePlan p, ReprofileOptions o)
    {
        var path = p.Path;
        int n = path.Count;
        const double MinRadius = 80, Dead = 0.015;
        double max = Math.Max(0.005, o.AutoCrossMax);
        // Fahrtrichtung (mathematischer Winkel, links herum = zunehmend), über ±1 m, ohne Sprung bei ±180°
        var theta = new double[n];
        for (int i = 0; i < n; i++)
        {
            var a = path[Math.Max(0, i - 4)]; var b = path[Math.Min(n - 1, i + 4)];
            theta[i] = Math.Atan2(b.Z - a.Z, b.X - a.X);
            if (i > 0) theta[i] += Math.Round((theta[i - 1] - theta[i]) / (2 * Math.PI)) * 2 * Math.PI;
        }
        var kappa = new double[n];
        int step = 80;   // 20 m je Seite
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - step), b = Math.Min(n - 1, i + step);
            double ds = path[b].Dist - path[a].Dist;
            kappa[i] = ds > 1 ? (theta[b] - theta[a]) / ds : 0;
        }
        kappa = RoadPlanner.MovingAverage(path, kappa, 40);
        var res = new double[n];
        int curves = 0;
        bool inCurve = false;
        for (int i = 0; i < n; i++)
        {
            double c = -kappa[i] * MinRadius * max;
            c = Math.Clamp(c, -max, max);
            double a = Math.Abs(c);
            res[i] = Math.Sign(c) * Math.Max(0, a - Dead) * max / Math.Max(1e-6, max - Dead);
            if (i >= p.CoreFrom && i <= p.CoreTo)
            {
                bool now = Math.Abs(res[i]) > 0.02;
                if (now && !inCurve) curves++;
                inCurve = now;
            }
        }
        p.AutoCurves = curves;
        return res;
    }

    /// Querneigung: glatte Kurve durch die Zielwerte der Markierungen (unveränderte behalten ihren alten Wert), davor und danach
    /// läuft sie über höchstens CrossRun Meter aus; an Kreuzungen läuft sie aus (die kreuzende Straße bleibt eben)
    static void CrossProfile(ReprofilePlan p, ReprofileOptions o)
    {
        var path = p.Path;
        int n = path.Count;
        p.CrossDelta = new double[n];
        p.MaxCross = 0;
        if (!p.AnyCross) return;
        double sFirst = path[p.CoreFrom].Dist, sLast = path[p.CoreTo].Dist;
        if (o.AutoCross)
        {
            // Automatik: Querneigung aus der Krümmung der Straße; von Hand eingegebene Werte an Markierungen ziehen sie dort hin
            // (Einfluss 40 m weit, weich auslaufend)
            var auto = AutoBanking(p, o);
            for (int i = p.CoreFrom; i <= p.CoreTo; i++)
            {
                double c = auto[i];
                for (int k = 0; k < p.MarkerIndex.Length; k++)
                {
                    if (!p.MarkerCrossManual[k]) continue;
                    double diff = p.MarkerCrossNew[k] - auto[p.MarkerIndex[k]];
                    c += diff * Fade5(Math.Abs(path[i].Dist - path[p.MarkerIndex[k]].Dist) / 40);
                }
                p.CrossDelta[i] = c - p.OldCross[i];
            }
        }
        else
        {
            var knots = new List<(double S, double V)>();
            for (int k = 0; k < p.MarkerIndex.Length; k++)
            {
                double s = path[p.MarkerIndex[k]].Dist;
                if (knots.Count > 0 && s - knots[^1].S < 1.0) continue;
                knots.Add((s, o.Even ? p.MarkerCrossNew[k] : p.MarkerCrossNew[k] - p.MarkerCrossOld[k]));
            }
            var curve = new Pchip(knots);
            for (int i = 0; i < n; i++)
            {
                double s = path[i].Dist;
                if (s >= sFirst && s <= sLast)
                {
                    double c = curve.At(s);
                    p.CrossDelta[i] = o.Even ? c - p.OldCross[i] : c;
                }
            }
        }
        double endIn = p.CrossDelta[p.CoreFrom], endOut = p.CrossDelta[p.CoreTo];
        double lenIn = Math.Max(1e-6, Math.Min(CrossRun, sFirst)), lenOut = Math.Max(1e-6, Math.Min(CrossRun, path[n - 1].Dist - sLast));
        for (int i = 0; i < p.CoreFrom; i++) p.CrossDelta[i] = endIn * Fade((sFirst - path[i].Dist) / lenIn);
        for (int i = p.CoreTo + 1; i < n; i++) p.CrossDelta[i] = endOut * Fade((path[i].Dist - sLast) / lenOut);
        foreach (var j in p.Junctions)
            for (int i = 0; i < n; i++)
            {
                double f = Math.Clamp((Math.Abs(path[i].Dist - j.Dist) - j.PinHalf) / 12, 0, 1);
                p.CrossDelta[i] *= f * f * (3 - 2 * f);
            }
        for (int i = 0; i < n; i++) p.MaxCross = Math.Max(p.MaxCross, Math.Abs(p.OldCross[i] + p.CrossDelta[i]));
    }

    /// Böschungsbreite je Wegpunkt: je größer die Änderung, desto breiter geht sie ins Gelände über (5 m je Meter, 8–40 m),
    /// dann gleitendes Maximum ±25 m, damit der Rand ruhig verläuft
    static double[] SlopeWidths(ReprofilePlan p)
    {
        int n = p.Path.Count;
        var raw = new double[n];
        for (int i = 0; i < n; i++) raw[i] = Math.Clamp((Math.Abs(p.Delta[i]) + Math.Abs(p.CrossDelta[i]) * p.Edge) * 5, 8, 40);
        var res = new double[n];
        int hi = 0;
        var q = new LinkedList<int>();
        for (int k = 0; k < n; k++)
        {
            while (hi < n && p.Path[hi].Dist <= p.Path[k].Dist + 25)
            {
                while (q.Count > 0 && raw[q.Last.Value] <= raw[hi]) q.RemoveLast();
                q.AddLast(hi++);
            }
            while (p.Path[q.First.Value].Dist < p.Path[k].Dist - 25) q.RemoveFirst();
            res[k] = raw[q.First.Value];
        }
        return res;
    }

    static void Grade(ReprofilePlan p)
    {
        var path = p.Path;
        p.MaxGrade = 0;
        // über 10 m gemessen: die alten Höhen liegen im Meterraster, auf kürzeren Strecken wären die Stufen als Steigung zu sehen
        for (int i = 40; i < path.Count; i += 4)
        {
            double g = Math.Abs(p.New[i] - p.New[i - 40]) / Math.Max(1e-6, path[i].Dist - path[i - 40].Dist);
            if (g > p.MaxGrade) { p.MaxGrade = g; p.MaxGradeAt = path[i].Dist; }
        }
    }

    static void Obstacles(WorldData w, ReprofilePlan p)
    {
        var path = p.Path;
        double reach = p.Edge + 42;
        double x0 = path.Min(q => q.X) - reach, x1 = path.Max(q => q.X) + reach, z0 = path.Min(q => q.Z) - reach, z1 = path.Max(q => q.Z) + reach;
        int count = 0;
        foreach (var pf in w.Prefabs)
        {
            if (pf.IsTile || pf.Def == null || pf.Def.Kind == PrefabKind.Part) continue;
            if (pf.X > x1 || pf.X + pf.SizeX < x0 || pf.Z > z1 || pf.Z + pf.SizeZ < z0) continue;
            for (int k = 0; k < path.Count; k += 8)
            {
                if (Math.Abs(p.Delta[k]) < 0.3) continue;
                double dx = Math.Max(Math.Max(pf.X - path[k].X, 0), path[k].X - (pf.X + pf.SizeX));
                double dz = Math.Max(Math.Max(pf.Z - path[k].Z, 0), path[k].Z - (pf.Z + pf.SizeZ));
                if (Math.Sqrt(dx * dx + dz * dz) < p.Edge + p.Blend[k]) { count++; break; }
            }
        }
        if (count > 0) p.Warnings.Add(Loc.F("{0} building(s) are close to the changed road. The ground under a building stays as it is, so a step can remain there.", count));
    }

    // ---------- Kreuzungen finden ----------

    /// Wege, die die Strecke kreuzen oder einmünden: knapp außerhalb des Belags auf beiden Seiten nach Belag suchen.
    /// Stücke auf beiden Seiten auf gleicher Höhe der Strecke = Kreuzung, einseitig = Einmündung
    static List<JunctionPlan> FindJunctions(RoadScan scan, List<PathPoint> path, double roadHalf)
    {
        var res = new List<JunctionPlan>();
        int n = path.Count;
        double probe = roadHalf + 2.5, length = path[n - 1].Dist, endGap = 6;
        var runs = new List<(int side, int a, int b)>();
        foreach (int side in new[] { 1, -1 })
        {
            int start = -1, lastOn = -1, gap = 0;
            for (int k = 0; k < n; k += 4)
            {
                bool on = false;
                if (path[k].Dist > endGap && length - path[k].Dist > endGap)
                {
                    var (nx, nz) = RoadPlanner.Normal(path, k);
                    on = scan.Road(path[k].X + nx * probe * side, path[k].Z + nz * probe * side);
                }
                if (on) { if (start < 0) start = k; lastOn = k; gap = 0; }
                else if (start >= 0 && ++gap >= 2) { runs.Add((side, start, lastOn)); start = -1; gap = 0; }
            }
            if (start >= 0) runs.Add((side, start, lastOn));
        }
        runs.RemoveAll(u => path[u.b].Dist - path[u.a].Dist > 50);   // Straße läuft daneben her
        var used = new HashSet<int>();
        int Mid((int side, int a, int b) u) => (u.a + u.b) / 2;
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].side != 1) continue;
            int best = -1;
            double bd = 25;
            for (int j = 0; j < runs.Count; j++)
            {
                if (runs[j].side != -1 || used.Contains(j)) continue;
                double d = Math.Abs(path[Mid(runs[j])].Dist - path[Mid(runs[i])].Dist);
                if (d < bd) { bd = d; best = j; }
            }
            var members = new List<(int side, int a, int b)> { runs[i] };
            used.Add(i);
            if (best >= 0) { used.Add(best); members.Add(runs[best]); }
            res.Add(MakeJunction(scan, path, members, probe, roadHalf));
        }
        for (int i = 0; i < runs.Count; i++)
            if (!used.Contains(i)) res.Add(MakeJunction(scan, path, new List<(int, int, int)> { runs[i] }, probe, roadHalf));
        res.RemoveAll(j => j == null);
        res.Sort((x, y) => x.Dist.CompareTo(y.Dist));
        return res;
    }

    static JunctionPlan MakeJunction(RoadScan scan, List<PathPoint> path, List<(int side, int a, int b)> members, double probe, double roadHalf)
    {
        int mid = (int)members.Average(u => (u.a + u.b) / 2.0);
        var c = path[mid];
        var j = new JunctionPlan { Dist = c.Dist, Center = new Point(c.X, c.Z), Through = members.Count > 1 };
        foreach (var u in members)
        {
            int m = (u.a + u.b) / 2;
            var (nx, nz) = RoadPlanner.Normal(path, m);
            var normal = new Vector(nx * u.side, nz * u.side);
            var at = new Point(path[m].X + normal.X * probe, path[m].Z + normal.Y * probe);
            // Richtung des abzweigenden Wegs: Hauptachse der Belagpixel um die Mündung, ohne die bearbeitete Straße selbst
            double sx = 0, sz = 0, sxx = 0, szz = 0, sxz = 0;
            int cnt = 0;
            for (int dz = -7; dz <= 7; dz++)
                for (int dx = -7; dx <= 7; dx++)
                {
                    if (dx * dx + dz * dz > 49) continue;
                    var q = new Point(at.X + dx, at.Y + dz);
                    if (!scan.Road(q.X, q.Y)) continue;
                    double lateral = (q.X - path[m].X) * normal.X + (q.Y - path[m].Z) * normal.Y;
                    if (lateral < roadHalf + 1) continue;
                    sx += dx; sz += dz; sxx += dx * dx; szz += dz * dz; sxz += dx * dz; cnt++;
                }
            var dir = normal;
            if (cnt >= 8)
            {
                double mx = sx / cnt, mz = sz / cnt;
                double a = 0.5 * Math.Atan2(2 * (sxz / cnt - mx * mz), (sxx / cnt - mx * mx) - (szz / cnt - mz * mz));
                dir = new Vector(Math.Cos(a), Math.Sin(a));
                if (dir.X * normal.X + dir.Y * normal.Y < 0) dir = -dir;
                if (dir.X * normal.X + dir.Y * normal.Y < 0.3) dir = normal;   // Richtung unklar: quer zur Straße
            }
            var perp = new Vector(-dir.Y, dir.X);
            var (lo, hi) = RoadFollower.Run(scan, at, perp);
            double armWidth = Math.Clamp(hi - lo + 1, 3, 40);
            var ext = RoadFollower.Extend(scan, at, dir, 260, armWidth, true);
            if (ext.Points.Count == 0) continue;
            var arm = new BranchArm { Width = ext.Width, Stop = ext.Stop, Start = roadHalf + 5 };
            // ein Ast, der gleich in eine weitere Kreuzung läuft, hat keinen Platz für einen Übergang (Teil desselben Knotens): weglassen
            double reserve = ext.Points.Count * 2.0 + (at - j.Center).Length - arm.Start - (ext.Stop is "tile" or "junction" ? 6 : 0);
            if (ext.Stop == "junction" && reserve < 10) continue;
            arm.Points.Add(j.Center);
            arm.Points.Add(at);
            arm.Points.AddRange(ext.Points);
            double len = 0;
            for (int k = 1; k < arm.Points.Count; k++) len += (arm.Points[k] - arm.Points[k - 1]).Length;
            arm.Length = len;
            j.Arms.Add(arm);
            j.Kind = (byte)Math.Max(j.Kind, scan.W.RoadAt((int)Math.Floor(at.X), (int)Math.Floor(at.Y)));
        }
        if (j.Arms.Count == 0) return null;
        if (j.Kind == 0) j.Kind = WorldData.Gravel;
        return j;
    }

    // ---------- Anwenden ----------

    public sealed record Stats(int HeightPoints);

    /// Änderung in die Welt eintragen (nur Höhen; der Belag bleibt). Bei jeder Zelle gilt: Änderung der bearbeiteten Straße im
    /// Bereich voller Wirkung, daneben weich ausgeblendet; an den Kreuzungen übernimmt weich die Änderung der kreuzenden Straße.
    public static Stats Apply(WorldData w, WorldEdits e, ReprofilePlan p, IProgress<string> progress)
    {
        if (p.Error != null || !p.Changes) return new Stats(0);
        var path = p.Path;
        int pn = path.Count;
        double edge = p.Edge;
        progress?.Report(Loc.T("Calculating distances to the road …"));

        // Arme der mitgezogenen Kreuzungen in 1-m-Schritten
        var arms = new List<(List<Point> Pts, double[] Along, double Delta, double Edge, double Bl, double Start, double Fade)>();
        foreach (var j in p.Junctions.Where(j => j.Followed))
            foreach (var a in j.Arms)
            {
                var pts = Resample(a.Points, 1);
                var along = new double[pts.Count];
                for (int k = 1; k < pts.Count; k++) along[k] = along[k - 1] + (pts[k] - pts[k - 1]).Length;
                arms.Add((pts, along, j.Delta, a.Width / 2 + 1, Math.Clamp(Math.Abs(j.Delta) * 6, 8, 45), a.Start, a.Fade));
            }

        // Raster um alles
        double bx0 = path.Min(q => q.X), bx1 = path.Max(q => q.X), bz0 = path.Min(q => q.Z), bz1 = path.Max(q => q.Z);
        double reach = edge + 40 + 2;
        foreach (var a in arms)
        {
            double r = a.Edge + a.Bl + 2;
            bx0 = Math.Min(bx0, a.Pts.Min(q => q.X) - (r - reach)); bx1 = Math.Max(bx1, a.Pts.Max(q => q.X) + (r - reach));
            bz0 = Math.Min(bz0, a.Pts.Min(q => q.Y) - (r - reach)); bz1 = Math.Max(bz1, a.Pts.Max(q => q.Y) + (r - reach));
        }
        int gx0 = (int)Math.Floor(bx0 - reach) - 1, gz0 = (int)Math.Floor(bz0 - reach) - 1;
        int gw = (int)Math.Ceiling(bx1 + reach) + 2 - gx0, gh = (int)Math.Ceiling(bz1 + reach) + 2 - gz0;
        var gd = new float[gw * gh];
        var gi = new int[gw * gh];
        Array.Fill(gd, float.MaxValue);
        Array.Fill(gi, -1);
        var ids = new List<int>();
        for (int k = 0; k < pn; k += 2) ids.Add(k);
        if (ids[^1] != pn - 1) ids.Add(pn - 1);
        int rad = (int)Math.Ceiling(edge + 40 + 2) + 1;
        foreach (int k in ids)
        {
            double px = path[k].X, pz = path[k].Z;
            int cx = (int)Math.Floor(px) - gx0, cz = (int)Math.Floor(pz) - gz0;
            for (int dz = -rad; dz <= rad; dz++)
            {
                int gz = cz + dz;
                if (gz < 0 || gz >= gh) continue;
                for (int dx = -rad; dx <= rad; dx++)
                {
                    int gx = cx + dx;
                    if (gx < 0 || gx >= gw) continue;
                    double ex = gx0 + gx + 0.5 - px, ez = gz0 + gz + 0.5 - pz;
                    float d = (float)Math.Sqrt(ex * ex + ez * ez);
                    int g = gz * gw + gx;
                    if (d < gd[g]) { gd[g] = d; gi[g] = k; }
                }
            }
        }

        // Änderung der kreuzenden Straßen (größere Wirkung gewinnt)
        var db = new float[gw * gh];
        var armCore = new bool[gw * gh];   // Zellen im Belagbereich eines Arms
        foreach (var a in arms)
        {
            int an = a.Pts.Count;
            int ar = (int)Math.Ceiling(a.Edge + a.Bl) + 2;
            // nur das Rechteck um den Arm (spart Speicher bei langen Strecken mit vielen Kreuzungen)
            int ax0 = Math.Max(0, (int)Math.Floor(a.Pts.Min(q => q.X)) - gx0 - ar), ax1 = Math.Min(gw - 1, (int)Math.Floor(a.Pts.Max(q => q.X)) - gx0 + ar);
            int az0 = Math.Max(0, (int)Math.Floor(a.Pts.Min(q => q.Y)) - gz0 - ar), az1 = Math.Min(gh - 1, (int)Math.Floor(a.Pts.Max(q => q.Y)) - gz0 + ar);
            int lw = ax1 - ax0 + 1, lh = az1 - az0 + 1;
            if (lw <= 0 || lh <= 0) continue;
            var ad = new float[lw * lh];
            var ai = new int[lw * lh];
            Array.Fill(ad, float.MaxValue);
            Array.Fill(ai, -1);
            for (int k = 0; k < an; k++)
            {
                int cx = (int)Math.Floor(a.Pts[k].X) - gx0, cz = (int)Math.Floor(a.Pts[k].Y) - gz0;
                for (int dz = -ar; dz <= ar; dz++)
                {
                    int gz = cz + dz;
                    if (gz < az0 || gz > az1) continue;
                    for (int dx = -ar; dx <= ar; dx++)
                    {
                        int gx = cx + dx;
                        if (gx < ax0 || gx > ax1) continue;
                        double ex = gx0 + gx + 0.5 - a.Pts[k].X, ez = gz0 + gz + 0.5 - a.Pts[k].Y;
                        float d = (float)Math.Sqrt(ex * ex + ez * ez);
                        int l = (gz - az0) * lw + gx - ax0;
                        if (d < ad[l]) { ad[l] = d; ai[l] = k; }
                    }
                }
            }
            for (int gz = az0; gz <= az1; gz++)
                for (int gx = ax0; gx <= ax1; gx++)
                {
                    int g = gz * gw + gx, l = (gz - az0) * lw + gx - ax0, k = ai[l];
                    if (k < 0) continue;
                    double d = ad[l];
                    if (d > a.Edge + a.Bl) continue;
                    int x = gx0 + gx, z = gz0 + gz;
                    if (!w.Contains(x, z)) continue;
                    if (k == an - 1 && d > a.Edge) continue;   // hinter dem Ende des Arms
                    double u = d <= a.Edge ? 1 : 1 - (d - a.Edge) / a.Bl;
                    u = u * u * (3 - 2 * u);
                    if (d <= a.Edge) armCore[g] = true;
                    double along = a.Along[k];
                    double value = a.Delta * (along <= a.Start ? 1 : Fade5((along - a.Start) / a.Fade));
                    double v = value * u;
                    if (Math.Abs(v) > Math.Abs(db[g])) db[g] = (float)v;
                }
        }

        // Bereiche, die unverändert bleiben: Stadt-Kacheln und Grundflächen von Gebäuden
        var keep = new bool[gw * gh];
        foreach (var pf in w.Prefabs)
        {
            if (pf.Def == null || pf.Def.Kind == PrefabKind.Part && !pf.Name.Contains("driveway", StringComparison.OrdinalIgnoreCase)) continue;
            int margin = pf.IsTile ? 0 : 1;
            int x0 = Math.Max(gx0, pf.X - margin), x1 = Math.Min(gx0 + gw - 1, pf.X + pf.SizeX - 1 + margin);
            int z0 = Math.Max(gz0, pf.Z - margin), z1 = Math.Min(gz0 + gh - 1, pf.Z + pf.SizeZ - 1 + margin);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++) keep[(z - gz0) * gw + x - gx0] = true;
        }

        // Schutz: Straßen, die weder zur bearbeiteten Strecke noch zu einem Arm gehören, sowie Kacheln und Gebäude bleiben, wie sie sind.
        // Weich ausgeblendet (Mittel über 7 × 7 m), damit neben einem dünnen Belagstreifen oder einer Hauswand keine Stufe entsteht.
        var protect = new float[gw * gh];
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx, x = gx0 + gx, z = gz0 + gz;
                if (keep[g]) { protect[g] = 1; continue; }
                if (!w.Contains(x, z) || w.RoadMask[w.Index(x, z)] == 0) continue;
                // Belag unmittelbar neben der Strecke (bis 12 m) gehört zur Kreuzung (Einmündungen, Parallelstücke) und folgt weich mit
                if (!armCore[g] && !(gi[g] >= 0 && gd[g] <= edge + 12)) protect[g] = 1;
            }
        protect = Blur(protect, gw, gh, 3);

        progress?.Report(Loc.T("Changing the heights …"));
        double sx = path[Math.Min(4, pn - 1)].X - path[0].X, sz = path[Math.Min(4, pn - 1)].Z - path[0].Z;
        double tex = path[pn - 1].X - path[Math.Max(0, pn - 5)].X, tez = path[pn - 1].Z - path[Math.Max(0, pn - 5)].Z;
        double sl = Math.Max(1e-6, Math.Sqrt(sx * sx + sz * sz)), el = Math.Max(1e-6, Math.Sqrt(tex * tex + tez * tez));
        int changed = 0;
        var df = new float[gw * gh];   // Höhenänderung je Zelle (m); erst danach geglättet und eingetragen
        var soft = new bool[gw * gh];  // Zellen im Böschungsbereich (nicht im Belagbereich der Strecke oder eines Astes)
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx, k = gi[g];
                int x = gx0 + gx, z = gz0 + gz;
                if (!w.Contains(x, z) || keep[g]) continue;
                bool mainCore = k >= 0 && gd[g] <= edge;
                double um = 0, dm = 0;
                if (k >= 0)
                {
                    double d = gd[g], bl = p.Blend[k];
                    if (d <= edge + bl)
                    {
                        um = d <= edge ? 1 : 1 - (d - edge) / bl;
                        um = um * um * (3 - 2 * um);
                        // hinter den Enden der Strecke geht die Straße weiter: dort nichts ändern
                        bool beyond = k == pn - 1 && ((x + 0.5 - path[k].X) * tex + (z + 0.5 - path[k].Z) * tez) / el > 0.5
                                      || k == 0 && ((x + 0.5 - path[0].X) * sx + (z + 0.5 - path[0].Z) * sz) / sl < -0.5;
                        if (beyond) um = 0;
                        dm = p.Delta[k];
                        // Querneigung: Änderung × seitlicher Abstand zur Mitte (höchstens bis zum Rand des Belags), + = links höher
                        if (p.CrossDelta[k] != 0)
                        {
                            var (nx, nz) = RoadPlanner.Normal(path, k);
                            dm += p.CrossDelta[k] * Math.Clamp((x + 0.5 - path[k].X) * nx + (z + 0.5 - path[k].Z) * nz, -edge, edge);
                        }
                    }
                }
                double free = 1 - protect[g];   // 0 = geschützt (Kachel, Gebäude, fremde Straße)
                double delta = um * dm * (mainCore ? 1 : free) + (1 - um) * db[g] * (armCore[g] ? 1 : free);
                df[g] = (float)delta;
                soft[g] = !mainCore && !armCore[g];
            }
        // In der Böschung die Änderung über 7 × 7 m glätten: wo die nächsten Wegpunkte einer engen Kurve weit auseinander liegen, entsteht
        // sonst eine Naht (Sprung der Änderung entlang einer Linie)
        var smooth = Blur(df, gw, gh, 3);
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx, x = gx0 + gx, z = gz0 + gz;
                if (!w.Contains(x, z) || keep[g]) continue;
                double delta = soft[g] ? smooth[g] : df[g];
                if (Math.Abs(delta) < 1.0 / 512) continue;
                int hi = w.Index(x, z);
                var nv = (ushort)Math.Clamp(Math.Round(w.Heights[hi] + delta * 256), 0, 65535);
                if (nv != w.Heights[hi]) { e.SetHeight(hi, nv); changed++; }
            }
        return new Stats(changed);
    }

    // Mittel über (2r+1) × (2r+1) Zellen, in zwei Durchgängen
    static float[] Blur(float[] src, int gw, int gh, int r)
    {
        var tmp = new float[src.Length];
        var res = new float[src.Length];
        float norm = 1f / (2 * r + 1);
        for (int z = 0; z < gh; z++)
            for (int x = 0; x < gw; x++)
            {
                float s = 0;
                for (int k = -r; k <= r; k++) { int xx = x + k; if (xx >= 0 && xx < gw) s += src[z * gw + xx]; }
                tmp[z * gw + x] = s * norm;
            }
        for (int z = 0; z < gh; z++)
            for (int x = 0; x < gw; x++)
            {
                float s = 0;
                for (int k = -r; k <= r; k++) { int zz = z + k; if (zz >= 0 && zz < gh) s += tmp[zz * gw + x]; }
                res[z * gw + x] = s * norm;
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

    // ---------- glatte Kurve durch Knoten (monotone kubische Hermite-Interpolation, ohne Überschwingen) ----------

    sealed class Pchip
    {
        readonly double[] x, y, m;

        public Pchip(List<(double S, double V)> k)
        {
            int n = k.Count;
            x = k.Select(q => q.S).ToArray();
            y = k.Select(q => q.V).ToArray();
            m = new double[n];
            if (n < 2) return;
            var h = new double[n - 1];
            var d = new double[n - 1];
            for (int i = 0; i < n - 1; i++) { h[i] = x[i + 1] - x[i]; d[i] = (y[i + 1] - y[i]) / h[i]; }
            if (n == 2) { m[0] = m[1] = d[0]; return; }
            for (int i = 1; i < n - 1; i++)
            {
                if (d[i - 1] * d[i] <= 0) { m[i] = 0; continue; }
                double w1 = 2 * h[i] + h[i - 1], w2 = h[i] + 2 * h[i - 1];
                m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
            }
            // an den Enden die Steigung des ersten bzw. letzten Abschnitts (die übliche Dreipunkt-Formel würde dort steiler anfangen)
            m[0] = d[0];
            m[n - 1] = d[n - 2];
        }

        public double At(double s)
        {
            int n = x.Length;
            if (n == 1) return y[0];
            if (s <= x[0]) return y[0];
            if (s >= x[n - 1]) return y[n - 1];
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (x[mid] <= s) lo = mid; else hi = mid; }
            double h = x[hi] - x[lo], t = (s - x[lo]) / h, t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * y[lo] + (t3 - 2 * t2 + t) * h * m[lo] + (-2 * t3 + 3 * t2) * y[hi] + (t3 - t2) * h * m[hi];
        }
    }
}
