using System.Windows;
using System.Windows.Controls;
using MapEditor.Map;

namespace MapEditor;

/// Window "Create roads" (stage 1): settings, "Plan network" and the result; the preview shows the map.
/// Not modal – the main window calculates (Planner) and shows the network; closing removes the preview (Closed).
public partial class RoadNetWindow : Window
{
    readonly bool ready;
    public NetworkSpec Spec { get; private set; }
    public bool BreakInWasteland => true;   // always on (the user's request 2026-11-23)
    /// calculates with the settings and returns the result text
    public Func<NetworkSpec, Task<string>> Planner { get; set; }
    /// builds the last planned network (stage 2) and returns the result text
    public Func<Task<string>> Builder { get; set; }
    /// is there a network with at least one road to build?
    public Func<bool> HasPlan { get; set; }
    // editing the plan by hand (the user's request 2026-11-12); the main window changes the plan and the map
    public Action<bool> SetGravel { get; set; }
    public Action<double> SetWidth { get; set; }
    public Action DeleteRoad { get; set; }
    public Action UndoPlan { get; set; }
    public Action ShowSaved { get; set; }
    public Action ClearVia { get; set; }
    /// draw a new road on/off (true = on)
    public Action<bool> DrawRoad { get; set; }
    /// a line of the places to check was clicked (index in the list given to SetSpots)
    public Action<int> JumpToSpot { get; set; }

    /// list of places to check below the result (empty = hidden); every line is a link
    public void SetSpots(IReadOnlyList<string> texts)
    {
        SpotPanel.Children.Clear();
        SpotBox.Visibility = texts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < texts.Count; i++)
        {
            int index = i;
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(texts[i]));
            link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AccentBrush");
            link.Click += (_, _) => JumpToSpot?.Invoke(index);
            SpotPanel.Children.Add(new TextBlock(link) { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 2) });
        }
    }

    /// state of drawing a new road: on/off and the hint (null = none)
    public void ShowDrawing(bool on, string note, bool problem = false)
    {
        DrawRoadButton.IsChecked = on;
        DrawNote.Text = note ?? "";
        DrawNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        DrawNote.SetResourceReference(TextBlock.ForegroundProperty, problem ? "ErrorBrush" : "AccentBrush");
    }

    void DrawRoad_Click(object sender, RoutedEventArgs e) => DrawRoad?.Invoke(DrawRoadButton.IsChecked == true);

    /// message about rerouting (null = none; busy = shown as progress, not as a problem)
    public void ShowRouteNote(string text, bool busy = false)
    {
        RouteNote.Text = text ?? "";
        RouteNote.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        RouteNote.SetResourceReference(TextBlock.ForegroundProperty, busy ? "HintTextBrush" : "ErrorBrush");
    }

    void ClearVia_Click(object sender, RoutedEventArgs e) => ClearVia?.Invoke();

    /// change the selected node tile: kind "X"/"T"/"I" (null = keep) and turn by 90° (the user's request 2026-11-16)
    public Action<string, bool> ChangeTile { get; set; }

    /// show the selected node tile (null = none); kind "X", "T" or "I"
    public void ShowTile(string kind, string info)
    {
        TileBox.Visibility = kind == null ? Visibility.Collapsed : Visibility.Visible;
        if (kind == null) return;
        showing = true;
        TileInfo.Text = info;
        TileKindBox.SelectedIndex = kind == "X" ? 0 : kind == "T" ? 1 : 2;
        showing = false;
    }

    /// message about the node tile (null = none; busy = shown as progress, not as a problem)
    public void ShowTileNote(string text, bool busy = false)
    {
        TileNote.Text = text ?? "";
        TileNote.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        TileNote.SetResourceReference(TextBlock.ForegroundProperty, busy ? "HintTextBrush" : "ErrorBrush");
    }

    void TileKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (showing || !ready || TileKindBox.SelectedIndex < 0) return;
        ChangeTile?.Invoke(TileKindBox.SelectedIndex switch { 0 => "X", 1 => "T", _ => "I" }, false);
    }

    void TurnTile_Click(object sender, RoutedEventArgs e) => ChangeTile?.Invoke(null, true);
    bool planned;
    bool showing;   // filling the fields of the selected road (no change events)

    /// state from the main window: a plan to build is shown / undo in the plan possible / saved plan can be shown again
    public void SetPlanState(bool hasPlan, bool canUndo, bool canShowSaved)
    {
        planned = hasPlan;
        PlanUndoButton.IsEnabled = canUndo;
        DrawRoadButton.IsEnabled = hasPlan;
        LegendBox.Visibility = hasPlan ? Visibility.Visible : Visibility.Collapsed;
        ShowSavedButton.Visibility = canShowSaved ? Visibility.Visible : Visibility.Collapsed;
        Check();
    }

    public void SetResult(string text) { if (text != null) ResultText.Text = text; }

    /// show the selected road (null = none)
    public void ShowSelection(NetRoad r, string info)
    {
        SelectedBox.Visibility = r == null ? Visibility.Collapsed : Visibility.Visible;
        if (r == null) return;
        showing = true;
        SelectedInfo.Text = info;
        KindBox.SelectedIndex = r.Gravel ? 1 : 0;
        ClearViaButton.IsEnabled = r.Via.Count > 0;
        WidthBox.Text = r.Width.ToString("0.#", Loc.Num);
        showing = false;
    }

    void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (showing || !ready || KindBox.SelectedIndex < 0) return;
        SetGravel?.Invoke(KindBox.SelectedIndex == 1);
    }

    void TakeWidth()
    {
        if (showing || SelectedBox.Visibility != Visibility.Visible) return;
        if (Percent(WidthBox.Text, out double v) && v >= 4 && v <= 24) { ErrorText.Text = ""; SetWidth?.Invoke(v); }
        else ErrorText.Text = Loc.T("Roadway width: 4 to 24 m.");
    }

    void WidthBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { TakeWidth(); e.Handled = true; }
    }
    void WidthBox_LostFocus(object sender, RoutedEventArgs e) => TakeWidth();
    void DeleteRoad_Click(object sender, RoutedEventArgs e) => DeleteRoad?.Invoke();
    void PlanUndo_Click(object sender, RoutedEventArgs e) => UndoPlan?.Invoke();
    void ShowSaved_Click(object sender, RoutedEventArgs e) => ShowSaved?.Invoke();

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        bool inBox = e.OriginalSource is TextBox;
        // Esc while drawing a new road cancels the drawing (not the window)
        if (e.Key == System.Windows.Input.Key.Escape && DrawRoadButton.IsChecked == true) { DrawRoad?.Invoke(false); e.Handled = true; return; }
        if (!busy && !inBox && e.Key == System.Windows.Input.Key.Z && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control && PlanUndoButton.IsEnabled)
        { UndoPlan?.Invoke(); e.Handled = true; return; }
        if (!busy && !inBox && e.Key == System.Windows.Input.Key.Delete && SelectedBox.Visibility == Visibility.Visible)
        { DeleteRoad?.Invoke(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    public RoadNetWindow(NetworkSpec start)
    {
        InitializeComponent();
        Loc.Translate(this);
        Fill(start);
        SeedBox.Text = start.Seed;
        ready = true;
        Check();
    }

    void Fill(NetworkSpec start)
    {
        GradeBox.Text = (start.Grade * 100).ToString("0.#", Loc.Num);
        MaxGradeBox.Text = (start.MaxGrade * 100).ToString("0.#", Loc.Num);
        CutBox.Text = start.RoadCut.ToString("0.#", Loc.Num);
        FillBox.Text = Math.Max(0, start.RoadFill).ToString("0.#", Loc.Num);
        LoopsBox.SelectedIndex = Math.Clamp(start.Loops, 0, 2);
        CurvesBox.SelectedIndex = Math.Clamp(start.Curves, 0, 2);
        CutsBox.SelectedIndex = Math.Clamp(start.Cuts, 0, 2);
        NodeTilesBox.SelectedIndex = Math.Clamp(start.NodeTiles, 0, 2);
    }

    /// all values of the window back to default.ini (the seed stays)
    void ResetValues_Click(object sender, RoutedEventArgs e) => Fill(UserValues.LoadDefaults(new NetworkSpec()));

    void Text_Changed(object sender, TextChangedEventArgs e) => Check();
    void Selection_Changed(object sender, SelectionChangedEventArgs e) => Check();
    void Check_Changed(object sender, RoutedEventArgs e) => Check();
    void RandomSeed_Click(object sender, RoutedEventArgs e) => SeedBox.Text = new Random().Next(1000, 99999).ToString();
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    static bool Percent(string text, out double v) =>
        double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, Loc.Num, out v);

    /// read the input; null = error (text in ErrorText)
    NetworkSpec Read()
    {
        string error = null;
        var s = UserValues.Load(new NetworkSpec());
        s.Loops = Math.Max(0, LoopsBox.SelectedIndex); s.Curves = Math.Max(0, CurvesBox.SelectedIndex); s.Cuts = Math.Max(0, CutsBox.SelectedIndex); s.NodeTiles = Math.Max(0, NodeTilesBox.SelectedIndex); s.BreakInWasteland = true; s.Seed = SeedBox.Text.Trim();
        if (!Percent(GradeBox.Text, out double g) || g < 2 || g > 20) error = Loc.T("Usual gradient: 2 to 20 %.");
        else s.Grade = g / 100;
        if (!Percent(MaxGradeBox.Text, out double m) || m < 2 || m > 25) error ??= Loc.T("Gradient in tight passages: 2 to 25 %.");
        else if (m < g) error ??= Loc.T("The gradient in tight passages must not be smaller than the usual gradient.");
        else s.MaxGrade = m / 100;
        if (!Percent(CutBox.Text, out double cut) || cut < 0 || cut > 10) error ??= Loc.T("Cut: 0 to 10 m.");
        else s.RoadCut = cut;
        if (!Percent(FillBox.Text, out double fill) || fill < 0 || fill > 10) error ??= Loc.T("Embankment: 0 to 10 m.");
        else s.RoadFill = fill;
        ErrorText.Text = error ?? "";
        return error == null ? s : null;
    }

    void Check()
    {
        if (!ready) return;
        PlanButton.IsEnabled = Read() != null && !busy;
        BuildButton.IsEnabled = planned && !busy && Builder != null;
    }

    async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (!planned || Builder == null || busy) return;
        busy = true;
        Check();
        string before = ResultText.Text;
        ResultText.Text = Loc.T("Building the roads …");
        try
        {
            string text = await Builder();
            if (text != null) { ResultText.Text = text; planned = HasPlan?.Invoke() == true; }   // built: the preview is gone (the plan stays saved)
            else ResultText.Text = before;
        }
        catch (Exception ex) when (WorkCancel.IsCancel(ex)) { ResultText.Text = before; }
        catch (Exception ex) { ErrorLog.Write(ex); ResultText.Text = Loc.T("Building the roads failed:") + "\n" + ex.Message; }
        finally { busy = false; Check(); }
    }

    bool busy;

    async void Plan_Click(object sender, RoutedEventArgs e)
    {
        var s = Read();
        if (s == null || Planner == null || busy) return;
        Spec = s;
        busy = true;
        PlanButton.IsEnabled = false;
        ResultText.Text = Loc.T("Planning …");
        planned = false;
        try { ResultText.Text = await Planner(s); planned = HasPlan?.Invoke() == true; }
        catch (Exception ex) when (WorkCancel.IsCancel(ex)) { ResultText.Text = Loc.T("Cancelled."); }
        catch (Exception ex) { ErrorLog.Write(ex); ResultText.Text = Loc.T("Planning the road network failed:") + "\n" + ex.Message; }
        finally { busy = false; Check(); }
    }

    /// progress (may come from the background and arrive after the result – then do not show it any more)
    public void ShowProgress(string text) => Dispatcher.BeginInvoke(() => { if (busy) ResultText.Text = text; });
}
