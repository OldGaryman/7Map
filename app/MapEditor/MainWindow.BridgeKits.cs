using System.Windows;
using MapEditor.Map;

namespace MapEditor;

/// Bridge mods (the user's request 2026-11-24): the window "Bridge mods" (Settings › Mods) lists the kits – YAMEE_Bridges when it is in the
/// Mods folder, the bridge of the game and kits added by hand – each with check boxes (active, asphalt, gravel). The planners and the bridge
/// tool use the active ones (BridgeKits: per place a mixed order, the first that fits). While the bridge tool is on, a small window lists
/// the active kits to choose one by hand (or "randomly").
public partial class MainWindow
{
    List<BridgeKitDef> bridgeKitList;   // all kits; null = not read yet (again after changing the game folder)
    BridgePickWindow bridgePick;

    /// description of a piece also without an open map
    /// description of a piece of a bridge kit: kind Bridge (no part: own group, own layer; the user's request 2026-11-26)
    PrefabDef DescribePiece(string name)
    {
        PrefabDef def;
        if (world != null) def = PartDef(name);
        else
        {
            string file = prefabFiles?.GetValueOrDefault(name);
            if (file == null) return null;
            def = PrefabCatalog.Describe(file);
            if (!def.Found) return null;
        }
        if (def != null) def.Kind = PrefabKind.Bridge;
        return def;
    }

    List<BridgeKitDef> BridgeKitList()
    {
        if (bridgeKitList != null) return bridgeKitList;
        string game = GameFolder;
        prefabFiles ??= game == null ? new Dictionary<string, string>() : PrefabCatalog.AllFiles(game);
        bridgeKitList = BridgeKitStore.All(prefabFiles, DescribePiece);
        MarkBridgePieces();
        return bridgeKitList;
    }

    /// pieces of bridge kits on the map are bridges (kits added by hand are only known once the list of kits is read)
    void MarkBridgePieces()
    {
        if (world == null || bridgeKitList == null) return;
        bool changed = false;
        foreach (var p in world.PrefabSnapshot())
            if (p.Def != null && p.Def.Kind != PrefabKind.Bridge && BridgeKits.IsBridgePiece(p.Name)) { p.Def.Kind = PrefabKind.Bridge; changed = true; }
        if (changed) Map.InvalidateVisual();
    }

    /// the active kits that were found; null = none
    BridgeKits ActiveBridgeKits()
    {
        var k = new BridgeKits(BridgeKitList().Where(q => q.Active));
        return k.Kits.Count > 0 ? k : null;
    }

    void SaveBridgeKits()
    {
        try { BridgeKitStore.Save(BridgeKitList()); }
        catch (Exception ex) { ErrorLog.Write(ex); MessageBox.Show(this, Loc.T("Saving the bridge kits failed:") + "\n" + ex.Message, Loc.T("Bridge mods"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// Settings › Mods › Bridge mods
    void BridgeMods_Click(object sender, RoutedEventArgs e) => ShowBridgeMods();

    void ShowBridgeMods()
    {
        if (GameFolder == null)
        {
            MessageBox.Show(this, Loc.T("No game folder is set. The bridge pieces come from the game and its Mods folder (Settings › Paths)."), Loc.T("Bridge mods"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        bridgeKitList = null;   // read bridges.json anew: the values besides the width of the road are changed only there
        var list = BridgeKitList();
        var dlg = new BridgeModsWindow(list, prefabFiles, DescribePiece, SaveBridgeKits) { Owner = this };
        dlg.ShowDialog();
        SaveBridgeKits();
        SetBridgeKit();
        if (bridgeSwap != null) { swapOptions = SwapOptions(bridgeSwap); FillBridgeSwap(); }
        else bridgePick?.Fill(ActiveKitsFor(settings.BridgeGravel), settings.BridgeKitChoice, settings.BridgeGravel);
    }

    /// active kits that can be chosen for the surface (all active ones when none is meant for it)
    List<BridgeKitDef> ActiveKitsFor(bool gravel)
    {
        var all = ActiveBridgeKits()?.Kits ?? new List<BridgeKitDef>();
        var fit = all.Where(k => k.For(gravel)).ToList();
        return fit.Count > 0 ? fit : all;
    }

    /// the small window with the bridges while the bridge tool is on (like the window of the buildings, only bridges)
    void UpdateBridgePick(bool on)
    {
        if (!on)
        {
            if (bridgePick != null) { var p = bridgePick; bridgePick = null; Activate(); p.Close(); }
            return;
        }
        if (bridgePick == null)
        {
            var win = new BridgePickWindow { Owner = this };
            bridgePick = win;
            win.Chosen += id => { settings.BridgeKitChoice = id; settings.Save(); };
            win.SwapChosen += BridgeSwapChosen;
            win.SwapDone += EndBridgeSwap;
            win.ModsRequested += ShowBridgeMods;
            // closed with X: a swap ends with it (planned bridges: there is no tool to keep the window)
            win.Closed += (_, _) => { if (bridgePick != win) return; bridgePick = null; if (bridgeSwap != null) { bridgeSwap = null; swapOptions = new(); Map.BridgeSelected = null; } };
            win.Show();
            Activate();
        }
        if (bridgeSwap != null) FillBridgeSwap();
        else bridgePick.Fill(ActiveKitsFor(settings.BridgeGravel), settings.BridgeKitChoice, settings.BridgeGravel);
    }

    /// the kit for the bridge tool: the chosen one if it is active (and found), otherwise all active ones in mixed order
    BridgeKits KitsForTool(bool gravel)
    {
        var active = ActiveBridgeKits();
        if (active == null) return null;
        var chosen = settings.BridgeKitChoice != null ? active.Get(settings.BridgeKitChoice) : null;
        return chosen != null ? new BridgeKits(new[] { chosen }) : active;
    }
}
