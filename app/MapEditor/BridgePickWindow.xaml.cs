using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MapEditor.Map;

namespace MapEditor;

/// Window "Bridges" beside the map while the bridge tool is on: the active kits with picture, size and spans (like the window of the
/// buildings, only bridges). Chosen(id) = a kit was chosen (null = randomly one that fits).
public partial class BridgePickWindow : Window
{
    public event Action<string> Chosen;
    public event Action ModsRequested;
    /// swap mode: a kit was chosen for the bridge (id)
    public event Action<string> SwapChosen;
    /// swap mode ended with "Done"
    public event Action SwapDone;
    bool filling;
    /// the window shows the kits for one bridge to swap it (FillSwap) instead of the choice for new bridges (Fill)
    public bool SwapMode { get; private set; }

    public BridgePickWindow()
    {
        InitializeComponent();
        Loc.Translate(this);
        KitList.SelectionChanged += (_, _) =>
        {
            if (filling || KitList.SelectedItem is not ListBoxItem item) return;
            if (SwapMode) SwapChosen?.Invoke(item.Tag as string);
            else Chosen?.Invoke(item.Tag as string);
        };
        ModsButton.Click += (_, _) => ModsRequested?.Invoke();
        DoneButton.Click += (_, _) => SwapDone?.Invoke();
        IsVisibleChanged += (_, _) => { if (IsVisible) ScrollToSelected(); };
        KitList.LayoutUpdated += (_, _) => TryScroll();
        Loaded += (_, _) =>
        {
            // beside the right edge of the main window
            if (Owner == null) return;
            Left = Math.Max(0, Owner.Left + Owner.ActualWidth - Width - 330);
            Top = Owner.Top + 190;
        };
    }

    /// swap mode: every kit for the surface with the note whether it fits this place; the current one is selected
    public void FillSwap(string header, string intro, IReadOnlyList<BridgeOption> options)
    {
        filling = true;
        try
        {
            SwapMode = true;
            HeaderText.Text = header;
            IntroText.Text = intro;
            DoneButton.Visibility = Visibility.Visible;
            KitList.Items.Clear();
            if (options.Count == 0)
            {
                KitList.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.T("No bridge kit is active. Click Bridge mods … below.") } });
                return;
            }
            ListBoxItem sel = null;
            foreach (var o in options)
            {
                var row = Row(o.Kit);
                if (row is DockPanel d && d.Children[1] is StackPanel text)
                {
                    // green = fits, orange = fits when the river is narrowed (asks first), grey = not possible here
                    bool reshape = !o.Current && o.Site.Problem == null && o.Site.NarrowFrom > 0;
                    string note = o.Current ? Loc.T("This bridge is here now.")
                        : o.Site.Problem != null ? o.Site.Problem
                        : reshape ? Loc.F("River is adapted: narrowed from {0:0} m to {1} m.", o.Site.NarrowFrom, o.Site.WaterWidth)
                        : Loc.F("Fits: {0} m long, {1} middle piece(s).", o.Site.Length, o.Site.Tiles);
                    text.Children.Add(new TextBlock
                    {
                        Text = note, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                        Foreground = o.Current || o.Site.Problem == null && !reshape ? ThemeManager.Brush("AccentBrush", Brushes.ForestGreen)
                            : reshape ? ThemeManager.Brush("WarnBrush", Brushes.DarkOrange) : Subtle,
                    });
                }
                var item = new ListBoxItem { Tag = o.Kit.Id, Padding = new Thickness(4, 3, 4, 3), Content = row, IsEnabled = o.Current || o.Site.Problem == null };
                KitList.Items.Add(item);
                if (o.Current) sel = item;
            }
            KitList.SelectedItem = sel;
            LoadImages();
            ScrollToSelected();
        }
        finally { filling = false; }
    }

    public void Fill(IReadOnlyList<BridgeKitDef> kits, string chosen, bool gravel)
    {
        filling = true;
        try
        {
            SwapMode = false;
            IntroText.Text = Loc.T("Choose a bridge, then click onto a straight piece of a river. Randomly = per place one of the kits that fits the river.");
            DoneButton.Visibility = Visibility.Collapsed;
            HeaderText.Text = gravel ? Loc.T("Bridges for gravel roads") : Loc.T("Bridges for asphalt roads");
            KitList.Items.Clear();
            if (kits.Count == 0)
            {
                KitList.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.T("No bridge kit is active. Click Bridge mods … below.") } });
                return;
            }
            var random = new ListBoxItem { Tag = null, Padding = new Thickness(4, 6, 4, 6), Content = RandomRow(kits.Count) };
            KitList.Items.Add(random);
            ListBoxItem sel = random;
            foreach (var k in kits)
            {
                var item = new ListBoxItem { Tag = k.Id, Padding = new Thickness(4, 3, 4, 3), Content = Row(k) };
                KitList.Items.Add(item);
                if (k.Id == chosen) sel = item;
            }
            KitList.SelectedItem = sel;
            LoadImages();
            ScrollToSelected();
        }
        finally { filling = false; }
    }

    /// show the chosen (swap mode: current) bridge in the list at once (the user's request 2026-11-26), in the middle of the list where
    /// possible. Waits until the window is visible and the list has its size (a window that is just opening has none yet – the first
    /// version scrolled only when the window was open already); the list scrolls in pixels (CanContentScroll off in the XAML)
    bool scrollPending;

    void ScrollToSelected()
    {
        scrollPending = KitList.SelectedItem != null;
        if (scrollPending) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, TryScroll);
    }

    void TryScroll()
    {
        if (!scrollPending) return;
        if (KitList.SelectedItem is not ListBoxItem item) { scrollPending = false; return; }
        if (!IsVisible || KitList.ActualHeight <= 0 || item.ActualHeight <= 0 || FindScroll(KitList) is not ScrollViewer sv || sv.ViewportHeight <= 0) return;
        if (!item.IsDescendantOf(sv)) return;
        scrollPending = false;
        var top = item.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
        sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + top - (sv.ViewportHeight - item.ActualHeight) / 2));
    }

    static ScrollViewer FindScroll(DependencyObject d)
    {
        if (d is ScrollViewer s) return s;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScroll(VisualTreeHelper.GetChild(d, i)) is ScrollViewer found) return found;
        return null;
    }

    static Brush Subtle =>ThemeManager.Brush("SubtleTextBrush", Brushes.DimGray);

    static FrameworkElement RandomRow(int n)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = Loc.T("Randomly"), FontWeight = FontWeights.SemiBold });
        p.Children.Add(new TextBlock { Text = Loc.F("one of the {0} kits, the first that fits the river", n), FontSize = 12, Foreground = Subtle, TextWrapping = TextWrapping.Wrap });
        return p;
    }

    internal static FrameworkElement Row(BridgeKitDef k)
    {
        var grid = new DockPanel();
        var img = new Border { Width = 96, Height = 60, Background = ThemeManager.Brush("FieldBrush", Brushes.Gainsboro), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0), Tag = k };
        DockPanel.SetDock(img, Dock.Left);
        grid.Children.Add(img);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = k.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = k.Mod, FontSize = 12, Foreground = Subtle });
        text.Children.Add(new TextBlock { Text = SizeText(k), FontSize = 12, Foreground = Subtle, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);
        return grid;
    }

    internal static string SizeText(BridgeKitDef k) =>
        (k.RoadWidth < k.Width ? Loc.F("{0} m wide, road {1} m", k.Width, k.RoadWidth) : Loc.F("{0} m wide", k.Width)) + "  ·  " + (k.Single ? Loc.F("water up to {0} m", k.MinWater) : Loc.F("water {0}–{1} m", k.MinWater, k.MaxWater));

    void LoadImages() => LoadImages(this, KitList.Items.OfType<ListBoxItem>()
        .Select(i => i.Content is DockPanel d && d.Children[0] is Border b && b.Tag is BridgeKitDef k ? (b, k) : default)
        .Where(x => x.b != null).ToList());

    /// preview pictures read in the background, set on the thread of the window (the borders belong to it)
    internal static void LoadImages(Window w, List<(Border b, BridgeKitDef k)> targets)
    {
        var disp = w.Dispatcher;
        Task.Run(() =>
        {
            foreach (var (b, k) in targets)
            {
                var bmp = PreviewImage(k);
                if (bmp != null) disp.BeginInvoke(() => { b.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill }; });
            }
        });
    }

    /// preview image of the end piece (.jpg next to its .xml), otherwise of the middle piece
    internal static BitmapImage PreviewImage(BridgeKitDef k)
    {
        foreach (var file in new[] { k.EndImage, k.TileImage })
        {
            if (file == null || !File.Exists(file)) continue;
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(file);
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.DecodePixelWidth = 120;
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch (Exception) { }
        }
        return null;
    }
}
