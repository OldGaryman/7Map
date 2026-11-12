using System.Globalization;

namespace MapEditor.Map;

public sealed record BackupInfo(string Folder, DateTime Time, string Description);

/// Backups before every change: folder "Backups\&lt;world&gt;\YYYY-MM-DD_hh-mm-ss" next to the world folder, with the files
/// that the app changes. "Undo" puts the newest back and deletes it afterwards. At most Keep backups remain.
public static class Backups
{
    public const int Keep = 10;
    static readonly string[] Files = { "dtm.raw", "splat3.png", "splat4.png", "biomes.png", "prefabs.xml", WorldData.SpawnFile, WorldData.PlaySpawnFile, WorldData.TestSpawnFile };
    const string Stamp = "yyyy-MM-dd_HH-mm-ss";

    public static string Root(WorldData w) => Root(w.Folder);
    static string Root(string folder) => Path.Combine(Path.GetDirectoryName(folder.TrimEnd('\\', '/')), "Backups", new DirectoryInfo(folder).Name);

    public static BackupInfo Create(WorldData w, string description) => Create(w.Folder, description);

    /// backup of a project folder, also before the world is loaded (e.g. before taking over foreign changes)
    public static BackupInfo Create(string folder, string description)
    {
        var now = DateTime.Now;
        string dir = Path.Combine(Root(folder), now.ToString(Stamp, CultureInfo.InvariantCulture));
        // The folder name has only seconds: if it already exists (two backups within one second), count one second further,
        // otherwise the second backup would overwrite the first and the older state would be lost (audit finding F2)
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

    /// The backups of a project belong to the project folder: if it is put aside (renamed), they must go with it. Otherwise
    /// a freshly copied project of the same name shows the backups of the old one, and "Undo" plays its files over the
    /// new copy (audit finding F32).
    public static void MoveWithProject(string oldFolder, string newFolder)
    {
        string from = Root(oldFolder), to = Root(newFolder);
        if (!Directory.Exists(from) || Directory.Exists(to)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to));
        Directory.Move(from, to);
    }

    /// backups, newest first
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
            // older backups do not know the play/test file: then drop it, on loading they are created again from spawnpoints.xml
            else if ((f == WorldData.PlaySpawnFile || f == WorldData.TestSpawnFile) && File.Exists(target)) File.Delete(target);
        }
        Directory.Delete(b.Folder, true);
    }
}
