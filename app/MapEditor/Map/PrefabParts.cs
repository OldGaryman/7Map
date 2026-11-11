namespace MapEditor.Map;

/// Teile eines Gebäudes (v. a. Zufahrten), wie sie das Spiel beim Erzeugen der Welt aus den „PartSpawn“-Markierungen setzt
/// (Wunsch des Nutzers: Zufahrten, damit Straßen anschließen können). An der Originalwelt geprüft (29.10.2026, 2.905 Teile):
/// Lage = Rechteck der Markierung wie das Gebäude gedreht, kleinste Ecke (2.464 genau); Drehung = Gebäude + Markierung + 2
/// (2.485); Höhe = Bodenhöhe des Gebäudes + YOffset des Teils, also y = Gebäude-y − Gebäude-YOffset + Teil-YOffset (2.619).
/// Mehrere Markierungen derselben Gruppe sind Alternativen: je Gruppe wird eine gesetzt (fest aus der Lage gewählt).
public static class PrefabParts
{
    /// Teile für das Gebäude p an seiner Lage; defOf liefert die Beschreibung eines Teils (null = unbekannt, wird übersprungen)
    public static List<PlacedPrefab> For(PlacedPrefab p, Func<string, PrefabDef> defOf)
    {
        var list = new List<PlacedPrefab>();
        if (p.Def?.Parts == null || p.Def.Parts.Count == 0) return list;
        int index = 0;
        var groups = p.Def.Parts.Select(m => (m, key: string.IsNullOrEmpty(m.Group) ? "#" + index++ : m.Group)).GroupBy(t => t.key);
        foreach (var g in groups)
        {
            var options = g.Select(t => t.m).ToList();
            var m = options[Pick(p, g.Key, options.Count)];
            // Wahrscheinlichkeit der Markierung (POIMarkerPartSpawnChance, z. B. vier Konzertschilder je 25 %): fest aus der Lage
            // gewürfelt – vorher standen alle vier Schilder übereinander (9.11.2026)
            // (nur bei Gruppen mit einer Markierung; bei Alternativen wird ohnehin eine gewählt)
            if (options.Count == 1 && m.Chance < 1 && Pick(p, g.Key + "/c", 1000) >= m.Chance * 1000) continue;
            string name = m.PartNames[Pick(p, g.Key + "/n", m.PartNames.Length)];
            var def = defOf(name);
            if (def == null) continue;
            var (ax, az) = PlacedPrefab.Rotate(m.X, m.Z, p.Rotation, p.Def.SizeX, p.Def.SizeZ);
            var (bx, bz) = PlacedPrefab.Rotate(m.X + m.SizeX, m.Z + m.SizeZ, p.Rotation, p.Def.SizeX, p.Def.SizeZ);
            list.Add(new PlacedPrefab
            {
                Name = name,
                Def = def,
                X = p.X + (int)Math.Round(Math.Min(ax, bx)),
                Z = p.Z + (int)Math.Round(Math.Min(az, bz)),
                Rotation = ((p.Rotation + m.Rotation + 2) % 4 + 4) % 4,
                Y = p.Y - p.Def.YOffset + def.YOffset,
            });
        }
        return list;
    }

    /// Teile, die zu einem Gebäude gehören: auf der Grundfläche bzw. höchstens 1 m daneben und in seinen Markierungen genannt
    /// (in Stadt-Kacheln stehen daneben auch Teile der Kachel wie Laternen und Hinterhöfe – die bleiben)
    public static List<PlacedPrefab> Attached(WorldData w, PlacedPrefab p)
    {
        if (p.Def == null || p.Def.Kind == PrefabKind.Part) return new List<PlacedPrefab>();
        var own = new HashSet<string>(p.Def.Parts.SelectMany(m => m.PartNames), StringComparer.OrdinalIgnoreCase);
        return w.Prefabs.Where(q => q != p && q.Def?.Kind == PrefabKind.Part && own.Contains(q.Name)
            && q.X < p.X + p.SizeX + 1 && q.X + q.SizeX > p.X - 1 && q.Z < p.Z + p.SizeZ + 1 && q.Z + q.SizeZ > p.Z - 1).ToList();
    }

    /// Zufahrt (Teil mit „driveway“ im Namen)
    public static bool IsDriveway(PlacedPrefab q) => q.Def?.Kind == PrefabKind.Part && q.Name.Contains("driveway", StringComparison.OrdinalIgnoreCase);

    /// Fangpunkt einer Zufahrt: Mitte der Kante, die am weitesten vom Gebäude weg liegt. In der Originalwelt (29.10.2026)
    /// endet die Straße bei 477 von 479 Zufahrten höchstens 2 m neben diesem Punkt (Gebäude wie diner_01 haben selbst keine RoadExit).
    public static (double X, double Z) DrivewayExit(PlacedPrefab q, PlacedPrefab owner)
    {
        double mx = q.X + q.SizeX / 2.0, mz = q.Z + q.SizeZ / 2.0;
        // Seite, an der die Zufahrt am weitesten über das Gebäude hinausragt (Süd, Nord, West, Ost)
        var sides = new[]
        {
            (Out: owner.Z - q.Z, P: (mx, (double)q.Z)),
            (Out: q.Z + q.SizeZ - (owner.Z + owner.SizeZ), P: (mx, (double)(q.Z + q.SizeZ))),
            (Out: owner.X - q.X, P: ((double)q.X, mz)),
            (Out: q.X + q.SizeX - (owner.X + owner.SizeX), P: ((double)(q.X + q.SizeX), mz)),
        };
        var best = sides.MaxBy(s => s.Out);
        if (best.Out > 0) return best.P;
        // ganz innerhalb: an der langen Kante der Zufahrt, weg von der Gebäudemitte
        double cx = owner.X + owner.SizeX / 2.0, cz = owner.Z + owner.SizeZ / 2.0;
        if (q.SizeZ <= q.SizeX) return mz < cz ? sides[0].P : sides[1].P;
        return mx < cx ? sides[2].P : sides[3].P;
    }

    /// Fangpunkt mit der Breite des Anschlusses in m (0 = unbekannt)
    public readonly record struct ExitPoint(double X, double Z, PlacedPrefab Owner, int Width);

    /// Breite einer Zufahrt am Fangpunkt: Länge der Kante, an der der Punkt liegt
    public static int DrivewayWidth(PlacedPrefab q, PlacedPrefab owner)
    {
        var (_, z) = DrivewayExit(q, owner);
        bool horizontal = Math.Abs(z - q.Z) < 0.01 || Math.Abs(z - (q.Z + q.SizeZ)) < 0.01;   // Süd-/Nordkante
        return horizontal ? q.SizeX : q.SizeZ;
    }

    /// Alle Fangpunkte eines Gebäudes mit Breite: RoadExit-Markierungen (Breite = längere Seite der Markierung), Außenkanten der
    /// Zufahrten (Breite = Kantenlänge) und Anschlüsse aus dem eigenen Belag des Gebäudes (PrefabSurface.EdgeExits, Wunsch des
    /// Nutzers 3.11.2026), soweit dort nicht schon ein Fangpunkt liegt (5 m). used: schon gezählte Zufahrten (eine kann an zwei
    /// Gebäuden liegen).
    public static List<ExitPoint> ExitsFor(WorldData w, PlacedPrefab pf, HashSet<PlacedPrefab> used = null, bool withDriveways = true)
    {
        var list = new List<ExitPoint>();
        if (pf.Def == null) return list;
        for (int i = 0; i < pf.Def.Exits.Count; i++)
        {
            var (rx, rz) = PlacedPrefab.Rotate(pf.Def.Exits[i].X, pf.Def.Exits[i].Z, pf.Rotation, pf.Def.SizeX, pf.Def.SizeZ);
            var (sx, sz) = i < pf.Def.ExitSizes.Count ? pf.Def.ExitSizes[i] : (0, 0);
            list.Add(new ExitPoint(pf.X + rx, pf.Z + rz, pf, Math.Max(sx, sz)));
        }
        if (pf.IsTile || pf.Def.Kind == PrefabKind.Part) return list;
        if (withDriveways && pf.Def.Parts.Count > 0)
            foreach (var q in Attached(w, pf))
                if (IsDriveway(q) && (used == null || used.Add(q)))
                {
                    var (x, z) = DrivewayExit(q, pf);
                    list.Add(new ExitPoint(x, z, pf, DrivewayWidth(q, pf)));
                }
        if (pf.Def.Kind == PrefabKind.Poi)
        {
            // bis das Einlesen aller Gebäude fertig ist (EdgeExitsWarmUp), zählen nur schon gelesene; danach wird bei Bedarf gelesen
            var edge = w.EdgeExitsOf(pf.Name, pf.Def, cachedOnly: !w.EdgeExitsReady);
            if (edge != null)
                foreach (var e in edge)
                {
                    var (rx, rz) = PlacedPrefab.Rotate(e.LX, e.LZ, pf.Rotation, pf.Def.SizeX, pf.Def.SizeZ);
                    double x = pf.X + rx, z = pf.Z + rz;
                    if (list.Any(l => Math.Abs(l.X - x) < 5 && Math.Abs(l.Z - z) < 5)) continue;
                    list.Add(new ExitPoint(x, z, pf, e.Width));
                }
        }
        return list;
    }

    /// Alle Fangpunkte der Welt mit Breite: Straßenausgänge der Stadt-Kacheln (aus ihren Blöcken, WorldData.TileExits) und
    /// die Fangpunkte jedes Gebäudes (ExitsFor)
    public static List<ExitPoint> AllExitsWithWidth(WorldData w, Func<PlacedPrefab, bool> near = null)
    {
        var list = new List<ExitPoint>();
        var used = new HashSet<PlacedPrefab>();
        foreach (var e in w.TileExits)
            if (near == null || near(e.Tile)) list.Add(new ExitPoint(e.X, e.Z, e.Tile, w.TileExitWidth(e.Tile, e.X, e.Z).Width));
        foreach (var pf in w.Prefabs)
        {
            if (near != null && !near(pf)) continue;
            list.AddRange(ExitsFor(w, pf, used));
        }
        return list;
    }

    /// Alle Fangpunkte der Welt: Ein-/Ausfahrten (RoadExit) jedes Gebäudes, die Außenkanten der Zufahrten (gehören zum Gebäude,
    /// damit die Straße dort dessen Bodenhöhe übernimmt) und die Anschlüsse aus dem Belag des Gebäudes
    public static List<(double X, double Z, PlacedPrefab Owner)> AllExits(WorldData w, Func<PlacedPrefab, bool> near = null)
        => AllExitsWithWidth(w, near).Select(e => (e.X, e.Z, e.Owner)).ToList();
    // feste Wahl aus Lage und Gruppe (string.GetHashCode wechselt bei jedem Programmstart, deshalb selbst gerechnet)
    static int Pick(PlacedPrefab p, string key, int count)
    {
        if (count <= 1) return 0;
        unchecked
        {
            uint h = (uint)(p.X * 73856093) ^ (uint)(p.Z * 19349663) ^ (uint)(p.Rotation * 83492791);
            foreach (char c in key) h = (h ^ c) * 16777619;
            return (int)(h % (uint)count);
        }
    }
}
