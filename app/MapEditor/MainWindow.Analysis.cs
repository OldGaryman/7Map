using System.Globalization;
using System.Windows;
using System.Windows.Input;
using MapEditor.Map;

namespace MapEditor;

/// Project check (the user's request 2026-11-13): instead of keeping data of other game versions, compare the project with the
/// installed game (main game folder) and show what is missing there; replacements are offered, nothing is changed without a choice.
/// Opens by itself after loading when something is missing (once per project and session), otherwise via Home > Convert.
public partial class MainWindow
{
    AnalysisWindow analysisWindow;
    CheckResult lastCheck;
    readonly HashSet<string> checkedProjects = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<CheckIssue, int> shownCopy = new();

    // descriptions of the installed game for the replacements: name → description (buildings/fillers) and all files (parts)
    string targetFolder;
    Dictionary<string, string> targetFiles;
    Dictionary<string, PrefabDef> targetIndex;
    readonly Dictionary<string, PrefabDef> targetParts = new(StringComparer.OrdinalIgnoreCase);

    async void CheckButton_Click(object sender, RoutedEventArgs e) => await ShowCheck(false);

    /// installed game = main game folder (most players always have the newest version)
    string TargetGame => settings.MainGameFolder;

    void UseTarget(string game)
    {
        if (string.Equals(game, targetFolder, StringComparison.OrdinalIgnoreCase)) return;
        targetFolder = game;
        targetFiles = null;
        targetIndex = null;
        targetParts.Clear();
    }

    // all buildings and fillers of the installed game (read once, about 1,000 descriptions)
    Dictionary<string, PrefabDef> TargetIndex()
    {
        if (targetIndex != null) return targetIndex;
        targetFiles ??= PrefabCatalog.AllFiles(targetFolder);
        var index = new Dictionary<string, PrefabDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, file) in targetFiles)
        {
            string f = file.Replace('/', '\\');
            if (f.Contains("\\Parts\\", StringComparison.OrdinalIgnoreCase) || f.Contains("\\RWGTiles\\", StringComparison.OrdinalIgnoreCase)) continue;
            var d = PrefabCatalog.Describe(file);
            if (!d.Found) continue;
            d.Kind = name.Contains("_filler", StringComparison.OrdinalIgnoreCase) ? PrefabKind.Filler : PrefabKind.Poi;
            index[name] = d;
        }
        foreach (var (name, label) in PrefabCatalog.Labels(targetFolder, index.Keys)) index[name].Label = label;
        return targetIndex = index;
    }

    // description of a part in the installed game (driveways of a replacement)
    PrefabDef TargetPartDef(string name)
    {
        if (targetParts.TryGetValue(name, out var def)) return def.Found ? def : null;
        targetFiles ??= PrefabCatalog.AllFiles(targetFolder);
        string file = targetFiles.GetValueOrDefault(name);
        def = file == null ? new PrefabDef() : PrefabCatalog.Describe(file);
        def.Kind = PrefabKind.Part;
        targetParts[name] = def;
        return def.Found ? def : null;
    }

    async Task<CheckResult> RunCheck()
    {
        var w = world;
        string game = TargetGame;
        if (w == null || game == null) return null;
        UseTarget(game);
        var names = w.Prefabs.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = await Task.Run(() =>
        {
            var defs = PrefabCatalog.Load(game, names);
            return ProjectCheck.Run(w, defs, TargetIndex);
        });
        result.Project = projectProfile;
        result.Target = GameLocator.ProfileOf(game);
        result.TargetFolder = game;
        return result;
    }

    /// after loading a project: check it once; the window opens only if something is missing
    async Task AutoCheck()
    {
        if (world == null || !checkedProjects.Add(world.Folder)) return;
        await ShowCheck(true);
    }

    async Task ShowCheck(bool auto)
    {
        if (world == null) return;
        if (TargetGame == null)
        {
            if (!auto) MessageBox.Show(this, Loc.T("7 Days to Die was not found. Choose the game folder under Settings > Paths."), Loc.T("Convert project"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        CheckResult r;
        Mouse.OverrideCursor = auto ? null : Cursors.Wait;
        try { r = await RunCheck(); }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); return; }
        finally { Mouse.OverrideCursor = null; }
        if (r == null) return;
        lastCheck = r;
        if (auto && r.Issues.Count == 0) return;
        if (analysisWindow == null || !analysisWindow.IsLoaded)
        {
            analysisWindow = new AnalysisWindow { Owner = this };
            analysisWindow.ShowIssue += ShowCheckIssue;
            analysisWindow.ReplaceRequested += ReplaceMissing;
            analysisWindow.AutoRequested += ReplaceAll;
            analysisWindow.Closed += (_, _) => { analysisWindow = null; Map.Selected = null; };
            analysisWindow.Show();
        }
        shownCopy.Clear();
        analysisWindow.Show(r, n => TargetIndex().GetValueOrDefault(n));
        analysisWindow.Activate();
    }

    // show the next copy of an entry on the map (blue frame)
    void ShowCheckIssue(CheckIssue i)
    {
        if (world == null || i.Instances.Count == 0) return;
        int k = shownCopy.GetValueOrDefault(i);
        shownCopy[i] = k + 1;
        var p = i.Instances[k % i.Instances.Count];
        if (!world.Prefabs.Contains(p)) return;
        Map.Selected = p;
        Map.CenterOn(p.X + (p.Def?.Found == true ? p.SizeX / 2.0 : 0), p.Z + (p.Def?.Found == true ? p.SizeZ / 2.0 : 0), 0.75);
    }

    /// replace the chosen buildings: one editing step for all copies (undo with Ctrl+Z)
    void ReplaceMissing(List<(CheckIssue Issue, string Name)> chosen)
    {
        if (world == null || edits == null || Map.Busy) return;
        var w = world;
        var index = TargetIndex();
        double bumps = settings.EraserBumps;
        var jobs = new List<(PlacedPrefab Old, PlacedPrefab New, List<PlacedPrefab> OldParts, List<PlacedPrefab> NewParts, bool Wild)>();
        int skipped = 0;
        foreach (var (issue, name) in chosen)
        {
            if (!index.TryGetValue(name, out var def)) continue;
            foreach (var old in issue.Instances.Where(w.Prefabs.Contains))
            {
                var lot = ProjectCheck.LotOf(w, old) ?? (old.Def is { Found: true } ? TileLots.Under(w, old) : null);
                if (lot != null && !TileLots.Fits(def, lot)) { skipped++; continue; }   // another lot of the same building with other size/tag
                var p = ProjectCheck.Replacement(w, old, lot, name, def);
                jobs.Add((old, p, ProjectCheck.OldParts(w, old, lot), PrefabParts.For(p, TargetPartDef, lot?.District), lot == null));
            }
        }
        if (jobs.Count == 0)
        {
            analysisWindow?.SetResult(Loc.T("Nothing replaced: the chosen buildings do not fit."));
            return;
        }
        string detail = "Ersetzt (Projektprüfung): " + string.Join("; ", jobs.Select(j => string.Format(CultureInfo.InvariantCulture,
            "{0} {1},{2},{3} r{4} -> {5} {6},{7},{8} r{9}", j.Old.Name, j.Old.X, j.Old.Y, j.Old.Z, j.Old.Rotation, j.New.Name, j.New.X, j.New.Y, j.New.Z, j.New.Rotation)));
        RunPrefabStep(Loc.F("{0} building(s) replaced (project check)", jobs.Count), detail, e =>
        {
            foreach (var j in jobs)
            {
                foreach (var q in j.OldParts) e.RemovePrefab(q);
                e.ReplacePrefab(j.Old, j.New);
                foreach (var q in j.NewParts) e.AddPrefab(q);
                if (!j.Wild || !ShapesGround(j.New)) continue;   // on a lot the tile gives the height
                GroundShaper.Flatten(w, e, j.New, j.New.GroundHeight, bumps);
                foreach (var q in j.NewParts) GroundShaper.Flatten(w, e, q, j.New.GroundHeight, bumps);
            }
        }, null);
        string msg = Loc.F("{0} building(s) replaced (undo with Ctrl+Z).", jobs.Count) + (skipped > 0 ? " " + Loc.F("{0} copy/copies left: the replacement does not fit their lot.", skipped) : "");
        _ = RefreshCheckAfter(msg);
    }

    // all city tiles of the installed game (for "Replace everything automatically")
    List<(string Name, PrefabDef Def)> TargetTiles()
    {
        targetFiles ??= PrefabCatalog.AllFiles(targetFolder);
        var list = new List<(string, PrefabDef)>();
        foreach (var (name, file) in targetFiles)
        {
            if (!name.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase)) continue;
            var d = PrefabCatalog.Describe(file);
            if (!d.Found) continue;
            d.Kind = PrefabKind.Tile;
            list.Add((name, d));
        }
        foreach (var (name, label) in PrefabCatalog.Labels(targetFolder, list.Select(t => t.Item1)))
            list.First(t => t.Item1.Equals(name, StringComparison.OrdinalIgnoreCase)).Item2.Label = label;
        return list.OrderBy(t => t.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// "Replace everything automatically" (the user's request 2026-11-20, converting a 3.4 project for 2.6 with Undead Legacy):
    /// tiles, buildings, fillers and parts in one editing step (ProjectConvert)
    async void ReplaceAll()
    {
        if (world == null || edits == null || Map.Busy || TargetGame == null) return;
        if (MessageBox.Show(analysisWindow, Loc.T("Replace everything that the installed game does not have or that does not fit?\n\nCity tiles get a tile of the same district and shape with fitting road exits; their lots are built up again. Buildings and fillers get a fitting replacement, missing parts are removed. Then the project is saved and main.ttw and map_info.xml are written for the installed game version.\n\nUndo restores the saved backup."),
                Loc.T("Convert project"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var w = world;
        UseTarget(TargetGame);
        Dictionary<string, PrefabDef> index;
        List<(string Name, PrefabDef Def)> tiles;
        Mouse.OverrideCursor = Cursors.Wait;
        try { (index, tiles) = await Task.Run(() => (TargetIndex(), TargetTiles())); }
        finally { Mouse.OverrideCursor = null; }
        if (world != w || edits == null) return;
        double bumps = settings.EraserBumps;
        ConvertReport report = null;
        RunPrefabStep(Loc.T("Project converted to the installed game (project check)"), "Projektprüfung: alles automatisch ersetzt", e =>
        {
            report = ProjectConvert.Run(w, e, index, tiles, TargetPartDef, bumps);
        }, null);
        if (report == null) return;
        string format = await ConvertProjectFormat();
        string msg = Loc.F("Replaced: {0} city tile(s), {1} building(s) on lots, {2} in the wilderness; {3} part(s) removed.", report.Tiles, report.LotBuildings, report.WildBuildings, report.PartsRemoved)
            + (report.Kept > 0 ? " " + Loc.F("{0} could not be replaced (nothing fits).", report.Kept) : "") + " " + Loc.T("Undo with Ctrl+Z.")
            + (report.Notes.Count > 0 ? "\n" + string.Join("\n", report.Notes.Take(5)) + (report.Notes.Count > 5 ? "\n…" : "") : "")
            + (format != null ? "\n" + format : "");
        await RefreshCheckAfter(msg);
    }

    /// after replacing (the user's request 2026-11-20): save the project, then write main.ttw and map_info.xml in the format of the
    /// installed game and mark the project for it; the world folder in the game follows the new profile (2.6 + Undead Legacy → its
    /// own GeneratedWorlds). Returns the message text, null = nothing to do or not saved
    async Task<string> ConvertProjectFormat()
    {
        var target = GameLocator.ProfileOf(TargetGame);
        if (world == null || target?.Version == null) return null;
        bool sameFormat = projectProfile != null && projectProfile.Matches(target)
            && GameVersion.Parse(GameVersion.TtwVersionText(File.Exists(Path.Combine(world.Folder, "main.ttw")) ? File.ReadAllBytes(Path.Combine(world.Folder, "main.ttw")) : null))?.Short == target.Version.Short;
        if (sameFormat) return null;
        // saving makes the backup (with main.ttw, map_info.xml and yamee_project.json): "Undo" after saving brings the old version back
        bool saved = edits.HasChanges;
        if (saved && !await Save()) return Loc.T("The project was not saved, so it still has the format of the old game version.");
        string backup = saved ? Backups.List(world).FirstOrDefault()?.Folder : null;
        backup ??= Backups.Create(world, "Vor Konvertierung auf " + target.Text).Folder;
        List<string> changed;
        try { changed = ProjectConvert.ConvertFormat(world.Folder, target, GameLocator.WorldsFolderFor(target), backup); }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex))
        {
            ErrorLog.Write(ex);
            return Loc.T("Writing main.ttw/map_info.xml failed:") + " " + ex.Message;
        }
        // the world in the game: the folder of the new profile (otherwise "Copy to the game" would write into the old version)
        string worlds = GameLocator.WorldsFolderFor(target);
        if (settings.GameWorlds.TryGetValue(world.Name, out var dir) && !string.Equals(Path.GetDirectoryName(dir), worlds, StringComparison.OrdinalIgnoreCase))
        {
            settings.GameWorlds[world.Name] = Path.Combine(worlds, world.Name);
            settings.Save();
        }
        projectProfile = target;
        projectGameExact = true;
        ShowVersionInfo();
        UpdateUndo();
        return Loc.F("Project saved and converted to {0} (main.ttw, map_info.xml). The files the old game version made from the map were moved to the backup.", target.Text);
    }

    async Task RefreshCheckAfter(string msg)
    {
        await ShowCheck(false);
        analysisWindow?.SetResult(msg);
    }
}
