namespace MapEditor.Map;

/// Kopie der reinen Terrainkarte im Projektordner (Wunsch des Nutzers 7.11.2026): Gelände, Biome, Wasser, eigene Straßen und
/// Gebäude so, wie sie vor den ersten Städten waren. „Städte erzeugen → Ersetzen“ legt sie zurück, statt Städte, Straßen und
/// POIs einzeln zu löschen. Angelegt beim Erzeugen einer Welt und vor dem ersten Erzeugen von Städten (wenn noch keine da ist).
/// Spawnpunkte gehören nicht dazu (sie werden sofort gespeichert und bleiben, wie der Nutzer sie gesetzt hat).
public static class TerrainCopy
{
    public const string FolderName = "yamee_terrain";
    static readonly string[] Files = { "dtm.raw", "splat3.png", "splat4.png", "biomes.png", "prefabs.xml" };

    public static string Dir(string project) => Path.Combine(project, FolderName);

    /// gibt es eine vollständige Kopie?
    public static bool Exists(string project) => Files.All(f => File.Exists(Path.Combine(Dir(project), f)));

    /// Zeitpunkt der Kopie (dtm.raw); null = keine
    public static DateTime? Time(string project)
    {
        string f = Path.Combine(Dir(project), "dtm.raw");
        return File.Exists(f) ? File.GetLastWriteTime(f) : null;
    }

    /// die Map-Dateien des Projekts (so wie gespeichert) als Terrainkopie ablegen; vorhandene Kopie wird ersetzt
    public static void Save(string project)
    {
        string dir = Dir(project);
        Directory.CreateDirectory(dir);
        foreach (var f in Files)
        {
            string src = Path.Combine(project, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(dir, f), true);
        }
    }

    /// Terrainkopie zurück ins Projekt legen. Die vom Spiel erzeugten Dateien des letzten Tests passen danach nicht mehr und
    /// werden entfernt (das Spiel erzeugt sie beim nächsten Laden neu).
    public static void Restore(string project)
    {
        string dir = Dir(project);
        foreach (var f in Files) File.Copy(Path.Combine(dir, f), Path.Combine(project, f), true);
        foreach (var f in new[] { "dtm_processed.raw", "splat3_processed.png", "splat4_processed.png", "splat3_half.png", "splat4_half.png", "checksums.txt" })
        {
            string p = Path.Combine(project, f);
            if (File.Exists(p)) File.Delete(p);
        }
    }
}
