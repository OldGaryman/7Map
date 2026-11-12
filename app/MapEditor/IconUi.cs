using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MapEditor;

/// Helpers for the icon font (Bootstrap Icons, Assets\bootstrap-icons.ttf); the characters are in Icons (Icons.cs)
public static class IconUi
{
    /// font with the icons; for buttons and texts built in code (in XAML: style IconText)
    public static readonly FontFamily Font = new(new Uri("pack://application:,,,/YAMEE;component/"), "./Assets/#bootstrap-icons");

    /// Button with only an icon (e.g. trash can in lists): without border, with tooltip
    public static Button IconButton(string icon, string toolTip, object tag = null, double size = 14)
    {
        var b = new Button
        {
            Content = icon, FontFamily = Font, FontSize = size, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = toolTip, Tag = tag, VerticalAlignment = VerticalAlignment.Center
        };
        return b;
    }

    /// Column header of a sortable list: text, with a small arrow behind it for the selected column
    public static void SetSortHead(TextBlock head, string text, bool sorted, bool descending)
    {
        head.Inlines.Clear();
        head.Inlines.Add(new Run(text));
        if (sorted)
            head.Inlines.Add(new Run(" " + (descending ? Icons.CaretDownFill : Icons.CaretUpFill)) { FontFamily = Font, FontSize = head.FontSize * 0.85 });
    }
}
