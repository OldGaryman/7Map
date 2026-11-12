using System.Windows;
using System.Windows.Controls;
using MapEditor.Map;

namespace MapEditor;

/// Window "Create cities": number and size per town type, height steps, ramps; returns the settings (Spec)
public partial class CityWindow : Window
{
    public CitySpec Spec { get; private set; }
    readonly List<(TownType Type, TextBox Count, TextBox Min, TextBox Max)> rows = new();
    readonly bool ready;

    public CityWindow(CitySpec start)
    {
        InitializeComponent();
        Loc.Translate(this);
        foreach (var t in start.Types)
        {
            var row = new DockPanel { Style = (Style)FindResource("FormRow") };
            var label = new TextBlock { Style = (Style)FindResource("FormLabelWide"), Text = t.Label, ToolTip = Loc.F("Game name: {0}", t.Name) };
            var count = new TextBox { Style = (Style)FindResource("FormField"), Text = t.Count.ToString() };
            var min = new TextBox { Style = (Style)FindResource("FormField"), Text = t.TilesMin.ToString(), Margin = new Thickness(12, 0, 0, 0) };
            var dash = new TextBlock { Style = (Style)FindResource("FormUnit"), Text = "–" };
            var max = new TextBox { Style = (Style)FindResource("FormFieldNext"), Text = t.TilesMax.ToString() };
            var biomes = new TextBlock { Style = (Style)FindResource("FormInfo"), Text = string.Join(", ", t.Biomes.Select(b => Loc.T(BiomePalette.Names[b]))) };
            foreach (var box in new[] { count, min, max }) box.TextChanged += Text_Changed;
            row.Children.Add(label); row.Children.Add(count); row.Children.Add(min); row.Children.Add(dash); row.Children.Add(max); row.Children.Add(biomes);
            TypeRows.Children.Add(row);
            rows.Add((t, count, min, max));
        }
        PartsBox.SelectedIndex = Math.Clamp(start.MaxParts, 0, 3);
        ElevationBox.SelectedIndex = Math.Clamp(start.Elevation, 0, 2);
        DeltaMinBox.Text = start.DeltaMin.ToString();
        DeltaMaxBox.Text = start.DeltaMax.ToString();
        GradeBox.Text = (start.RoadGrade * 100).ToString("0.#", Loc.Num);
        TradersBox.Text = start.TradersPerBiome.ToString();
        FillBox.IsChecked = start.FillLots;
        SeedBox.Text = start.Seed;
        ready = true;
        Check();
    }

    void Text_Changed(object sender, TextChangedEventArgs e) => Check();
    void Selection_Changed(object sender, SelectionChangedEventArgs e) => Check();
    void RandomSeed_Click(object sender, RoutedEventArgs e) => SeedBox.Text = new Random().Next(1000, 99999).ToString();

    /// read the input; null = error (text in ErrorText)
    CitySpec Read()
    {
        string error = null;
        var s = UserValues.Load(new CitySpec());   // values without a field in the window (e.g. road gradient) from default.ini/user.ini
        s.MaxParts = PartsBox.SelectedIndex; s.Elevation = Math.Max(0, ElevationBox.SelectedIndex); s.FillLots = FillBox.IsChecked == true; s.Seed = SeedBox.Text.Trim();
        foreach (var (type, count, min, max) in rows)
        {
            var t = type.Clone();
            if (!int.TryParse(count.Text, out t.Count) || t.Count < 0 || t.Count > 50) error = Loc.T("Number of towns: 0 to 50.");
            if (!int.TryParse(min.Text, out t.TilesMin) || !int.TryParse(max.Text, out t.TilesMax) || t.TilesMin < 1 || t.TilesMax < t.TilesMin || t.TilesMax > 80)
                error = Loc.T("Tiles: whole numbers from 1 to 80, \"from\" not larger than \"to\".");
            s.Types.Add(t);
        }
        if (!int.TryParse(DeltaMinBox.Text, out s.DeltaMin) || !int.TryParse(DeltaMaxBox.Text, out s.DeltaMax) || s.DeltaMin < 0 || s.DeltaMax < s.DeltaMin || s.DeltaMax > 60)
            error = Loc.T("Height difference: whole metres from 0 to 60, \"from\" not larger than \"to\".");
        if (!double.TryParse(GradeBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, Loc.Num, out double g) || g < 3 || g > 25) error = Loc.T("Road gradient: 3 to 25 %.");
        else s.RoadGrade = g / 100;
        if (!int.TryParse(TradersBox.Text, out s.TradersPerBiome) || s.TradersPerBiome < 0 || s.TradersPerBiome > 10) error = Loc.T("Traders per biome: 0 to 10.");
        if (s.Types.All(t => t.Count == 0)) error ??= Loc.T("At least one town is needed.");
        ErrorText.Text = error ?? "";
        return error == null ? s : null;
    }

    void Check()
    {
        if (!ready) return;
        var s = Read();
        CreateButton.IsEnabled = s != null;
        if (s != null) TradersText.Text = Loc.F("up to {0} traders in all", s.TradersPerBiome * 5);
    }

    void Create_Click(object sender, RoutedEventArgs e)
    {
        var s = Read();
        if (s == null) return;
        Spec = s;
        DialogResult = true;
    }
}
