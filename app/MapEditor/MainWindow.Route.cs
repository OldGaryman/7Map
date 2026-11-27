using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MapEditor.Map;

namespace MapEditor;

/// Road generation from point to point (the user's request 2026-11-17): in the ribbon "Roads" next to the tools. A route between two
/// clicked places is searched like "Generate roads" (RoadNetwork) and shown as up to four variants (natural, direct, flat, serpentine);
/// the chosen one can be dragged through via points. Extras: close a dead end (target found automatically), gravel paths to buildings
/// near the route, gateway tiles where the route meets an existing road (T, X), and
/// re-laying a piece of an existing road. "Apply" builds everything as one edit step (RouteBuilder → NetworkBuilder).
public partial class MainWindow
{
    bool routeMode;
    string routeKind = "asphalt";     // asphalt, gravel, deadend, relay
    WorldData routeWorld;
    RoadNetwork routeNet;
    string routeNetKey;
    RoadNetwork.NetEnd routeStartEnd, routeEndEnd;     // as clicked (direction set when searching)
    Point? relayA;                                     // re-lay: first click on the road
    List<Point> relayOld;                              // re-lay: old course (removed when applying)
    double relayWidth;
    bool relayGravel;
    SegmentResult deleteSeg;                           // delete road: the marked piece between two junctions
    readonly List<RouteVariant> routeVariants = new();
    RouteVariant routeChosen;
    List<RouteTile> routeTiles = new();
    List<NetRoad> routeLegs = new();
    List<NetRoad> routeFeeders = new();
    readonly List<string> routeNotes = new();
    string routeStatus;                                // short text while searching
    int routeVersion;                                  // stale background results are dropped

    static readonly Color[] RouteColors = { Color.FromRgb(30, 110, 230), Color.FromRgb(230, 120, 20), Color.FromRgb(40, 160, 70), Color.FromRgb(180, 50, 190) };

    bool RouteGravel => routeKind == "gravel" || (routeKind == "relay" && relayGravel);

    async void RouteTool_Click(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        string kind = (string)button.Tag;
        if (button.IsChecked != true) { EndRoute(); return; }
        if (routeMode && routeKind == kind) return;
        if (!await ConfirmSwitch(sender)) { button.IsChecked = false; UpdateToolModule(); return; }
        if (world == null || edits == null) { button.IsChecked = false; UpdateToolModule(); return; }
        // both use the road preview on the map: close the "Generate roads" window
        if (roadNetWindow != null)
        {
            roadNetWindow.Close();
            roadNetWindow = null;
        }
        // end the other tools first (they would end the route tool in UpdateToolModule)
        EndRoute(update: false);
        ClearRoad();
        if (Map.EraseMode) SetEraseMode(false);
        EndSpawnMode();
        EndGhost();
        routeMode = true;
        routeKind = kind;
        routeWorld = world;
        ResetRoute();
        Map.NetworkBrush = RouteBrush;
        Map.ShowExits = true;   // snap points (bridge ends, entrances) like the road tools
        Map.MapClicked += RouteMapClicked;
        Map.NetViaDragged += RouteViaDragged;
        Map.NetViaRemoved += RouteViaRemoved;
        Map.NetRoadClicked += RouteRoadClicked;
        Map.RouteEndDragged += RouteEndDragged;
        ShowRouteValues();
        UpdateToolModule();
        // read the terrain for the search while the user clicks (deleting a road needs no search)
        if (kind == "delete") return;
        try { await EnsureRouteNet(); }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); }
        UpdateToolModule();
    }

    /// end the tool (the route not applied is discarded)
    void EndRoute(bool update = true)
    {
        if (!routeMode) return;
        routeMode = false;
        ResetRoute();
        Map.MapClicked -= RouteMapClicked;
        Map.NetViaDragged -= RouteViaDragged;
        Map.NetViaRemoved -= RouteViaRemoved;
        Map.NetRoadClicked -= RouteRoadClicked;
        Map.RouteEndDragged -= RouteEndDragged;
        Map.NetworkBrush = null;
        Map.PickMode = false;
        Map.ShowExits = false;
        Map.NetworkPreview = null;
        if (update) UpdateToolModule();
    }

    /// forget start, end and the courses (the tool stays on)
    void ResetRoute()
    {
        routeVersion++;
        routeStartEnd = routeEndEnd = null;
        relayA = null; relayOld = null;
        deleteSeg = null;
        routeVariants.Clear();
        routeChosen = null;
        routeTiles = new(); routeLegs = new(); routeFeeders = new();
        routeNotes.Clear();
        routeStatus = null;
        Map.RouteStart = Map.RouteEnd = null;
        Map.RemovePreview = null;
        Map.RouteProfile = null;
        RouteVariantList.Children.Clear();
        Map.NetworkSelected = null;
        Map.PickMode = routeMode;
        Map.NetworkPreview = routeMode ? new NetworkPlan { Towns = routeNet?.TownList ?? new() } : null;
        if (routeMode || Profile.Result != null && Map.Result == null) ShowProfile(null);
    }

    Brush RouteBrush(NetRoad r)
    {
        int k = routeVariants.FindIndex(v => v.Road == r);
        if (k >= 0)
        {
            var c = RouteColors[(int)routeVariants[k].Style % RouteColors.Length];
            bool chosen = routeVariants[k] == routeChosen;
            if (chosen && routeLegs.Count > 0 && !(routeLegs.Count == 1 && routeLegs[0] == r)) c.A = 60;   // drawn as pieces through the tiles
            else c.A = chosen ? (byte)230 : (byte)90;
            return new SolidColorBrush(c);
        }
        if (routeFeeders.Contains(r)) return new SolidColorBrush(Color.FromArgb(220, 160, 105, 45));
        if (routeChosen != null && routeLegs.Contains(r)) { var c = RouteColors[(int)routeChosen.Style % RouteColors.Length]; c.A = 230; return new SolidColorBrush(c); }
        return new SolidColorBrush(Color.FromArgb(230, 35, 35, 45));   // connectors of junction tiles
    }

    // ---------- values on the left ----------

    void ShowRouteValues()
    {
        loadingRouteValues = true;
        RouteGroupTitle.Text = Loc.T(RouteTitle());
        RouteWidthRow.Visibility = routeKind == "relay" ? Visibility.Collapsed : Visibility.Visible;
        RouteWidthBox.Text = (routeKind == "gravel" ? settings.RouteGravelWidth : settings.RouteAsphaltWidth).ToString("0.#", Num);
        RouteGradeBox.Text = settings.RouteGrade.ToString("0.#", Num);
        RouteMaxGradeBox.Text = settings.RouteMaxGrade.ToString("0.#", Num);
        RoutePoisCheck.IsChecked = settings.RoutePois;
        RoutePoiDistBox.Text = settings.RoutePoiDistance.ToString("0", Num);
        RoutePoiDistBox.IsEnabled = settings.RoutePois;
        RouteTilesCheck.IsChecked = settings.RouteJunctionTiles;
        RouteTilesCheck.IsEnabled = routeKind != "gravel";
        RouteBankingCheck.IsChecked = settings.RouteBanking;
        RouteBankingBox.Text = settings.RouteBankingMax.ToString("0.#", Num);
        RouteBankingBox.IsEnabled = settings.RouteBanking;
        RouteBreakWastelandCheck.IsChecked = settings.RouteBreakInWasteland;
        loadingRouteValues = false;
    }
    bool loadingRouteValues;

    string RouteTitle() => routeKind switch { "gravel" => "Gravel route", "deadend" => "Close dead end", "relay" => "Re-lay road", "delete" => "Delete road", _ => "Asphalt route" };

    static double? ReadNumber(TextBox box, double min, double max)
    {
        string t = box.Text.Trim().Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < min || v > max) return null;
        return v;
    }

    void RouteValue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        RouteValue_Changed(sender, e);
        e.Handled = true;
    }

    async void RouteValue_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingRouteValues || !IsLoaded) return;
        bool bad = false;
        double? width = ReadNumber(RouteWidthBox, 3, 24), grade = ReadNumber(RouteGradeBox, 2, 40), max = ReadNumber(RouteMaxGradeBox, 3, 50), dist = ReadNumber(RoutePoiDistBox, 20, 1000);
        void Mark(TextBox b, bool ok) { b.Foreground = ok ? ThemeManager.Brush("TextBrush", Brushes.Black) : ThemeManager.Brush("ErrorBrush", Brushes.Firebrick); bad |= !ok; }
        Mark(RouteWidthBox, width != null); Mark(RouteGradeBox, grade != null); Mark(RouteMaxGradeBox, max != null && (grade == null || max >= grade)); Mark(RoutePoiDistBox, dist != null);
        var old = (settings.RouteAsphaltWidth, settings.RouteGravelWidth, settings.RouteGrade, settings.RouteMaxGrade, settings.RoutePois, settings.RoutePoiDistance, settings.RouteJunctionTiles, settings.RouteBreakInWasteland);
        if (width is double wv) { if (routeKind == "gravel") settings.RouteGravelWidth = wv; else if (routeKind != "relay") settings.RouteAsphaltWidth = wv; }
        if (grade is double g) settings.RouteGrade = g;
        if (max is double m && m >= settings.RouteGrade) settings.RouteMaxGrade = m;
        if (dist is double d) settings.RoutePoiDistance = d;
        settings.RoutePois = RoutePoisCheck.IsChecked == true;
        settings.RouteJunctionTiles = RouteTilesCheck.IsChecked == true;
        settings.RouteBreakInWasteland = RouteBreakWastelandCheck.IsChecked == true;
        // banking in curves: only used when building, the course stays
        double? bank = ReadNumber(RouteBankingBox, 0.5, 12);
        Mark(RouteBankingBox, bank != null);
        bool bankChanged = settings.RouteBanking != (RouteBankingCheck.IsChecked == true) || (bank is double bv && bv != settings.RouteBankingMax);
        settings.RouteBanking = RouteBankingCheck.IsChecked == true;
        if (bank is double bm) settings.RouteBankingMax = bm;
        RouteBankingBox.IsEnabled = settings.RouteBanking;
        if (bankChanged) settings.Save();
        RoutePoiDistBox.IsEnabled = settings.RoutePois;
        var now = (settings.RouteAsphaltWidth, settings.RouteGravelWidth, settings.RouteGrade, settings.RouteMaxGrade, settings.RoutePois, settings.RoutePoiDistance, settings.RouteJunctionTiles, settings.RouteBreakInWasteland);
        if (now == old) return;
        settings.Save();
        if (bad || !routeMode || routeStartEnd == null || routeEndEnd == null || Map.Busy) return;
        // gradient, width or wasteland break changed: search again; extras only: compose again
        if (now.RouteGrade != old.RouteGrade || now.RouteMaxGrade != old.RouteMaxGrade || now.RouteAsphaltWidth != old.RouteAsphaltWidth || now.RouteGravelWidth != old.RouteGravelWidth || now.RouteBreakInWasteland != old.RouteBreakInWasteland)
            await SearchRoute();
        else await ComposeRoute();
    }

    double RouteWidth => routeKind == "relay" ? relayWidth : routeKind == "gravel" ? settings.RouteGravelWidth : settings.RouteAsphaltWidth;

    NetworkSpec RouteSpec()
    {
        var spec = UserValues.Load(new NetworkSpec { Seed = world?.Name ?? "YAMEE" });
        spec.Grade = settings.RouteGrade / 100;
        spec.MaxGrade = Math.Max(spec.Grade + 0.01, settings.RouteMaxGrade / 100);
        spec.BreakInWasteland = settings.RouteBreakInWasteland;
        return spec;
    }

    /// cost grid of the route search: read once, again after every change of the world (applied, undone)
    async Task<RoadNetwork> EnsureRouteNet()
    {
        var w = world;
        if (w == null || edits == null) return null;
        string key = $"{w.GetHashCode()}|{edits.Steps.Count}|{edits.Undone.Count()}|{edits.Steps.LastOrDefault()?.GetHashCode()}|{settings.RouteBreakInWasteland}";
        if (routeNet == null || routeNetKey != key)
        {
            routeStatus = Loc.T("Reading the terrain for the route search (only the first time after a change) …");
            UpdateToolModule();
            SetNodeTileDefs();
            var spec = RouteSpec();
            bool wasBusy = Map.Busy;
            if (!wasBusy) SetBusyTitle(routeStatus);
            Map.Busy = true;
            try { routeNet = await Task.Run(() => RoadNetwork.ForRoutes(w, spec)); }
            finally { Map.Busy = wasBusy; }
            routeNetKey = key;
            routeStatus = null;
            if (routeMode && Map.NetworkPreview != null) { Map.NetworkPreview.Towns = routeNet.TownList; Map.InvalidateVisual(); }
        }
        routeNet.BridgeEndList = FindBridgeEnds();
        routeNet.SetGrades(settings.RouteGrade / 100, settings.RouteMaxGrade / 100);
        return routeNet;
    }

    // ---------- clicks on the map ----------

    double ClickTolerance => Math.Max(20, 14 / Math.Max(Map.Scale, 1e-6));

    async void RouteMapClicked(Point p)
    {
        if (!routeMode || world == null || Map.Busy) return;
        try
        {
            if (routeKind == "delete") { await DeleteRoadClick(p); return; }
            var net = await EnsureRouteNet();
            if (net == null) return;
            if (routeStartEnd != null && routeEndEnd != null)
            {
                if (routeVariants.Count == 0)
                {
                    // no course was found: a click sets another end (the user's hint 2026-11-17: only Cancel was left)
                    await MoveRouteEnd(false, p, net);
                    return;
                }
                // click near a variant: choose it
                var hit = routeVariants.OrderBy(v => RoadNetwork.Project(v.Road.Points, p).Off).FirstOrDefault();
                if (hit != null && RoadNetwork.Project(hit.Road.Points, p).Off <= ClickTolerance) { ChooseVariant(hit); return; }
                SetRouteNote(Loc.T("Drag the chosen route to change its course, drag the green or red point to move the start or end, or click \"New start\"."));
                return;
            }
            if (routeKind == "relay") { await RelayClick(p, net); return; }
            if (routeKind == "deadend") { await DeadEndClick(p, net); return; }
            var end = net.RouteEndAt(p, ClickTolerance, out string problem);
            if (end == null) { SetRouteNote(problem); return; }
            if (routeStartEnd == null)
            {
                routeStartEnd = end;
                Map.RouteStart = end.P;
                routeNotes.Clear();
                UpdateToolModule();
                return;
            }
            if ((end.P - routeStartEnd.P).Length < 30) { SetRouteNote(Loc.T("Start and end are too close together.")); return; }
            routeEndEnd = end;
            Map.RouteEnd = end.P;
            await SearchRoute();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); SetRouteNote(Loc.T("Searching the course failed:") + " " + ex.Message); }
    }

    /// start (green) or end (red) point dragged on the map
    async void RouteEndDragged(bool isStart, Point p)
    {
        if (!routeMode || world == null || Map.Busy) return;
        try
        {
            var net = await EnsureRouteNet();
            if (net != null) await MoveRouteEnd(isStart, p, net);
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); SetRouteNote(Loc.T("Searching the course failed:") + " " + ex.Message); }
    }

    /// put the start or the end at another place and search again (dragged points of the old course are dropped)
    async Task MoveRouteEnd(bool isStart, Point p, RoadNetwork net)
    {
        if (routeKind == "relay")
        {
            var scan = new RoadScan(world);
            Point? a = isStart ? p : relayA, b = isStart ? routeEndEnd?.P : p;
            if (a is not Point ap) return;
            var sa = RoadFollower.Center(scan, ap);
            if (sa == null || scan.InTile(sa.Center.X, sa.Center.Y)) { SetRouteNote(Loc.T("Please click on an existing road (outside the town tiles).")); return; }
            relayA = sa.Center;
            Map.RouteStart = sa.Center;
            routeStartEnd = routeEndEnd = null;
            if (b is Point bp) await RelayClick(bp, net);
            else UpdateToolModule();
            return;
        }
        if (isStart && routeKind == "deadend")
        {
            routeStartEnd = routeEndEnd = null;
            Map.RouteStart = Map.RouteEnd = null;
            await DeadEndClick(p, net);
            return;
        }
        var end = net.RouteEndAt(p, ClickTolerance, out string problem);
        if (end == null) { SetRouteNote(problem); return; }
        var other = isStart ? routeEndEnd : routeStartEnd;
        if (other != null && (end.P - other.P).Length < 30) { SetRouteNote(Loc.T("Start and end are too close together.")); return; }
        if (isStart) { routeStartEnd = end; Map.RouteStart = end.P; }
        else { routeEndEnd = end; Map.RouteEnd = end.P; }
        routeNotes.Clear();
        if (routeStartEnd != null && routeEndEnd != null) await SearchRoute();
        else UpdateToolModule();
    }

    async Task DeadEndClick(Point p, RoadNetwork net)
    {
        var exit = net.FreeTownExits.OrderBy(e => (new Point(e.X, e.Z) - p).Length).FirstOrDefault();
        if (exit == null || (new Point(exit.X, exit.Z) - p).Length > ClickTolerance)
        {
            var red = net.TownList.SelectMany(t => t.Exits).Where(e => !e.Reachable && !e.Used && !e.Street)
                .OrderBy(e => (new Point(e.X, e.Z) - p).Length).FirstOrDefault();
            if (red != null && (new Point(red.X, red.Z) - p).Length <= ClickTolerance)
                SetRouteNote(Loc.T("This exit cannot be connected:") + " " + red.Why);
            else
                SetRouteNote(Loc.T("Please click a dead-end sign (a free exit of a town)."));
            return;
        }
        var start = new RoadNetwork.NetEnd(exit, null, new Point(exit.X, exit.Z), new Vector(exit.DX, exit.DZ));
        routeStartEnd = start;
        Map.RouteStart = start.P;
        int version = routeVersion;
        var w = world;
        RoadNetwork.NetEnd target = null;
        string problem = null;
        await RouteBusy(Loc.T("Looking for the best target …"), async progress =>
        {
            target = await Task.Run(() => net.AutoTarget(start, RouteGravel, RouteWidth, progress, out problem));
        });
        if (version != routeVersion || world != w) return;
        if (target == null) { routeStartEnd = null; Map.RouteStart = null; SetRouteNote(problem); return; }
        routeEndEnd = target;
        Map.RouteEnd = target.P;
        await SearchRoute();
    }

    async Task RelayClick(Point p, RoadNetwork net)
    {
        var scan = new RoadScan(world);
        var spot = RoadFollower.Center(scan, p);
        if (spot == null || scan.InTile(spot.Center.X, spot.Center.Y) || (spot.Center - p).Length > Math.Max(ClickTolerance, spot.Width / 2 + 2))
        {
            SetRouteNote(Loc.T("Please click on an existing road (outside the town tiles)."));
            return;
        }
        if (relayA == null)
        {
            relayA = spot.Center;
            Map.RouteStart = spot.Center;
            routeNotes.Clear();
            UpdateToolModule();
            return;
        }
        var a = relayA.Value;
        var b = spot.Center;
        int version = routeVersion;
        var w = world;
        (TraceResult Old, RoadNetwork.NetEnd Start, RoadNetwork.NetEnd End)? res = null;
        string problem = null;
        await RouteBusy(Loc.T("Following the old road …"), async _ =>
        {
            res = await Task.Run(() => { try { return net.BeginRelay(a, b, out problem); } finally { net.EndRelay(); } });
        });
        if (version != routeVersion || world != w) return;
        if (res == null) { SetRouteNote(problem); return; }
        var r = res.Value;
        relayOld = r.Old.Points;
        relayWidth = Math.Clamp(Math.Round(r.Old.Width - 2), 3, 24);
        relayGravel = r.Old.Kind == Surface.Gravel;
        routeStartEnd = r.Start;
        routeEndEnd = r.End;
        Map.RouteStart = r.Start.P;
        Map.RouteEnd = r.End.P;
        await SearchRoute();
    }

    // ---------- search and compose ----------

    /// both ends of the route with their directions (re-laying: from the trace)
    (RoadNetwork.NetEnd S, RoadNetwork.NetEnd E) OrientedEnds() =>
        routeKind == "relay" ? (routeStartEnd, routeEndEnd)
        : (RoadNetwork.Orient(routeStartEnd, routeEndEnd.P), RoadNetwork.Orient(routeEndEnd, routeStartEnd.P));

    /// run a search on the route net; re-laying frees the corridor of the old road for it
    T WithRelay<T>(RoadNetwork net, Func<T> f)
    {
        if (routeKind != "relay" || relayOld == null) return f();
        net.BeginRelay(relayOld[0], relayOld[^1], out _);
        try { return f(); }
        finally { net.EndRelay(); }
    }

    async Task SearchRoute()
    {
        if (routeStartEnd == null || routeEndEnd == null) return;
        var net = await EnsureRouteNet();
        if (net == null) return;
        int version = ++routeVersion;
        var w = world;
        var (s, e) = OrientedEnds();
        bool gravel = RouteGravel;
        double width = RouteWidth;
        List<RouteVariant> list = null;
        string problem = null;
        await RouteBusy(Loc.T("Searching the course …"), async progress =>
        {
            list = await Task.Run(() => WithRelay(net, () => net.RouteVariants(s, e, Array.Empty<Point>(), gravel, width, progress, out problem)));
        });
        if (version != routeVersion || world != w) return;
        routeVariants.Clear();
        routeChosen = null;
        if (list == null || list.Count == 0) { SetRouteNote(problem); ShowRoutePlan(); return; }
        routeVariants.AddRange(list);
        routeChosen = list[0];
        FocusRoute();
        await ComposeRoute();
    }

    /// show the whole route on the map if a part of it is outside the visible area
    void FocusRoute()
    {
        var pts = routeVariants.SelectMany(v => v.Road.Points).ToList();
        if (pts.Count == 0 || Map.ActualWidth < 50) return;
        double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), z0 = pts.Min(p => p.Y), z1 = pts.Max(p => p.Y);
        bool Visible(double x, double z) { double sx = Map.ToScreenX(x), sy = Map.ToScreenY(z); return sx > 20 && sy > 20 && sx < Map.ActualWidth - 20 && sy < Map.ActualHeight - 20; }
        if (Visible(x0, z0) && Visible(x1, z1) && Visible(x0, z1) && Visible(x1, z0) && Math.Max(x1 - x0, z1 - z0) * Map.Scale > 120) return;
        double s = Math.Min(Map.ActualWidth * 0.75 / Math.Max(50, x1 - x0), Map.ActualHeight * 0.6 / Math.Max(50, z1 - z0));
        Map.SetScale(Math.Clamp(s, 0.05, 2));
        Map.CenterOn((x0 + x1) / 2, (z0 + z1) / 2);
    }

    void ChooseVariant(RouteVariant v)
    {
        if (v == routeChosen || Map.Busy) return;
        routeChosen = v;
        _ = ComposeRoute();
    }

    /// the chosen course with its extras: junction tiles at existing roads, paths to buildings, height profile
    async Task ComposeRoute()
    {
        var v = routeChosen;
        var net = routeNet;
        if (v == null || net == null || routeStartEnd == null || routeEndEnd == null) { ShowRoutePlan(); return; }
        int version = ++routeVersion;
        var w = world;
        var (s, e) = OrientedEnds();
        bool tilesWanted = settings.RouteJunctionTiles && !RouteGravel && routeKind != "relay";
        bool pois = settings.RoutePois;
        double poiDist = settings.RoutePoiDistance;
        var spec = RouteSpec();
        var notes = new List<string>();
        List<RouteTile> tiles = new();
        List<NetRoad> legs = null, feeders = new();
        RoadResult profile = null;
        await RouteBusy(Loc.T("Preparing the route …"), async progress =>
        {
            await Task.Run(() =>
            {
                var line = v.Road.Points;
                double total = RoadNetwork.Project(line, line[^1]).Along;
                if (tilesWanted)
                {
                    foreach (var (end, atStart) in new[] { (s, true), (e, false) })
                    {
                        if (end.Exit?.Street != true || end.Exit.Gravel) continue;
                        progress.Report(Loc.T("Placing a junction tile …"));
                        var other = atStart ? e.P : s.P;
                        var rt = net.StreetTileNear(end.P, new Vector(end.Exit.StreetDX, end.Exit.StreetDZ), end.Exit.StreetHalf, other, false, out string why, out double moved);
                        if (rt == null) { notes.Add(Loc.F("No T gateway at the {0}: {1} (also not within 360 m along the road)", atStart ? Loc.T("start") : Loc.T("end"), why)); continue; }
                        if (moved > 0) notes.Add(Loc.F("The T gateway does not fit right at the {0}; it is placed {1:0} m further along the road ({2}).", atStart ? Loc.T("start") : Loc.T("end"), moved, why));
                        if (atStart) { rt.Out = rt.In; rt.In = null; rt.Along = 0; } else rt.Along = total;
                        tiles.Add(rt);
                    }
                    foreach (var sc in NetworkBuilder.StreetCrossings(w, line, new List<Point>()))
                    {
                        if (w.RoadAt((int)Math.Floor(sc.P.X), (int)Math.Floor(sc.P.Y)) != WorldData.Asphalt) continue;
                        progress.Report(Loc.T("Placing a crossing tile …"));
                        var (along, _) = RoadNetwork.Project(line, sc.P);
                        var before = RoadNetwork.At(line, Math.Max(0, along - 80)).P;
                        var rt = net.StreetTile(sc.P, new Vector(sc.DX, sc.DZ), sc.Half, before, true, out string why);
                        if (rt == null) { notes.Add(Loc.F("No crossing gateway at {0}: {1}", PosText(sc.P), why)); continue; }
                        rt.Along = along;
                        tiles.Add(rt);
                    }
                }
                if (tiles.Count > 0)
                {
                    progress.Report(Loc.T("Searching the course through the tiles …"));
                    legs = WithRelay(net, () => net.RouteThroughTiles(s, e, v, tiles, out string why2) ?? Fail(why2));
                    if (legs == null) { tiles.Clear(); legs = new List<NetRoad> { v.Road }; }
                }
                else legs = new List<NetRoad> { v.Road };
                // re-laying: paths that joined the old road get a connection to the new course
                if (routeKind == "relay" && relayOld != null)
                {
                    var old = relayOld;
                    double oldLen = RoadNetwork.Project(old, old[^1]).Along;
                    feeders.AddRange(WithRelay(net, () => net.ReconnectBranches(old, relayWidth + 2, 25, oldLen - 25, legs, progress)));
                }
                if (pois)
                    foreach (var leg in legs)
                        feeders.AddRange(net.PoiFeeders(leg, poiDist, Math.Min(5, RouteWidthFor(true)), progress, notes));
                progress.Report(Loc.T("Height profile …"));
                var longest = legs.OrderByDescending(l => l.Length).First();
                profile = RouteBuilder.Preview(w, longest, spec);
                List<NetRoad> Fail(string why) { notes.Add(Loc.T("The tiles were left out:") + " " + why); return null; }
            });
        });
        if (version != routeVersion || world != w) return;
        routeTiles = tiles;
        routeLegs = legs ?? new();
        routeFeeders = feeders;
        routeNotes.Clear();
        routeNotes.AddRange(notes);
        Map.RouteProfile = profile;
        ShowProfile(profile);
        ShowRoutePlan();
    }

    double RouteWidthFor(bool gravel) => gravel ? settings.RouteGravelWidth : settings.RouteAsphaltWidth;

    static string PosText(Point p) => Loc.F("{0} {1}, {2} {3}", Math.Abs(Math.Round(p.X)), p.X < 0 ? Loc.T("W") : Loc.T("E"), Math.Abs(Math.Round(p.Y)), p.Y < 0 ? Loc.T("S") : Loc.T("N"));

    /// preview on the map: variants (chosen one opaque), pieces through the tiles, connectors, paths, tiles, old road (red)
    void ShowRoutePlan()
    {
        if (!routeMode) return;
        var plan = new NetworkPlan { Towns = routeNet?.TownList ?? new() };
        foreach (var v in routeVariants.Where(v => v != routeChosen)) plan.Roads.Add(v.Road);
        if (routeChosen != null)
        {
            plan.Roads.Add(routeChosen.Road);
            foreach (var t in routeTiles) plan.Roads.AddRange(t.Connectors);
            if (!(routeLegs.Count == 1 && routeLegs[0] == routeChosen.Road)) plan.Roads.AddRange(routeLegs);
            plan.Roads.AddRange(routeFeeders);
            plan.NodeTiles.AddRange(routeTiles.Select(t => t.Tile));
            plan.Junctions.AddRange(routeFeeders.Select(f => f.Junction ?? f.Points[^1]));
        }
        var remove = new List<List<Point>>();
        if (relayOld != null) remove.Add(relayOld);
        foreach (var t in routeTiles) remove.AddRange(t.OldPieces.Select(o => o.Points));
        Map.RemovePreview = remove.Count > 0 ? remove : null;
        Map.NetworkPreview = plan;
        Map.NetworkSelected = routeChosen?.Road;
        Map.PickMode = true;
        FillRouteVariantList();
        UpdateToolModule();
    }

    void FillRouteVariantList()
    {
        RouteVariantList.Children.Clear();
        foreach (var v in routeVariants)
        {
            var r = v.Road;
            var swatch = new System.Windows.Shapes.Rectangle { Width = 22, Height = 6, Fill = new SolidColorBrush(RouteColors[(int)v.Style % RouteColors.Length]), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock
            {
                Text = Loc.F("{0}: {1:0.00} km, steepest {2:0.#} %", Capital(RoadNetwork.StyleName(v.Style)), r.Length / 1000, r.MaxGrade * 100)
                    + (r.Cut ? " · " + Loc.F("cut up to {0:0} m", r.MaxCut) : "") + (r.Bridges.Count > 0 ? " · " + Loc.F("{0} bridge(s)", r.Bridges.Count) : "") + (v.Via.Count > 0 ? " · " + Loc.F("{0} point(s) dragged", v.Via.Count) : ""),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(swatch);
            panel.Children.Add(text);
            var rb = new RadioButton { Content = panel, IsChecked = v == routeChosen, GroupName = "routeVariants", Margin = new Thickness(0, 2, 0, 2), Tag = v };
            rb.Click += (_, _) => ChooseVariant(v);
            RouteVariantList.Children.Add(rb);
        }
    }

    static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    void SetRouteNote(string text)
    {
        routeNotes.Clear();
        if (!string.IsNullOrEmpty(text)) routeNotes.Add(text);
        UpdateToolModule();
    }

    /// background work with a status in the box above the map and on the progress bar; the map is locked meanwhile
    async Task RouteBusy(string text, Func<IProgress<string>, Task> work)
    {
        routeStatus = text;
        UpdateToolModule();
        var progress = BusyProgress(text, cancelable: true, t => { routeStatus = t; UpdateRouteModule(); });
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try { await work(progress); }
        finally
        {
            Map.Busy = false;
            Cursor = null;
            routeStatus = null;
            UpdateToolModule();
        }
    }

    // ---------- dragging the chosen route ----------

    void RouteViaDragged(NetRoad road, int index, Point grab, Point drop)
    {
        var v = routeChosen;
        if (!routeMode || v == null || road != v.Road || Map.Busy) return;
        var vias = v.Via.ToList();
        if (index >= 0 && index < vias.Count) vias[index] = drop;
        else
        {
            double t = RoadNetwork.Project(road.Points, grab).Along;
            int at = vias.Count(p => RoadNetwork.Project(road.Points, p).Along < t);
            vias.Insert(at, drop);
        }
        _ = RerouteRoute(vias);
    }

    void RouteViaRemoved(NetRoad road, int index)
    {
        var v = routeChosen;
        if (!routeMode || v == null || road != v.Road || Map.Busy || index < 0 || index >= v.Via.Count) return;
        var vias = v.Via.ToList();
        vias.RemoveAt(index);
        _ = RerouteRoute(vias);
    }

    void RouteRoadClicked(NetRoad r)
    {
        if (!routeMode) return;
        var v = routeVariants.FirstOrDefault(x => x.Road == r);
        if (v != null) ChooseVariant(v);
    }

    async Task RerouteRoute(List<Point> vias)
    {
        var v = routeChosen;
        var net = routeNet;
        if (v == null || net == null) return;
        int version = ++routeVersion;
        var w = world;
        var (s, e) = OrientedEnds();
        RouteVariant nv = null;
        string problem = null;
        await RouteBusy(Loc.T("Searching the course …"), async _ =>
        {
            nv = await Task.Run(() => WithRelay(net, () => net.Reroute(v, s, e, vias, out problem)));
        });
        if (version != routeVersion || world != w) return;
        if (nv == null) { SetRouteNote(problem); return; }
        int k = routeVariants.IndexOf(v);
        if (k >= 0) routeVariants[k] = nv; else routeVariants.Add(nv);
        routeChosen = nv;
        await ComposeRoute();
    }

    void RouteClearVia_Click(object sender, RoutedEventArgs e)
    {
        if (routeChosen == null || Map.Busy || routeChosen.Via.Count == 0) return;
        _ = RerouteRoute(new List<Point>());
    }

    void RouteRestart_Click(object sender, RoutedEventArgs e)
    {
        if (Map.Busy) return;
        ResetRoute();
        UpdateToolModule();
    }

    // ---------- box above the map ----------

    void UpdateRouteModule()
    {
        if (!routeMode) return;
        ToolTitle.Text = Loc.T(RouteTitle()) + (routeChosen != null ? string.Format(Num, " – {0:#,0} m", routeLegs.Count > 0 ? routeLegs.Sum(l => l.Length) : routeChosen.Road.Length) : "");
        string hint;
        if (routeKind == "delete")
        {
            UpdateDeleteModule();
            return;
        }
        if (routeStatus != null) hint = routeStatus;
        else if (routeStartEnd != null && routeEndEnd != null && routeVariants.Count == 0)
            hint = Loc.T("No course was found. Drag the green (start) or red (end) point to another place, or click another end.");
        else if (routeKind == "relay")
            hint = relayA == null ? Loc.T("Click the existing road where the new course should begin.")
                : routeEndEnd == null ? Loc.T("Now click the same road where the new course should end (at least 200 m further).")
                : Loc.T("Choose a variant. Drag the route to lead it through a point, right-click a dragged point to remove it. The green and red points can be dragged too. The old road (red dashes) disappears when you click Apply.");
        else if (routeKind == "deadend" && routeStartEnd == null) hint = Loc.T("Click a dead-end sign (orange point = free town exit). YAMEE looks for the best road or town exit to connect it to.");
        else if (routeStartEnd == null) hint = Loc.T("Click where the route begins: a free town exit (orange point), the entrance of a building, an existing road or free ground.");
        else if (routeEndEnd == null) hint = Loc.T("Now click where the route ends.") + " " + Loc.F("Start: {0}.", RoadNetwork.EndText(routeStartEnd));
        else hint = Loc.T("Choose a variant. Drag the route to lead it through a point, right-click a dragged point to remove it. The green and red points can be dragged too. Then Apply.");
        ToolHint.Text = hint;
        RouteEndsText.Text = routeStartEnd != null && routeEndEnd != null
            ? Loc.F("Start: {0} – end: {1}.", RoadNetwork.EndText(routeStartEnd), RoadNetwork.EndText(routeEndEnd))
              + (routeTiles.Count > 0 ? " " + Loc.F("Tiles: {0}.", string.Join(", ", routeTiles.Select(t => t.Kind == "X" ? Loc.T("crossing gateway") : Loc.T("T gateway")))) : "")
              + (routeFeeders.Count > 0 ? " " + Loc.F("{0} path(s) to buildings.", routeFeeders.Count) : "")
            : "";
        RouteEndsText.Visibility = RouteEndsText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RouteButtons.Visibility = routeStartEnd != null || relayA != null ? Visibility.Visible : Visibility.Collapsed;
        RouteViaButton.Visibility = routeChosen?.Via.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var warn = routeNotes.Select(n => "⚠ " + n).ToList();
        if (routeChosen != null && routeChosen.Road.MaxGrade > settings.RouteMaxGrade / 100 + 0.005)
            warn.Add("⚠ " + Loc.F("Steepest place {0:0.#} % (terrain along the route) – more than allowed; the road gets cuts and embankments there. Try the serpentine or drag the route.", routeChosen.Road.MaxGrade * 100));
        ToolWarnText.Text = string.Join("\n", warn);
        ToolWarnBox.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolApplyButton.IsEnabled = routeChosen != null && routeStatus == null;
    }

    // ---------- apply ----------

    async Task ApplyRoute()
    {
        if (routeKind == "delete") { await ApplyDeleteRoad(); return; }
        var v = routeChosen;
        var w = world;
        if (v == null || w == null || edits == null || Map.Busy) return;
        var spec = RouteSpec();
        var job = new RouteJob();
        if (relayOld != null && routeKind == "relay")
        {
            double len = RoadNetwork.Project(relayOld, relayOld[^1]).Along;
            job.Remove.Add(SliceLine(relayOld, 25, len - 25));
            job.RemoveWidth.Add(relayWidth + 2);
        }
        foreach (var t in routeTiles)
            foreach (var o in t.OldPieces)
            {
                job.Remove.Add(SliceLine(o.Points, 8, o.Length - 8));
                job.RemoveWidth.Add(o.Width);
            }
        foreach (var t in routeTiles) job.Roads.AddRange(t.Connectors);
        job.Roads.AddRange(routeLegs.Count > 0 ? routeLegs : new List<NetRoad> { v.Road });
        job.Roads.AddRange(routeFeeders);
        foreach (var jr in job.Roads) jr.CurveBanking = settings.RouteBanking ? settings.RouteBankingMax / 100 : 0;
        job.Tiles.AddRange(routeTiles.Select(t => t.Tile));
        List<(PlacedPrefab Building, List<PlacedPrefab> Parts)> lotBuildings = null;
        if (job.Tiles.Any(t => t.Def?.Lots.Count > 0) && await EnsurePoiIndex())
        {
            var tp = new NetworkPlan();
            tp.NodeTiles.AddRange(job.Tiles);
            lotBuildings = NetworkBuilder.NodeLotBuildings(tp, poiIndex, (b, district) => PrefabParts.For(b, PartDef, district), spec.Seed + "/" + routeVersion);
        }
        string title = Loc.F("{0} ({1:0.00} km)", Loc.T(RouteTitle()), job.Roads.Where(r => !routeFeeders.Contains(r)).Sum(r => r.Length) / 1000);
        string detail = string.Format(CultureInfo.InvariantCulture, "Route {0} ({1}) von {2:0},{3:0} nach {4:0},{5:0}: {6} Straßen, {7} Kacheln, {8} Wege zu Gebäuden, Steigung {9:0.#} / {10:0.#} %",
            routeKind, v.Style, routeStartEnd.P.X, routeStartEnd.P.Y, routeEndEnd.P.X, routeEndEnd.P.Y, job.Roads.Count, job.Tiles.Count, routeFeeders.Count, spec.Grade * 100, spec.MaxGrade * 100);
        NetworkBuildResult res = null;
        routeStatus = Loc.T("Building the route …");
        UpdateToolModule();
        var progress = BusyProgress(Loc.T("Building the route …"), cancelable: true, t => { routeStatus = t; UpdateRouteModule(); });
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var r = RouteBuilder.Build(w, e, job, spec, progress, lotBuildings);
                res ??= r;
            }));
            if (world != w) return;
            step.Note = Loc.F("{0} road(s), {1:0.0} km; steepest place {2:0.#} %.", res.Roads, res.Length / 1000, res.MaxGrade * 100)
                + (res.Bridges > 0 ? " " + Loc.F("{0} bridge(s) over rivers.", res.Bridges) : "")
                + (res.Warnings.Count > 0 ? " " + string.Join(" ", res.Warnings) : "");
            Refresh(step);
            var warnings = res.Warnings.ToList();
            ResetRoute();
            if (warnings.Count > 0) { routeNotes.AddRange(warnings); }
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex))
        {
            ErrorLog.Write(ex);
            MessageBox.Show(this, Loc.T("Building the route failed:") + "\n" + ex.Message, Loc.T("Road generation"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Map.Busy = false;
            Cursor = null;
            routeStatus = null;
            UpdateButtons();
            UpdateUndo();
            UpdateToolModule();
        }
    }

    // ---------- delete a road between two junctions (the user's request 2026-11-23) ----------

    async Task DeleteRoadClick(Point p)
    {
        var w = world;
        int version = ++routeVersion;
        SegmentResult seg = null;
        string problem = null;
        await RouteBusy(Loc.T("Following the road …"), async _ =>
        {
            seg = await Task.Run(() => RoadSegment.Find(w, p, out problem));
        });
        if (version != routeVersion || world != w) return;
        deleteSeg = seg;
        routeNotes.Clear();
        if (seg == null) routeNotes.Add(problem);
        Map.RemovePreview = seg != null ? new List<List<Point>> { seg.Points } : null;
        UpdateToolModule();
    }

    static string StopText(string stop) => stop switch
    {
        "junction" => Loc.T("junction"),
        "tile" => Loc.T("town tile"),
        "end" => Loc.T("road end"),
        _ => Loc.T("end of the search (8 km)"),
    };

    void UpdateDeleteModule()
    {
        var seg = deleteSeg;
        ToolTitle.Text = Loc.T("Delete road") + (seg != null ? string.Format(Num, " – {0:#,0} m", seg.Length) : "");
        ToolHint.Text = routeStatus ?? (seg == null
            ? Loc.T("Click a road: the piece up to the next junctions (or town tile, road end) is marked.")
            : Loc.T("The marked piece (red dashes) disappears when you click Delete; its ground becomes natural. At the junctions the remaining road gets its edge back. Click another road to mark it instead."));
        if (seg != null)
        {
            string kind = seg.Kind == WorldData.Asphalt ? Loc.T("asphalt") : Loc.T("gravel");
            string text = Loc.F("{0}, about {1:0} m wide. From: {2} – to: {3}.", Capital(kind), seg.Width, StopText(seg.StartStop), StopText(seg.EndStop));
            foreach (var j in seg.Joints)
                text += "\n" + (j.Gravel == null
                    ? Loc.F("Junction at {0:0}, {1:0}: the remaining roads stay as they are (no straight road across the mouth).", j.Center.X, j.Center.Y)
                    : j.Kind == WorldData.Asphalt
                    ? Loc.F("Junction at {0:0}, {1:0}: the asphalt road gets back its gravel edge ({2:0.#} m).", j.Center.X, j.Center.Y, j.GravelWidth)
                    : Loc.F("Junction at {0:0}, {1:0}: the gravel road gets back its edge.", j.Center.X, j.Center.Y));
            RouteEndsText.Text = text;
        }
        else RouteEndsText.Text = "";
        RouteEndsText.Visibility = RouteEndsText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RouteButtons.Visibility = Visibility.Collapsed;
        var warn = routeNotes.Where(n => !string.IsNullOrEmpty(n)).Select(n => "⚠ " + n).ToList();
        ToolWarnText.Text = string.Join("\n", warn);
        ToolWarnBox.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolApplyButton.IsEnabled = seg != null && routeStatus == null;
    }

    async Task ApplyDeleteRoad()
    {
        var seg = deleteSeg;
        var w = world;
        if (seg == null || w == null || edits == null || Map.Busy) return;
        string title = Loc.F("Delete road ({0:#,0} m)", seg.Length);
        string detail = string.Format(CultureInfo.InvariantCulture, "Straße gelöscht von {0:0},{1:0} bis {2:0},{3:0}, {4:0} m, Breite {5:0} m, {6} Kreuzungen repariert",
            seg.Points[0].X, seg.Points[0].Y, seg.Points[^1].X, seg.Points[^1].Y, seg.Length, seg.Width, seg.Joints.Count);
        routeStatus = Loc.T("Deleting the road …");
        UpdateToolModule();
        var progress = BusyProgress(Loc.T("Deleting the road …"), cancelable: true, t => { routeStatus = t; UpdateRouteModule(); });
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            var step = await Task.Run(() => edits.Run(title, detail, e => RoadSegment.Delete(w, e, seg, progress)));
            if (world != w) return;
            Refresh(step);
            ResetRoute();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex))
        {
            ErrorLog.Write(ex);
            MessageBox.Show(this, Loc.T("Deleting the road failed:") + "\n" + ex.Message, Loc.T("Delete road"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Map.Busy = false;
            Cursor = null;
            routeStatus = null;
            UpdateButtons();
            UpdateUndo();
            UpdateToolModule();
        }
    }

    static List<Point> SliceLine(List<Point> line, double from, double to)
    {
        var res = new List<Point>();
        if (to <= from) return res;
        res.Add(RoadNetwork.At(line, from).P);
        double walked = 0;
        for (int q = 0; q + 1 < line.Count; q++)
        {
            walked += (line[q + 1] - line[q]).Length;
            if (walked > from + 0.5 && walked < to - 0.5) res.Add(line[q + 1]);
        }
        res.Add(RoadNetwork.At(line, to).P);
        return res;
    }
}
