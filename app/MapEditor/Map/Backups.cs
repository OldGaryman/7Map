using System.Globalization;

namespace MapEditor.Map;

public sealed record BackupInfo(string Folder, DateTime Time, string Description);

/// Sicherungen vor jeder Änderung: Ordner „Backups\&lt;Welt&gt;\JJJJ-MM-TT_hh-mm-ss“ neben dem Weltordner, mit den Dateien,
/// die die App ändert. „Rückgängig“ legt die neueste zurück und löscht sie danach. Es bleiben höchstens Keep Sicherungen.
public static class Backups
{
    public const int Keep = 10;
    static readonly string[] Files = { "dtm.raw", "splat3.png", "splat4.png", "biomes.png", "prefabs.xml", WorldData.SpawnFile, WorldData.PlaySpawnFile, WorldData.TestSpawnFile };
    const string Stamp = "yyyy-MM-dd_HH-mm-ss";

    public static string Root(WorldData w) => Root(w.Folder);
    static string Root(string folder) => Path.Combine(Path.GetDirectoryName(folder.TrimEnd('\\', '/')), "Backups", new DirectoryInfo(folder).Name);

    public static BackupInfo Create(WorldData w, string description) => Create(w.Folder, description);

    /// Sicherung eines Projektordners, auch bevor die Welt geladen ist (z. B. vor dem Übernehmen fremder Änderungen)
    public static BackupInfo Create(string folder, string description)
    {
        var now = DateTime.Now;
        string dir = Path.Combine(Root(folder), now.ToString(Stamp, CultureInfo.InvariantCulture));
        // Der Ordnername hat nur Sekunden: gibt es ihn schon (zwei Sicherungen in einer Sekunde), eine Sekunde weiterzählen,
        // sonst überschriebe die zweite Sicherung die erste und der ältere Stand ginge verloren (Prüfbericht F2)
        while (Directory.Exists(dir))
        {
            now = now.AddSeconds(1);
            dir = Path.Combine(Root(folder), now.ToString(Stamp, CultureInfo.InvariantCulture));
        }
        Directory.CreateDirectory(dir);
        foreach (var f in Files)
        {
            string p = Path.Combine(folder, f);
            if (File.Exists(p)) File.Copy(p, Path.Combine(dir, f), true);
        }
        File.WriteAllText(Path.Combine(dir, "info.txt"), description);
        foreach (var old in List(folder).Skip(Keep)) Directory.Delete(old.Folder, true);
        return new BackupInfo(dir, now, description);
    }

    /// Die Sicherungen eines Projekts gehören zum Projektordner: wird er beiseitegelegt (umbenannt), müssen sie mit. Sonst zeigt
    /// ein frisch kopiertes Projekt gleichen Namens die Sicherungen des alten, und „Rückgängig“ spielt dessen Dateien über die
    /// neue Kopie (Prüfbericht F32).
    public static void MoveWithProject(string oldFolder, string newFolder)
    {
        string from = Root(oldFolder), to = Root(newFolder);
        if (!Directory.Exists(from) || Directory.Exists(to)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to));
        Directory.Move(from, to);
    }

    /// Sicherungen, neueste zuerst
    public static List<BackupInfo> List(WorldData w) => List(w.Folder);

    static List<BackupInfo> List(string folder)
    {
        var list = new List<BackupInfo>();
        if (!Directory.Exists(Root(folder))) return list;
        foreach (var dir in Directory.GetDirectories(Root(folder)))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(dir), Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
            string info = Path.Combine(dir, "info.txt");
            list.Add(new BackupInfo(dir, t, File.Exists(info) ? File.ReadAllText(info) : ""));
        }
        return list.OrderByDescending(b => b.Time).ToList();
    }

    public static void Restore(WorldData w, BackupInfo b)
    {
        foreach (var f in Files)
        {
            string p = Path.Combine(b.Folder, f), target = Path.Combine(w.Folder, f);
            if (File.Exists(p)) File.Copy(p, target, true);
            // ältere Sicherungen kennen die Spiel-/Testdatei nicht: dann weg damit, beim Laden entstehen sie neu aus spawnpoints.xml
            else if ((f == WorldData.PlaySpawnFile || f == WorldData.TestSpawnFile) && File.Exists(target)) File.Delete(target);
        }
        Directory.Delete(b.Folder, true);
    }
}
