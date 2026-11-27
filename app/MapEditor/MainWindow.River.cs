using System.Globalization;
using System.Windows;
using System.Windows.Input;
using MapEditor.Map;

namespace MapEditor;

/// Connect lakes (the user's request 2026-11-18, FLUSS_ANALYSE.md): click into lake A and lake B, "Find path" searches the course of
/// the river (Map/River.cs) and shows it as an editable line; "Apply" builds it: both lakes and the river get the level of the lower lake,
/// the higher lake is dug deeper. With three or more points set by hand the line itself is the course (no search).
public partial class MainWindow
{
    RiverPlan connectPlan;   // found path (null = not searched yet)
    double riverWidthMin = 16, riverWidthMax = 28, riverDepth = 4.5;

    static string HelpConnect => Loc.T("Connect lakes: click into the first and the second lake, then Find path or Enter · drag a point = move it · drag right = move the map · Esc = discard");

    double AreaWidthNow => waterKind == 4 ? (riverWidthMin + riverWidthMax) / 2 : waterWidth;

    void ShowRiverValues()
    {
        riverWidthMin = settings.RiverWidthMin; riverWidthMax = settings.RiverWidthMax; riverDepth = settings.RiverDepth;
        RiverWidthMinBox.Text = Fmt(riverWidthMin);
        RiverWidthMaxBox.Text = Fmt(riverWidthMax);
        RiverDepthBox.Text = Fmt(riverDepth);
        BridgeGravelRadio.IsChecked = settings.BridgeGravel;
        BridgeAsphaltRadio.IsChecked = !settings.BridgeGravel;
    }

    /// bridge tool: approaches of asphalt or gravel (saved at once like all tool values)
    void BridgeSurface_Click(object sender, RoutedEventArgs e)
    {
        bool gravel = BridgeGravelRadio.IsChecked == true;
        if (settings.BridgeGravel == gravel) return;
        settings.BridgeGravel = gravel;
        settings.Save();
    }

    void ReadRiverValues(bool save)
    {
        riverWidthMin = ReadBox(RiverWidthMinBox, 6, 60, riverWidthMin);
        riverWidthMax = Math.Max(riverWidthMin, ReadBox(RiverWidthMaxBox, 6, 80, riverWidthMax));
        riverDepth = ReadBox(RiverDepthBox, 1, 15, riverDepth);
        if (!save || (settings.RiverWidthMin == riverWidthMin && settings.RiverWidthMax == riverWidthMax && settings.RiverDepth == riverDepth)) return;
        settings.RiverWidthMin = riverWidthMin; settings.RiverWidthMax = riverWidthMax; settings.RiverDepth = riverDepth;
        settings.Save();
    }

    /// text of the Apply button and the hint in the box above the map
    string ConnectApplyText => connectPlan == null && Map.Area.Count == 2 ? Loc.T("Find path") : Loc.T("Apply");

    string ConnectHint => Map.Area.Count == 0
        ? Loc.T("Click into the first lake, then into the second lake. Then click Find path.")
        : Map.Area.Count == 1
        ? Loc.T("Now click into the second lake.")
        : connectPlan == null && Map.Area.Count == 2
        ? Loc.T("Click Find path (or press Enter): YAMEE searches a course through low land. With more points set by hand, the line itself is the course.")
        : Loc.T("Change the course if you like: drag a point = move it, click on the line = add a point, right-click a point = delete it. The first and the last point must stay in the lakes. Then Apply. Esc = discard.");

    static string HelpBridge => Loc.T("Bridge: click onto a straight piece of a river · drag right = move the map · Esc = end the tool");
    static string BridgeHint => Loc.T("Click onto a straight piece of a river that flows roughly north–south or west–east. The bridge is built at once; undo with Ctrl+Z.");

    /// bridge over the river at the clicked point (Map/Bridge.cs): one edit step
    async Task PlaceBridge(Point p)
    {
        if (world == null || edits == null || Map.Busy) return;
        var w = world;
        string game = GameFolder;
        prefabFiles ??= await Task.Run(() => game == null ? new Dictionary<string, string>() : PrefabCatalog.AllFiles(game));
        bool gravel = settings.BridgeGravel;
        var kits = KitsForTool(gravel);
        if (kits == null)
        {
            MessageBox.Show(this, Loc.T("No bridge kit is active. Choose one under Settings › Mods › Bridge mods (YAMEE_Bridges appears there when it is in the Mods folder of the game)."),
                Loc.T("Bridge"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var site = kits.Find(w, p.X, p.Y, gravel);
        bool narrow = false;
        // one bridge chosen by hand and the river too wide for it: narrow the river after asking (like swapping, the user's request 2026-11-26)
        if (site.TooWide && kits.Kits.Count == 1)
        {
            var ns = Bridge.Find(w, p.X, p.Y, kits.Kits[0], Bridge.MaxAngle, null, null, narrow: true);
            if (ns.Problem == null)
            {
                string q = Loc.F("For {0} the river has to be reshaped: it is {1:0} m wide here and is narrowed to {2} m under the bridge (about {3:0} m on each bank). Up- and downstream the banks run out over about {4} m.",
                    kits.Kits[0].Label, ns.NarrowFrom, ns.WaterWidth, ns.NarrowPerSide, Bridge.BlendFor(ns)) + "\n\n" + Loc.T("Build anyway? (Undo with Ctrl+Z.)");
                if (MessageBox.Show(this, q, Loc.T("Bridge"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                site = ns;
                narrow = true;
            }
        }
        site.Gravel = gravel;
        if (site.Problem != null) { BridgeNote.Text = site.Problem; MessageBox.Show(this, site.Problem, Loc.T("Bridge"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var kit = kits.Get(site.Kit);
        var (endDef, tileDef) = kits.Defs(site);
        var c = site.Center;
        string title = Loc.F("Bridge at {0:0}, {1:0}", c.X, c.Y);
        string detail = string.Format(CultureInfo.InvariantCulture, "Brücke bei {0:0},{1:0}: {2}, {3} Mittelstück(e), {4}, Fahrbahn {5}, Wasser {6}, Zufahrten {7}", c.X, c.Y, kit?.Label ?? site.Kit, site.Tiles, site.AlongX ? "West–Ost" : "Nord–Süd", site.Deck, site.Level, gravel ? "Schotter" : "Asphalt");
        SetBusyTitle(Loc.T("Building bridge …"));
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            BridgeStats st = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var s = kit != null ? Bridge.Find(w, p.X, p.Y, kit, narrow: narrow) : null;   // again when repeating the step (the map may have changed before it)
                if (s == null || s.Problem != null) return;
                s.Gravel = gravel;
                var r = Bridge.Build(w, e, s, endDef, tileDef);
                st ??= r;
            }));
            if (st == null) return;
            step.Note = (kit != null ? kit.Label + ": " : "") + (gravel
                ? Loc.F("Bridge with {0} middle piece(s), {1} m long ({2}), deck at {3} m. The river was made straight and {4} m wide there; gravel approaches of {5} m on both sides.",
                    site.Tiles, site.Length, site.AlongX ? Loc.T("west–east") : Loc.T("north–south"), site.Deck, site.WaterWidth, Bridge.Approach)
                : Loc.F("Bridge with {0} middle piece(s), {1} m long ({2}), deck at {3} m. The river was made straight and {4} m wide there; asphalt approaches of {5} m on both sides.",
                    site.Tiles, site.Length, site.AlongX ? Loc.T("west–east") : Loc.T("north–south"), site.Deck, site.WaterWidth, Bridge.Approach));
            BridgeNote.Text = step.Note;
            Refresh(step);
            Recompute();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex))
        {
            MessageBox.Show(this, Loc.T("Building the bridge failed:") + "\n" + ex.Message, Loc.T("Bridge"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Map.Busy = false;
            Cursor = null;
            UpdateButtons();
            UpdateUndo();
            UpdateToolModule();
        }
    }

    async Task ConnectLakes()
    {
        if (world == null || edits == null || Map.Area.Count < 2) return;
        WaterValue_Changed(null, null);
        var pts = Map.Area.ToList();
        var w = world;
        var a = River.FindLake(w, pts[0].X, pts[0].Y);
        var b = River.FindLake(w, pts[^1].X, pts[^1].Y);
        string problem = a == null ? Loc.T("The first point does not lie in a lake.")
            : b == null ? Loc.T("The last point does not lie in a lake.")
            : a.Cells.Length > 0 && b.Cells.Length > 0 && a.Cells[0] == b.Cells[0] ? Loc.T("Both points lie in the same lake.")
            : Math.Abs(a.Level - b.Level) > River.MaxLower ? Loc.F("The water levels differ by {0} m (at most {1} m possible): the higher lake would have to be dug far too deep.", Math.Abs(a.Level - b.Level), River.MaxLower)
            : null;
        if (problem != null) { MessageBox.Show(this, problem, Loc.T("Connect lakes"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        SetBusyTitle(connectPlan == null && pts.Count == 2 ? Loc.T("Searching river course …") : Loc.T("Digging river …"));
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            if (connectPlan == null && pts.Count == 2)
            {
                int seed = Random.Shared.Next(1, 1_000_000);
                var plan = await Task.Run(() => River.Plan(w, a, b, seed));
                if (plan.Problem != null)
                {
                    MessageBox.Show(this, plan.Problem, Loc.T("Connect lakes"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                connectPlan = plan;
                Map.SetArea(plan.Line);
                string msg = Loc.F("River found: {0:0} m long, water level {1} m.", plan.Length, plan.Level);
                if (a.Level != b.Level) msg += " " + Loc.F("The higher lake is dug {0} m deeper.", Math.Abs(a.Level - b.Level));
                if (plan.MaxCut > 12) msg += " " + Loc.F("The valley cuts up to {0:0} m deep into the land.", plan.MaxCut);
                if (plan.LowGround > 0) msg += " " + Loc.F("On about {0} m the land is lower than the bank: a low embankment is built there.", plan.LowGround);
                RiverPlanNote.Text = msg;
                return;
            }
            var spec = new RiverSpec
            {
                ClickA = pts[0], ClickB = pts[^1], Line = pts, WidthMin = riverWidthMin, WidthMax = riverWidthMax, Depth = riverDepth,
                Seed = Random.Shared.Next(1, 1_000_000), Biome = waterBiome,
            };
            double cx = (pts[0].X + pts[^1].X) / 2, cz = (pts[0].Y + pts[^1].Y) / 2;
            string title = Loc.F("River between lakes at {0:0}, {1:0}", cx, cz);
            string detail = string.Format(CultureInfo.InvariantCulture, "Fluss zwischen Seen bei {0:0},{1:0}: {2} Punkte, Breite {3}–{4} m, Tiefe {5} m, Startwert {6}",
                cx, cz, pts.Count, spec.WidthMin, spec.WidthMax, spec.Depth, spec.Seed);
            RiverStats st = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var s = River.Build(w, e, spec);
                st ??= s;
            }));
            if (st == null || st.Water == 0) return;
            Map.ClearArea();
            connectPlan = null;
            step.Note = Loc.F("River {0:0} m long, {1:#,0} m² of water at {2} m above sea level.", st.Length, st.Water, st.Level) + " " +
                        Loc.F("{0:#,0} height points changed.", st.Changed);
            if (st.LoweredBy > 0) step.Note += " " + Loc.F("The higher lake was dug {0} m deeper.", st.LoweredBy);
            if (st.Embankment > 0) step.Note += " " + Loc.F("Where the land is lower than the bank, an embankment was built ({0:#,0} m²).", st.Embankment);
            if (st.Spared > 0) step.Note += " " + Loc.F("{0:#,0} m² on roads, buildings or tiles were left alone.", st.Spared);
            Refresh(step);
            Recompute();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex))
        {
            MessageBox.Show(this, Loc.T("Changing the water failed:") + "\n" + ex.Message, Loc.T("Water"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Map.Busy = false;
            Cursor = null;
            UpdateButtons();
            UpdateUndo();
            UpdateToolModule();
        }
    }
}
