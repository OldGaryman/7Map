using System.Diagnostics;
using System.Security.Cryptography;

namespace MapEditor.Map;

/// Abgleich mit dem Spiel (wie tools/game_sync.ps1, ohne Mod): Map-Dateien in den Weltordner des Spiels kopieren
/// und die vom Spiel erzeugten Dateien dort löschen, damit es sie aus den neuen Dateien neu erzeugt.
public static class GameSync
{
    public static readonly string[] Sources = { "biomes.png", "dtm.raw", "main.ttw", "map_info.xml", "prefabs.xml", "radiation.png", "spawnpoints.xml", "splat3.png", "splat4.png" };
    public static readonly string[] Generated = { "dtm_processed.raw", "splat3_processed.png", "splat3_half.png", "splat4_processed.png", "splat4_half.png", "checksums.txt" };

    /// läuft das Spiel oder ein Dedicated Server? (der Server heißt „7DaysToDieServer“, Prüfbericht F5)
    public static bool GameRunning() => Process.GetProcessesByName("7DaysToDie").Length > 0 || Process.GetProcessesByName("7DaysToDieServer").Length > 0;

    // ---------- Fremde Änderungen an der Welt im Spiel erkennen (Wunsch des Nutzers 30.10.2026) ----------
    // Nach jedem Abgleich (Projekt angelegt, ins Spiel übernommen, aus dem Spiel übernommen oder abgelehnt) merkt sich das
    // Projekt in yamee_sync.json Größe, Zeit und Prüfsumme der Map-Dateien im Spiel. Ändert sie danach jemand anderes (Editor des
    // Spiels, eine andere YAMEE), erkennt die App das beim Öffnen. Spawnpunkte zählen nicht (verwaltet die App selbst).
    const string SyncFile = "yamee_sync.json";
    static readonly string[] Watched = Sources.Where(f => f != WorldData.SpawnFile).ToArray();

    public sealed record FileStamp(long Size, DateTime Time, string Hash);

    /// Stand der Map-Dateien im Spiel als „bekannt“ merken
    public static void RecordGameState(string project, string game)
    {
        if (game == null || !Directory.Exists(game)) return;
        var state = new Dictionary<string, FileStamp>();
        foreach (var f in Watched)
        {
            string g = Path.Combine(game, f);
            if (!File.Exists(g)) continue;
            var fi = new FileInfo(g);
            state[f] = new FileStamp(fi.Length, fi.LastWriteTimeUtc, Hash(g));
        }
        File.WriteAllText(Path.Combine(project, SyncFile), System.Text.Json.JsonSerializer.Serialize(state));
    }

    /// Map-Dateien, die im Spiel seit dem letzten Abgleich von jemand anderem geändert wurden (und anders sind als im Projekt).
    /// Ohne gemerkten Stand (ältere Projekte): nur Dateien, die im Spiel neuer sind als im Projekt.
    public static List<string> ChangedInGame(string project, string game)
    {
        var list = new List<string>();
        if (game == null || !Directory.Exists(game)) return list;
        Dictionary<string, FileStamp> known = null;
        string sync = Path.Combine(project, SyncFile);
        try { if (File.Exists(sync)) known = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, FileStamp>>(File.ReadAllText(sync)); }
        catch (Exception) { known = null; }
        foreach (var f in Watched)
        {
            string g = Path.Combine(game, f), p = Path.Combine(project, f);
            if (!File.Exists(g)) continue;
            var fi = new FileInfo(g);
            FileStamp s = null;
            if (known != null && known.TryGetValue(f, out s) && s.Size == fi.Length && s.Time == fi.LastWriteTimeUtc) continue;   // unverändert
            if (SameFile(g, p)) continue;                                                                               // wie im Projekt
            if (known == null && fi.LastWriteTimeUtc <= File.GetLastWriteTimeUtc(p)) continue;                          // Projekt ist neuer
            if (s != null && s.Hash == Hash(g)) continue;                                                               // nur die Zeit anders
            list.Add(f);
        }
        return list;
    }

    /// Geänderte Map-Dateien und die erzeugten Dateien des Spiels ins Projekt holen (vorher Sicherung durch den Aufrufer)
    public static void TakeOverFromGame(string project, string game, IEnumerable<string> files)
    {
        foreach (var f in files.Concat(Generated))
        {
            string g = Path.Combine(game, f);
            if (File.Exists(g)) File.Copy(g, Path.Combine(project, f), true);
        }
        RecordGameState(project, game);
    }

    static string Hash(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// Weltordner im Spiel: zuerst die von Mods (z. B. Undead Legacy), dann das normale Spiel
    public static string GuessGameWorld(string worldName) =>
        GameLocator.WorldFolders().Select(g => Path.Combine(g, worldName)).FirstOrDefault(Directory.Exists);

    /// Holt die vom Spiel erzeugten Dateien ins Projekt, wenn die Map-Dateien im Spiel genau dem Projekt entsprechen
    /// (dann gehören sie zu diesem Stand, z. B. splat3_processed.png mit den Stadtstraßen). Liefert die Anzahl geholter Dateien.
    public static int FetchGenerated(string project, string game)
    {
        if (game == null || !Directory.Exists(game)) return 0;
        if (!Generated.All(f => File.Exists(Path.Combine(game, f)))) return 0;
        // Spawnpunkte zählen nicht: im Spiel steht evtl. der Testpunkt, und sie ändern nichts an den erzeugten Dateien
        if (!Sources.Where(f => f != WorldData.SpawnFile).All(f => SameFile(Path.Combine(project, f), Path.Combine(game, f)))) return 0;
        int n = 0;
        foreach (var f in Generated)
        {
            string src = Path.Combine(game, f), dst = Path.Combine(project, f);
            if (SameFile(src, dst)) continue;
            File.Copy(src, dst, true);
            n++;
        }
        return n;
    }

    public sealed record DeployResult(List<string> Copied, List<string> Deleted);

    /// Map-Dateien ins Spiel kopieren (Spawnpunkte: die gerade benutzte spawnpoints.xml, siehe WorldData.ActivateSpawns)
    public static DeployResult Deploy(string project, string game)
    {
        if (GameRunning()) throw new InvalidOperationException(Loc.T("The game is still running. Please quit 7 Days to Die first."));
        var res = new DeployResult(new(), new());
        Directory.CreateDirectory(game);   // neue Welt aus dem Terraingenerator: Ordner im Spiel gibt es noch nicht
        foreach (var f in Sources)
        {
            string src = Path.Combine(project, f), dst = Path.Combine(game, f);
            if (!File.Exists(src)) continue;
            if (SameFile(src, dst)) continue;
            // erst als .neu daneben kopieren, dann ersetzen: bricht das Kopieren ab (Platte voll), liegt im Spiel keine halbe Datei
            string tmp = dst + ".neu";
            try { File.Copy(src, tmp, true); File.Move(tmp, dst, true); }
            catch { try { File.Delete(tmp); } catch (IOException) { } throw; }
            res.Copied.Add(f);
        }
        foreach (var f in Generated)
        {
            string p = Path.Combine(game, f);
            if (!File.Exists(p)) continue;
            File.Delete(p);
            res.Deleted.Add(f);
        }
        return res;
    }

    static bool SameFile(string a, string b)
    {
        if (!File.Exists(a) || !File.Exists(b)) return false;
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        // Blockweise vergleichen und beim ersten Unterschied aufhören (statt von beiden Dateien die ganze Prüfsumme zu bilden;
        // dtm.raw hat 128 MB, Prüfbericht F8)
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        var ba = new byte[81920];
        var bb = new byte[81920];
        int n;
        while ((n = fa.Read(ba, 0, ba.Length)) > 0)
        {
            fb.ReadExactly(bb, 0, n);
            if (!ba.AsSpan(0, n).SequenceEqual(bb.AsSpan(0, n))) return false;
        }
        return true;
    }
}
