using System.Globalization;
using System.Windows;
using System.Windows.Input;
using MapEditor.Map;

namespace MapEditor;

/// Swap bridges (the user's request 2026-11-25): a click onto a planned bridge (preview of "Generate roads" or of the wilderness POIs)
/// or onto a built bridge on the map opens the window "Bridges" with every active kit for its surface and whether it fits there.
/// Planned: the bridge and the straight piece of the line are changed in the plan (the ground follows when the plan is built).
/// Built: the bridge tool of its surface is switched on, the swap removes the old bridge with its approaches and the road in front of
/// them and builds the new one with ground and road (one edit step, Map/BridgeSwap.cs).
public partial class MainWindow
{
    sealed class SwapTarget
    {
        public NetRoad Road;          // planned road of "Generate roads"
        public WildPath Path;         // planned wilderness path
        public int Index;             // number of the bridge on the road/path
        public BuiltBridge Built;     // bridge on the map
        public bool Gravel;
        public BridgeSite Site => Built?.Site ?? (Road != null ? Road.Bridges[Index] : Path.Bridges[Index]);
    }

    SwapTarget bridgeSwap;
    List<BridgeOption> swapOptions = new();

    void InitBridgeSwap() => Map.PlannedBridgeClicked += PlannedBridgeClicked;

    void PlannedBridgeClicked(BridgeSite b)
    {
        if (world == null || Map.Busy) return;
        SwapTarget t = null;
        if (roadNetWindow != null && netFile != null && Map.NetworkPreview == netFile.Plan)
            foreach (var r in netFile.Plan.Roads)
            {
                int i = r.Bridges.IndexOf(b);
                if (i >= 0) { t = new SwapTarget { Road = r, Index = i, Gravel = r.Gravel }; break; }
            }
        if (t == null && Map.WildernessPreview != null)
            foreach (var p in Map.WildernessPreview.Paths)
            {
                int i = p.Bridges.IndexOf(b);
                if (i >= 0) { t = new SwapTarget { Path = p, Index = i, Gravel = true }; break; }
            }
        if (t == null) return;
        ShowRibbonTab("roads");
        StartBridgeSwap(t);
    }

    /// a built bridge piece was clicked (without a tool or with the bridge tool): the bridge tool of its surface and the swap choice
    bool BuiltBridgeClicked(PlacedPrefab p)
    {
        if (world == null || p == null || !IsKitPiece(p.Name) || Map.Busy) return false;
        var bb = BridgeSwap.FromPieces(world, p, BridgeKitList().Where(k => k.Found));
        if (bb == null) return false;
        if (WaterBridgeButton.IsChecked != true || settings.BridgeGravel != bb.Site.Gravel)
            BridgeTool_Click(bb.Site.Gravel ? RT_BridgeGravel : RT_BridgeAsphalt, new RoutedEventArgs());
        ShowRibbonTab("roads");
        if (WaterBridgeButton.IsChecked != true) return true;   // the tool was not switched on (open work, cancelled)
        StartBridgeSwap(new SwapTarget { Built = bb, Gravel = bb.Site.Gravel });
        return true;
    }

    /// the bridge tool clicked onto the map: a built bridge there is chosen for swapping instead of placing a new bridge
    bool BridgeAt(Point p)
    {
        if (world == null) return false;
        var piece = world.PrefabSnapshot().FirstOrDefault(q => q.Def != null && IsKitPiece(q.Name) && q.Contains(p.X, p.Y));
        return piece != null && BuiltBridgeClicked(piece);
    }

    /// a piece of a known bridge kit. The list of kits is read here if needed: BridgeKits.IsBridgePiece knows the names of the other kits
    /// (rwg, wooden, concrete bridges …) only after it was read – before, a click on a bridge of the town generator opened the building window
    /// (the user's report 2026-11-26)
    bool IsKitPiece(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return BridgeKitList().Any(k => (k.End?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false) || (k.Tile?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    void StartBridgeSwap(SwapTarget t)
    {
        bridgeSwap = t;
        Map.BridgeSelected = t.Site;
        swapOptions = SwapOptions(t);
        UpdateBridgePick(true);
    }

    void EndBridgeSwap()
    {
        if (bridgeSwap == null) return;
        bridgeSwap = null;
        swapOptions = new();
        Map.BridgeSelected = null;
        UpdateMenuState();
    }

    /// the plan the swap belongs to is gone (window closed, planned again, built): end the swap
    void CheckBridgeSwap()
    {
        var t = bridgeSwap;
        if (t == null) return;
        bool alive = t.Built != null ? world != null && !Map.Busy ? t.Built.Pieces.All(world.PrefabSnapshot().Contains) : true
            : t.Road != null ? netFile != null && Map.NetworkPreview == netFile.Plan && netFile.Plan.Roads.Contains(t.Road) && t.Index < t.Road.Bridges.Count
            : Map.WildernessPreview != null && Map.WildernessPreview.Paths.Contains(t.Path) && t.Index < t.Path.Bridges.Count;
        if (!alive)
        {
            bridgeSwap = null;
            swapOptions = new();
            Map.BridgeSelected = null;
            if (bridgePick != null && WaterBridgeButton.IsChecked != true) { var p = bridgePick; bridgePick = null; p.Close(); }
            else bridgePick?.Fill(ActiveKitsFor(settings.BridgeGravel), settings.BridgeKitChoice, settings.BridgeGravel);
        }
    }

    List<BridgeOption> SwapOptions(SwapTarget t)
    {
        var kits = ActiveKitsFor(t.Gravel);
        if (t.Built == null) return BridgeSwap.Options(world, t.Site, kits);
        var others = world.PrefabSnapshot().Where(p => !t.Built.Pieces.Contains(p)).ToList();
        var list = BridgeSwap.Options(world, t.Site, kits, others, BridgeSwap.OldCorridor(t.Site));
        // the bridge on the map may belong to a kit that is switched off now: show it too as the current one
        if (!list.Any(o => o.Current) && t.Built.Kit != null) list.Insert(0, new BridgeOption(t.Built.Kit, t.Site, true));
        return list;
    }

    void FillBridgeSwap()
    {
        var t = bridgeSwap;
        if (t == null || bridgePick == null) return;
        var c = t.Site.Center;
        string header = t.Built != null ? Loc.F("Bridge at {0:0}, {1:0}", c.X, c.Y) : Loc.F("Planned bridge at {0:0}, {1:0}", c.X, c.Y);
        string intro = t.Built != null
            ? Loc.T("Choose another bridge for this place. YAMEE removes the old bridge with its approaches and the road up to 100–300 m in front of them, builds the new bridge with its ground and joins the road again (one step, undo with Ctrl+Z).")
            : Loc.T("Choose another bridge for this place. The plan is changed at once; the ground is adapted when the plan is built.");
        bridgePick.FillSwap(header, intro, swapOptions);
    }

    async void BridgeSwapChosen(string id)
    {
        var t = bridgeSwap;
        if (t == null || id == null || Map.Busy) return;
        var o = swapOptions.FirstOrDefault(x => x.Kit.Id == id);
        if (o == null || o.Current || o.Site.Problem != null) { FillBridgeSwap(); return; }
        // orange: the river has to be narrowed for this bridge – ask first (the user's request 2026-11-26)
        if (o.Site.NarrowFrom > 0)
        {
            string text = Loc.F("For {0} the river has to be reshaped: it is {1:0} m wide here and is narrowed to {2} m under the bridge (about {3:0} m on each bank). Up- and downstream the banks run out over about {4} m.",
                o.Kit.Label, o.Site.NarrowFrom, o.Site.WaterWidth, o.Site.NarrowPerSide, Bridge.BlendFor(o.Site))
                + "\n\n" + (t.Built != null ? Loc.T("Swap anyway? (Undo with Ctrl+Z.)") : Loc.T("Swap anyway? The river is reshaped when the plan is built."));
            if (MessageBox.Show(this, text, Loc.T("Bridges"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { FillBridgeSwap(); return; }
        }
        if (t.Built != null) { await SwapBuiltBridge(t, o); return; }
        bool ok;
        if (t.Road != null)
        {
            PushNetUndo();
            ok = BridgeSwap.InPlan(t.Road, t.Index, o.Site);
            if (ok)
            {
                NetworkPlanEdit.Refresh(netFile.Plan);
                NetPlanChanged();
            }
            else netUndo.RemoveAt(netUndo.Count - 1);
        }
        else
        {
            ok = BridgeSwap.InPlan(t.Path, t.Index, o.Site);
            if (ok) { var plan = Map.WildernessPreview; Map.WildernessPreview = null; Map.WildernessPreview = plan; }
        }
        if (!ok)
        {
            MessageBox.Show(this, Loc.T("The planned line does not run straight over this bridge; it could not be swapped."), Loc.T("Bridges"), MessageBoxButton.OK, MessageBoxImage.Information);
            FillBridgeSwap();
            return;
        }
        Map.BridgeSelected = t.Site;
        swapOptions = SwapOptions(t);
        FillBridgeSwap();
    }

    async Task SwapBuiltBridge(SwapTarget t, BridgeOption o)
    {
        var w = world;
        if (w == null || edits == null) return;
        SetBridgeKit();   // the road builder finds the pieces of the kit there
        var old = t.Built;
        var kit = o.Kit;
        var spec = UserValues.Load(new NetworkSpec { Seed = w.Name });
        var c = old.Site.Center;
        string title = Loc.F("Swap bridge at {0:0}, {1:0}", c.X, c.Y);
        string detail = string.Format(CultureInfo.InvariantCulture, "Brücke bei {0:0},{1:0} getauscht: {2} → {3} ({4} Mittelstück(e), {5} m)", c.X, c.Y,
            old.Kit?.Label ?? old.Site.Kit, kit.Label, o.Site.Tiles, o.Site.Length);
        var progress = BusyProgress(Loc.T("Swapping the bridge …"), cancelable: true);
        BridgeSwapResult res = null;
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var r = BridgeSwap.Build(w, e, old, kit, spec, progress);
                res ??= r;
            }));
            if (world != w) return;
            if (res == null)
            {
                MessageBox.Show(this, Loc.T("The bridge could not be swapped (the old bridge was not found any more, or the new one does not fit)."), Loc.T("Bridges"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            step.Note = Loc.F("{0} instead of {1}: {2} m long, {3} middle piece(s), deck at {4} m.", kit.Label, old.Kit?.Label ?? old.Site.Kit, res.Site.Length, res.Site.Tiles, res.Site.Deck)
                + " " + (res.Roads == 2 ? Loc.T("The road was joined again on both sides.") : res.Roads == 1 ? Loc.T("The road was joined again on one side.") : Loc.T("No road leads to the bridge."))
                + (res.Warnings.Count > 0 ? " " + string.Join(" ", res.Warnings) : "");
            BridgeNote.Text = step.Note;
            Refresh(step);
            Recompute();
            // the new bridge is the one to swap now
            var piece = world.PrefabSnapshot().FirstOrDefault(p => p.Def != null && BridgeKits.IsBridgePiece(p.Name) && p.Contains(res.Site.Center.X, res.Site.Center.Y));
            var nb = piece == null ? null : BridgeSwap.FromPieces(world, piece, BridgeKitList().Where(k => k.Found));
            if (nb != null) StartBridgeSwap(new SwapTarget { Built = nb, Gravel = nb.Site.Gravel });
            else EndBridgeSwap();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex))
        {
            ErrorLog.Write(ex);
            MessageBox.Show(this, Loc.T("Swapping the bridge failed:") + "\n" + ex.Message, Loc.T("Bridges"), MessageBoxButton.OK, MessageBoxImage.Error);
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
