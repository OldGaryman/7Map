using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MapEditor.Map;

namespace MapEditor;

/// Window "Create world": enter values, look at the preview; "Create world" returns the settings (Spec),
/// the main window then writes the world into the project folder and opens it.
public partial class GeneratorWindow : Window
{
    public GeneratorSpec Spec { get; private set; }
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly TextBox[] shares;
    readonly Func<string, bool> nameTaken, gameTaken;
    readonly string gameFolder;   // for the height stamps of the game (null = noise only)
    int version, pending;   // pending = running calculations (hint "calculating" visible while one is running)
    bool ready, nameEdited;
    WorldGenerator last;

    public GeneratorWindow(GeneratorSpec start, Func<string, bool> nameTaken, Func<string, bool> gameTaken, string gameFolder)
    {
        InitializeComponent();
        Loc.Translate(this);
        this.nameTaken = nameTaken;
        this.gameTaken = gameTaken;
        this.gameFolder = gameFolder;
        shares = new[] { Share0, Share1, Share2, Share3, Share4 };
        start ??= new GeneratorSpec();
        if (string.IsNullOrWhiteSpace(start.Seed)) start.Seed = RandomSeed();
        SeedBox.Text = start.Seed;
        NameBox.Text = start.Name == new GeneratorSpec().Name ? SuggestName(start.Seed) : start.Name;
        foreach (ComboBoxItem item in SizeBox.Items) if ((string)item.Tag == start.Size.ToString()) SizeBox.SelectedItem = item;
        if (SizeBox.SelectedItem == null) SizeBox.SelectedIndex = 3;
        for (int b = 0; b < 5; b++) shares[b].Text = start.Shares[b].ToString();
        MountainsSlider.Value = start.Mountains * 100;
        HillsSlider.Value = start.Hills * 100;
        ValleysSlider.Value = start.Valleys * 100;
        ErosionSlider.Value = start.Erosion * 100;
        RoughSlider.Value = start.Rough * 100;
        LakesBox.SelectedIndex = Math.Clamp(start.Lakes, 0, 3);
        AnomaliesBox.SelectedIndex = Math.Clamp(start.Anomalies, 0, 3);
        spawnCount = start.SpawnCount;   // not adjustable in the window (default.ini / user.ini)
        timer.Tick += (_, _) => { timer.Stop(); _ = UpdatePreview(); };
        ready = true;
        UpdateTexts();
        Loaded += (_, _) => _ = UpdatePreview();   // only with a window: then the continuations run on the UI thread
    }

    readonly int spawnCount;

    static string RandomSeed()
    {
        const string letters = "bcdfghklmnprstvwz", vowels = "aeiou";
        var r = new Random();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 3; i++) { sb.Append(letters[r.Next(letters.Length)]); sb.Append(vowels[r.Next(vowels.Length)]); }
        sb[0] = char.ToUpperInvariant(sb[0]);
        return sb.ToString();
    }

    static string SuggestName(string seed) => Loc.F("{0} Land", string.IsNullOrWhiteSpace(seed) ? "New" : seed.Trim());

    void RandomSeed_Click(object sender, RoutedEventArgs e) => SeedBox.Text = RandomSeed();

    void Name_Changed(object sender, TextChangedEventArgs e) { if (ready && NameBox.IsKeyboardFocusWithin) nameEdited = true; Check(); }

    void Value_Changed(object sender, EventArgs e)
    {
        if (!ready) return;
        // name follows the start value as long as the user has not changed it themselves
        if (sender == SeedBox && !nameEdited) NameBox.Text = SuggestName(SeedBox.Text);
        UpdateTexts();
        timer.Stop();
        timer.Start();
    }

    void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Value_Changed(sender, e);
    void Text_Changed(object sender, TextChangedEventArgs e) => Value_Changed(sender, e);
    void Selection_Changed(object sender, SelectionChangedEventArgs e) => Value_Changed(sender, e);

    void PreviewMode_Click(object sender, RoutedEventArgs e) => _ = UpdatePreview(onlyRedraw: true);

    void UpdateTexts()
    {
        MountainsText.Text = $"{MountainsSlider.Value:0} %";
        HillsText.Text = $"{HillsSlider.Value:0} %";
        ValleysText.Text = $"{ValleysSlider.Value:0} %";
        ErosionText.Text = $"{ErosionSlider.Value:0} %";
        RoughText.Text = $"{RoughSlider.Value:0} %";
        int sum = 0;
        foreach (var t in shares) if (int.TryParse(t.Text, out int v)) sum += v;
        ShareSum.Text = Loc.F("Sum: {0} %", sum) + (sum != 100 ? " " + Loc.T("(is scaled to 100 %)") : "");
        Check();
    }

    /// read the input; null = error (text in ErrorText)
    GeneratorSpec Read(bool forCreate)
    {
        string error = null;
        var s = new GeneratorSpec
        {
            Name = NameBox.Text.Trim(),
            Seed = SeedBox.Text.Trim(),
            Size = int.Parse((string)((ComboBoxItem)SizeBox.SelectedItem).Tag),
            Mountains = MountainsSlider.Value / 100,
            Hills = HillsSlider.Value / 100,
            Valleys = ValleysSlider.Value / 100,
            Erosion = ErosionSlider.Value / 100,
            GameFolder = gameFolder,
            Rough = RoughSlider.Value / 100,
            Lakes = LakesBox.SelectedIndex,
            Anomalies = AnomaliesBox.SelectedIndex,
        };
        for (int b = 0; b < 5; b++)
        {
            if (!int.TryParse(shares[b].Text, out int v) || v < 0 || v > 100) { error = Loc.T("Biome shares: whole numbers from 0 to 100."); v = 0; }
            s.Shares[b] = v;
        }
        if (s.Shares.Sum() == 0) error = Loc.T("At least one biome needs a share above 0 %.");
        s.SpawnCount = spawnCount;
        if (forCreate)
        {
            if (s.Name.Length == 0) error = Loc.T("Please enter a name for the world.");
            else if (s.Name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || s.Name.EndsWith('.'))
                error = Loc.T("The name must not contain any of these characters: \\ / : * ? \" < > |");
            else if (gameTaken(s.Name))
                error = Loc.F("The game already has a world \"{0}\". Please choose another name (otherwise it would be overwritten).", s.Name);
        }
        ErrorText.Text = error ?? "";
        return error == null ? s : null;
    }

    void Check()
    {
        if (!ready) return;
        var s = Read(true);
        CreateButton.IsEnabled = s != null;
        if (s != null && nameTaken(s.Name)) ErrorText.Text = Loc.F("A project \"{0}\" already exists – it is kept and you will be asked.", s.Name);
    }

    async Task UpdatePreview(bool onlyRedraw = false)
    {
        bool heights = ShowHeights.IsChecked == true;
        if (onlyRedraw && last != null)
        {
            Show(last, heights);
            return;
        }
        var s = Read(false);
        if (s == null) return;
        int my = ++version;
        pending++;
        PreviewBusy.Visibility = Visibility.Visible;
        try
        {
            var g = await Task.Run(() =>
            {
                var gen = new WorldGenerator(s);
                gen.BuildCoarse(s.Size / 420.0);
                return gen;
            });
            if (my != version) return;   // meanwhile a new input
            last = g;
            Show(g, ShowHeights.IsChecked == true);
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { if (--pending == 0) PreviewBusy.Visibility = Visibility.Collapsed; }
    }

    void Show(WorldGenerator g, bool heights)
    {
        var px = g.RenderPreview(out int n, heights);
        PreviewImage.Source = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px, n * 4);
        var (min, max) = g.HeightRange();
        PreviewInfo.Text = Loc.F("Ground {0:0}–{1:0} m · {2} lake(s)", min, max, g.LakeList.Count) + " · " +
            (g.StampCount > 0 ? Loc.F("{0} height stamps from the game", g.StampCount) : Loc.T("game not found: without height stamps")) +
            (g.Anomalies.Count > 0 ? " · " + Loc.F("{0} unique formation(s)", g.Anomalies.Count) : "");
    }

    void Create_Click(object sender, RoutedEventArgs e)
    {
        var s = Read(true);
        if (s == null) return;
        Spec = s;
        DialogResult = true;
    }
}
