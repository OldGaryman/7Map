using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MapEditor;

/// Hilfen für die Symbolschrift (Bootstrap Icons, Assets\bootstrap-icons.ttf); die Zeichen stehen in Icon (Icons.cs)
public static class IconUi
{
    /// Schrift mit den Symbolen; für Knöpfe und Texte, die im Code gebaut werden (in XAML: Stil IconText)
    public static readonly FontFamily Font = new(new Uri("pack://application:,,,/YAMEE;component/"), "./Assets/#bootstrap-icons");

    /// Knopf nur mit Symbol (z. B. Papierkorb in Listen): ohne Rahmen, mit Hinweistext
    public static Button IconButton(string icon, string toolTip, object tag = null, double size = 14)
    {
        var b = new Button
        {
            Content = icon, FontFamily = Font, FontSize = size, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = toolTip, Tag = tag, VerticalAlignment = VerticalAlignment.Center
        };
        return b;
    }

    /// Spaltenkopf einer sortierbaren Liste: Text, bei der gewählten Spalte dahinter ein kleiner Pfeil
    public static void SetSortHead(TextBlock head, string text, bool sorted, bool descending)
    {
        head.Inlines.Clear();
        head.Inlines.Add(new Run(text));
        if (sorted)
            head.Inlines.Add(new Run(" " + (descending ? Icons.CaretDownFill : Icons.CaretUpFill)) { FontFamily = Font, FontSize = head.FontSize * 0.85 });
    }
}
