namespace MapEditor.Map;

// Symbole = Zeichen der Schrift bootstrap-icons (Icons.cs); im Prefab-Fenster nur der Text, in der Liste und den Gruppenknöpfen mit IconText/IconFont

/// Gruppe für die Gebäudeliste (Symbol oben in der rechten Seite)
public sealed record PrefabCategory(string Key, string Icon, string Title, string[] Prefixes);

/// Einteilung der Prefabs nach dem Anfang ihres Namens (Zoning und Tags sind im Spiel zu lückenhaft: 3.660 von 4.584
/// Gebäuden in My World haben Zoning „none“). Ausgezählt an My World am 28.10.2026: jeder Name fällt in eine Gruppe.
public static class PrefabCategories
{
    public static readonly PrefabCategory[] All =
    {
        new("home", Icons.HouseFill, "Housing", new[] { "house", "abandoned", "cabin", "trailer", "apartments", "motel", "hotel", "housing", "lodge" }),
        new("shop", Icons.CartFill, "Shops and food", new[] { "store", "gas", "fastfood", "diner", "restaurant", "bar", "bank", "post", "bowling", "commercial", "business", "carlot", "auto", "roadside", "rest" }),
        new("city", Icons.BuildingsFill, "Downtown", new[] { "downtown", "skyscraper", "office" }),
        new("industry", Icons.BuildingGear, "Industry and utilities", new[] { "warehouse", "factory", "utility", "industrial", "junkyard", "sawmill", "mine", "quarry", "weigh", "water", "radio", "installation" }),
        new("public", Icons.ShieldFill, "Military and public services", new[] { "army", "base", "bombshelter", "bunker", "police", "prison", "fire", "school", "ranger", "church", "funeral", "cemetery" }),
        new("farm", Icons.Tree, "Countryside and farms", new[] { "farm", "barn", "countrytown", "country", "rural", "field" }),
        new("wild", Icons.Fire, "Wilderness and camps", new[] { "site", "survivor", "cave", "wilderness", "oldwest", "crater", "park", "body" }),
        new("trader", Icons.CurrencyDollar, "Traders", new[] { "trader" }),
        new("ruin", Icons.Bricks, "Ruins and open lots", new[] { "remnant", "rubble", "lot", "parking" }),
        new("tile", Icons.Grid3x3GapFill, "City tiles", new[] { "rwg" }),
        new("part", Icons.Wrench, "Parts (driveways, signs …)", new[] { "part" }),
    };

    public static readonly PrefabCategory Other = new("other", Icons.QuestionCircle, "Other", Array.Empty<string>());

    public static PrefabCategory Of(string name)
    {
        string first = name.Split('_')[0].ToLowerInvariant();
        foreach (var c in All)
            if (c.Prefixes.Contains(first)) return c;
        return Other;
    }
}
