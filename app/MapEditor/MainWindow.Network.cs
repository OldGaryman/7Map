using System.Globalization;
using System.Windows;
using MapEditor.Map;

namespace MapEditor;

/// Create roads between the cities (the user's request, 2026-11-07, step 3 after terrain and cities).
/// Stage 1: plan the network and show it on the map (RoadNetwork), the world stays unchanged. The window stays open
/// so that the preview can be looked at, values changed and planned again; closing removes the preview.
/// Stage 2: "Build roads" enters the shown network (NetworkBuilder) – one edit step, Ctrl+Z undoes everything.
public partial class MainWindow
{
    RoadNetWindow roadNetWindow;
    NetworkSpec lastNetwork;
    // edit the plan by hand before building (the user's request 2026-11-12): the plan (with the settings used for it) is
    // saved in the project (NetworkPlanStore) after every change; "undo in the plan" = snapshots of the file content.
    // After building the plan stays saved (marked as built) and can be shown, edited and built again after Ctrl+Z.
    NetworkPlanFile netFile;
    readonly List<string> netUndo = new();
    EditStep netBuiltStep;   // edit step of the last building (to know whether it was undone)
    List<NetSpot> netSpots = new();   // places to check of the shown plan (list in the window)

    async void RoadNetButton_Click(object sender, RoutedEventArgs e)
    {
        if (world == null || edits == null) return;
        if (roadNetWindow != null) { roadNetWindow.Activate(); return; }
        if (!await ConfirmSwitch(null)) return;
        EndRoute();   // both use the road preview on the map
        SetNodeTileDefs();
        var saved = NetworkPlanStore.Load(world, out string problem);
        CheckBuilt(saved);
        var start = lastNetwork?.Clone() ?? saved?.Spec?.Clone() ?? UserValues.Load(new NetworkSpec { Seed = world.Name });
        var dlg = new RoadNetWindow(start) { Owner = this };
        dlg.Left = Left + Math.Max(0, ActualWidth - dlg.Width - 420);
        dlg.Top = Top + 120;
        dlg.Planner = PlanNetwork;
        dlg.Builder = BuildNetwork;
        dlg.HasPlan = () => Map.NetworkPreview?.Roads.Count > 0;
        dlg.SetGravel = g => ChangeNetRoad(r => NetworkPlanEdit.SetGravel(r, g));
        dlg.SetWidth = v => ChangeNetRoad(r => r.Width = v);
        dlg.DeleteRoad = DeleteNetRoad;
        dlg.UndoPlan = UndoNetPlan;
        dlg.ShowSaved = ShowSavedNetPlan;
        dlg.ClearVia = () => { if (Map.NetworkSelected is NetRoad r && r.Via.Count > 0) _ = RerouteNetRoad(r, new List<Point>()); };
        dlg.DrawRoad = SetNetDrawing;
        dlg.ChangeTile = ChangeNodeTile;
        dlg.JumpToSpot = i => { if (i >= 0 && i < netSpots.Count) { var p = netSpots[i].P; Map.NetFocus = p; Map.CenterOn(p.X, p.Y, 0.5); } };
        dlg.Closed += (_, _) =>
        {
            SetNetDrawing(false);
            Map.NetworkPreview = null; Map.NetFocus = null; Map.NodeTileSelected = null; netSpots.Clear(); roadNetWindow = null; netFile = null; netUndo.Clear(); netEditor = null;
            Map.NetRoadClicked -= NetRoadClicked;
            Map.NetViaDragged -= NetViaDragged;
            Map.NetViaRemoved -= NetViaRemoved;
            Map.NetNodeDragged -= NetNodeDragged;
            Map.NodeTileDragged -= NodeTileDragged;
            Map.NodeTileClicked -= NodeTileClicked;
            Map.MapClicked -= NetMapClicked;
            CheckBridgeSwap();
            Activate();
        };
        Map.NetRoadClicked += NetRoadClicked;
        Map.NetViaDragged += NetViaDragged;
        Map.NetViaRemoved += NetViaRemoved;
        Map.NetNodeDragged += NetNodeDragged;
        Map.NodeTileDragged += NodeTileDragged;
        Map.NodeTileClicked += NodeTileClicked;
        Map.MapClicked += NetMapClicked;
        roadNetWindow = dlg;
        netUndo.Clear();
        dlg.Show();
        netFile = saved;
        if (saved != null && !saved.Built)
        {
            Map.NetworkPreview = saved.Plan;
            dlg.SetResult(Loc.T("The saved plan of this project is shown. Click a road on the map to change or delete it.") + "\n\n" + NetworkSummary(saved.Plan));
        }
        else if (saved != null) dlg.SetResult(BuiltNote(saved));
        else if (problem != null) dlg.SetResult(problem);
        UpdateNetWindow();
    }

    /// a plan marked as built whose roads are no longer on the map (towns replaced, terrain copy or backup restored):
    /// mark it as not built (saved), so it is shown and can be built again without the question
    void CheckBuilt(NetworkPlanFile f)
    {
        if (f == null || !f.Built || world == null) return;
        if (netBuiltStep != null && edits != null && edits.Steps.Contains(netBuiltStep) && !edits.Undone.Contains(netBuiltStep)) return;
        if (NetworkPlanEdit.ShareOnMap(world, f.Plan) >= 0.5) return;
        f.Built = false;
        f.BuiltAt = null;
        try { NetworkPlanStore.Save(world, f); }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); }
    }

    static string BuiltNote(NetworkPlanFile f) =>
        Loc.F("The saved plan of this project was built on {0:yyyy-MM-dd HH:mm}.", (f.BuiltAt ?? DateTime.Now).ToLocalTime()) + "\n"
        + Loc.T("After undoing (Ctrl+Z): \"Show saved plan\" to change and build it again.");

    void UpdateNetWindow()
    {
        var dlg = roadNetWindow;
        if (dlg == null) return;
        bool shown = Map.NetworkPreview?.Roads.Count > 0;
        dlg.SetPlanState(shown, netUndo.Count > 0, !shown && netFile != null);
        var r = Map.NetworkSelected;
        dlg.ShowSelection(r, r == null ? null : RoadInfo(Map.NetworkPreview, r));
        var plan = Map.NetworkPreview;
        var tile = Map.NodeTileSelected;
        if (tile != null && plan?.NodeTiles.Contains(tile) != true) Map.NodeTileSelected = tile = null;
        dlg.ShowTile(tile == null ? null : RoadNetwork.KindOf(tile), tile == null ? null : TileInfo(plan, tile));
        CheckBridgeSwap();
        netSpots = plan == null ? new() : NetworkPlanEdit.Spots(plan, netFile?.Spec ?? lastNetwork ?? new NetworkSpec());
        if (plan == null) Map.NetFocus = null;
        dlg.SetSpots(netSpots.Select(s => s.Text).ToList());
    }

    static string RoadInfo(NetworkPlan plan, NetRoad r)
    {
        string kind = r.Manual ? (r.Gravel ? Loc.T("Gravel road drawn by hand") : Loc.T("Road drawn by hand")) : r.ToEdge ? Loc.T("Road to the map edge") : r.Gravel ? Loc.T("Gravel road") : r.TownLink ? Loc.T("Town connection") : r.Feeder ? Loc.T("Feeder road")
            : r.Loop ? Loc.T("Cross connection") : Loc.T("Main road");
        var lines = new List<string> { Loc.F("{0}, {1:0.00} km, gradient up to {2:0.#} %.", kind, r.Length / 1000, r.MaxGrade * 100) };
        var from = r.From?.Street == true ? Loc.T("a town street") : r.From != null ? Loc.T("a town exit") : r.StartRoad != null ? Loc.T("another road") : Loc.T("a free end");
        var to = r.To != null ? Loc.T("a town exit") : r.EndRoad != null ? Loc.T("another road") : r.ToEdge ? Loc.T("the map edge") : Loc.T("a free end");
        lines.Add(Loc.F("From {0} to {1}.", from, to));
        int deps = plan == null ? 0 : NetworkPlanEdit.Dependents(plan, r).Count;
        if (deps > 0) lines.Add(Loc.F("{0} other road(s) branch off this road.", deps));
        if (r.Via.Count > 0) lines.Add(Loc.F("Routed through {0} point(s) set by hand.", r.Via.Count));
        return string.Join("\n", lines);
    }

    void NetRoadClicked(NetRoad r)
    {
        if (roadNetWindow == null || Map.NetworkPreview == null) return;
        if (Map.NetworkSelected != r) roadNetWindow.ShowRouteNote(null);
        Map.NetworkSelected = r;
        Map.NodeTileSelected = null;
        UpdateNetWindow();
    }

    static string TileInfo(NetworkPlan plan, PlacedPrefab tile)
    {
        var town = plan.Towns.FirstOrDefault(t => t.Node && t.Tiles.Contains(tile));
        string kind = RoadNetwork.KindOf(tile) switch { "X" => Loc.T("Crossing (X)"), "T" => Loc.T("T junction (T)"), _ => Loc.T("Straight gateway (I)") };
        int all = town?.Exits.Count ?? 0, used = town?.Exits.Count(e => e.Used) ?? 0;
        return Loc.F("{0}: {1}, at {2} / {3}, turned {4}°.", kind, tile.Name, tile.X, tile.Z, tile.Rotation * 90) + "\n"
            + Loc.F("{0} of {1} connection points have a road.", used, all);
    }

    /// node tile clicked on the map: chosen (box in the window)
    void NodeTileClicked(PlacedPrefab tile)
    {
        if (roadNetWindow == null || Map.NetworkPreview == null) return;
        if (Map.NodeTileSelected != tile) roadNetWindow.ShowTileNote(null);
        Map.NetworkSelected = null;
        Map.NodeTileSelected = tile;
        UpdateNetWindow();
    }

    /// change the selected node tile: kind (null = keep) and/or turn by 90°
    async void ChangeNodeTile(string kind, bool turn)
    {
        var dlg = roadNetWindow;
        var file = netFile;
        var w = world;
        var tile = Map.NodeTileSelected;
        if (dlg == null || file == null || w == null || tile == null || Map.Busy || !file.Plan.NodeTiles.Contains(tile)) return;
        if (kind == RoadNetwork.KindOf(tile) && !turn) return;
        Map.Busy = true;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var editor = await NetEditor(w, file, t => dlg.ShowTileNote(t, busy: true));
            string before = NetworkPlanStore.Serialize(file);
            string problem = await Task.Run(() => editor.ChangeNodeTile(file.Plan, tile, kind, turn));
            if (world != w || netFile != file) return;
            if (problem != null) { dlg.ShowTileNote(problem); UpdateNetWindow(); return; }
            dlg.ShowTileNote(null);
            if (NetworkPlanStore.Serialize(file) == before) { UpdateNetWindow(); return; }
            netUndo.Add(before);
            Map.NodeTileSelected = file.Plan.NodeTiles.FirstOrDefault(t => t.X == tile.X && t.Z == tile.Z);
            NetPlanChanged();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); dlg.ShowTileNote(Loc.T("Searching the course failed:") + " " + ex.Message); }
        finally
        {
            Map.Busy = false;
            Cursor = null;
            UpdateNetWindow();
        }
    }

    // ---------- stage 2: route a planned road through via points (the user's request 2026-11-12) ----------

    RoadNetwork netEditor;          // cost grid for rerouting (built at the first drag, kept while the window is open)
    WorldData netEditorWorld;
    string netEditorSpec;

    static string SpecKey(NetworkSpec s) => NetworkPlanStore.SpecText(s);

    /// Alt held (while dragging a road of the plan: the ends near junctions and tiles may change more)
    static bool AltHeld => (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0;

    /// via point dragged: index ≥ 0 = move it, -1 = new point (inserted by the place where the road was grabbed)
    void NetViaDragged(NetRoad road, int index, Point grab, Point drop)
    {
        if (roadNetWindow == null || netFile == null || Map.Busy) return;
        var vias = road.Via.ToList();
        if (index >= 0 && index < vias.Count) vias[index] = drop;
        else
        {
            double t = RoadNetwork.Project(road.Points, grab).Along;
            int at = vias.Count(v => RoadNetwork.Project(road.Points, v).Along < t);
            vias.Insert(at, drop);
        }
        _ = RerouteNetRoad(road, vias, AltHeld);   // Alt held while dragging: more freedom at the ends
    }

    /// junction or crossing ring dragged (the user's request 2026-11-12): which = -1 start junction of the road, -2 its end
    /// junction, k ≥ 0 its crossing k. A junction lands on the nearest other planned road, a crossing moves along the road it
    /// crosses; the road is searched again (its via points stay).
    async void NetNodeDragged(NetRoad road, int which, Point drop)
    {
        var dlg = roadNetWindow;
        var file = netFile;
        var w = world;
        if (dlg == null || file == null || w == null || Map.Busy || !file.Plan.Roads.Contains(road)) return;
        if (which >= road.CrossedRoads.Count) return;
        bool free = AltHeld;
        Map.NetworkSelected = road;
        UpdateNetWindow();
        double tol = Math.Max(40, 20 / Math.Max(Map.Scale, 1e-6));
        RoadNetwork.NetEnd target = null;
        (Point Q, NetRoad B)? pin = null;
        if (which < 0)
        {
            var not = NetworkPlanEdit.DependsOn(file.Plan, road);
            not.Add(road);
            target = RoadNetwork.FindRoadPlace(file.Plan, drop, tol, not);
            if (target == null)
            {
                dlg.ShowRouteNote(Loc.T("Please drop the junction on another planned road (not on this road and not on a road that branches off it)."));
                return;
            }
        }
        else
        {
            var b = road.CrossedRoads[which];
            var (along, off) = RoadNetwork.Project(b.Points, drop);
            double len = RoadNetwork.Project(b.Points, b.Points[^1]).Along;
            if (off > tol || along < 60 || along > len - 60)
            {
                dlg.ShowRouteNote(Loc.T("Please drop the crossing on the road it crosses (at least 60 m from its ends)."));
                return;
            }
            pin = (RoadNetwork.Nearest(b.Points, drop), b);
        }
        Map.Busy = true;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var editor = await NetEditor(w, file, t => dlg.ShowRouteNote(t, busy: true));
            string problem = null;
            List<Point> ordered = null;
            var line = await Task.Run(() =>
            {
                List<Point> l, o; string p;
                // via points left beside the crossed road from an earlier move of this crossing go (the new place replaces them)
                if (pin is (Point q, NetRoad br))
                    l = editor.Reroute(file.Plan, road, road.Via.Where(v => RoadNetwork.Project(br.Points, v).Off > 60).ToList(), out p, out o, new[] { (q, br) }, free);
                else l = editor.MoveJunction(file.Plan, road, which == -1, target, out p, out o);
                problem = p; ordered = o;
                return l;
            });
            if (world != w || netFile != file) return;
            if (line == null) { dlg.ShowRouteNote(problem); return; }
            string before = NetworkPlanStore.Serialize(file);
            road.Points = line;
            road.Via = ordered;
            if (which == -1) road.StartRoad = target.Road;
            else if (which == -2) { road.EndRoad = target.Road; road.Junction = target.P; }
            editor.Remeasure(road);
            NetworkPlanEdit.UpdateCrossings(file.Plan, road);
            if (!NetworkPlanEdit.Reorder(file.Plan))
            {
                netFile = NetworkPlanStore.Deserialize(w, before, out _);   // junctions in a circle: leave everything as it was
                Map.NetworkSelected = null;
                NetPlanChanged();
                dlg.ShowRouteNote(Loc.T("The roads would branch off each other in a circle – the junction cannot go there."));
                return;
            }
            NetworkPlanEdit.Refresh(file.Plan);
            netUndo.Add(before);
            dlg.ShowRouteNote(null);
            NetPlanChanged();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); dlg.ShowRouteNote(Loc.T("Searching the course failed:") + " " + ex.Message); }
        finally
        {
            Map.Busy = false;
            Cursor = null;
        }
    }

    void NetViaRemoved(NetRoad road, int index)
    {
        if (roadNetWindow == null || netFile == null || Map.Busy || index < 0 || index >= road.Via.Count) return;
        var vias = road.Via.ToList();
        vias.RemoveAt(index);
        _ = RerouteNetRoad(road, vias);
    }

    /// search the course of the road again through these via points; on success the plan changes (undo in the plan possible)
    /// cost grid for searches by hand (read once per world and settings); note shows what happens
    async Task<RoadNetwork> NetEditor(WorldData w, NetworkPlanFile file, Action<string> note)
    {
        string key = SpecKey(file.Spec);
        if (netEditor == null || netEditorWorld != w || netEditorSpec != key)
        {
            string msg = Loc.T("Reading the terrain for the route search (only the first time) …");
            SetBusyTitle(msg);
            note(msg);
            var spec = file.Spec.Clone();
            netEditor = await Task.Run(() => RoadNetwork.ForEditing(w, spec));
            netEditorWorld = w; netEditorSpec = key;
        }
        else
        {
            string msg = Loc.T("Searching the course …");
            SetBusyTitle(msg);
            note(msg);
        }
        return netEditor;
    }

    // ---------- stage 3: draw a new road between two places (the user's request 2026-11-12) ----------

    bool netDrawing;
    RoadNetwork.NetEnd netDrawStart;

    void SetNetDrawing(bool on)
    {
        if (on && (roadNetWindow == null || netFile == null || Map.NetworkPreview == null)) on = false;
        netDrawing = on;
        netDrawStart = null;
        Map.PickMode = on;
        if (on) { Map.NetworkSelected = null; UpdateNetWindow(); }
        roadNetWindow?.ShowDrawing(on, on ? Loc.T("Click a free town exit (orange point) or a planned road where the new road begins. Esc cancels.") : null);
    }

    async void NetMapClicked(Point p)
    {
        var dlg = roadNetWindow;
        var file = netFile;
        var w = world;
        if (!netDrawing || dlg == null || file == null || w == null || Map.Busy) return;
        var end = RoadNetwork.FindEnd(file.Plan, p, Math.Max(30, 14 / Math.Max(Map.Scale, 1e-6)));
        if (end == null)
        {
            dlg.ShowDrawing(true, Loc.T("There is no free town exit or planned road here. Please click closer to one."), problem: true);
            return;
        }
        if (netDrawStart == null)
        {
            netDrawStart = end;
            Map.PickStart = end.P;
            dlg.ShowDrawing(true, Loc.T("Now click the free town exit or the planned road where the new road ends."));
            return;
        }
        var start = netDrawStart;
        Map.Busy = true;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var editor = await NetEditor(w, file, t => dlg.ShowDrawing(true, t));
            string problem = null;
            var road = await Task.Run(() => { var r = editor.NewRoad(file.Plan, start, end, out var pr); problem = pr; return r; });
            if (world != w || netFile != file || !netDrawing) return;
            if (road == null) { dlg.ShowDrawing(true, problem + " " + Loc.T("Click another end, or Esc to cancel."), problem: true); return; }
            netUndo.Add(NetworkPlanStore.Serialize(file));
            file.Plan.Roads.Add(road);
            NetworkPlanEdit.UpdateCrossings(file.Plan, road);
            NetworkPlanEdit.Refresh(file.Plan);
            SetNetDrawing(false);
            NetPlanChanged();
            Map.NetworkSelected = road;
            UpdateNetWindow();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); dlg.ShowDrawing(true, Loc.T("Searching the course failed:") + " " + ex.Message, problem: true); }
        finally
        {
            Map.Busy = false;
            Cursor = null;
        }
    }

    /// Esc in the main window while drawing a new road
    bool TryCancelNetDrawing()
    {
        if (!netDrawing) return false;
        SetNetDrawing(false);
        return true;
    }

    async Task RerouteNetRoad(NetRoad road, List<Point> vias, bool free = false)
    {
        var w = world;
        var file = netFile;
        var dlg = roadNetWindow;
        if (w == null || file == null || dlg == null || Map.Busy) return;
        Map.Busy = true;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var editor = await NetEditor(w, file, t => dlg.ShowRouteNote(t, busy: true));
            string problem = null;
            List<Point> ordered = vias;
            var line = await Task.Run(() =>
            {
                var l = editor.Reroute(file.Plan, road, vias, out var p, out var o, null, free);
                problem = p; ordered = o;
                return l;
            });
            if (world != w || netFile != file) return;   // window closed or plan replaced meanwhile
            if (line == null) { dlg.ShowRouteNote(problem); return; }
            netUndo.Add(NetworkPlanStore.Serialize(file));
            road.Points = line;
            road.Via = ordered;
            editor.Remeasure(road);
            NetworkPlanEdit.UpdateCrossings(file.Plan, road);
            NetworkPlanEdit.Refresh(file.Plan);
            dlg.ShowRouteNote(null);
            NetPlanChanged();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); dlg.ShowRouteNote(Loc.T("Searching the course failed:") + " " + ex.Message); }
        finally
        {
            Map.Busy = false;
            Cursor = null;
        }
    }

    /// remember the state for "undo in the plan" (before a change)
    void PushNetUndo()
    {
        if (netFile == null) return;
        netUndo.Add(NetworkPlanStore.Serialize(netFile));
        if (netUndo.Count > 50) netUndo.RemoveAt(0);
    }

    /// plan changed: save, redraw, update the window
    void NetPlanChanged()
    {
        if (netFile == null || world == null) return;
        try { NetworkPlanStore.Save(world, netFile); }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); }
        var sel = Map.NetworkSelected;
        Map.NetworkPreview = netFile.Plan;
        Map.NetworkSelected = sel != null && netFile.Plan.Roads.Contains(sel) ? sel : null;
        roadNetWindow?.SetResult(NetworkSummary(netFile.Plan));
        UpdateNetWindow();
    }

    void ChangeNetRoad(Action<NetRoad> change)
    {
        var r = Map.NetworkSelected;
        if (r == null || netFile == null || Map.Busy) return;
        string before = NetworkPlanStore.Serialize(netFile);
        change(r);
        if (NetworkPlanStore.Serialize(netFile) == before) { UpdateNetWindow(); return; }
        netUndo.Add(before);
        NetPlanChanged();
    }

    void DeleteNetRoad()
    {
        var r = Map.NetworkSelected;
        var plan = Map.NetworkPreview;
        if (r == null || plan == null || netFile == null || Map.Busy) return;
        bool withDependents = false;
        var deps = NetworkPlanEdit.Dependents(plan, r);
        if (deps.Count > 0)
        {
            var answer = MessageBox.Show(roadNetWindow ?? (Window)this,
                Loc.F("{0} other road(s) branch off this road.\n\nYes = delete them too\nNo = keep them (they then end freely, as a dead end)\nCancel = delete nothing", deps.Count),
                Loc.T("Delete road"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            withDependents = answer == MessageBoxResult.Yes;
        }
        PushNetUndo();
        NetworkPlanEdit.Delete(plan, r, withDependents);
        // node tiles take the shape of the roads that still use them (step 3, the user's request 2026-11-14)
        int changed = RoadNetwork.AdaptNodeTiles(world, plan, netFile.Spec);
        Map.NetworkSelected = null;
        NetPlanChanged();
        if (changed > 0) roadNetWindow?.ShowRouteNote(Loc.F("{0} node tile(s) adapted to the roads that still use them (crossing → T → straight gateway, or removed).", changed));
    }

    /// node tile dragged to a new place (step 3): the roads of the tile are searched again to its moved exits
    async void NodeTileDragged(PlacedPrefab tile, int x0, int z0)
    {
        var dlg = roadNetWindow;
        var file = netFile;
        var w = world;
        if (dlg == null || file == null || w == null || Map.Busy || !file.Plan.NodeTiles.Contains(tile)) return;
        // the moved tile is the chosen one (its box shows the messages)
        Map.NetworkSelected = null;
        Map.NodeTileSelected = tile;
        UpdateNetWindow();
        Map.Busy = true;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var editor = await NetEditor(w, file, t => dlg.ShowTileNote(t, busy: true));
            string before = NetworkPlanStore.Serialize(file);
            string problem = await Task.Run(() => editor.MoveNodeTile(file.Plan, tile, x0, z0));
            if (world != w || netFile != file) return;
            if (problem != null) { dlg.ShowTileNote(Loc.T("The node tile cannot go there:") + " " + problem); return; }
            netUndo.Add(before);
            dlg.ShowTileNote(null);
            Map.NodeTileSelected = file.Plan.NodeTiles.FirstOrDefault(t => t.X == x0 && t.Z == z0);
            NetPlanChanged();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); dlg.ShowTileNote(Loc.T("Searching the course failed:") + " " + ex.Message); }
        finally
        {
            Map.Busy = false;
            Cursor = null;
        }
    }

    void UndoNetPlan()
    {
        if (netUndo.Count == 0 || world == null || Map.Busy) return;
        SetNetDrawing(false);   // the start of a road being drawn belongs to the old state
        string text = netUndo[^1];
        netUndo.RemoveAt(netUndo.Count - 1);
        var f = NetworkPlanStore.Deserialize(world, text, out _);
        if (f == null) return;
        netFile = f;
        Map.NetworkSelected = null;
        NetPlanChanged();
    }

    void ShowSavedNetPlan()
    {
        if (world == null) return;
        SetNetDrawing(false);
        var f = NetworkPlanStore.Load(world, out string problem);
        CheckBuilt(f);
        if (f == null) { roadNetWindow?.SetResult(problem ?? Loc.T("There is no saved road plan in this project.")); netFile = null; UpdateNetWindow(); return; }
        netFile = f;
        netUndo.Clear();
        Map.NetworkSelected = null;
        Map.NetworkPreview = f.Plan;
        roadNetWindow?.SetResult((f.Built ? BuiltNote(f) + "\n\n" : "") + NetworkSummary(f.Plan));
        UpdateNetWindow();
    }

    /// Ctrl+Z in the main window while the road network window is open and the plan has changes: undo in the plan
    bool TryUndoNetPlan()
    {
        if (roadNetWindow == null || netUndo.Count == 0 || Map.NetworkPreview == null) return false;
        UndoNetPlan();
        return true;
    }

    /// Del in the main window: delete the selected planned road
    bool TryDeleteNetRoad()
    {
        if (roadNetWindow == null || Map.NetworkSelected == null) return false;
        DeleteNetRoad();
        return true;
    }

    /// descriptions of the node tiles for the planner and the saved plan (read here: TileDef uses fields of the window,
    /// the planner runs in the background)
    void SetNodeTileDefs()
    {
        var defs = RoadNetwork.NodeTileNames.ToDictionary(n => n, TileDef, StringComparer.OrdinalIgnoreCase);
        RoadNetwork.TileDefs = n => defs.GetValueOrDefault(n);
        SetBridgeKit();
    }

    /// the active bridge kits (window "Bridge mods") for the planners (roads, routes, wilderness paths); without them rivers stay obstacles
    void SetBridgeKit() => RoadNetwork.Bridges = ActiveBridgeKits();

    async Task<string> PlanNetwork(NetworkSpec spec)
    {
        SetNetDrawing(false);
        SetNodeTileDefs();
        lastNetwork = spec;
        UserValues.Remember(spec);
        var w = world;
        if (w == null || Map.Busy) return Loc.T("Please wait until the current work is finished.");
        var win = roadNetWindow;
        IProgress<string> progress = BusyProgress(Loc.T("Planning roads …"), cancelable: true, t => win?.ShowProgress(t));
        NetworkPlan plan;
        Map.Busy = true;
        try { plan = await Task.Run(() => new RoadNetwork(w, spec).Plan(progress)); }
        finally { Map.Busy = false; }
        if (world != w) return Loc.T("The world was changed meanwhile – please plan again.");
        if (netFile != null) PushNetUndo();   // a new plan can be taken back as well
        netFile = new NetworkPlanFile { Plan = plan, Spec = spec.Clone() };
        Map.NetworkSelected = null;
        Map.NetworkPreview = plan;
        try { NetworkPlanStore.Save(w, netFile); }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); }
        UpdateNetWindow();
        return NetworkSummary(plan);
    }

    /// build the shown network (one edit step); null = nothing built (text stays)
    async Task<string> BuildNetwork()
    {
        SetNetDrawing(false);
        var plan = Map.NetworkPreview;
        var spec = (netFile?.Spec ?? lastNetwork)?.Clone();
        if (spec != null && roadNetWindow != null) spec.BreakInWasteland = roadNetWindow.BreakInWasteland;
        var w = world;
        if (plan == null || plan.Roads.Count == 0 || spec == null || w == null || edits == null) return null;
        if (Map.Busy) return Loc.T("Please wait until the current work is finished.");
        CheckBuilt(netFile);
        if (netFile?.Built == true)
        {
            bool stillThere = netBuiltStep != null && edits.Steps.Contains(netBuiltStep);
            bool undone = netBuiltStep != null && edits.Undone.Contains(netBuiltStep);
            if (!undone)
            {
                string q = stillThere
                    ? Loc.T("This plan has already been built and the roads are still on the map. Building again puts them in a second time.\n\nBuild anyway?")
                    : Loc.T("This plan has already been built once. Only build it again if you have undone that building.\n\nBuild anyway?");
                if (MessageBox.Show(roadNetWindow ?? (Window)this, q, Loc.T("Build roads"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return null;
            }
        }
        // buildings for the lots of the node tiles (index and parts are read here: they use fields of the window)
        List<(PlacedPrefab Building, List<PlacedPrefab> Parts)> lotBuildings = null;
        if (plan.NodeTiles.Any(t => t.Def?.Lots.Count > 0) && await EnsurePoiIndex())
            lotBuildings = NetworkBuilder.NodeLotBuildings(plan, poiIndex, (b, district) => PrefabParts.For(b, PartDef, district), spec.Seed);
        var win = roadNetWindow;
        IProgress<string> progress = BusyProgress(Loc.T("Building the roads …"), cancelable: true, t => win?.ShowProgress(t));
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
                var r = NetworkBuilder.Build(w, e, plan, spec, progress, lotBuildings);
                res ??= r;   // result of the first pass (later ones: repeat/recalculate)
            }));
            if (world != w) return null;
            step.Note = Loc.F("{0} road(s), {1:0.0} km.", res.Roads, res.Length / 1000);
            netBuiltStep = step;
            if (netFile != null)
            {
                netFile.Built = true;
                netFile.BuiltAt = DateTime.UtcNow;
                try { NetworkPlanStore.Save(w, netFile); }
                catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); }
            }
            netUndo.Clear();
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
            UpdateNetWindow();
        }
    }

    static string BuildSummary(NetworkBuildResult res)
    {
        // short and to the point (the user's request 2026-11-26, see TEXT_GUIDE.md): no gradient details, no notes list
        var lines = new List<string> { Loc.F("{0} road(s) built, {1:0.0} km in all.", res.Roads, res.Length / 1000) };
        if (res.NodeTiles > 0) lines.Add(Loc.F("{0} node tile(s) placed, {1} building(s) on their lots.", res.NodeTiles, res.NodeBuildings));
        if (res.Bridges > 0) lines.Add(Loc.F("{0} bridge(s) over rivers.", res.Bridges));
        lines.Add("");
        lines.Add(Loc.T("Undo with Ctrl+Z, keep with Save. The plan stays saved in the project."));
        return string.Join("\n", lines);
    }

    static string NetworkSummary(NetworkPlan plan)
    {
        if (plan.Towns.Count == 0) return Loc.T("There are no towns on this map. Generate towns first.");
        var roads = plan.Roads;
        double asphalt = roads.Where(r => !r.Gravel).Sum(r => r.Length) / 1000, gravel = roads.Where(r => r.Gravel).Sum(r => r.Length) / 1000;
        int connected = plan.Towns.Count(t => t.Connected);
        // short and to the point (the user's request 2026-11-26, see TEXT_GUIDE.md): the colours and how to edit stand in the
        // legend at the top of the result; no notes list, no gradient statistics
        int bridges = roads.Sum(r => r.Bridges.Count);
        var lines = new List<string>
        {
            Loc.F("{0} of {1} towns connected ({2} Old West).", connected, plan.Towns.Count, plan.Towns.Count(t => t.OldWest)),
            Loc.F("{0} roads: {1:0.0} km asphalt, {2:0.0} km gravel.", roads.Count, asphalt, gravel),
            Loc.F("{0} junctions, {1} crossings, {2} cross connections.", plan.Junctions.Count, plan.CrossingPoints.Count, roads.Count(r => r.Loop)),
        };
        if (RoadNetwork.Bridges == null) lines.Add(Loc.T("No bridge kit active – roads go around rivers."));
        else if (bridges > 0) lines.Add(Loc.F("{0} bridge(s) over rivers.", bridges));
        if (plan.NodeTiles.Count > 0)
            lines.Add(Loc.F("Node tiles: {0} T junction(s), {1} crossing(s), {2} straight gateway(s).", plan.NodeTiles.Count(t => t.Name.Equals(RoadNetwork.NodeT, StringComparison.OrdinalIgnoreCase)),
                plan.NodeTiles.Count(t => t.Name.Equals(RoadNetwork.NodeX, StringComparison.OrdinalIgnoreCase)),
                plan.NodeTiles.Count(t => RoadNetwork.NodeStraight.Contains(t.Name, StringComparer.OrdinalIgnoreCase))));
        lines.Add(Loc.F("Unused town exits (dead ends): {0}.", plan.Towns.Sum(t => t.Exits.Count(e => e.Reachable && !e.Used))));
        return string.Join("\n", lines);
    }
}
