using System.Windows;
using System.Windows.Controls;
using MapEditor.Map;

namespace MapEditor;

/// Window "Add bridge kit": end piece and middle piece (or none = one piece) from all prefabs of the game and its mods
public partial class BridgeAddWindow : Window
{
    public string End { get; private set; }
    public string Tile { get; private set; }
    public string Label { get; private set; }

    readonly List<(string Name, string Mod)> all;
    const string NoTile = "\u0001";
    bool labelTouched, settingLabel;

    public BridgeAddWindow(IReadOnlyDictionary<string, string> files)
    {
        InitializeComponent();
        Loc.Translate(this);
        all = files.Select(f => (f.Key, BridgeKitStore.ModOf(f.Value))).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();
        FilterBox.Text = "bridge";
        FilterBox.TextChanged += (_, _) => Fill();
        EndList.SelectionChanged += (_, _) => Update();
        TileList.SelectionChanged += (_, _) => Update();
        LabelBox.TextChanged += (_, _) => { if (!settingLabel) labelTouched = true; };
        OkButton.Click += (_, _) =>
        {
            Label = LabelBox.Text.Trim();
            if (Label == "") Label = End;
            DialogResult = true;
        };
        Fill();
        Loaded += (_, _) => FilterBox.Focus();
    }

    void Fill()
    {
        string f = FilterBox.Text.Trim();
        string end = End, tile = (TileList.SelectedItem as ListBoxItem)?.Tag as string;
        var shown = all.Where(p => f == "" || p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Mod.Contains(f, StringComparison.OrdinalIgnoreCase)).Take(800).ToList();
        EndList.Items.Clear(); TileList.Items.Clear();
        TileList.Items.Add(new ListBoxItem { Tag = NoTile, Content = Loc.T("(none – bridge of one piece)"), FontStyle = FontStyles.Italic });
        foreach (var (name, mod) in shown)
        {
            EndList.Items.Add(Item(name, mod));
            TileList.Items.Add(Item(name, mod));
        }
        EndList.SelectedItem = EndList.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == end);
        TileList.SelectedItem = TileList.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == tile);
    }

    static ListBoxItem Item(string name, string mod)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = name });
        p.Children.Add(new TextBlock { Text = mod, FontSize = 11.5, Foreground = ThemeManager.Brush("SubtleTextBrush", System.Windows.Media.Brushes.DimGray) });
        return new ListBoxItem { Tag = name, Content = p, Padding = new Thickness(4, 2, 4, 2) };
    }

    void Update()
    {
        End = (EndList.SelectedItem as ListBoxItem)?.Tag as string ?? End;
        var t = (TileList.SelectedItem as ListBoxItem)?.Tag as string;
        Tile = t == NoTile ? null : t;
        OkButton.IsEnabled = End != null && t != null && Tile != End;
        if (!labelTouched && End != null)
        {
            settingLabel = true;
            string mod = all.FirstOrDefault(p => p.Name == End).Mod;
            LabelBox.Text = (string.IsNullOrEmpty(mod) ? "" : mod + ": ") + End;
            settingLabel = false;
        }
    }
}
