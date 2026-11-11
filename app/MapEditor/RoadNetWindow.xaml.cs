using System.Windows;
using System.Windows.Controls;
using MapEditor.Map;

namespace MapEditor;

/// Fenster „Straßen erzeugen“ (Stufe 1): Einstellungen, „Netz planen“ und das Ergebnis; die Vorschau zeigt die Karte.
/// Nicht modal – das Hauptfenster rechnet (Planner) und zeigt das Netz; Schließen nimmt die Vorschau weg (Closed).
public partial class RoadNetWindow : Window
{
    readonly bool ready;
    public NetworkSpec Spec { get; private set; }
    /// rechnet mit den Einstellungen und liefert den Ergebnistext
    public Func<NetworkSpec, Task<string>> Planner { get; set; }
    /// baut das zuletzt geplante Netz (Stufe 2) und liefert den Ergebnistext
    public Func<Task<string>> Builder { get; set; }
    /// gibt es ein Netz mit mindestens einer Straße zum Bauen?
    public Func<bool> HasPlan { get; set; }
    bool planned;

    public RoadNetWindow(NetworkSpec start)
    {
        InitializeComponent();
        Loc.Translate(this);
        GradeBox.Text = (start.Grade * 100).ToString("0.#", Loc.Num);
        MaxGradeBox.Text = (start.MaxGrade * 100).ToString("0.#", Loc.Num);
        CutBox.Text = start.RoadCut.ToString("0.#", Loc.Num);
        FillBox.Text = Math.Max(0, start.RoadFill).ToString("0.#", Loc.Num);
        LoopsBox.SelectedIndex = Math.Clamp(start.Loops, 0, 2);
        CurvesBox.SelectedIndex = Math.Clamp(start.Curves, 0, 2);
        CutsBox.SelectedIndex = Math.Clamp(start.Cuts, 0, 2);
        SeedBox.Text = start.Seed;
        ready = true;
        Check();
    }

    void Text_Changed(object sender, TextChangedEventArgs e) => Check();
    void Selection_Changed(object sender, SelectionChangedEventArgs e) => Check();
    void RandomSeed_Click(object sender, RoutedEventArgs e) => SeedBox.Text = new Random().Next(1000, 99999).ToString();
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    static bool Percent(string text, out double v) =>
        double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, Loc.Num, out v);

    /// Eingaben lesen; null = Fehler (Text in ErrorText)
    NetworkSpec Read()
    {
        string error = null;
        var s = UserValues.Load(new NetworkSpec());
        s.Loops = Math.Max(0, LoopsBox.SelectedIndex); s.Curves = Math.Max(0, CurvesBox.SelectedIndex); s.Cuts = Math.Max(0, CutsBox.SelectedIndex); s.Seed = SeedBox.Text.Trim();
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
            if (text != null) { ResultText.Text = text; planned = false; }   // gebaut: Plan ist verbraucht
            else ResultText.Text = before;
        }
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
        catch (Exception ex) { ErrorLog.Write(ex); ResultText.Text = Loc.T("Planning the road network failed:") + "\n" + ex.Message; }
        finally { busy = false; Check(); }
    }

    /// Fortschritt (kann aus dem Hintergrund kommen und nach dem Ergebnis eintreffen – dann nicht mehr anzeigen)
    public void ShowProgress(string text) => Dispatcher.BeginInvoke(() => { if (busy) ResultText.Text = text; });
}
