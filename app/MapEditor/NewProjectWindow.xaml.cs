using System.Windows;
using System.Windows.Controls;

namespace MapEditor;

/// Window "New project": new map (name, optionally straight to the generator) or open a map from the game.
public partial class NewProjectWindow : Window
{
    readonly Func<string, string> validate;   // error text for a name, null = fine

    public bool NewMap => NewMapRadio.IsChecked == true;
    public string MapName => NameBox.Text.Trim();
    public bool GoToGenerator => GeneratorCheck.IsChecked == true;

    readonly Func<Window, string, bool> confirmGame;   // a world of this name in the game: ask, true = use the name

    public NewProjectWindow(Func<string, string> validate, Func<Window, string, bool> confirmGame)
    {
        InitializeComponent();
        Loc.Translate(this);
        this.validate = validate;
        this.confirmGame = confirmGame;
        NameBox.Text = GeneratorWindow.SuggestName(GeneratorWindow.RandomSeed());   // a random name is offered at once
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    void RandomName_Click(object sender, RoutedEventArgs e) => NameBox.Text = GeneratorWindow.SuggestName(GeneratorWindow.RandomSeed());

    void Kind_Click(object sender, RoutedEventArgs e)
    {
        NewMapBox.IsEnabled = NewMap;
        ErrorText.Text = "";
    }

    void Name_Changed(object sender, TextChangedEventArgs e) => ErrorText.Text = "";

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (NewMap)
        {
            string error = validate(MapName);
            if (error != null) { ErrorText.Text = error; return; }
            if (!confirmGame(this, MapName)) return;
        }
        DialogResult = true;
    }
}
