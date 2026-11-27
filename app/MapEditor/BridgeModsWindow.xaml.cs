using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MapEditor.Map;

namespace MapEditor;

/// Window "Bridge mods" (Settings › Mods): all kits with check boxes active / asphalt / gravel, the values of the chosen kit on the right.
/// Every change is saved at once (save()).
public partial class BridgeModsWindow : Window
{
    readonly List<BridgeKitDef> kits;
    readonly IReadOnlyDictionary<string, string> files;
    readonly Func<string, PrefabDef> describe;
    readonly Action save;
    BridgeKitDef current;

    public BridgeModsWindow(List<BridgeKitDef> kits, IReadOnlyDictionary<string, string> files, Func<string, PrefabDef> describe, Action save)
    {
        InitializeComponent();
        Loc.Translate(this);
        this.kits = kits; this.files = files; this.describe = describe; this.save = save;
        KitList.SelectionChanged += (_, _) => ShowKit((KitList.SelectedItem as ListBoxItem)?.Tag as BridgeKitDef);
        AddButton.Click += (_, _) => AddKit();
        RemoveButton.Click += (_, _) => RemoveKit();
        CloseButton.Click += (_, _) => Close();
        Fill(kits.FirstOrDefault());
    }

    static Brush Subtle => ThemeManager.Brush("SubtleTextBrush", Brushes.DimGray);

    void Fill(BridgeKitDef select)
    {
        KitList.Items.Clear();
        ListBoxItem sel = null;
        if (kits.Count == 0)
            KitList.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                Text = Loc.T("No bridge kit found. YAMEE_Bridges appears here when it is in the Mods folder of the game; other kits can be added by hand.") } });
        foreach (var k in kits)
        {
            var item = new ListBoxItem { Tag = k, Padding = new Thickness(4, 4, 4, 4), Content = Row(k) };
            KitList.Items.Add(item);
            if (k == select) sel = item;
        }
        KitList.SelectedItem = sel;
        if (sel == null) ShowKit(null);
        LoadImages();
    }

    /// state of a kit in one line (and the color)
    static (string Text, Brush Color) State(BridgeKitDef k) =>
        !k.Found ? (Loc.T("✖ pieces not found in the game folder"), ThemeManager.Brush("ErrorBrush", Brushes.Firebrick))
        : k.Problem != null ? ("✖ " + k.Problem, ThemeManager.Brush("ErrorBrush", Brushes.Firebrick))
        : k.Tested ? (Loc.T("✔ tested in the game"), ThemeManager.Brush("AccentBrush", Brushes.ForestGreen))
        : (Loc.T("⚠ analysed, not tested in the game yet"), ThemeManager.Brush("OrangeTextBrush", Brushes.DarkOrange));

    FrameworkElement Row(BridgeKitDef k)
    {
        var grid = new DockPanel();
        var active = new CheckBox { IsChecked = k.Active, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0), IsEnabled = k.Usable || k.Active,
            ToolTip = Loc.T("Active: YAMEE may build this bridge") };
        active.Click += (_, _) => { k.Active = active.IsChecked == true; Changed(false); };
        DockPanel.SetDock(active, Dock.Left);
        grid.Children.Add(active);
        var img = new Border { Width = 96, Height = 60, Background = ThemeManager.Brush("FieldBrush", Brushes.Gainsboro), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0), Tag = k };
        DockPanel.SetDock(img, Dock.Left);
        grid.Children.Add(img);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = k.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = k.Mod + "  ·  " + (k.Single ? k.End : k.End + " + " + k.Tile), FontSize = 12, Foreground = Subtle, TextWrapping = TextWrapping.Wrap });
        if (k.Usable) text.Children.Add(new TextBlock { Text = BridgePickWindow.SizeText(k), FontSize = 12, Foreground = Subtle });
        var (st, col) = State(k);
        text.Children.Add(new TextBlock { Text = st, FontSize = 12, Foreground = col, TextWrapping = TextWrapping.Wrap });
        var surf = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var asphalt = new CheckBox { Content = Loc.T("Asphalt"), IsChecked = k.Asphalt, Margin = new Thickness(0, 0, 14, 0), ToolTip = Loc.T("Use this bridge for asphalt roads") };
        var gravel = new CheckBox { Content = Loc.T("Gravel"), IsChecked = k.Gravel, ToolTip = Loc.T("Use this bridge for gravel roads and paths") };
        asphalt.Click += (_, _) => { k.Asphalt = asphalt.IsChecked == true; Changed(false); };
        gravel.Click += (_, _) => { k.Gravel = gravel.IsChecked == true; Changed(false); };
        surf.Children.Add(asphalt); surf.Children.Add(gravel);
        text.Children.Add(surf);
        grid.Children.Add(text);
        return grid;
    }

    void LoadImages() => BridgePickWindow.LoadImages(this, KitList.Items.OfType<ListBoxItem>()
        .Select(i => i.Content is DockPanel d && d.Children.Count > 1 && d.Children[1] is Border b && b.Tag is BridgeKitDef k ? (b, k) : default)
        .Where(x => x.b != null).ToList());

    void ShowKit(BridgeKitDef k)
    {
        current = k;
        Details.IsEnabled = k != null;
        RemoveButton.IsEnabled = k != null && k.Removable;
        if (k == null) { DetailTitle.Text = Loc.T("No kit chosen"); DetailPieces.Text = ""; return; }
        DetailTitle.Text = k.Label;
        DetailPieces.Text = k.Single ? Loc.F("One piece: {0}", k.End) : Loc.F("End piece: {0}\nMiddle piece: {1}", k.End, k.Tile);
    }

    /// save and show the changed row (rebuild = also the list)
    void Changed(bool rebuild = true)
    {
        save();
        if (!rebuild) return;
        var k = current;
        foreach (var item in KitList.Items.OfType<ListBoxItem>())
            if (item.Tag == k) { item.Content = Row(k); break; }
        LoadImages();
        ShowKit(k);
    }

    void AddKit()
    {
        var dlg = new BridgeAddWindow(files) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        string id = "user:" + dlg.End + "|" + (dlg.Tile ?? "");
        var have = kits.FirstOrDefault(k => k.Id == id || (k.End.Equals(dlg.End, StringComparison.OrdinalIgnoreCase) && string.Equals(k.Tile ?? "", dlg.Tile ?? "", StringComparison.OrdinalIgnoreCase)));
        if (have != null)
        {
            MessageBox.Show(this, Loc.F("This kit is already in the list ({0}).", have.Label), Loc.T("Bridge mods"), MessageBoxButton.OK, MessageBoxImage.Information);
            Fill(have);
            return;
        }
        var kit = new BridgeKitDef { Id = id, Label = dlg.Label, End = dlg.End, Tile = dlg.Tile, Mod = BridgeKitStore.ModOf(files.GetValueOrDefault(dlg.End)), Asphalt = true, Gravel = true };
        var a = BridgeAnalyzer.Analyze(kit, files.GetValueOrDefault(kit.End), kit.Single ? null : files.GetValueOrDefault(kit.Tile));
        kit.CopyValuesFrom(a);
        BridgeKitStore.Resolve(kit, files, describe);
        kit.Active = kit.Usable;
        kits.Add(kit);
        BridgeKits.RememberPieces(new[] { kit });
        save();
        Fill(kit);
        if (kit.Problem != null)
            MessageBox.Show(this, Loc.T("The kit was added, but it does not fit yet:") + "\n" + kit.Problem, Loc.T("Bridge mods"), MessageBoxButton.OK, MessageBoxImage.Warning);
        // the game copies the air of such a piece into the ground under the bridge (see BridgeAnalyzer.AirWipes)
        else if (BridgeAnalyzer.AirWipes(files.GetValueOrDefault(kit.End)) || (!kit.Single && BridgeAnalyzer.AirWipes(files.GetValueOrDefault(kit.Tile))))
            MessageBox.Show(this, Loc.T("Warning: the piece copies its air into the ground (CopyAirBlocks True or YOffset below −1) – the terrain under the bridge goes missing in the game. Our pieces use YOffset −1 and CopyAirBlocks False."),
                Loc.T("Bridge mods"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void RemoveKit()
    {
        var k = current;
        if (k == null || !k.Removable) return;
        if (MessageBox.Show(this, Loc.F("Remove „{0}“ from the list? The mod itself stays in the game folder.", k.Label), Loc.T("Bridge mods"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        kits.Remove(k);
        BridgeKitStore.MarkRemoved(k);
        save();
        Fill(kits.FirstOrDefault());
    }
}
