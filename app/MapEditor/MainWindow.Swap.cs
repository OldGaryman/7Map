using System.Globalization;
using System.Windows;
using System.Windows.Input;
using MapEditor.Map;

namespace MapEditor;

/// Gebäude auf einem Bauplatz einer Stadt-Kachel austauschen (Wunsch des Nutzers 29.10.2026): Knopf „Austauschen …“ im
/// Fenster „Gebäude“, Auswahl nur aus Gebäuden, die nach den Regeln des Spiels auf den Platz passen (TileLots.Fits).
public partial class MainWindow
{
    Dictionary<string, PrefabDef> poiIndex;   // alle Gebäudebeschreibungen aus Spiel und Mods (ohne Teile und Kacheln), einmal gelesen
    TileLot detailLot;                        // Bauplatz der Kopie im Fenster „Gebäude“ (null = keiner)

    // im Fenster „Gebäude“: Bauplatz der gewählten Kopie anzeigen, Knopf nur auf einem Bauplatz
    void ShowLot(PlacedPrefab p)
    {
        detailLot = p == null || world == null ? null : TileLots.Under(world, p);
        if (p != null && p.IsTile && world != null) { detailLot = null; ShowTile(p); return; }
        var win = prefabWindow;
        win.LotPanel.Visibility = detailLot == null ? Visibility.Collapsed : Visibility.Visible;
        if (detailLot == null) return;
        win.LotText.Text = Loc.F("Lot {0} × {1} m in city tile {2} (district {3}{4}).", detailLot.Width, detailLot.Depth, detailLot.Tile.Name, detailLot.District,
            string.IsNullOrEmpty(detailLot.Marker.Tag) ? "" : ", " + Loc.F("tag {0}", detailLot.Marker.Tag));
    }

    async void SwapButton_Click(object sender, RoutedEventArgs e)
    {
        var old = detailPrefab;
        if (old != null && old.IsTile) { SwapTileDialog(old); return; }
        var lot = detailLot;
        if (old == null || lot == null || world == null || edits == null) return;
        prefabWindow.SwapButton.IsEnabled = false;
        try { if (!await EnsurePoiIndex()) return; }
        finally { prefabWindow.SwapButton.IsEnabled = true; }
        if (detailPrefab != old || detailLot != lot) return;   // inzwischen etwas anderes gewählt
        string chosen = ChooseForLot(lot, old.Name, Loc.F("Swap \"{0}\" for:", Caption(old)), prefabWindow);
        if (chosen != null) SwapPrefab(old, lot, chosen, poiIndex[chosen]);
    }

    // einmal alle Beschreibungen lesen (ca. 1.000 Dateien, im Hintergrund); false = kein Spielordner
    async Task<bool> EnsurePoiIndex()
    {
        if (poiIndex != null) return true;
        if (prefabFiles == null) return false;
        string game = GameFolder;
        var files = prefabFiles;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            poiIndex = await Task.Run(() =>
            {
                var index = new Dictionary<string, PrefabDef>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, file) in files)
                {
                    string f = file.Replace('/', '\\');
                    if (f.Contains("\\Parts\\", StringComparison.OrdinalIgnoreCase) || f.Contains("\\RWGTiles\\", StringComparison.OrdinalIgnoreCase)) continue;
                    var d = PrefabCatalog.Describe(file);
                    if (!d.Found) continue;
                    d.Kind = name.Contains("_filler", StringComparison.OrdinalIgnoreCase) ? PrefabKind.Filler : PrefabKind.Poi;
                    index[name] = d;
                }
                foreach (var (name, label) in PrefabCatalog.Labels(game, index.Keys)) index[name].Label = label;
                return index;
            });
        }
        finally { Mouse.OverrideCursor = null; }
        return true;
    }

    // Fenster mit den Gebäuden, die genau auf den Platz passen; Ergebnis = gewählter Name (null = abgebrochen)
    string ChooseForLot(TileLot lot, string exclude, string header, Window owner, bool building = false)
    {
        var candidates = poiIndex.Where(kv => !kv.Key.Equals(exclude, StringComparison.OrdinalIgnoreCase) && TileLots.Fits(kv.Value, lot))
            .Select(kv => new SwapCandidate(kv.Key, kv.Value.Label, kv.Value.Tier, kv.Value.SizeX, kv.Value.SizeZ, ImageOf(kv.Key)))
            .OrderBy(c => c.Tier).ThenBy(c => string.IsNullOrEmpty(c.Label) ? c.Name : c.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        string rule = Loc.F("{0} building(s) fit this lot: exactly {1} × {2} m, tag {3}{4}.", candidates.Count, lot.Width, lot.Depth, lot.District,
            string.IsNullOrEmpty(lot.Marker.Tag) ? "" : " " + Loc.F("and {0}", lot.Marker.Tag));
        var dlg = new SwapWindow(header, rule, candidates, building) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Chosen : null;
    }

    /// Klick auf einen freien Bauplatz einer Stadt-Kachel in der Karte (Wunsch des Nutzers: nach dem Löschen wieder genau
    /// passend bebauen, ohne von Hand zu zielen): Auswahl wie beim Austauschen, Lage/Drehung/Höhe wie das Spiel
    async void BuildOnLot(TileLot lot)
    {
        if (world == null || edits == null || Map.Busy) return;
        if (!await ConfirmSwitch(null)) return;   // unfertige Straße/Kette: erst fragen
        if (Map.EraseMode) SetEraseMode(false);   // Pinsel wirken sofort, nur das Werkzeug beenden
        if (Map.EditMode || Map.RoadPoints.Count > 0) ClearRoad();
        if (!await EnsurePoiIndex()) return;
        string chosen = ChooseForLot(lot, null, Loc.F("Build on the free lot {0} × {1} m (city tile {2}):", lot.Width, lot.Depth, lot.Tile.Name), this, building: true);
        if (chosen == null) return;
        var p = TileLots.Place(lot, chosen, poiIndex[chosen]);
        var parts = PrefabParts.For(p, PartDef);
        string detail = string.Format(CultureInfo.InvariantCulture, "Gebäude {0} auf freiem Bauplatz bei {1},{2},{3} Drehung {4} (Kachel {5})",
            p.Name, p.X, p.Y, p.Z, p.Rotation, lot.Tile.Name) + PartsDetail(parts);
        RunPrefabStep(Loc.F("Building {0} placed on lot", Caption(p)), detail, e =>
        {
            e.AddPrefab(p);
            foreach (var q in parts) e.AddPrefab(q);
        }, p, () => PartsHint(parts));
    }

    string ImageOf(string name)
    {
        string file = prefabFiles?.GetValueOrDefault(name);
        string jpg = file == null ? null : Path.ChangeExtension(file, ".jpg");
        return jpg != null && File.Exists(jpg) ? jpg : null;
    }

    // altes Gebäude samt eigenen Teilen weg, neues an dieselbe Stelle der Liste, Zufahrten aus seinen Markierungen.
    // Das Gelände bleibt: die Höhe gibt die Kachel vor.
    void SwapPrefab(PlacedPrefab old, TileLot lot, string name, PrefabDef def)
    {
        var p = TileLots.Place(lot, name, def);
        var oldParts = PrefabParts.Attached(world, old);
        var newParts = PrefabParts.For(p, PartDef);
        string detail = string.Format(CultureInfo.InvariantCulture, "Gebäude {0} bei {1},{2},{3} Drehung {4} getauscht gegen {5} bei {6},{7},{8} Drehung {9} (Kachel {10})",
            old.Name, old.X, old.Y, old.Z, old.Rotation, p.Name, p.X, p.Y, p.Z, p.Rotation, lot.Tile.Name) + PartsDetail(newParts);
        RunPrefabStep(Loc.F("Building {0} replaced by {1}", Caption(old), Caption(p)), detail, e =>
        {
            foreach (var q in oldParts) e.RemovePrefab(q);
            e.ReplacePrefab(old, p);
            foreach (var q in newParts) e.AddPrefab(q);
        }, p, () => PartsHint(newParts));
    }
}
