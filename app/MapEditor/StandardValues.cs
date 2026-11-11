using System.Globalization;
using System.Text;
using MapEditor.Map;

namespace MapEditor;

/// Werkseinstellungen aus default.ini neben YAMEE.exe (Wunsch des Nutzers 29.10.2026, Dateinamen englisch). Fehlt die Datei,
/// wird sie mit den eingebauten Werten angelegt; eine alte Standardwerte.ini wird dabei übernommen und umbenannt.
/// „Werte zurücksetzen“ in den Einstellungen liest sie jedes Mal neu – so lassen sich die Werkswerte auch von Hand ändern.
/// Zahlen mit Komma oder Punkt; Zeilen mit ; sind Kommentare. Schlüssel englisch (die alten deutschen werden weiter verstanden).
public sealed class StandardValues
{
    public RoadValues Asphalt = RoadValues.From(RoadSettings.Asphalt());
    public RoadValues Gravel = RoadValues.From(RoadSettings.Gravel());
    public double EraserSize = 32;
    public bool EraserSquare;
    public int EraserBumps = 2;   // 0 keine, 1 leicht, 2 mittel, 3 stark
    public double BiomeBrushSize = 32;
    public bool BiomeBrushSquare;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] BumpNames = { "None", "Light", "Medium", "Strong" };
    static readonly string[] OldBumpNames = { "Keine", "Leicht", "Mittel", "Stark" };
    static StandardValues current;

    /// neben YAMEE.exe (Wunsch des Nutzers); ist der Programmordner nicht beschreibbar (installiert unter „Programme“)
    /// und liegt dort keine, dann unter %APPDATA%\YAMEE
    public static string FilePath => filePath ??= ChooseFile();
    static string filePath;

    static string ChooseFile()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "default.ini");
        if (File.Exists(beside) || File.Exists(OldFilePath) || CanWrite(AppContext.BaseDirectory)) return beside;
        string data = AppSettings.DataFolder;
        Directory.CreateDirectory(data);
        return Path.Combine(data, "default.ini");
    }

    static bool CanWrite(string dir)
    {
        try
        {
            string test = Path.Combine(dir, ".yamee_write_test");
            File.WriteAllText(test, "");
            File.Delete(test);
            return true;
        }
        catch (Exception) { return false; }
    }
    static string OldFilePath => Path.Combine(AppContext.BaseDirectory, "Standardwerte.ini");

    /// zuletzt gelesene Werkswerte (für Startwerte, wenn in settings.json noch nichts steht)
    public static StandardValues Current => current ??= Load();

    public RoadSettings RoadFor(Surface s)
    {
        var r = RoadSettings.For(s);
        (s == Surface.Asphalt ? Asphalt : s == Surface.Gravel ? Gravel : null)?.ApplyTo(r);
        return r;
    }

    /// Datei lesen (fehlende Datei anlegen); unlesbare Werte bleiben auf den eingebauten Werten
    public static StandardValues Load()
    {
        var v = new StandardValues();
        current = v;   // früh setzen: AppSettings liest beim Anlegen StandardValues.Current
        try
        {
            bool migrate = !File.Exists(FilePath) && File.Exists(OldFilePath);
            string file = migrate ? OldFilePath : FilePath;
            if (!File.Exists(file)) { File.WriteAllText(FilePath, v.ToIni() + UserValues.DefaultSections(Array.Empty<string>()), Encoding.UTF8); return v; }
            string section = "";
            bool hasBiomes = false;
            var seen = new List<string>();
            foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line == "" || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim().ToLowerInvariant(); seen.Add(section); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim().ToLowerInvariant(), val = line[(eq + 1)..].Trim();
                if (section == "asphalt") SetRoad(v.Asphalt, key, val);
                else if (section is "gravel" or "schotter") SetRoad(v.Gravel, key, val);
                else if (section is "eraser" or "radiergummi") v.SetEraser(key, val);
                else if (section is "biomes" or "biome") { hasBiomes = true; v.SetBiomes(key, val); }
            }
            // ältere default.ini ohne Biom-Pinsel: Abschnitt hinten anhängen (der Rest der Datei bleibt, wie er ist)
            // (seit 10.11.2026 auch die Abschnitte für Generator, Städte, Straßen, Wildnis und Werkzeuge)
            if (!migrate)
            {
                string missing = (hasBiomes ? "" : v.BiomesIni() + "\r\n") + UserValues.DefaultSections(seen);
                if (missing.Length > 0) File.AppendAllText(FilePath, "\r\n" + missing, Encoding.UTF8);
                UserValues.AddMissingKeys(FilePath);   // neue Werte nach einem Update in vorhandene Abschnitte
                UserValues.AddNotes(FilePath);
            }
            if (migrate)
            {
                // alte deutsche Datei: Werte als default.ini (englische Schlüssel) schreiben, alte Datei nur umbenennen (nichts löschen)
                File.WriteAllText(FilePath, v.ToIni() + UserValues.DefaultSections(Array.Empty<string>()), Encoding.UTF8);
                File.Move(OldFilePath, OldFilePath + ".old", true);
            }
        }
        catch (Exception) { }   // Datei gesperrt oder kaputt: eingebaute Werte
        return v;
    }

    static bool Num(string s, out double d) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, Inv, out d);

    static void SetRoad(RoadValues r, string key, string val)
    {
        if (!Num(val, out double d)) return;
        switch (key)
        {
            case "width" or "breite": r.Width = d; break;
            case "shoulder" or "schotterrand": r.Shoulder = d; break;
            case "grassedge" or "rasenkante": r.GrassEdge = d; break;
            case "maxgrade": r.MaxGrade = d / 100; break;                                      // %
            case "hoechstesteigung": r.MaxGrade = d / 10; break;                                // alte Datei: m auf 10 m
            case "smoothing" or "glaettung": r.Smooth = d; break;
            case "slopemin" or "boeschungmindestens": r.Blend = d; break;
            case "slopepermeter" or "boeschungjemeterhoehe": r.CutSlope = d; break;
            case "junctionflatupto" or "kreuzungebenbis": r.JunctionTolerance = d / 100; break;  // %
            case "crossslopeupto" or "querneigungbis": r.CrossSlope = d / 100; break;          // %
        }
    }

    void SetEraser(string key, string val)
    {
        switch (key)
        {
            case "brushsize" or "pinselgroesse": if (Num(val, out double d)) EraserSize = d; break;
            case "brushshape" or "pinselform": EraserSquare = val.Trim().ToLowerInvariant() is "square" or "eckig"; break;
            case "bumps" or "unebenheit":
                int i = Array.FindIndex(BumpNames, n => n.Equals(val.Trim(), StringComparison.OrdinalIgnoreCase));
                if (i < 0) i = Array.FindIndex(OldBumpNames, n => n.Equals(val.Trim(), StringComparison.OrdinalIgnoreCase));
                if (i >= 0) EraserBumps = i;
                break;
        }
    }

    void SetBiomes(string key, string val)
    {
        switch (key)
        {
            case "brushsize": if (Num(val, out double d)) BiomeBrushSize = d; break;
            case "brushshape": BiomeBrushSquare = val.Trim().ToLowerInvariant() is "square" or "eckig"; break;
        }
    }

    string BiomesIni() =>
        "[Biomes]\r\n" +
        $"BrushSize = {BiomeBrushSize.ToString("0.##", Inv)}\r\n" +
        $"BrushShape = {(BiomeBrushSquare ? "square" : "round")}\r\n";

    string ToIni()
    {
        string F(double d) => d.ToString("0.##", Inv);
        string Road(string name, RoadValues r) =>
            $"[{name}]\r\n" +
            $"Width = {F(r.Width)}\r\n" +
            $"Shoulder = {F(r.Shoulder)}\r\n" +
            $"GrassEdge = {F(r.GrassEdge)}\r\n" +
            $"MaxGrade = {F(r.MaxGrade * 100)}\r\n" +
            $"Smoothing = {F(r.Smooth)}\r\n" +
            $"SlopeMin = {F(r.Blend)}\r\n" +
            $"SlopePerMeter = {F(r.CutSlope)}\r\n" +
            $"JunctionFlatUpTo = {F((r.JunctionTolerance ?? 0.02) * 100)}\r\n" +
            $"CrossSlopeUpTo = {F((r.CrossSlope ?? 0) * 100)}\r\n\r\n";
        return "; Factory defaults of the map editor. \"Reset values\" in the settings reads this file.\r\n" +
               "; Width, Shoulder, GrassEdge, Smoothing, SlopeMin, SlopePerMeter in meters; MaxGrade in %; JunctionFlatUpTo and CrossSlopeUpTo in %.\r\n" +
               "; BrushShape: round or square; Bumps: None, Light, Medium or Strong. Decimal point or comma.\r\n\r\n" +
               Road("Asphalt", Asphalt) + Road("Gravel", Gravel) +
               "[Eraser]\r\n" +
               $"BrushSize = {F(EraserSize)}\r\n" +
               $"BrushShape = {(EraserSquare ? "square" : "round")}\r\n" +
               $"Bumps = {BumpNames[Math.Clamp(EraserBumps, 0, 3)]}\r\n\r\n" +
               BiomesIni();
    }
}
