using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MapEditor.Map;

namespace MapEditor;

/// Project check (the user's request 2026-11-13): lists what of the project does not exist in the installed game and offers
/// replacements (default "keep"). The main window calculates (ProjectCheck) and replaces; this window only shows and collects the choice.
public partial class AnalysisWindow : Window
{
    /// a row was clicked: show (the next copy of) it on the map
    public event Action<CheckIssue> ShowIssue;
    /// "Replace chosen": issue and chosen name
    public event Action<List<(CheckIssue Issue, string Name)>> ReplaceRequested;

    readonly Dictionary<CheckIssue, ComboBox> choices = new();
    Func<string, PrefabDef> describe;

    public AnalysisWindow()
    {
        InitializeComponent();
        Loc.Translate(this);
    }

    static string KindText(PrefabKind k) => k switch
    {
        PrefabKind.Tile => Loc.T("City tile"),
        PrefabKind.Part => Loc.T("Part"),
        PrefabKind.Bridge => Loc.T("Bridge"),
        PrefabKind.Filler => Loc.T("Filler"),
        _ => Loc.T("Building"),
    };

    static string Size(PrefabDef d) => d == null ? "?" : $"{d.SizeX} × {d.SizeZ} m";

    static string ProblemText(CheckIssue i) => i.Problem switch
    {
        CheckProblem.Missing => Loc.T("does not exist in the installed game") + (i.Old != null ? " (" + Size(i.Old) + ")" : ""),
        CheckProblem.SizeChanged => Loc.F("other size in the installed game: {0} instead of {1}", Size(i.New), Size(i.Old)),
        _ => Loc.F("no longer fits its lot ({0} × {1} m) in the installed game: {2}", i.Lot?.Width, i.Lot?.Depth, Size(i.New)),
    };

    /// show a result; describe = description of a replacement (label, size, tier) in the installed game
    public void Show(CheckResult r, Func<string, PrefabDef> describe)
    {
        this.describe = describe;
        string project = r.Project?.Version != null ? r.Project.Text : Loc.T("unknown version");
        string game = r.Target?.Version != null ? r.Target.Version.Display + (r.Target.Mod != null ? " + " + GameProfile.ModName(r.Target.Mod) : "") : Loc.T("not found");
        VersionsText.Text = Loc.F("Project: {0}   ·   Installed game: {1}", project, game) + (r.TargetFolder != null ? "\n" + r.TargetFolder : "");
        if (r.Issues.Count == 0)
            SummaryText.Text = Loc.T("Everything on the map exists in the installed game.");
        else
        {
            var parts = new List<string>();
            foreach (var (k, text) in new[] { (PrefabKind.Poi, "{0} building(s)"), (PrefabKind.Filler, "{0} filler(s)"), (PrefabKind.Tile, "{0} city tile(s)"), (PrefabKind.Part, "{0} part(s)"), (PrefabKind.Bridge, "{0} bridge piece(s)") })
                if (r.Count(k) > 0) parts.Add(Loc.F(text, r.Count(k)));
            SummaryText.Text = Loc.F("To check: {0}.", string.Join(", ", parts));
        }
        choices.Clear();
        IssueList.Items.Clear();
        foreach (var i in r.Issues) IssueList.Items.Add(new ListBoxItem { Content = Row(i), Tag = i, Padding = new Thickness(6, 4, 6, 4) });
        ResultText.Text = "";
        bool otherFormat = r.Target?.Version != null && r.Project?.Version != null && !r.Project.Matches(r.Target);
        if (otherFormat) SummaryText.Text += " " + Loc.F("The project is still marked for {0}: \"Replace everything automatically\" also converts it to {1}.", r.Project.Text, r.Target.Text);
        AutoButton.IsEnabled = r.Issues.Count > 0 || otherFormat;
        UpdateButton();
    }

    FrameworkElement Row(CheckIssue i)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        var kind = new TextBlock { Text = KindText(i.Kind), VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("HintTextBrush", System.Windows.Media.Brushes.Gray) };
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string label = i.Old?.Label is { Length: > 0 } l ? l : i.New?.Label is { Length: > 0 } n ? n : null;
        name.Children.Add(new TextBlock { Text = label != null ? $"{label} ({i.Name})" : i.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        name.Children.Add(new TextBlock
        {
            Text = ProblemText(i) + (i.Lot != null ? " · " + Loc.F("on a lot of {0}", i.Lot.Tile.Name) : ""),
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = ThemeManager.Brush(i.Problem == CheckProblem.Missing ? "ErrorBrush" : "OrangeTextBrush", System.Windows.Media.Brushes.Firebrick),
        });
        var count = new TextBlock { Text = i.Instances.Count + "×", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(name, 1); Grid.SetColumn(count, 2);
        grid.Children.Add(kind); grid.Children.Add(name); grid.Children.Add(count);
        if (i.CanReplace)
        {
            var box = new ComboBox { VerticalAlignment = VerticalAlignment.Center, MaxDropDownHeight = 360 };
            box.Items.Add(new ComboBoxItem { Content = Loc.T("– keep –"), Tag = null });
            foreach (var c in i.Candidates)
            {
                var d = describe?.Invoke(c);
                string text = (d?.Label is { Length: > 0 } dl ? $"{dl} ({c})" : c) + (d != null ? $" · {d.SizeX} × {d.SizeZ} m" : "") + (d?.Tier > 0 ? " " + PrefabDef.Stars(d.Tier) : "");
                box.Items.Add(new ComboBoxItem { Content = text, Tag = c, ToolTip = text });
            }
            if (i.Candidates.Count == 0) { box.IsEnabled = false; box.ToolTip = Loc.T("No building of the installed game fits here."); }
            box.SelectedIndex = 0;
            box.SelectionChanged += (_, _) => UpdateButton();
            choices[i] = box;
            Grid.SetColumn(box, 3);
            grid.Children.Add(box);
        }
        else
        {
            var none = new TextBlock { Text = i.Kind == PrefabKind.Tile ? Loc.T("swap by hand (window \"Building\") or \"Replace everything automatically\"")
                    : i.Problem == CheckProblem.Missing ? Loc.T("removed by \"Replace everything automatically\"") : Loc.T("only shown"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeManager.Brush("HintTextBrush", System.Windows.Media.Brushes.Gray), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(none, 3);
            grid.Children.Add(none);
        }
        return grid;
    }

    List<(CheckIssue, string)> Chosen() =>
        choices.Where(kv => (kv.Value.SelectedItem as ComboBoxItem)?.Tag is string).Select(kv => (kv.Key, (string)((ComboBoxItem)kv.Value.SelectedItem).Tag)).ToList();

    void UpdateButton() => ReplaceButton.IsEnabled = Chosen().Count > 0;

    /// message after replacing
    public void SetResult(string text) => ResultText.Text = text;

    void Issue_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((IssueList.SelectedItem as ListBoxItem)?.Tag is CheckIssue i) { justSelected = true; ShowIssue?.Invoke(i); }
    }

    bool justSelected;

    // a second click on the same row shows the next copy (the selection does not change then)
    void Issue_Click(object sender, MouseButtonEventArgs e)
    {
        if (justSelected) { justSelected = false; return; }   // this click selected the row: already shown
        if (e.OriginalSource is DependencyObject d && FindParent<ComboBox>(d) != null) return;
        if (e.OriginalSource is DependencyObject o && FindParent<ListBoxItem>(o) is ListBoxItem item && item.IsSelected && item.Tag is CheckIssue i && e.ClickCount == 1)
            Dispatcher.BeginInvoke(() => ShowIssue?.Invoke(i));
    }

    static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    void Replace_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Chosen();
        if (chosen.Count > 0) ReplaceRequested?.Invoke(chosen);
    }

    /// "Replace everything automatically" (the user's request 2026-11-20: convert a 3.4 project for 2.6)
    public event Action AutoRequested;

    void Auto_Click(object sender, RoutedEventArgs e) => AutoRequested?.Invoke();

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
