using System.Globalization;
using System.Windows;
using MapEditor.Map;

namespace MapEditor;

/// Wildnis-POIs erzeugen (Wunsch des Nutzers 9.11.2026, Schritt 4 nach Terrain, Städten und Straßen): Gebäude mit dem Tag
/// „wilderness“ außerhalb der Städte setzen und die mit Einfahrt über 5 m breite Schotterwege an die nächste Straße oder einen
/// schon geplanten Weg anschließen. Wie „Straßen erzeugen“: erst planen und in der Karte ansehen, dann bauen (ein Schritt).
public partial class MainWindow
{
    WildernessWindow wildernessWindow;
    WildernessSpec lastWilderness;

    async void WildernessButton_Click(object sender, RoutedEventArgs e)
    {
        if (world == null || edits == null) return;
        if (wildernessWindow != null) { wildernessWindow.Activate(); return; }
        if (!await ConfirmSwitch(null)) return;
        // Hinweis (kein Verbot): Die Wege der POIs schließen an Straßen an, daher besser zuerst die Straßen erzeugen
        long asphalt = await Task.Run(() => world.RoadMask.LongCount(v => v == 2));
        if (asphalt < 100000)
            Msg.Info(this, settings, "wilderness-roads-first", Loc.T("Tip: generate the roads first (Creation → Roads). The gravel paths of the wilderness POIs connect to the nearest road; without roads they can only connect to each other, and buildings with a driveway will often find no place.\n\nYou can continue anyway."),
                Loc.T("Wilderness POIs"));
        var start = lastWilderness?.Clone() ?? UserValues.Load(new WildernessSpec { Seed = world.Name });
        var dlg = new WildernessWindow(start) { Owner = this };
        dlg.Left = Left + Math.Max(0, ActualWidth - dlg.Width - 420);
        dlg.Top = Top + 120;
        dlg.Planner = PlanWilderness;
        dlg.Builder = BuildWilderness;
        dlg.HasPlan = () => Map.WildernessPreview?.Pois.Count > 0;
        dlg.Closed += (_, _) => { Map.WildernessPreview = null; wildernessWindow = null; Activate(); };
        wildernessWindow = dlg;
        dlg.Show();
    }

    async Task<string> PlanWilderness(WildernessSpec spec)
    {
        lastWilderness = spec;
        UserValues.Remember(spec);
        var w = world;
        if (w == null || Map.Busy) return Loc.T("Please wait until the current work is finished.");
        if (!await EnsurePoiIndex()) return Loc.T("The game folder is needed to find the wilderness buildings (Settings → Paths).");
        // Gebäude mit Tag „wilderness“ und die Teile, die sie setzen (Zufahrten) – vorher lesen, PartDef nutzt Felder des Fensters
        var candidates = poiIndex.Where(kv => kv.Value.Tags.Contains("wilderness")).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var parts = new Dictionary<string, PrefabDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in candidates.Values.SelectMany(d => d.Parts).SelectMany(m => m.PartNames).Distinct(StringComparer.OrdinalIgnoreCase))
            if (PartDef(n) is PrefabDef d) parts[n] = d;
        var rules = WildernessRules.Load(GameFolder);
        var win = wildernessWindow;
        IProgress<string> progress = new Progress<string>(t => win?.ShowProgress(t));
        WildernessPlan plan;
        Map.Busy = true;
        try { plan = await Task.Run(() => new WildernessGenerator(w, spec, rules, candidates, n => parts.GetValueOrDefault(n)).Plan(progress)); }
        finally { Map.Busy = false; }
        if (world != w) return Loc.T("The world was changed meanwhile – please plan again.");
        Map.WildernessPreview = plan;
        return WildernessSummary(plan);
    }

    /// den gezeigten Plan bauen (ein Bearbeitungsschritt); null = nichts gebaut
    async Task<string> BuildWilderness()
    {
        var plan = Map.WildernessPreview;
        var spec = lastWilderness;
        var w = world;
        if (plan == null || plan.Pois.Count == 0 || spec == null || w == null || edits == null) return null;
        if (Map.Busy) return Loc.T("Please wait until the current work is finished.");
        var win = wildernessWindow;
        IProgress<string> progress = new Progress<string>(t => win?.ShowProgress(t));
        string title = Loc.F("{0} wilderness POI(s)", plan.Pois.Count);
        string detail = string.Format(CultureInfo.InvariantCulture, "Wildnis-POIs (Startwert {0}, Dichte {1:0.#} je km², Wege {2} m + Bankett {9:0.#} m, Steigung {3:0.#} % / eng {4:0.#} %): {5} Gebäude, {6} Schotterwege, {7:0.0} km; Gebäude: {8}",
            spec.Seed, spec.Density, $"{spec.PathWidthMin:0.#}–{spec.PathWidth:0.#}", spec.Grade * 100, spec.MaxGrade * 100, plan.Pois.Count, plan.Paths.Count, plan.Paths.Sum(p => p.Length) / 1000,
            string.Join(";", plan.Pois.Select(p => string.Format(CultureInfo.InvariantCulture, "{0} {1},{2},{3} r{4}", p.Building.Name, p.Building.X, p.Building.Y, p.Building.Z, p.Building.Rotation))), spec.Verge);
        WildernessBuildResult res = null;
        Map.Busy = true;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var r = WildernessGenerator.Build(w, e, plan, spec, progress);
                res ??= r;   // Ergebnis des ersten Durchgangs (spätere: Wiederholen/Neu rechnen)
            }));
            if (world != w) return null;
            step.Note = Loc.F("{0} building(s), {1} gravel path(s), {2:0.0} km.", res.Pois, res.Paths, res.Length / 1000);
            Map.WildernessPreview = null;
            Refresh(step);
            return WildernessBuildSummary(res);
        }
        finally
        {
            Map.Busy = false;
            Cursor = null;
            UpdateButtons();
            UpdateUndo();
        }
    }

    static string WildernessBuildSummary(WildernessBuildResult res)
    {
        var lines = new List<string>
        {
            Loc.F("{0} building(s) placed, ground levelled under each.", res.Pois),
            Loc.F("{0} gravel path(s) built, {1:0.0} km in all.", res.Paths, res.Length / 1000),
            Loc.F("Steepest place {0:0.#} %; {1} path(s) needed more than the usual gradient.", res.MaxGrade * 100, res.SteepPaths),
            Loc.F("Deepest cut {0:0} m, highest embankment {1:0} m.", res.MaxCut, res.MaxFill),
        };
        string text = string.Join("\n", lines);
        if (res.Warnings.Count > 0) text += "\n\n" + Loc.T("Notes:") + "\n" + string.Join("\n", res.Warnings.Select(p => "• " + p));
        text += "\n\n" + Loc.T("Undo with Ctrl+Z, keep with Save. Then save and copy the project into the game, and start a new game there.");
        return text;
    }

    static string WildernessSummary(WildernessPlan plan)
    {
        if (plan.Candidates == 0 && plan.Problems.Count > 0) return string.Join("\n", plan.Problems);
        var biomes = Enumerable.Range(0, 5).Where(b => plan.TargetPerBiome[b] > 0 || plan.PerBiome[b] > 0)
            .Select(b => "• " + Loc.F("{0}: {1} of {2}", Loc.T(BiomePalette.Names[b]), plan.PerBiome[b], plan.TargetPerBiome[b]));
        int withExit = plan.Pois.Count(p => p.HasExit);
        var lines = new List<string>
        {
            Loc.F("{0} wilderness POIs planned ({1} different buildings to choose from).", plan.Pois.Count, plan.Candidates),
            string.Join("\n", biomes),
            Loc.F("{0} with a driveway, all connected by a gravel path; {1} without a driveway (caves, camps, ruins …).", withExit, plan.Pois.Count - withExit),
            Loc.F("{0} gravel paths, {1:0.0} km; {2} of them join another new path.", plan.Paths.Count, plan.Paths.Sum(p => p.Length) / 1000, plan.Paths.Count(p => p.Target != null)),
            plan.Paths.Count > 0 ? Loc.F("Path length: on average {0:0} m, at most {1:0} m.", plan.Paths.Average(p => p.Length), plan.Paths.Max(p => p.Length)) : "",
            Loc.F("Planned in {0:0.0} s.", plan.Seconds),
        };
        if (plan.Existing.Sum() > 0)
            lines.Insert(1, Loc.F("{0} wilderness POI(s) are already on the map; only the rest up to the density is added.", plan.Existing.Sum()));
        string text = string.Join("\n", lines.Where(l => l.Length > 0));
        if (plan.Problems.Count > 0) text += "\n\n" + Loc.T("Notes:") + "\n" + string.Join("\n", plan.Problems.Select(p => "• " + p));
        text += "\n\n" + Loc.T("On the map: green frame = new building, brown line = gravel path.")
            + "\n" + Loc.T("Check them on the map, then click \"Build POIs\".");
        return text;
    }
}
