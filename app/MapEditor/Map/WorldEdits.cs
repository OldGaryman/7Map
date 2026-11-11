using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MapEditor.Map;

/// Ein Bearbeitungsschritt (Straße, Entfernen, Radiergummi): alte Werte zum Zurücknehmen und der geänderte Bereich.
public sealed class EditStep
{
    public string Title { get; init; }
    /// ausführliche Beschreibung für die Sicherung (Punkte, Einstellungen)
    public string Detail { get; init; }
    /// Ergebnis für die Bearbeitungsreihenfolge (zweite Zeile im Eintrag, z. B. ausgesparte Gebäude); null = keins
    public string Note { get; set; }
    internal readonly List<(int i, ushort v)> Heights = new();
    internal readonly List<(int x, int z, uint v)> Splat = new();
    internal readonly List<(int i, byte v)> Roads = new();
    /// Biom je Pixel von biomes.png (Index wie WorldData.Biomes, alter Wert)
    internal readonly List<(int i, byte v)> Biomes = new();
    /// Wasser je Meter (Index wie WorldData.WaterMask, alter Blauwert aus splat4.png)
    internal readonly List<(int i, byte v)> Water = new();
    /// durch geänderten Boden nachgezogene Wasserpixel (SyncWater): neu mit Wasser bedeckt / trocken gefallen
    public int WaterAdded, WaterRemoved;
    /// Der Schritt setzt das Wasser selbst (See, Fluss, Wasser entfernen): kein automatisches Nachziehen (WorldEdits.SyncWater)
    internal bool NoWaterSync;
    internal bool TestSpawnChanged;
    internal SpawnPoint OldTestSpawn;
    /// Gebäude hinzugefügt, verschoben oder entfernt: je Änderung, wie sie zurückgenommen wird (rückwärts ausführen)
    internal readonly List<Action> PrefabUndo = new();
    /// Rechnet den Schritt noch einmal (für Wiederholen und nach dem Löschen eines früheren Eintrags)
    public Action<WorldEdits> Replay { get; internal set; }
    /// geänderter Bereich in Weltkoordinaten (Meter)
    public int X0 = int.MaxValue, Z0 = int.MaxValue, X1 = int.MinValue, Z1 = int.MinValue;
    public bool IsEmpty => Heights.Count == 0 && Splat.Count == 0 && Roads.Count == 0 && Biomes.Count == 0 && Water.Count == 0 && !TestSpawnChanged && PrefabUndo.Count == 0;
    public bool HasArea => X1 >= X0;

    internal void Touch(int x, int z)
    {
        if (x < X0) X0 = x;
        if (x > X1) X1 = x;
        if (z < Z0) Z0 = z;
        if (z > Z1) Z1 = z;
    }
}

/// Änderungen an der Welt zuerst nur im Programm (Wunsch des Nutzers): Höhen und Straßenmaske direkt in WorldData,
/// Belag in einer Kopie von splat3.png im Speicher (wird erst beim ersten Malen geladen). „Speichern“ legt eine Sicherung
/// an und schreibt dtm.raw, splat3.png und spawnpoints.xml; bis dahin lässt sich jeder Schritt einzeln zurücknehmen.
public sealed class WorldEdits
{
    readonly WorldData w;
    byte[] splat;   // BGRA wie splat3.png, Zeile 0 = Norden
    readonly List<EditStep> steps = new();
    EditStep current;

    /// Belag in splat3.png (BGRA als Zahl): A255 R255 = Asphalt, A255 G255 = Schotter, 0 = leer
    public const uint AsphaltPixel = 0xFFFF0000, GravelPixel = 0xFF00FF00, EmptyPixel = 0;

    public WorldEdits(WorldData world) => w = world;

    readonly List<EditStep> undone = new();   // zurückgenommen, für „Wiederholen“ (der nächste zuletzt)

    public IReadOnlyList<EditStep> Steps => steps;
    /// zurückgenommene Schritte in der Reihenfolge, in der „Wiederholen“ sie zurückholt
    public IEnumerable<EditStep> Undone => Enumerable.Reverse(undone);
    public bool HasChanges => steps.Count > 0;
    public bool CanRedo => undone.Count > 0;

    /// Neuen Schritt ausführen (op ändert die Welt über diese WorldEdits); ein neuer Schritt verwirft „Wiederholen“
    public EditStep Run(string title, string detail, Action<WorldEdits> op)
    {
        var s = RunCore(title, detail, op);
        if (!s.IsEmpty) undone.Clear();
        return s;
    }

    EditStep RunCore(string title, string detail, Action<WorldEdits> op)
    {
        Begin(title, detail);
        try { op(this); if (!current.NoWaterSync) SyncWater(); }   // jeder Schritt: das Wasser folgt dem geänderten Boden
        catch { Cancel(); throw; }
        var s = Commit();
        s.Replay = op;
        return s;
    }

    /// Zuletzt zurückgenommenen Schritt noch einmal ausführen
    public EditStep Redo()
    {
        if (undone.Count == 0) return null;
        var s = undone[^1];
        undone.RemoveAt(undone.Count - 1);
        var r = RunCore(s.Title, s.Detail, s.Replay);
        r.Note = s.Note;
        return r;
    }

    /// Eintrag der Bearbeitungsliste löschen (index: erst die aktiven Schritte, dann die zurückgenommenen wie Undone).
    /// Bei einem aktiven Schritt werden die späteren zurückgenommen und danach neu gerechnet. Liefert alle betroffenen
    /// Schritte (für den Bereich, der neu gezeichnet werden muss).
    public List<EditStep> Delete(int index)
    {
        var touched = new List<EditStep>();
        if (index >= steps.Count)
        {
            int j = undone.Count - 1 - (index - steps.Count);
            if (j >= 0) undone.RemoveAt(j);
            return touched;
        }
        var later = new List<EditStep>();
        while (steps.Count > index + 1)
        {
            var s = steps[^1];
            steps.RemoveAt(steps.Count - 1);
            Revert(s);
            later.Insert(0, s);
            touched.Add(s);
        }
        var del = steps[index];
        steps.RemoveAt(index);
        Revert(del);
        touched.Add(del);
        foreach (var s in later)
        {
            var r = RunCore(s.Title, s.Detail, s.Replay);
            r.Note = s.Note;
            touched.Add(r);
        }
        return touched;
    }

    /// Schritte ab Nummer count endgültig zurücknehmen (Abbrechen des Radiergummis), ohne sie für „Wiederholen“ zu merken
    public List<EditStep> DiscardFrom(int count)
    {
        var touched = new List<EditStep>();
        while (steps.Count > count)
        {
            var s = steps[^1];
            steps.RemoveAt(steps.Count - 1);
            Revert(s);
            touched.Add(s);
        }
        return touched;
    }

    public EditStep Begin(string title, string detail)
    {
        if (current != null) throw new InvalidOperationException(Loc.T("An edit step is already running."));
        return current = new EditStep { Title = title, Detail = detail };
    }

    /// Schritt abschließen; ein Schritt ohne Änderung wird verworfen
    public EditStep Commit()
    {
        var s = current;
        current = null;
        if (s != null && !s.IsEmpty) steps.Add(s);
        return s;
    }

    /// Schritt abbrechen (z. B. nach einem Fehler): bisherige Änderungen zurücknehmen
    public void Cancel()
    {
        if (current == null) return;
        Revert(current);
        current = null;
    }

    /// Letzten Schritt zurücknehmen; liefert ihn (für den Bereich, der neu gezeichnet werden muss)
    public EditStep Undo()
    {
        if (steps.Count == 0) return null;
        var s = steps[^1];
        steps.RemoveAt(steps.Count - 1);
        Revert(s);
        if (s.Replay != null) undone.Add(s);
        return s;
    }

    void Revert(EditStep s)
    {
        for (int k = s.Heights.Count - 1; k >= 0; k--) w.Heights[s.Heights[k].i] = s.Heights[k].v;
        for (int k = s.Roads.Count - 1; k >= 0; k--) w.RoadMask[s.Roads[k].i] = s.Roads[k].v;
        for (int k = s.Biomes.Count - 1; k >= 0; k--) w.Biomes[s.Biomes[k].i] = s.Biomes[k].v;
        for (int k = s.Water.Count - 1; k >= 0; k--) w.WaterMask[s.Water[k].i] = s.Water[k].v;
        for (int k = s.Splat.Count - 1; k >= 0; k--) WriteSplat(s.Splat[k].x, s.Splat[k].z, s.Splat[k].v);
        if (s.TestSpawnChanged) { w.Spawns.RemoveAll(x => x.Test); if (s.OldTestSpawn != null) w.Spawns.Add(s.OldTestSpawn); }
        for (int k = s.PrefabUndo.Count - 1; k >= 0; k--) s.PrefabUndo[k]();
    }

    EditStep Cur => current ?? throw new InvalidOperationException(Loc.T("No edit step has been started."));

    /// Höhe setzen (Index wie dtm.raw, Wert = Meter · 256)
    public void SetHeight(int i, ushort v)
    {
        if (w.Heights[i] == v) return;
        var s = Cur;
        s.Heights.Add((i, w.Heights[i]));
        s.Touch(i % w.Size - w.Half, i / w.Size - w.Half);
        w.Heights[i] = v;
    }

    /// Biom unter dem Wasser setzen (Wunsch des Nutzers 6.11.2026: der Boden unter Wasser soll überall gleich sein): jedes Pixel von biomes.png
    /// (8 × 8 m), das in diesem Schritt Wasser bekommen hat und zur Hälfte oder mehr unter Wasser liegt, wird auf dieses Biom gesetzt.
    /// biome < 0 = nichts ändern. Das Biom bestimmt die Bodenschichten (Waldboden, Sandstein, Schnee …) und die Pflanzen.
    public int PaintWaterBiome(int biome)
    {
        if (biome < 0) return 0;
        var s = Cur;
        int size = w.Size, half = w.Half, bs = w.BiomeSize, m = size / bs;
        var pixels = new HashSet<int>();
        foreach (var (i, _) in s.Water)
        {
            if (w.WaterMask[i] == 0) continue;
            int x = i % size - half, z = i / size - half;
            pixels.Add(((half - 1 - z) * bs / size) * bs + (x + half) * bs / size);
        }
        int n = 0;
        foreach (int p in pixels)
        {
            if (w.Biomes[p] == biome) continue;
            int bx = p % bs, by = p / bs, wet = 0;
            int x0 = bx * m - half, z1 = half - 1 - by * m;
            for (int dz = 0; dz < m; dz++)
                for (int dx = 0; dx < m; dx++)
                    if (w.WaterMask[w.Index(x0 + dx, z1 - dz)] != 0) wet++;
            if (wet * 2 < m * m) continue;
            SetBiome(p, (byte)biome);
            n++;
        }
        return n;
    }

    /// Schritt, der das Wasser selbst setzt (See, Fluss, Wasser entfernen): SyncWater am Ende entfällt
    public void SkipWaterSync() => Cur.NoWaterSync = true;

    const double WaterMinDepth = -Lake.WetMargin;   // Boden bis 1 m (WetMargin) über dem Spiegel bekommt auch Wasser (siehe Lake.WetMargin)

    /// Wird nach jedem Schritt automatisch aufgerufen (RunCore). Wasser an den geänderten Boden anpassen (Wunsch des Nutzers 6.11.2026): Wurde der Boden unter Wasser so erhöht, dass er den Spiegel
    /// erreicht, verschwindet das Wasserpixel. Wurde Boden neben Wasser so weit abgesenkt, dass er unter dem Spiegel liegt, bekommt er das
    /// Wasser des Nachbarn (nur Zellen, deren Höhe in diesem Schritt geändert wurde; Straßen und Gebäude bleiben trocken). Gehört zum
    /// selben Schritt, also gemeinsam rückgängig zu machen.
    void SyncWater()
    {
        var s = Cur;
        if (s.Heights.Count == 0) return;
        int size = w.Size, half = w.Half;
        var changed = new HashSet<int>();
        foreach (var (i, _) in s.Heights) changed.Add(i);
        int removed = 0, added = 0;
        foreach (int i in changed)
        {
            int level = w.WaterMask[i];
            if (level != 0 && w.Heights[i] >= (level - WaterMinDepth) * 256.0) { SetWater(i, 0); removed++; }
        }
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        foreach (int i in changed)
        {
            int x = i % size - half, z = i / size - half;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
        }
        var rects = new List<(int x0, int z0, int x1, int z1)>();
        foreach (var b in w.Prefabs)
            if (b.X - 1 <= maxX && b.X + b.SizeX + 1 >= minX && b.Z - 1 <= maxZ && b.Z + b.SizeZ + 1 >= minZ)
                rects.Add((b.X - 1, b.Z - 1, b.X + b.SizeX, b.Z + b.SizeZ));
        bool Blocked(int i)
        {
            if (w.RoadMask[i] != 0) return true;
            int x = i % size - half, z = i / size - half;
            foreach (var r in rects) if (x >= r.x0 && x <= r.x1 && z >= r.z0 && z <= r.z1) return true;
            return false;
        }
        bool Inside(int i) { int cx = i % size, cz = i / size; return cx >= 3 && cz >= 3 && cx < size - 3 && cz < size - 3; }
        bool Under(int i, int level) => w.Heights[i] < (level - WaterMinDepth) * 256.0;
        int[] step4 = { 1, -1, size, -size };
        var queue = new Queue<int>();
        foreach (int i in changed)
        {
            if (w.WaterMask[i] != 0 || !Inside(i) || Blocked(i)) continue;
            foreach (int d in step4)
            {
                int level = w.WaterMask[i + d];
                if (level != 0 && Under(i, level)) { SetWater(i, (byte)level); added++; queue.Enqueue(i); break; }
            }
        }
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            int level = w.WaterMask[c];
            foreach (int d in step4)
            {
                int n = c + d;
                if (!changed.Contains(n) || w.WaterMask[n] != 0 || !Inside(n) || Blocked(n) || !Under(n, level)) continue;
                SetWater(n, (byte)level); added++; queue.Enqueue(n);
            }
        }
        s.WaterAdded += added; s.WaterRemoved += removed;
    }

    public void SetRoad(int i, byte kind)
    {
        if (w.RoadMask[i] == kind) return;
        var s = Cur;
        s.Roads.Add((i, w.RoadMask[i]));
        s.Touch(i % w.Size - w.Half, i / w.Size - w.Half);
        w.RoadMask[i] = kind;
    }

    /// Wasserspiegel eines Meters setzen (Blauwert in splat4.png, 0 = kein Wasser; Index wie dtm.raw)
    public void SetWater(int i, byte level)
    {
        if (w.WaterMask[i] == level) return;
        var s = Cur;
        s.Water.Add((i, w.WaterMask[i]));
        s.Touch(i % w.Size - w.Half, i / w.Size - w.Half);
        w.WaterMask[i] = level;
    }

    /// Biom eines Pixels von biomes.png setzen (Index by · BiomeSize + bx, Zeile 0 = Norden); Höhen bleiben unverändert
    public void SetBiome(int i, byte biome)
    {
        if (w.Biomes[i] == biome) return;
        var s = Cur;
        s.Biomes.Add((i, w.Biomes[i]));
        int m = w.Size / w.BiomeSize, bx = i % w.BiomeSize, by = i / w.BiomeSize;
        int x0 = bx * m - w.Half, z1 = w.Half - 1 - by * m;
        s.Touch(x0, z1 - m + 1);
        s.Touch(x0 + m - 1, z1);
        w.Biomes[i] = biome;
    }

    public uint SplatAt(int x, int z)
    {
        LoadSplat();
        long p = PixelIndex(x, z);
        return (uint)(splat[p] | splat[p + 1] << 8 | splat[p + 2] << 16 | splat[p + 3] << 24);
    }

    public void SetSplat(int x, int z, uint v)
    {
        uint old = SplatAt(x, z);
        if (old == v) return;
        var s = Cur;
        s.Splat.Add((x, z, old));
        s.Touch(x, z);
        WriteSplat(x, z, v);
    }

    /// Testpunkt am Anfang einer neuen Straße (gehört zum Straßen-Schritt und wird mit ihm gespeichert). Zurücknehmen stellt
    /// nur den vorherigen Testpunkt wieder her; von Hand gesetzte Spielpunkte (sofort gespeichert) bleiben unberührt.
    /// Gebäude hinten an prefabs.xml anhängen
    public void AddPrefab(PlacedPrefab p)
    {
        var s = Cur;
        ClearCitySurface(p);
        w.Prefabs.Add(p);
        TouchPrefab(s, p);
        s.PrefabUndo.Add(() => w.Prefabs.Remove(p));
    }

    /// Gebäude aus prefabs.xml entfernen (Zurücknehmen setzt es wieder an dieselbe Stelle der Liste)
    public void RemovePrefab(PlacedPrefab p)
    {
        int i = w.Prefabs.IndexOf(p);
        if (i < 0) return;
        var s = Cur;
        ClearCitySurface(p);
        w.Prefabs.RemoveAt(i);
        TouchPrefab(s, p);
        s.PrefabUndo.Add(() => w.Prefabs.Insert(Math.Min(i, w.Prefabs.Count), p));
    }

    /// Gebäude gegen ein anderes tauschen, an derselben Stelle der Liste (Bauplatz einer Stadt-Kachel)
    public void ReplacePrefab(PlacedPrefab old, PlacedPrefab p)
    {
        int i = w.Prefabs.IndexOf(old);
        if (i < 0) { AddPrefab(p); return; }
        var s = Cur;
        ClearCitySurface(old);
        ClearCitySurface(p);
        w.Prefabs[i] = p;
        TouchPrefab(s, old);
        TouchPrefab(s, p);
        s.PrefabUndo.Add(() => { int k = w.Prefabs.IndexOf(p); if (k >= 0) w.Prefabs[k] = old; });
    }

    /// Belag und Straßenausgänge einer neu gesetzten Stadt-Kachel eintragen (aus den Blöcken der Kachel; Zurücknehmen stellt alles her)
    public void AddTileSurface(PlacedPrefab t)
    {
        var local = w.TileSurface(t, out var exits);
        if (local == null) return;
        var s = Cur;
        var city = w.EnsureCityMask();
        var oldCity = new List<(int i, byte v)>();
        for (int oz = 0; oz < t.SizeZ; oz++)
            for (int ox = 0; ox < t.SizeX; ox++)
            {
                byte k = local[ox + oz * t.SizeX];
                int x = t.X + ox, z = t.Z + oz;
                if (k == 0 || !w.Contains(x, z)) continue;
                int i = w.Index(x, z);
                if (city[i] != k) { oldCity.Add((i, city[i])); city[i] = k; }
                if (k > w.RoadMask[i]) SetRoad(i, k);
            }
        var added = exits.Select(e => (e.X, e.Z, t)).ToList();
        w.TileExits.AddRange(added);
        s.Touch(t.X, t.Z);
        s.Touch(t.X + t.SizeX, t.Z + t.SizeZ);
        s.PrefabUndo.Add(() =>
        {
            foreach (var (i, v) in oldCity) city[i] = v;
            foreach (var e in added) w.TileExits.Remove(e);
        });
    }

    /// Belag und Straßenausgänge einer entfernten Stadt-Kachel austragen (auch die Parkplätze ihrer Gebäude, die das Spiel
    /// in der Kachel gemalt hat); Belag aus eigenem splat3.png bleibt, wo er allein den Wert bestimmt
    public void RemoveTileSurface(PlacedPrefab t)
    {
        var s = Cur;
        var city = w.CityMask;
        var oldCity = new List<(int i, byte v)>();
        if (city != null)
            for (int z = Math.Max(t.Z, -w.Half); z < Math.Min(t.Z + t.SizeZ, w.Half); z++)
                for (int x = Math.Max(t.X, -w.Half); x < Math.Min(t.X + t.SizeX, w.Half); x++)
                {
                    int i = w.Index(x, z);
                    if (city[i] == 0) continue;
                    if (w.RoadMask[i] == city[i]) SetRoad(i, 0);
                    oldCity.Add((i, city[i]));
                    city[i] = 0;
                }
        var removed = w.TileExits.Where(e => e.Tile == t).ToList();
        w.TileExits.RemoveAll(e => e.Tile == t);
        s.Touch(t.X, t.Z);
        s.Touch(t.X + t.SizeX, t.Z + t.SizeZ);
        s.PrefabUndo.Add(() =>
        {
            foreach (var (i, v) in oldCity) city[i] = v;
            w.TileExits.AddRange(removed);
        });
    }

    /// Lage, Höhe (y in prefabs.xml) oder Drehung ändern
    public void MovePrefab(PlacedPrefab p, int x, int y, int z, int rotation)
    {
        var s = Cur;
        var old = (p.X, p.Y, p.Z, p.Rotation);
        ClearCitySurface(p);
        TouchPrefab(s, p);
        (p.X, p.Y, p.Z, p.Rotation) = (x, y, z, rotation);
        ClearCitySurface(p);
        TouchPrefab(s, p);
        s.PrefabUndo.Add(() => (p.X, p.Y, p.Z, p.Rotation) = old);
    }

    // Belag aus dem letzten Spieltest (Parkplätze der Gebäude in Stadt-Kacheln) passt nach der Änderung nicht mehr: aus der
    // Straßenmaske nehmen, wo er allein den Wert bestimmt (Hinweis des Nutzers: nach Drehen/Löschen blieb er stehen).
    // Das Spiel malt ihn beim nächsten Test neu.
    void ClearCitySurface(PlacedPrefab p)
    {
        if (p.IsTile || p.Def == null || w.CityMask == null) return;
        for (int z = Math.Max(p.Z - 1, -w.Half); z <= Math.Min(p.Z + p.SizeZ, w.Half - 1); z++)
            for (int x = Math.Max(p.X - 1, -w.Half); x <= Math.Min(p.X + p.SizeX, w.Half - 1); x++)
            {
                int i = w.Index(x, z);
                if (w.CityMask[i] != 0 && w.RoadMask[i] == w.CityMask[i]) SetRoad(i, 0);
            }
    }

    static void TouchPrefab(EditStep s, PlacedPrefab p)
    {
        s.Touch(p.X, p.Z);
        s.Touch(p.X + p.SizeX, p.Z + p.SizeZ);
    }

    public void SetTestSpawn(SpawnPoint p)
    {
        var s = Cur;
        if (!s.TestSpawnChanged) { s.TestSpawnChanged = true; s.OldTestSpawn = w.TestSpawn; }
        w.Spawns.RemoveAll(x => x.Test);
        var c = p.Clone();
        c.Test = true;
        w.Spawns.Add(c);
    }

    void WriteSplat(int x, int z, uint v)
    {
        long p = PixelIndex(x, z);
        splat[p] = (byte)v; splat[p + 1] = (byte)(v >> 8); splat[p + 2] = (byte)(v >> 16); splat[p + 3] = (byte)(v >> 24);
    }

    long PixelIndex(int x, int z) => ((long)(w.Half - 1 - z) * w.Size + x + w.Half) * 4;

    void LoadSplat()
    {
        if (splat != null) return;
        var src = PngTools.Open(Path.Combine(w.Folder, "splat3.png"));
        if (src.PixelWidth != w.Size || src.PixelHeight != w.Size) throw new InvalidDataException(Loc.F("{0} does not have the map size {1}.", "splat3.png", w.Size));
        var buf = new byte[(long)w.Size * w.Size * 4];
        src.CopyPixels(buf, w.Size * 4, 0);
        splat = buf;
    }

    /// Alle Schritte in die Map-Dateien schreiben (vorher Sicherung). Liefert die geschriebenen Dateien.
    public List<string> Save(string gameWorld, IProgress<string> progress, out int fetched)
    {
        fetched = 0;
        var files = new List<string>();
        if (steps.Count == 0) return files;
        bool heights = steps.Any(s => s.Heights.Count > 0), belag = steps.Any(s => s.Splat.Count > 0), spawns = steps.Any(s => s.TestSpawnChanged);
        bool prefabs = steps.Any(s => s.PrefabUndo.Count > 0), biomes = steps.Any(s => s.Biomes.Count > 0);
        bool water = steps.Any(s => s.Water.Count > 0);

        // erst die Ergebnisse des letzten Tests holen, solange die Map-Dateien im Spiel noch zum Projekt passen
        progress.Report(Loc.T("Checking results of the last test …"));
        fetched = GameSync.FetchGenerated(w.Folder, gameWorld);
        progress.Report(Loc.T("Creating backup …"));
        string title = steps.Count == 1 ? steps[0].Title : Loc.F("{0} changes", steps.Count) + ": " + string.Join("; ", steps.Select(s => s.Title));
        Backups.Create(w, title + Environment.NewLine + string.Join(Environment.NewLine, steps.Select(s => s.Detail).Where(d => !string.IsNullOrEmpty(d))));

        if (heights)
        {
            progress.Report(Loc.F("Writing {0} …", "dtm.raw"));
            var bytes = new byte[(long)w.Size * w.Size * 2];
            Buffer.BlockCopy(w.Heights, 0, bytes, 0, bytes.Length);
            WriteReplace(Path.Combine(w.Folder, "dtm.raw"), f => File.WriteAllBytes(f, bytes));
            files.Add("dtm.raw");
        }
        if (belag)
        {
            progress.Report(Loc.F("Writing {0} …", "splat3.png"));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w.Size, w.Size, 96, 96, PixelFormats.Bgra32, null, splat, w.Size * 4)));
            WriteReplace(Path.Combine(w.Folder, "splat3.png"), f => { using var fs = File.Create(f); enc.Save(fs); });
            files.Add("splat3.png");
        }
        if (water)
        {
            // ganz aus der Wassermaske: Blau = Spiegel, Rot/Grün/Alpha 0 (so sind die Originaldateien aufgebaut)
            progress.Report(Loc.F("Writing {0} …", "splat4.png"));
            int size = w.Size;
            var px = new byte[(long)size * size * 4];
            for (int row = 0; row < size; row++)
            {
                int src = (size - 1 - row) * size;   // PNG Zeile 0 = Norden, Maske Zeile 0 = Süden
                for (int x = 0; x < size; x++) px[((long)row * size + x) * 4] = w.WaterMask[src + x];
            }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4)));
            WriteReplace(Path.Combine(w.Folder, "splat4.png"), f => { using var fs = File.Create(f); enc.Save(fs); });
            files.Add("splat4.png");
        }
        if (biomes)
        {
            // ganz aus den Biomen im Speicher: biomes.png enthält nur die fünf Farben des Spiels (geprüft), also bleiben
            // unveränderte Pixel gleich
            progress.Report(Loc.F("Writing {0} …", "biomes.png"));
            int n = w.BiomeSize;
            var px = new byte[n * n * 4];
            for (int i = 0; i < n * n; i++)
            {
                var (r, g, b) = BiomePalette.SourceColor(w.Biomes[i]);
                px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255;
            }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px, n * 4)));
            WriteReplace(Path.Combine(w.Folder, "biomes.png"), f => { using var fs = File.Create(f); enc.Save(fs); });
            files.Add("biomes.png");
        }
        if (prefabs)
        {
            progress.Report(Loc.F("Writing {0} …", "prefabs.xml"));
            WriteReplace(Path.Combine(w.Folder, "prefabs.xml"), w.WritePrefabs);
            files.Add("prefabs.xml");
        }
        if (spawns)
        {
            w.WriteTestSpawn();
            files.Add(WorldData.TestSpawnFile);
            files.Add(WorldData.SpawnFile);
        }
        steps.Clear();
        undone.Clear();
        splat = null;   // Speicher frei; beim nächsten Malen wird splat3.png neu gelesen
        return files;
    }

    // Erst in eine neue Datei schreiben, dann ersetzen: bricht etwas ab, bleibt die alte Datei heil
    static void WriteReplace(string file, Action<string> write)
    {
        string tmp = file + ".neu";
        write(tmp);
        File.Move(tmp, file, true);
    }
}
