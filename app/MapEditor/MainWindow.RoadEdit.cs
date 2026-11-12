using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MapEditor.Map;

namespace MapEditor;

/// Edit an existing road (the user's request, 2026-11-02): place markers exactly on the road center, from the second
/// marker on change height and slope in between. Calculation in Map/RoadReprofile.cs; intersections in between: crossing roads
/// follow the change as far as they can without steep stretches, otherwise the intersection stays at the old height.
public partial class MainWindow
{
    bool roadEditMode;
    ReprofilePlan editPlan;
    RoadScan roadScan;
    readonly Dictionary<(double, double, double, double), TraceResult> traceCache = new();
    readonly ReprofileOptions editOptions = new();
    List<Point> prevMarkers = new();
    string editNotice, shownEditHeight, shownEditGrade, shownEditCross;

    static string HelpRoadEdit => Loc.T("Edit road: click onto a road = marker · drag a marker = move · right-click a marker = delete · Esc = cancel");

    void InitRoadEdit()
    {
        Map.MarkerRejected += () =>
        {
            editNotice = Loc.T("There is no road here (roads inside city tiles cannot be edited).");
            UpdateToolModule();
        };
    }

    async void RoadEditTool_Click(object sender, RoutedEventArgs e)
    {
        if (RoadEditTool.IsChecked != true) { ToolCancel_Click(sender, e); return; }
        if (!await ConfirmSwitch(sender)) { UpdateToolModule(); return; }
        if (world == null) { RoadEditTool.IsChecked = false; return; }
        if (Map.EraseMode) SetEraseMode(false);
        ClearRoad();
        roadScan = new RoadScan(world);
        traceCache.Clear();
        editPlan = null;
        prevMarkers = new List<Point>();
        editNotice = null;
        road = RoadSettings.Asphalt();
        roadEditMode = true;
        editOptions.Even = EditEvenRadio.IsChecked == true;
        editOptions.KeepJunctions = EditKeepJunctionsCheck.IsChecked == true;
        ReadAutoCross();
        Map.MarkerSnap = SnapMarker;
        Map.InsertIndex = EditInsertIndex;
        SetEditMode(true);
        HelpText.Text = HelpRoadEdit;
    }

    /// clean up at the end (cancel, apply, other tool)
    void EndRoadEdit()
    {
        if (!roadEditMode && Map.MarkerSnap == null) return;
        roadEditMode = false;
        Map.MarkerSnap = null;
        Map.InsertIndex = null;
        Map.ExtraResults = null;
        editPlan = null;
        roadScan = null;
        traceCache.Clear();
        prevMarkers = new List<Point>();
        editNotice = null;
    }

    Point? SnapMarker(Point p)
    {
        if (roadScan == null) return null;
        var spot = RoadFollower.Center(roadScan, p);
        if (spot == null) return null;
        editNotice = null;
        return new Point(Math.Round(spot.Center.X, 1), Math.Round(spot.Center.Y, 1));
    }

    // insert a new marker between the existing ones if it lies on the shown stretch, otherwise append it at the end
    int EditInsertIndex(Point w)
    {
        var plan = editPlan;
        int count = Map.RoadPoints.Count;
        if (plan == null || plan.Error != null) return count;
        int best = -1;
        double bd = 64;
        for (int i = 0; i < plan.Path.Count; i += 2)
        {
            double d = (plan.Path[i].X - w.X) * (plan.Path[i].X - w.X) + (plan.Path[i].Z - w.Y) * (plan.Path[i].Z - w.Y);
            if (d < bd) { bd = d; best = i; }
        }
        if (best < 0) return count;
        for (int k = 0; k < plan.MarkerIndex.Length; k++)
            if (best <= plan.MarkerIndex[k]) return k;
        return count;
    }

    void RoadEditChanged(bool finished)
    {
        if (!finished) return;   // when dragging calculate only at the end
        editNotice = null;
        var cur = Map.RoadPoints.ToList();
        int select = -1;
        if (cur.Count > prevMarkers.Count)
        {
            select = 0;
            while (select < prevMarkers.Count && prevMarkers[select] == cur[select]) select++;
            select = Math.Min(select, cur.Count - 1);
        }
        prevMarkers = cur;
        RecomputeRoadEdit();
        if (select >= 0) Map.SelectedPoint = select;
    }

    void RecomputeRoadEdit()
    {
        var pts = Map.RoadPoints.ToList();
        editPlan = null;
        if (world != null && pts.Count >= 2)
        {
            try
            {
                editPlan = RoadReprofile.Plan(world, pts, Map.PointHeights.ToList(), editOptions, traceCache, roadScan, Map.PointCross.ToList());
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                editPlan = new ReprofilePlan { Error = ex.Message };
            }
        }
        var plan = editPlan != null && editPlan.Error == null ? editPlan : null;
        var r = plan?.ToResult();
        Map.Result = r;
        ShowProfile(r);
        Map.ExtraResults = plan == null ? null : plan.Junctions.Where(j => j.Followed).SelectMany(j => j.Arms).Select(ArmResult).ToList();
        UpdateButtons();
        UpdateToolModule();
    }

    // branch of an intersection that is dragged along, for the map (purple)
    static RoadResult ArmResult(BranchArm a)
    {
        var path = new List<PathPoint>();
        double dist = 0;
        path.Add(new PathPoint(a.Points[0].X, a.Points[0].Y, 0, 0));
        for (int k = 1; k < a.Points.Count; k++)
        {
            var from = a.Points[k - 1];
            var to = a.Points[k];
            double len = (to - from).Length;
            int steps = Math.Max(1, (int)Math.Ceiling(len * 4));
            for (int s = 1; s <= steps; s++)
            {
                var q = from + (to - from) * ((double)s / steps);
                dist += len / steps;
                path.Add(new PathPoint(q.X, q.Y, dist, k - 1));
            }
        }
        return new RoadResult { Settings = new RoadSettings { Surface = Surface.Gravel, Width = a.Width }, Path = path };
    }

    // ---------- box above the map ----------

    void UpdateRoadEditModule()
    {
        var plan = editPlan != null && editPlan.Error == null ? editPlan : null;
        int m = Map.RoadPoints.Count;
        ToolTitle.Text = Loc.T("Edit road") + (plan != null ? string.Format(Num, " – {0:#,0} m", plan.Path[plan.CoreTo].Dist - plan.Path[plan.CoreFrom].Dist) : "");
        string hint = m == 0 ? Loc.T("Click onto the road you want to change. The marker snaps to the middle of the road.")
            : m == 1 ? Loc.T("Set a second marker further along the same road. Then you can change the height and the grade between the markers.")
            : Loc.T("Click a marker, then type its new height, the grade of the road up to it or its banking. Drag a marker = move, click onto the blue road = insert a marker, right-click a marker = delete.");
        if (editNotice != null) hint += "\n⚠ " + editNotice;
        ToolHint.Text = hint;
        ToolApplyButton.IsEnabled = plan != null && plan.Changes;

        var info = new List<string>();
        if (plan != null)
        {
            if (plan.RunIn > 1 || plan.RunOut > 1)
                info.Add(Loc.F("Run-out (purple): {0:0} m before the first marker, {1:0} m after the last one.", plan.RunIn, plan.RunOut));
            for (int k = 0; k < plan.MarkerOld.Length; k++)
                if (Math.Abs(plan.MarkerNew[k] - plan.MarkerOld[k]) >= 0.05)
                    info.Add(Loc.F("Marker {0}: {1:0.0} m → {2:0.0} m", k + 1, plan.MarkerOld[k], plan.MarkerNew[k]));
            for (int k = 0; k < plan.MarkerCrossOld.Length; k++)
                if (Math.Abs(plan.MarkerCrossNew[k] - plan.MarkerCrossOld[k]) >= 0.002)
                    info.Add(Loc.F("Marker {0}: banking {1:+0.0;-0.0;0.0} % → {2:+0.0;-0.0;0.0} %", k + 1, plan.MarkerCrossOld[k] * 100, plan.MarkerCrossNew[k] * 100));
            if (!plan.Changes) info.Add(Loc.T("Nothing changed yet: give a marker a new height, a grade or a banking."));
            int followed = plan.Junctions.Count(j => j.Followed), held = plan.Junctions.Count(j => j.Pinned || j.Limited);
            if (plan.Junctions.Count > 0)
            {
                info.Add(Loc.F("{0} junction(s) on the way: {1} crossing road(s) follow the change (green), {2} stay or are reduced (orange).",
                    plan.Junctions.Count, followed, held));
                foreach (var j in plan.Junctions.Where(j => j.Limited))
                    info.Add("  · " + Loc.F("Junction at {0:0}, {1:0}: the change is reduced to {2:+0.00;-0.00} m ({3}).", j.Center.X, j.Center.Y, j.Limit, j.Why));
            }
            int fixedAnchors = plan.Anchors.Count(a => a.Pinnable);
            if (fixedAnchors > 0)
                info.Add(Loc.F("{0} entrance(s) of buildings or exits of city tiles next to the road keep their height.", fixedAnchors));
            if (editOptions.AutoCross)
                info.Add(Loc.F("Automatic banking: {0} curve(s), up to {1:0.#} %.", plan.AutoCurves, plan.MaxCross * 100));
        }
        EditInfo.Text = string.Join("\n", info);
        var warn = new List<string>();
        if (editPlan?.Error != null) warn.Add(editPlan.Error);
        if (plan != null) warn.AddRange(plan.Warnings);
        EditWarn.Text = string.Join("\n", warn.Select(w => "⚠ " + w));
        EditWarn.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EditInfo.Visibility = info.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateEditPanel();
    }

    // height and slope of the chosen marker
    void UpdateEditPanel()
    {
        if (!roadEditMode) return;
        int i = Map.SelectedPoint, m = Map.RoadPoints.Count;
        var plan = editPlan != null && editPlan.Error == null ? editPlan : null;
        bool show = i >= 0 && i < m && Map.PointHeights.Count == m;
        EditHeightRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        EditGradeRow.Visibility = show && plan != null ? Visibility.Visible : Visibility.Collapsed;
        EditCrossRow.Visibility = show && plan != null && Map.PointCross.Count == m ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var pt = Map.RoadPoints[i];
        double terrain = world.HeightAt((int)Math.Floor(pt.X), (int)Math.Floor(pt.Y));
        bool manual = Map.PointHeights[i] is double;
        double current = plan != null && i < plan.MarkerOld.Length ? plan.MarkerOld[i] : terrain;
        EditMarkerLabel.Text = Loc.F("Marker {0}", i + 1);
        EditMarkerInfo.Text = Loc.F("Now {0:0.0} m", current) + " · " + (manual ? Loc.T("changed") : Loc.T("unchanged"));
        EditHeightBox.IsEnabled = plan != null;
        if (!EditHeightBox.IsKeyboardFocused)
        {
            shownEditHeight = ((Map.PointHeights[i] ?? current)).ToString("0.0", Num);
            EditHeightBox.Text = shownEditHeight;
            EditHeightBox.ClearValue(BorderBrushProperty);
        }
        if (plan == null || i >= plan.MarkerNew.Length) return;
        int seg = i > 0 ? i - 1 : 0;
        double len = plan.SegmentLength[seg], grade = len > 0 ? (plan.MarkerNew[seg + 1] - plan.MarkerNew[seg]) / len * 100 : 0;
        EditGradeLabel.Text = i > 0 ? Loc.F("Grade from marker {0}", i) : Loc.T("Grade to marker 2");
        EditGradeInfo.Text = Loc.F("{0:#,0} m long", len);
        if (!EditGradeBox.IsKeyboardFocused)
        {
            shownEditGrade = grade.ToString("+0.0;-0.0;0.0", Num);
            EditGradeBox.Text = shownEditGrade;
            EditGradeBox.ClearValue(BorderBrushProperty);
        }
        // banking of this marker
        if (Map.PointCross.Count != m) return;
        double nowCross = plan.MarkerCrossOld[i] * 100;
        EditCrossInfo.Text = Loc.F("Now {0:+0.0;-0.0;0.0} %", nowCross) + " · " + (Map.PointCross[i] is double ? Loc.T("changed") : Loc.T("unchanged"));
        if (!EditCrossBox.IsKeyboardFocused)
        {
            shownEditCross = ((Map.PointCross[i] ?? plan.MarkerCrossOld[i]) * 100).ToString("+0.0;-0.0;0.0", Num);
            EditCrossBox.Text = shownEditCross;
            EditCrossBox.ClearValue(BorderBrushProperty);
        }
    }

    void EditCross_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { shownEditCross = null; EditCross_Changed(sender, e); }
    }

    // banking typed in (percent; + = left side higher)
    void EditCross_Changed(object sender, RoutedEventArgs e)
    {
        int i = Map.SelectedPoint;
        if (!roadEditMode || i < 0 || i >= Map.PointCross.Count || EditCrossBox.Text == shownEditCross) return;
        double v = ReadBox(EditCrossBox, -30, 30, double.NaN);
        if (double.IsNaN(v)) return;
        shownEditCross = EditCrossBox.Text;
        Map.SetPointCross(i, v / 100);
    }

    void EditCrossReset_Click(object sender, RoutedEventArgs e)
    {
        if (roadEditMode && Map.SelectedPoint >= 0) Map.SetPointCross(Map.SelectedPoint, null);
    }

    void EditHeight_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { shownEditHeight = null; EditHeight_Changed(sender, e); }
    }

    void EditHeight_Changed(object sender, RoutedEventArgs e)
    {
        int i = Map.SelectedPoint;
        if (!roadEditMode || i < 0 || EditHeightBox.Text == shownEditHeight) return;   // only looked at, not changed
        double v = ReadBox(EditHeightBox, 0, 400, double.NaN);
        if (double.IsNaN(v)) return;
        shownEditHeight = EditHeightBox.Text;
        Map.SetPointHeight(i, v);
    }

    void EditGrade_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { shownEditGrade = null; EditGrade_Changed(sender, e); }
    }

    // slope typed in: the height of the later marker of the section follows from it
    void EditGrade_Changed(object sender, RoutedEventArgs e)
    {
        int i = Map.SelectedPoint;
        var plan = editPlan;
        if (!roadEditMode || i < 0 || plan == null || plan.Error != null || i >= plan.MarkerNew.Length || EditGradeBox.Text == shownEditGrade) return;
        double g = ReadBox(EditGradeBox, -60, 60, double.NaN);
        if (double.IsNaN(g)) return;
        shownEditGrade = EditGradeBox.Text;
        int seg = i > 0 ? i - 1 : 0;
        double len = plan.SegmentLength[seg], dh = g / 100 * len;
        if (i > 0) Map.SetPointHeight(i, plan.MarkerNew[i - 1] + dh);
        else Map.SetPointHeight(0, plan.MarkerNew[1] - dh);
    }

    void EditMarkerReset_Click(object sender, RoutedEventArgs e)
    {
        if (roadEditMode && Map.SelectedPoint >= 0) Map.SetPointHeight(Map.SelectedPoint, null);
    }

    // read the checkbox and maximum value of the automatic banking
    void ReadAutoCross()
    {
        editOptions.AutoCross = EditAutoCrossCheck.IsChecked == true;
        editOptions.AutoCrossMax = ReadBox(EditAutoCrossBox, 1, 20, editOptions.AutoCrossMax * 100) / 100;
        EditAutoCrossBox.IsEnabled = editOptions.AutoCross;
    }

    void EditAutoMax_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) EditOptions_Click(sender, e);
    }

    // values of the options from the settings into the fields
    void ShowEditValues()
    {
        EditEvenRadio.IsChecked = settings.EditEven;
        EditShapeRadio.IsChecked = !settings.EditEven;
        EditKeepJunctionsCheck.IsChecked = settings.EditKeepJunctions;
        EditAutoCrossCheck.IsChecked = settings.EditAutoCross;
        EditAutoCrossBox.Text = Fmt(settings.EditAutoCrossMax);
        EditAutoCrossBox.IsEnabled = settings.EditAutoCross;
        editOptions.Even = settings.EditEven; editOptions.KeepJunctions = settings.EditKeepJunctions;
        editOptions.AutoCross = settings.EditAutoCross; editOptions.AutoCrossMax = settings.EditAutoCrossMax / 100;
    }

    // remember changed options at once in user.ini
    void SaveEditOptions()
    {
        double max = editOptions.AutoCrossMax * 100;
        if (settings.EditEven == editOptions.Even && settings.EditKeepJunctions == editOptions.KeepJunctions && settings.EditAutoCross == editOptions.AutoCross
            && Math.Abs(settings.EditAutoCrossMax - max) < 1e-9) return;
        settings.EditEven = editOptions.Even; settings.EditKeepJunctions = editOptions.KeepJunctions;
        settings.EditAutoCross = editOptions.AutoCross; settings.EditAutoCrossMax = max;
        settings.Save();
    }

    void EditOptions_Click(object sender, RoutedEventArgs e)
    {
        editOptions.Even = EditEvenRadio.IsChecked == true;
        editOptions.KeepJunctions = EditKeepJunctionsCheck.IsChecked == true;
        ReadAutoCross();
        SaveEditOptions();
        if (roadEditMode) RecomputeRoadEdit();
    }

    // ---------- Apply ----------

    async Task ApplyRoadEdit()
    {
        var plan = editPlan;
        if (plan == null || plan.Error != null || world == null || edits == null || Map.Busy) return;
        if (!plan.Changes) return;
        if (plan.Warnings.Count > 0 && MessageBox.Show(this, Loc.T("Attention:") + "\n• " + string.Join("\n• ", plan.Warnings) + "\n\n" + Loc.T("Apply anyway?"),
                Loc.T("Apply"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (Map.EditMode) SetEditMode(false);

        var points = Map.RoadPoints.ToList();
        var targets = Map.PointHeights.ToList();
        var crosses = Map.PointCross.ToList();
        var options = new ReprofileOptions { Even = editOptions.Even, KeepJunctions = editOptions.KeepJunctions, AutoCross = editOptions.AutoCross, AutoCrossMax = editOptions.AutoCrossMax };
        double length = plan.Path[plan.CoreTo].Dist - plan.Path[plan.CoreFrom].Dist;
        string title = Loc.F("Edit road {0:#,0} m", length);
        string detail = "Strasse bearbeiten: Markierungen " + string.Join(";", points.Select((p, k) => string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0}={2}",
                p.X, p.Y, targets[k] is double h ? h.ToString("0.0", CultureInfo.InvariantCulture) : "auto"))) +
            (crosses.Any(c => c != null) ? ", Querneigung " + string.Join(";", crosses.Select(c => c is double q ? (q * 100).ToString("0.0", CultureInfo.InvariantCulture) : "auto")) : "") +
            (options.AutoCross ? string.Format(CultureInfo.InvariantCulture, ", automatische Querneigung bis {0:0.#} %", options.AutoCrossMax * 100) : "") +
            (options.Even ? ", gleichmaessige Neigung" : ", Form behalten") + (options.KeepJunctions ? ", Kreuzungen festgehalten" : ", Kreuzungen mitgezogen") +
            string.Format(CultureInfo.InvariantCulture, ", Auslauf {0:0}/{1:0} m", plan.RunIn, plan.RunOut);
        int followed = plan.Junctions.Count(j => j.Followed), held = plan.Junctions.Count(j => j.Pinned || j.Limited);

        Overlay.Visibility = Visibility.Visible;
        Map.Busy = true;   // blocks Ctrl+Z/Y/S while the background thread is calculating
        LoadingText.Text = Loc.T("Changing the road …");
        IProgress<string> progress = new Progress<string>(t => LoadingText.Text = t);
        var w = world;
        try
        {
            RoadReprofile.Stats stats = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                // recalculate on the terrain as it is now (also on repeat and after deleting earlier steps)
                var pl = RoadReprofile.Plan(w, points, targets, options, cross: crosses);
                if (pl.Error != null) return;
                var st = RoadReprofile.Apply(w, e, pl, progress);
                stats ??= st;
            }));
            Refresh(step);
            ClearRoad();
            step.Note = Loc.F("Applied: {0:#,0} height points changed.", stats?.HeightPoints ?? 0) +
                (followed + held > 0 ? " " + Loc.F("Junctions: {0} followed, {1} kept at their old height.", followed, held) : "");
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            MessageBox.Show(this, Loc.T("Applying failed:") + "\n" + ex.Message + "\n\n" + Loc.T("The map is unchanged."), Loc.T("Apply"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Map.Busy = false;
            Overlay.Visibility = Visibility.Collapsed;
            UpdateButtons();
            UpdateUndo();
        }
    }
}
