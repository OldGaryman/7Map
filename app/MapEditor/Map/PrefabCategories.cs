namespace MapEditor.Map;

// Icons = characters of the bootstrap-icons font (Icons.cs); in the prefab window only the text, in the list and the group buttons with IconText/IconFont

/// Group for the building list (icon at the top of the right side)
public sealed record PrefabCategory(string Key, string Icon, string Title, string[] Prefixes);

/// Division of the prefabs by the beginning of their name (zoning and tags are too patchy in the game: 3,660 of 4,584
/// buildings in My World have zoning "none"). Counted on My World on 2026-10-28: every name falls into a group.
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
