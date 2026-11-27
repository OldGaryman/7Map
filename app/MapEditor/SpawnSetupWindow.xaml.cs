using System.Windows;

namespace MapEditor;

/// Window "Create spawn points": seed and number (menu rebuild 2026-11-20). The number is remembered like all tool values.
public partial class SpawnSetupWindow : Window
{
    public const int MinCount = 1, MaxCount = 100;
    public string Seed { get; private set; }
    public int Count { get; private set; }
    readonly int defaultCount;

    public SpawnSetupWindow(string seed, int count, int defaultCount)
    {
        InitializeComponent();
        Loc.Translate(this);
        this.defaultCount = defaultCount;
        SeedBox.Text = seed;
        CountBox.Text = count.ToString();
    }

    static string RandomSeed()
    {
        const string letters = "bcdfghklmnprstvwz", vowels = "aeiou";
        var r = new Random();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 3; i++) { sb.Append(letters[r.Next(letters.Length)]); sb.Append(vowels[r.Next(vowels.Length)]); }
        sb[0] = char.ToUpperInvariant(sb[0]);
        return sb.ToString();
    }

    void RandomSeed_Click(object sender, RoutedEventArgs e) => SeedBox.Text = RandomSeed();

    void ResetCount_Click(object sender, RoutedEventArgs e) => CountBox.Text = defaultCount.ToString();

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CountBox.Text.Trim(), out int n) || n < MinCount || n > MaxCount)
        {
            ErrorText.Text = Loc.F("The number must be a whole number from {0} to {1}.", MinCount, MaxCount);
            return;
        }
        Seed = SeedBox.Text.Trim();
        Count = n;
        DialogResult = true;
    }
}
