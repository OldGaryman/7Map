using System.Globalization;
using System.Reflection;
using System.Text;
using MapEditor.Map;

namespace MapEditor;

/// All values the user can change (the user's request, 2026-11-10):
///  - default.ini (next to the exe) contains ALL these values (factory values; the developer changes them by hand, if the file or
///    a section is missing, the built-in values apply),
///  - user.ini (under %APPDATA%\YAMEE or YAMEE-Dev) contains only the values the user changed compared with default.ini.
/// Effective is: built-in value → default.ini → user.ini. Sections: [Generator] [Cities] [Roads] [Wilderness] [Tools]
/// (tool and interface values from AppSettings) as well as the already existing [Asphalt] [Gravel] [Eraser] [Biomes].
/// Numbers with point or comma, lists (biome shares) comma-separated, truth values true/false.
public static class UserValues
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly object gate = new();

    /// section → object type (fields or properties of type double, int, bool, int[]), plus omitted names
    static readonly (string Section, Type Type, string[] Skip)[] Reflected =
    {
        ("Generator", typeof(GeneratorSpec), Array.Empty<string>()),
        ("Cities", typeof(CitySpec), Array.Empty<string>()),   // TradersPerBiome: -1 = automatic according to map size
        ("Roads", typeof(NetworkSpec), Array.Empty<string>()),
        ("Wilderness", typeof(WildernessSpec), Array.Empty<string>()),
        // paths, folders and the four brush sections with their own format are not in [Tools]
        ("Tools", typeof(AppSettings), new[] { "EraserBumps", "EraserSize", "EraserSquare", "BiomeBrushSize", "BiomeBrushSquare" }),
    };

    static string UserPath
    {
        get
        {
            string env = Environment.GetEnvironmentVariable("MAPEDITOR_SETTINGS");
            return Path.Combine(env != null ? Path.GetDirectoryName(Path.GetFullPath(env)) : AppSettings.DataFolder, "user.ini");
        }
    }

    public static bool UserFileExists => File.Exists(UserPath);

    // ---------- Reading and writing the .ini files ----------

    public static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
    {
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return all;
            Dictionary<string, string> cur = null;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line == "" || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    string name = line[1..^1].Trim();
                    if (!all.TryGetValue(name, out cur)) all[name] = cur = new(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0 && cur != null) cur[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch (Exception) { }   // locked or broken: nothing read
        return all;
    }

    static Dictionary<string, string> Section(Dictionary<string, Dictionary<string, string>> ini, string name) =>
        ini.TryGetValue(name, out var d) ? d : new(StringComparer.OrdinalIgnoreCase);

    static string ToText(Dictionary<string, Dictionary<string, string>> ini, string header)
    {
        var sb = new StringBuilder();
        if (header != null) sb.Append(header).Append("\r\n");
        foreach (var (name, keys) in ini)
        {
            if (keys.Count == 0) continue;
            sb.Append('[').Append(name).Append("]\r\n");
            foreach (var (k, v) in keys) sb.Append(k).Append(" = ").Append(v).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    // ---------- Values of an object ----------

    sealed record Member(string Name, Type Type, Func<object, object> Get, Action<object, object> Set);

    /// gradients are a fraction in the Spec classes (0.18), uniformly in percent (18) in the .ini files – the user's request 2026-11-10
    static double IniScale(Type t, string name) =>
        (t == typeof(CitySpec) && name == "RoadGrade") || ((t == typeof(NetworkSpec) || t == typeof(WildernessSpec)) && name is "Grade" or "MaxGrade") ? 100 : 1;

    static IEnumerable<Member> Members(Type t, string[] skip)
    {
        foreach (var m in RawMembers(t, skip))
        {
            double k = m.Type == typeof(double) ? IniScale(t, m.Name) : 1;
            if (k == 1) yield return m;
            else yield return new Member(m.Name, m.Type, o => Math.Round((double)m.Get(o) * k, 6), (o, v) => m.Set(o, (double)v / k));
        }
    }

    static IEnumerable<Member> RawMembers(Type t, string[] skip)
    {
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!f.IsInitOnly && Supported(f.FieldType) && !skip.Contains(f.Name))
                yield return new Member(f.Name, f.FieldType, o => f.GetValue(o), (o, v) => f.SetValue(o, v));
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0 && p.GetGetMethod() != null && p.GetSetMethod() != null && Supported(p.PropertyType) && !skip.Contains(p.Name)
                && p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() == null)
                yield return new Member(p.Name, p.PropertyType, o => p.GetValue(o), (o, v) => p.SetValue(o, v));
    }

    static bool Supported(Type t) => t == typeof(double) || t == typeof(int) || t == typeof(bool) || t == typeof(int[]) || t == typeof(double[]);

    static string Format(object v) => v switch
    {
        double d => d.ToString("0.######", Inv),
        int i => i.ToString(Inv),
        bool b => b ? "true" : "false",
        int[] a => string.Join(",", a),
        double[] a => string.Join(";", a.Select(d => d.ToString("0.######", Inv))),
        _ => null,
    };

    static bool TryParse(Type t, string s, out object v)
    {
        v = null;
        s = s.Trim();
        if (t == typeof(double)) { if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, Inv, out double d)) { v = d; return true; } }
        else if (t == typeof(int)) { if (int.TryParse(s, NumberStyles.Integer, Inv, out int i)) { v = i; return true; } }
        else if (t == typeof(bool)) { if (bool.TryParse(s, out bool b)) { v = b; return true; } }
        else if (t == typeof(int[]))
        {
            var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var a = new int[parts.Length];
            for (int k = 0; k < parts.Length; k++) if (!int.TryParse(parts[k], NumberStyles.Integer, Inv, out a[k])) return false;
            v = a; return true;
        }
        else if (t == typeof(double[]))
        {
            var parts = s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var a = new double[parts.Length];
            for (int k = 0; k < parts.Length; k++) if (!double.TryParse(parts[k].Replace(',', '.'), NumberStyles.Float, Inv, out a[k])) return false;
            v = a; return true;
        }
        return false;
    }

    /// values of an object as key → text (null lists are missing)
    static Dictionary<string, string> Dump(object obj, string[] skip)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Members(obj.GetType(), skip))
        {
            string s = Format(m.Get(obj));
            if (s != null) d[m.Name] = s;
        }
        return d;
    }

    static void Apply(object obj, string[] skip, Dictionary<string, string> values)
    {
        foreach (var m in Members(obj.GetType(), skip))
            if (values.TryGetValue(m.Name, out string s) && TryParse(m.Type, s, out object v))
                m.Set(obj, v);
    }

    static (string Section, Type Type, string[] Skip) Find(Type t) => Reflected.First(r => r.Type == t);

    // ---------- Specs and tool values ----------

    /// apply factory values (built-in → default.ini) and afterwards the values of the user (user.ini) to the object
    public static T Load<T>(T obj)
    {
        var (section, type, skip) = Find(typeof(T));
        Apply(obj, skip, Effective(section, obj.GetType(), skip));
        return obj;
    }

    /// default.ini over the built-in values, user.ini over that
    static Dictionary<string, string> Effective(string section, Type type, string[] skip)
    {
        var d = DefaultsFor(section, type, skip);
        foreach (var (k, v) in Section(ReadIni(UserPath), section)) d[k] = v;
        return d;
    }

    /// built-in values, overlaid with default.ini
    static Dictionary<string, string> DefaultsFor(string section, Type type, string[] skip, bool useIni = true)
    {
        var d = Dump(Activator.CreateInstance(type), skip);
        if (useIni) foreach (var (k, v) in Section(ReadIni(StandardValues.FilePath), section)) d[k] = v;
        return d;
    }

    /// remember changed values of the object in user.ini (only those that differ from default.ini)
    public static void Remember<T>(T obj)
    {
        var (section, type, skip) = Find(typeof(T));
        WriteDiff(section, Dump(obj, skip), DefaultsFor(section, type, skip));
    }

    static bool SameValue(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return double.TryParse(a.Replace(',', '.'), NumberStyles.Float, Inv, out double x) &&
               double.TryParse(b.Replace(',', '.'), NumberStyles.Float, Inv, out double y) && Math.Abs(x - y) < 1e-9;
    }

    static void WriteDiff(string section, Dictionary<string, string> current, Dictionary<string, string> defaults)
    {
        lock (gate)
        {
            try
            {
                var ini = ReadIni(UserPath);
                var diff = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in current)
                    if (!defaults.TryGetValue(k, out string dv) || !SameValue(v, dv)) diff[k] = v;
                if (diff.Count == 0) ini.Remove(section); else ini[section] = diff;
                Directory.CreateDirectory(Path.GetDirectoryName(UserPath));
                File.WriteAllText(UserPath, ToText(ini, "; User values of the map editor: only what was changed compared with default.ini.\r\n; Delete a line (or this file) to get the default value back.\r\n"), new UTF8Encoding(false));
            }
            catch (Exception) { }   // locked: at the next save
        }
    }

    // ---------- AppSettings: tool values, roads, eraser, biome brush ----------

    static string[] ToolsSkip => Find(typeof(AppSettings)).Skip;

    /// names of the properties of AppSettings that are in .ini instead of settings.json
    public static bool IsManaged(string property) =>
        property is "Asphalt" or "Gravel" or "EraserBumps" or "EraserSize" or "EraserSquare" or "BiomeBrushSize" or "BiomeBrushSquare"
        || Members(typeof(AppSettings), Array.Empty<string>()).Any(m => m.Name == property);

    /// After reading settings.json: insert values from default.ini and user.ini. If user.ini does not exist yet and settings.json
    /// contained something other than the built-in values, it is taken over once into user.ini (earlier versions).
    public static void ApplyTo(AppSettings s, bool migrate)
    {
        if (migrate) Migrate(s);
        var ini = ReadIni(UserPath);
        Apply(s, ToolsSkip, Effective("Tools", typeof(AppSettings), ToolsSkip));
        var def = StandardValues.Current;
        var eraser = EraserDict(def); foreach (var (k, v) in Section(ini, "Eraser")) eraser[k] = v;
        var biomes = BiomeDict(def); foreach (var (k, v) in Section(ini, "Biomes")) biomes[k] = v;
        SetEraser(s, eraser); SetBiomes(s, biomes);
        s.Asphalt = RoadFromIni(def.Asphalt, Section(ini, "Asphalt"));
        s.Gravel = RoadFromIni(def.Gravel, Section(ini, "Gravel"));
    }

    /// write user.ini from the current state of the settings (only deviations from default.ini)
    public static void Save(AppSettings s)
    {
        var def = StandardValues.Current;
        WriteDiff("Tools", Dump(s, ToolsSkip), DefaultsFor("Tools", typeof(AppSettings), ToolsSkip));
        WriteDiff("Eraser", EraserDict(s), EraserDict(def));
        WriteDiff("Biomes", BiomeDict(s), BiomeDict(def));
        WriteDiff("Asphalt", s.Asphalt == null ? RoadDict(def.Asphalt) : RoadDict(s.Asphalt), RoadDict(def.Asphalt));
        WriteDiff("Gravel", s.Gravel == null ? RoadDict(def.Gravel) : RoadDict(s.Gravel), RoadDict(def.Gravel));
    }

    /// reset a tool: insert the named values again from default.ini (otherwise built-in); the caller saves afterwards
    /// (user.ini thereby loses the deviations of these values)
    public static void ResetTools(AppSettings s, params string[] names)
    {
        var d = DefaultsFor("Tools", typeof(AppSettings), ToolsSkip);
        foreach (var m in Members(typeof(AppSettings), ToolsSkip))
            if (names.Contains(m.Name) && d.TryGetValue(m.Name, out string text) && TryParse(m.Type, text, out object v))
                m.Set(s, v);
    }

    static void Migrate(AppSettings s)
    {
        if (UserFileExists) return;
        var def = StandardValues.Current;
        // tool values from settings.json: compare with the built-in values (not with default.ini, so that changes by the
        // developer in default.ini are not overruled by old saved values)
        WriteDiff("Tools", Dump(s, ToolsSkip), DefaultsFor("Tools", typeof(AppSettings), ToolsSkip, useIni: false));
        WriteDiff("Eraser", EraserDict(s), EraserDict(def));
        WriteDiff("Biomes", BiomeDict(s), BiomeDict(def));
        if (s.Asphalt != null) WriteDiff("Asphalt", RoadDict(s.Asphalt), RoadDict(def.Asphalt));
        if (s.Gravel != null) WriteDiff("Gravel", RoadDict(s.Gravel), RoadDict(def.Gravel));
    }

    static readonly string[] BumpNames = { "None", "Light", "Medium", "Strong" };
    static string Shape(bool square) => square ? "square" : "round";

    static Dictionary<string, string> EraserDict(AppSettings s) => new(StringComparer.OrdinalIgnoreCase)
    { ["BrushSize"] = Format(s.EraserSize), ["BrushShape"] = Shape(s.EraserSquare), ["Bumps"] = BumpNames[Math.Clamp(s.EraserBumps, 0, 3)] };
    static Dictionary<string, string> EraserDict(StandardValues s) => new(StringComparer.OrdinalIgnoreCase)
    { ["BrushSize"] = Format(s.EraserSize), ["BrushShape"] = Shape(s.EraserSquare), ["Bumps"] = BumpNames[Math.Clamp(s.EraserBumps, 0, 3)] };
    static Dictionary<string, string> BiomeDict(AppSettings s) => new(StringComparer.OrdinalIgnoreCase)
    { ["BrushSize"] = Format(s.BiomeBrushSize), ["BrushShape"] = Shape(s.BiomeBrushSquare) };
    static Dictionary<string, string> BiomeDict(StandardValues s) => new(StringComparer.OrdinalIgnoreCase)
    { ["BrushSize"] = Format(s.BiomeBrushSize), ["BrushShape"] = Shape(s.BiomeBrushSquare) };

    static void SetEraser(AppSettings s, Dictionary<string, string> d)
    {
        if (d.TryGetValue("BrushSize", out var a) && TryParse(typeof(double), a, out var v)) s.EraserSize = (double)v;
        if (d.TryGetValue("BrushShape", out a)) s.EraserSquare = a.Trim().Equals("square", StringComparison.OrdinalIgnoreCase);
        if (d.TryGetValue("Bumps", out a))
        {
            int i = Array.FindIndex(BumpNames, n => n.Equals(a.Trim(), StringComparison.OrdinalIgnoreCase));
            if (i >= 0) s.EraserBumps = i;
        }
    }

    static void SetBiomes(AppSettings s, Dictionary<string, string> d)
    {
        if (d.TryGetValue("BrushSize", out var a) && TryParse(typeof(double), a, out var v)) s.BiomeBrushSize = (double)v;
        if (d.TryGetValue("BrushShape", out a)) s.BiomeBrushSquare = a.Trim().Equals("square", StringComparison.OrdinalIgnoreCase);
    }

    static Dictionary<string, string> RoadDict(RoadValues r)
    {
        string F(double d) => Format(d);
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["Width"] = F(r.Width), ["Shoulder"] = F(r.Shoulder), ["GrassEdge"] = F(r.GrassEdge), ["MaxGrade"] = F(r.MaxGrade * 100),
            ["Smoothing"] = F(r.Smooth), ["SlopeMin"] = F(r.Blend), ["SlopePerMeter"] = F(r.CutSlope),
            ["JunctionFlatUpTo"] = F((r.JunctionTolerance ?? 0.02) * 100), ["CrossSlopeUpTo"] = F((r.CrossSlope ?? 0) * 100),
        };
    }

    /// road values: factory values with the changes from user.ini; null if the user changed nothing
    static RoadValues RoadFromIni(RoadValues factory, Dictionary<string, string> user)
    {
        if (user.Count == 0) return null;
        var d = RoadDict(factory);
        foreach (var (k, v) in user) d[k] = v;
        double N(string k) => TryParse(typeof(double), d[k], out var v) ? (double)v : 0;
        return new RoadValues
        {
            Width = N("Width"), Shoulder = N("Shoulder"), GrassEdge = N("GrassEdge"), MaxGrade = N("MaxGrade") / 100, Smooth = N("Smoothing"),
            Blend = N("SlopeMin"), CutSlope = N("SlopePerMeter"), JunctionTolerance = N("JunctionFlatUpTo") / 100, CrossSlope = N("CrossSlopeUpTo") / 100,
        };
    }

    // ---------- Town types (number and size per type): [CityTypes] in default.ini / user.ini ----------
    // The defaults come from the rules of the game (depend on the map size). So that they are in default.ini and the developer can
    // change them, the program enters missing lines when opening "Create cities": key <map class>.<type>[_<biome>],
    // value "count, tiles from, tiles to".

    static readonly string[] BiomeKeys = { "forest", "burntforest", "desert", "snow", "wasteland" };

    static string TypeKey(TownType t, string worldClass) =>
        (string.IsNullOrEmpty(worldClass) ? "any" : worldClass) + "." + t.Name + (t.PerBiome && t.Biomes.Length == 1 ? "_" + BiomeKeys[Math.Clamp(t.Biomes[0], 0, 4)] : "");

    static string TypeValue(TownType t) => $"{t.Count}, {t.TilesMin}, {t.TilesMax}";

    static bool ParseType(string s, out int count, out int min, out int max)
    {
        count = min = max = 0;
        var p = (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return p.Length == 3 && int.TryParse(p[0], out count) && int.TryParse(p[1], out min) && int.TryParse(p[2], out max);
    }

    /// enter missing town types (with the values of the game) into default.ini
    public static void EnsureCityTypes(IEnumerable<TownType> types, string worldClass)
    {
        try
        {
            string path = StandardValues.FilePath;
            var have = Section(ReadIni(path), "CityTypes");
            var add = types.Where(t => !have.ContainsKey(TypeKey(t, worldClass))).Select(t => TypeKey(t, worldClass) + " = " + TypeValue(t)).ToList();
            if (add.Count == 0) return;
            var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
            int head = lines.FindIndex(l => l.Trim().Equals("[CityTypes]", StringComparison.OrdinalIgnoreCase));
            if (head < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim() != "") lines.Add("");
                lines.Add("; Towns: <map class>.<town type>[_<biome>] = count, tiles from, tiles to (values of the game; change them here to change the defaults)");
                lines.Add("[CityTypes]");
                lines.AddRange(add);
            }
            else
            {
                int last = head;
                for (int i = head + 1; i < lines.Count && !lines[i].TrimStart().StartsWith('['); i++) if (lines[i].Trim() != "") last = i;
                lines.InsertRange(last + 1, add);
            }
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", Encoding.UTF8);
        }
        catch (Exception) { }   // file locked: the values of the game apply
    }

    /// apply values from default.ini and user.ini to the town types
    public static void ApplyCityTypes(IEnumerable<TownType> types, string worldClass)
    {
        var def = Section(ReadIni(StandardValues.FilePath), "CityTypes");
        var user = Section(ReadIni(UserPath), "CityTypes");
        foreach (var t in types)
        {
            string key = TypeKey(t, worldClass);
            string text = user.TryGetValue(key, out string u) ? u : def.TryGetValue(key, out string d) ? d : null;
            if (ParseType(text, out int c, out int mn, out int mx)) { t.Count = c; t.TilesMin = mn; t.TilesMax = mx; }
        }
    }

    /// remember changes of the user to the town types in user.ini (only deviations from default.ini or the rules of the game)
    public static void RememberCityTypes(IEnumerable<TownType> current, IEnumerable<TownType> gameRules, string worldClass)
    {
        var def = Section(ReadIni(StandardValues.FilePath), "CityTypes");
        var rules = gameRules.ToDictionary(t => TypeKey(t, worldClass), TypeValue);
        var now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var factory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in current)
        {
            string key = TypeKey(t, worldClass);
            now[key] = TypeValue(t);
            factory[key] = def.TryGetValue(key, out string d) ? d : rules.GetValueOrDefault(key) ?? TypeValue(t);
        }
        WriteDiff("CityTypes", now, factory);
    }

    // ---------- default.ini: add missing sections ----------

    /// explanations (as a comment line above the key) for values whose number is not self-explanatory
    static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cities.Elevation"] = "Location of the towns: 0 = plains and valleys, 1 = mixed (about every second town on a hill), 2 = preferably on hills",
        ["Cities.TradersPerBiome"] = "Traders per biome: -1 = automatic from the map size, otherwise 0 to 10",
        ["Cities.MaxParts"] = "Most parts (outlying districts) per town, 0 to 3",
        ["Cities.FillLots"] = "Fill the building lots of the city tiles with buildings (true/false)",
    };

    /// set comment lines from Notes above the keys where there is none yet (the rest of the file stays unchanged)
    public static bool AddNotes(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
            bool changed = false;
            string section = "";
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith('[') && t.EndsWith(']')) { section = t[1..^1]; continue; }
                int eq = t.IndexOf('=');
                if (eq <= 0 || t.StartsWith(';') || t.StartsWith('#')) continue;
                if (!Notes.TryGetValue(section + "." + t[..eq].Trim(), out string note)) continue;
                if (i > 0 && lines[i - 1].TrimStart().StartsWith(';')) continue;
                lines.Insert(i, "; " + note);
                i++;
                changed = true;
            }
            if (changed) File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", Encoding.UTF8);
            return changed;
        }
        catch (Exception) { return false; }
    }

    /// add missing keys in existing sections of default.ini (at the end of the respective section, the rest of the file stays
    /// unchanged). So after an update every new value is in default.ini. Returns true if something was added.
    public static bool AddMissingKeys(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
            bool changed = false;
            var std = new StandardValues();
            var builtIn = Reflected.Select(r => (r.Section, Values: Dump(Activator.CreateInstance(r.Type), r.Skip)))
                .Concat(new[] { ("Asphalt", RoadDict(std.Asphalt)), ("Gravel", RoadDict(std.Gravel)), ("Eraser", EraserDict(std)), ("Biomes", BiomeDict(std)) });
            foreach (var (section, values) in builtIn)
            {
                int head = lines.FindIndex(l => l.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));
                if (head < 0) continue;
                int end = head + 1;
                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int lastContent = head;
                for (; end < lines.Count && !lines[end].TrimStart().StartsWith('['); end++)
                {
                    string t = lines[end].Trim();
                    if (t == "") continue;
                    lastContent = end;
                    int eq = t.IndexOf('=');
                    if (eq > 0 && !t.StartsWith(';') && !t.StartsWith('#')) have.Add(t[..eq].Trim());
                }
                var add = values.Where(kv => !have.Contains(kv.Key)).Select(kv => kv.Key + " = " + kv.Value).ToList();
                if (add.Count == 0) continue;
                lines.InsertRange(lastContent + 1, add);
                changed = true;
            }
            if (changed) File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", Encoding.UTF8);
            return changed;
        }
        catch (Exception) { return false; }
    }

    /// text of the sections [Generator] [Cities] [Roads] [Wilderness] [Tools] with the built-in values
    public static string DefaultSections(IEnumerable<string> existing)
    {
        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var (section, type, skip) in Reflected)
        {
            if (have.Contains(section)) continue;
            sb.Append('[').Append(section).Append("]\r\n");
            foreach (var (k, v) in Dump(Activator.CreateInstance(type), skip))
            {
                if (Notes.TryGetValue(section + "." + k, out string note)) sb.Append("; ").Append(note).Append("\r\n");
                sb.Append(k).Append(" = ").Append(v).Append("\r\n");
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}
