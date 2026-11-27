using System.Windows;
using System.Windows.Controls;
using MapEditor.Map;

namespace MapEditor;

/// Window "Create cities": number and size per town type, height steps, ramps; returns the settings (Spec)
public partial class CityWindow : Window
{
    public CitySpec Spec { get; private set; }
    readonly List<(TownType Type, TextBox Count, TextBox Min, TextBox Max)> rows = new();
    readonly Func<CitySpec> loadDefaults;
    bool ready, wasReset;

    public CityWindow(CitySpec start, string sizeClass = null, Func<CitySpec> loadDefaults = null)
    {
        InitializeComponent();
        Loc.Translate(this);
        this.loadDefaults = loadDefaults;
        if (loadDefaults == null) ResetButton.Visibility = Visibility.Collapsed;
        if (sizeClass != null) TownsTitle.Text = Loc.F("Towns per biome (defaults for {0} maps)", sizeClass);
        // one block per biome: small heading with the biome colour, then the town sizes (the user's request 2026-11-15)
        foreach (var group in start.Types.GroupBy(t => t.Biomes.Length == 1 ? t.Biomes[0] : -1).OrderBy(g => g.Key))
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 28, 6) };
            var head = new DockPanel { Style = (Style)FindResource("FormRow") };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Width = (double)FindResource("FormLabelWidth"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(title, Dock.Left);
            if (group.Key >= 0)
            {
                var c = BiomePalette.Display[group.Key];
                title.Children.Add(new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                    BorderBrush = System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(1),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)c[0], (byte)c[1], (byte)c[2])),
                });
            }
            title.Children.Add(new TextBlock { Text = group.Key >= 0 ? Loc.T(BiomePalette.Names[group.Key]) : Loc.T("All biomes"), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(title);
            head.Children.Add(new TextBlock { Style = (Style)FindResource("FormHeader"), Text = Loc.T("Number") });
            head.Children.Add(new TextBlock { Style = (Style)FindResource("FormHeader"), Margin = new Thickness(12, 0, 0, 0), TextAlignment = TextAlignment.Left, Width = double.NaN, Text = Loc.T("Tiles from – to") });
            block.Children.Add(head);
            foreach (var t in group)
            {
                var row = new DockPanel { Style = (Style)FindResource("FormRow") };
                var label = new TextBlock { Style = (Style)FindResource("FormLabel"), Text = t.Size != null ? t.SizeLabel : t.Label, ToolTip = Loc.F("Game name: {0}", t.Name) };
                var count = new TextBox { Style = (Style)FindResource("FormField") };
                var min = new TextBox { Style = (Style)FindResource("FormField"), Margin = new Thickness(12, 0, 0, 0) };
                var dash = new TextBlock { Style = (Style)FindResource("FormUnit"), Text = "–" };
                var max = new TextBox { Style = (Style)FindResource("FormFieldNext") };
                foreach (var box in new[] { count, min, max }) box.TextChanged += Text_Changed;
                row.Children.Add(label); row.Children.Add(count); row.Children.Add(min); row.Children.Add(dash); row.Children.Add(max);
                block.Children.Add(row);
                rows.Add((t, count, min, max));
            }
            // two columns: pine | burnt, desert | snow, wasteland
            int index = TypeRows.Children.Count;
            if (index % 2 == 0) TypeRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(block, index / 2);
            Grid.SetColumn(block, index % 2);
            TypeRows.Children.Add(block);
        }
        ApplyValues(start);
    }

    static string RowKey(TownType t) => (t.Biomes.Length == 1 ? t.Biomes[0] : -1) + ":" + (t.Size ?? t.Name);

    void ApplyValues(CitySpec s)
    {
        ready = false;
        var byKey = s.Types.ToDictionary(RowKey, StringComparer.OrdinalIgnoreCase);
        foreach (var (type, count, min, max) in rows)
        {
            var t = byKey.TryGetValue(RowKey(type), out var match) ? match : type;
            count.Text = t.Count.ToString();
            min.Text = t.TilesMin.ToString();
            max.Text = t.TilesMax.ToString();
        }
        PartsBox.SelectedIndex = Math.Clamp(s.MaxParts, 0, 3);
        ElevationBox.SelectedIndex = Math.Clamp(s.Elevation, 0, 2);
        DeltaMinBox.Text = s.DeltaMin.ToString();
        DeltaMaxBox.Text = s.DeltaMax.ToString();
        GradeBox.Text = (s.RoadGrade * 100).ToString("0.#", Loc.Num);
        TradersBox.Text = s.TradersPerBiome.ToString();
        JunctionBox.Text = Math.Round(s.JunctionGateways * 100).ToString(Loc.Num);
        EarthworkBox.Text = Math.Round(s.Earthwork * 100).ToString(Loc.Num);
        FillBox.IsChecked = s.FillLots;
        RiverBox.IsChecked = s.RiverTowns;
        SeedBox.Text = s.Seed;
        ready = true;
        Check();
    }

    void Text_Changed(object sender, TextChangedEventArgs e) => Check();
    void Selection_Changed(object sender, SelectionChangedEventArgs e) => Check();
    void RandomSeed_Click(object sender, RoutedEventArgs e) => SeedBox.Text = new Random().Next(1000, 99999).ToString();

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (loadDefaults == null) return;
        wasReset = true;
        ApplyValues(loadDefaults());
    }

    /// read the input; null = error (text in ErrorText)
    CitySpec Read()
    {
        string error = null;
        var s = wasReset ? UserValues.LoadDefaults(new CitySpec()) : UserValues.Load(new CitySpec());   // values without a field in the window (e.g. EdgeSoft) from default.ini (or user.ini)
        s.MaxParts = PartsBox.SelectedIndex; s.Elevation = Math.Max(0, ElevationBox.SelectedIndex); s.FillLots = FillBox.IsChecked == true; s.RiverTowns = RiverBox.IsChecked == true; s.Seed = SeedBox.Text.Trim();
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
        if (!int.TryParse(JunctionBox.Text, out int jg) || jg < 0 || jg > 100) error = Loc.T("T and crossing gateways: 0 to 100 %.");
        else s.JunctionGateways = jg / 100.0;
        if (!int.TryParse(EarthworkBox.Text, out int ewp) || ewp < 50 || ewp > 300) error = Loc.T("Earthwork for towns: 50 to 300 %.");
        else s.Earthwork = ewp / 100.0;
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
