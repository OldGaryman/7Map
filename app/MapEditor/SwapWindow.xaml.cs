using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MapEditor;

/// A building that fits the lot (name, display name, tier, preview image next to the XML)
public sealed record SwapCandidate(string Name, string Label, int Tier, int SizeX, int SizeZ, string ImageFile);

/// Window "Swap building": list of the fitting buildings with preview image; Chosen = chosen name (null = cancelled)
public partial class SwapWindow : Window
{
    public string Chosen { get; private set; }
    readonly List<(SwapCandidate C, ListBoxItem Item)> rows = new();

    /// building = build on a free lot instead of swapping (different window title and button)
    public SwapWindow(string header, string rule, IReadOnlyList<SwapCandidate> candidates, bool building = false)
    {
        InitializeComponent();
        Loc.Translate(this);
        if (building)
        {
            Title = Loc.T("Build on lot");
            SwapButton.Content = Loc.T("Build");
        }
        HeaderText.Text = header;
        RuleText.Text = rule;
        foreach (var c in candidates)
        {
            var item = new ListBoxItem { Content = Row(c), Tag = c, Padding = new Thickness(4, 3, 4, 3) };
            item.MouseDoubleClick += (_, _) => Choose(c);
            CandidateList.Items.Add(item);
            rows.Add((c, item));
        }
        CandidateList.SelectionChanged += (_, _) => SwapButton.IsEnabled = CandidateList.SelectedItem != null;
        SwapButton.Click += (_, _) => { if ((CandidateList.SelectedItem as ListBoxItem)?.Tag is SwapCandidate c) Choose(c); };
        FilterBox.TextChanged += (_, _) => Filter();
        Loaded += (_, _) => { FilterBox.Focus(); LoadImages(); };
    }

    void Choose(SwapCandidate c)
    {
        Chosen = c.Name;
        DialogResult = true;
    }

    // search in display name and internal name
    void Filter()
    {
        string f = FilterBox.Text.Trim();
        foreach (var (c, item) in rows)
            item.Visibility = f == "" || c.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || (c.Label ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    static FrameworkElement Row(SwapCandidate c)
    {
        var grid = new DockPanel();
        var img = new Border { Width = 96, Height = 60, Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xE1)), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(img, Dock.Left);
        grid.Children.Add(img);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(c.Label) ? c.Name : c.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = c.Name, FontSize = 12, Foreground = ThemeManager.Brush("SubtleTextBrush", Brushes.DimGray) });
        string tier = c.Tier > 0 ? Loc.F("Tier {0}", c.Tier) + "  ·  " : "";
        text.Children.Add(new TextBlock { Text = tier + Loc.F("{0} × {1} m", c.SizeX, c.SizeZ), FontSize = 12, Foreground = ThemeManager.Brush("SubtleTextBrush", Brushes.DimGray) });
        grid.Children.Add(text);
        return grid;
    }

    // load the preview images in the background (decoded small) so that the window appears at once
    async void LoadImages()
    {
        foreach (var (c, item) in rows)
        {
            if (c.ImageFile == null) continue;
            var bmp = await Task.Run(() =>
            {
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.UriSource = new Uri(c.ImageFile);
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.DecodePixelWidth = 120;
                    b.EndInit();
                    b.Freeze();
                    return b;
                }
                catch (Exception) { return null; }
            });
            if (bmp == null || !IsLoaded) continue;
            if (((DockPanel)item.Content).Children[0] is Border border)
                border.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
        }
    }
}
