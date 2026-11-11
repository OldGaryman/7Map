using System.Globalization;
using System.Windows;
using MapEditor.Map;

namespace MapEditor;

/// Straßen zwischen den Städten erzeugen (Wunsch des Nutzers 7.11.2026, Schritt 3 nach Terrain und Städten).
/// Stufe 1: Netz planen und in der Karte zeigen (RoadNetwork), die Welt bleibt unverändert. Das Fenster bleibt offen,
/// damit man die Vorschau ansehen, Werte ändern und neu planen kann; Schließen nimmt die Vorschau weg.
/// Stufe 2: „Straßen bauen“ trägt das gezeigte Netz ein (NetworkBuilder) – ein Bearbeitungsschritt, Strg+Z nimmt alles zurück.
public partial class MainWindow
{
    RoadNetWindow roadNetWindow;
    NetworkSpec lastNetwork;

    async void RoadNetButton_Click(object sender, RoutedEventArgs e)
    {
        if (world == null || edits == null) return;
        if (roadNetWindow != null) { roadNetWindow.Activate(); return; }
        if (!await ConfirmSwitch(null)) return;
        var start = lastNetwork?.Clone() ?? UserValues.Load(new NetworkSpec { Seed = world.Name });
        var dlg = new RoadNetWindow(start) { Owner = this };
        dlg.Left = Left + Math.Max(0, ActualWidth - dlg.Width - 420);
        dlg.Top = Top + 120;
        dlg.Planner = PlanNetwork;
        dlg.Builder = BuildNetwork;
        dlg.HasPlan = () => Map.NetworkPreview?.Roads.Count > 0;
        dlg.Closed += (_, _) => { Map.NetworkPreview = null; roadNetWindow = null; Activate(); };
        roadNetWindow = dlg;
        dlg.Show();
    }

    async Task<string> PlanNetwork(NetworkSpec spec)
    {
        lastNetwork = spec;
        UserValues.Remember(spec);
        var w = world;
        if (w == null || Map.Busy) return Loc.T("Please wait until the current work is finished.");
        var win = roadNetWindow;
        IProgress<string> progress = new Progress<string>(t => win?.ShowProgress(t));
        NetworkPlan plan;
        Map.Busy = true;
        try { plan = await Task.Run(() => new RoadNetwork(w, spec).Plan(progress)); }
        finally { Map.Busy = false; }
        if (world != w) return Loc.T("The world was changed meanwhile – please plan again.");
        Map.NetworkPreview = plan;
        return NetworkSummary(plan);
    }

    /// das gezeigte Netz bauen (ein Bearbeitungsschritt); null = nichts gebaut (Text bleibt)
    async Task<string> BuildNetwork()
    {
        var plan = Map.NetworkPreview;
        var spec = lastNetwork;
        var w = world;
        if (plan == null || plan.Roads.Count == 0 || spec == null || w == null || edits == null) return null;
        if (Map.Busy) return Loc.T("Please wait until the current work is finished.");
        var win = roadNetWindow;
        IProgress<string> progress = new Progress<string>(t => win?.ShowProgress(t));
        string title = Loc.F("{0} road(s) between towns", plan.Roads.Count);
        string detail = string.Format(CultureInfo.InvariantCulture, "Straßen zwischen Städten (Startwert {0}, Steigung {1:0.#} % / eng {2:0.#} %, Querverbindungen {3}, Kurvigkeit {4}, Schneisen {5}): {6} Straßen, {7:0.0} km",
            spec.Seed, spec.Grade * 100, spec.MaxGrade * 100, spec.Loops, spec.Curves, spec.Cuts, plan.Roads.Count, plan.Roads.Sum(r => r.Length) / 1000);
        NetworkBuildResult res = null;
        Map.Busy = true;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var r = NetworkBuilder.Build(w, e, plan, spec, progress);
                res ??= r;   // Ergebnis des ersten Durchgangs (spätere: Wiederholen/Neu rechnen)
            }));
            if (world != w) return null;
            step.Note = Loc.F("{0} road(s), {1:0.0} km.", res.Roads, res.Length / 1000);
            Map.NetworkPreview = null;
            Refresh(step);
            return BuildSummary(res);
        }
        finally
        {
            Map.Busy = false;
            Cursor = null;
            UpdateButtons();
            UpdateUndo();
        }
    }

    static string BuildSummary(NetworkBuildResult res)
    {
        var lines = new List<string>
        {
            Loc.F("{0} road(s) built, {1:0.0} km in all.", res.Roads, res.Length / 1000),
            Loc.F("Steepest place {0:0.#} %; {1} road(s) needed more than the usual gradient.", res.MaxGrade * 100, res.SteepRoads),
            Loc.F("Deepest cut {0:0} m, highest embankment {1:0} m.", res.MaxCut, res.MaxFill),
            Loc.F("Embankments higher than 1.5 m: {0:0} m in all, longest {1:0} m.", res.FillLength, res.LongestFill),
        };
        string text = string.Join("\n", lines);
        if (res.Warnings.Count > 0) text += "\n\n" + Loc.T("Notes:") + "\n" + string.Join("\n", res.Warnings.Select(p => "• " + p));
        text += "\n\n" + Loc.T("Undo with Ctrl+Z, keep with Save. Next step: Creation → Wilderness POIs (optional), then save and copy the project into the game.");
        return text;
    }

    static string NetworkSummary(NetworkPlan plan)
    {
        if (plan.Towns.Count == 0) return Loc.T("There are no towns on this map. Generate towns first.");
        var roads = plan.Roads;
        double asphalt = roads.Where(r => !r.Gravel).Sum(r => r.Length) / 1000, gravel = roads.Where(r => r.Gravel).Sum(r => r.Length) / 1000;
        int connected = plan.Towns.Count(t => t.Connected);
        var lines = new List<string>
        {
            Loc.F("{0} of {1} towns connected ({2} Old West).", connected, plan.Towns.Count, plan.Towns.Count(t => t.OldWest)),
            Loc.F("{0} roads: {1:0.0} km asphalt, {2:0.0} km gravel.", roads.Count, asphalt, gravel),
            Loc.F("{0} junctions, {1} crossings, {2} cross connections, {3} roads with a cut (deepest about {4:0} m).",
                plan.Junctions.Count, plan.CrossingPoints.Count, roads.Count(r => r.Loop), roads.Count(r => r.Cut), roads.Count(r => r.Cut) > 0 ? roads.Max(r => r.MaxCut) : 0),
            Loc.F("{0} feeder road(s) from free town exits, {1} short link(s) from passing roads to town streets.", roads.Count(r => r.Feeder), roads.Count(r => r.TownLink)),
            Loc.F("Unused town exits (dead ends): {0}.", plan.Towns.Sum(t => t.Exits.Count(e => e.Reachable && !e.Used))),
            Loc.F("Gradient (terrain along the road, averaged over 100 m): up to {0:0.#} %; steeper than usual on {1:0.#} % of the length.",
                roads.Count > 0 ? roads.Max(r => r.MaxGrade) * 100 : 0, roads.Sum(r => r.Length) > 0 ? roads.Sum(r => r.SteepShare * r.Length) / roads.Sum(r => r.Length) * 100 : 0),
            Loc.F("Practically straight (over 300 m): {0:0.#} % of the length.", plan.StraightShare * 100),
            Loc.F("Planned in {0:0.0} s.", plan.Seconds),
        };
        string text = string.Join("\n", lines);
        if (plan.Problems.Count > 0) text += "\n\n" + Loc.T("Notes:") + "\n" + string.Join("\n", plan.Problems.Select(p => "• " + p));
        text += "\n\n" + Loc.T("On the map: dark = asphalt road, purple = cross connection, brown = gravel road, red dashes = cut, yellow ring = junction, magenta ring = crossing, orange dot = unused town exit.")
            + "\n" + Loc.T("Check the network on the map, then click \"Build roads\".");
        return text;
    }
}
