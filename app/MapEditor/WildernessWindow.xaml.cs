using System.Windows;
using System.Windows.Controls;
using MapEditor.Map;

namespace MapEditor;

/// Fenster „Wildnis-POIs erzeugen“: Einstellungen, „Planen“, „Bauen“ und das Ergebnis; die Vorschau zeigt die Karte.
/// Nicht modal – das Hauptfenster rechnet (Planner/Builder); Schließen nimmt die Vorschau weg (Closed).
public partial class WildernessWindow : Window
{
    static readonly double[] Densities = { 4.0, 7.0, 10.0 };   // wenige / normal (wie das Original) / viele, je km² Land
    readonly bool ready;
    public Func<WildernessSpec, Task<string>> Planner { get; set; }
    public Func<Task<string>> Builder { get; set; }
    public Func<bool> HasPlan { get; set; }
    bool planned, busy;

    public WildernessWindow(WildernessSpec start)
    {
        InitializeComponent();
        Loc.Translate(this);
        int k = Array.FindIndex(Densities, d => Math.Abs(d - start.Density) < 0.01);
        AmountBox.SelectedIndex = k < 0 ? 1 : k;
        WidthMinBox.Text = start.PathWidthMin.ToString("0.#", Loc.Num);
        WidthBox.Text = start.PathWidth.ToString("0.#", Loc.Num);
        VergeBox.Text = start.Verge.ToString("0.#", Loc.Num);
        GradeBox.Text = (start.Grade * 100).ToString("0.#", Loc.Num);
        MaxGradeBox.Text = (start.MaxGrade * 100).ToString("0.#", Loc.Num);
        MaxPathBox.Text = start.MaxPath.ToString("0", Loc.Num);
        SeedBox.Text = start.Seed;
        ready = true;
        Check();
    }

    void Text_Changed(object sender, TextChangedEventArgs e) => Check();
    void Selection_Changed(object sender, SelectionChangedEventArgs e) => Check();
    void RandomSeed_Click(object sender, RoutedEventArgs e) => SeedBox.Text = new Random().Next(1000, 99999).ToString();
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    static bool Number(string text, out double v) =>
        double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, Loc.Num, out v);

    /// Eingaben lesen; null = Fehler (Text in ErrorText)
    WildernessSpec Read()
    {
        string error = null;
        var s = UserValues.Load(new WildernessSpec());   // Werte ohne Feld im Fenster aus default.ini/user.ini
        s.Density = Densities[Math.Clamp(AmountBox.SelectedIndex, 0, 2)]; s.Seed = SeedBox.Text.Trim();
        if (!Number(WidthBox.Text, out double wd) || wd < 3 || wd > 12) error = Loc.T("Gravel path width: 3 to 12 m.");
        else s.PathWidth = wd;
        if (!Number(WidthMinBox.Text, out double wmin) || wmin < 3 || wmin > 12) error ??= Loc.T("Gravel path width: 3 to 12 m.");
        else if (wmin > wd) error ??= Loc.T("The narrow path width must not be larger than the wide one.");
        else s.PathWidthMin = wmin;
        if (!Number(VergeBox.Text, out double vg) || vg < 0 || vg > 4) error ??= Loc.T("Verge: 0 to 4 m.");
        else s.Verge = vg;
        if (!Number(GradeBox.Text, out double g) || g < 2 || g > 20) error ??= Loc.T("Usual gradient: 2 to 20 %.");
        else s.Grade = g / 100;
        if (!Number(MaxGradeBox.Text, out double m) || m < 2 || m > 25) error ??= Loc.T("Gradient in tight passages: 2 to 25 %.");
        else if (m < g) error ??= Loc.T("The gradient in tight passages must not be smaller than the usual gradient.");
        else s.MaxGrade = m / 100;
        if (!Number(MaxPathBox.Text, out double lp) || lp < 100 || lp > 3000) error ??= Loc.T("Longest path: 100 to 3000 m.");
        else s.MaxPath = lp;
        ErrorText.Text = error ?? "";
        return error == null ? s : null;
    }

    void Check()
    {
        if (!ready) return;
        PlanButton.IsEnabled = Read() != null && !busy;
        BuildButton.IsEnabled = planned && !busy && Builder != null;
    }

    async void Plan_Click(object sender, RoutedEventArgs e)
    {
        var s = Read();
        if (s == null || Planner == null || busy) return;
        busy = true;
        Check();
        ResultText.Text = Loc.T("Planning …");
        planned = false;
        try { ResultText.Text = await Planner(s); planned = HasPlan?.Invoke() == true; }
        catch (Exception ex) { ErrorLog.Write(ex); ResultText.Text = Loc.T("Planning the wilderness POIs failed:") + "\n" + ex.Message; }
        finally { busy = false; Check(); }
    }

    async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (!planned || Builder == null || busy) return;
        busy = true;
        Check();
        string before = ResultText.Text;
        ResultText.Text = Loc.T("Building …");
        try
        {
            string text = await Builder();
            if (text != null) { ResultText.Text = text; planned = false; }   // gebaut: Plan ist verbraucht
            else ResultText.Text = before;
        }
        catch (Exception ex) { ErrorLog.Write(ex); ResultText.Text = Loc.T("Building the wilderness POIs failed:") + "\n" + ex.Message; }
        finally { busy = false; Check(); }
    }

    /// Fortschritt (kann aus dem Hintergrund kommen und nach dem Ergebnis eintreffen – dann nicht mehr anzeigen)
    public void ShowProgress(string text) => Dispatcher.BeginInvoke(() => { if (busy) ResultText.Text = text; });
}
