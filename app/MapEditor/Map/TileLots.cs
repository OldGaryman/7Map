namespace MapEditor.Map;

/// Lot of a city tile in world coordinates (southwest corner, width in x, depth in z)
public sealed record TileLot(PlacedPrefab Tile, LotMarker Marker, string District, int X, int Z, int Width, int Depth);

/// Lots of the city tiles (rwg_tile_*) and which buildings fit on them (the user's request: swap buildings in tiles).
/// Checked on the original world (2026-10-29, metest `tilepois`): all 1,590 built lots meet without exception
/// – building exactly as large as the lot (rotated), – tag = district from the tile name (rwg_tile_countrytown_… → countrytown),
/// – additional tag of the lot (nodiagonal, culdesac …) is also on the building, – rotation = lot + tile + RotationToFaceNorth.
public static class TileLots
{
    /// district of a tile: first word after "rwg_tile_" (countrytown, residential, downtown …)
    public static string District(PlacedPrefab tile) =>
        tile.Name.Length > 9 ? tile.Name[9..].Split('_')[0].ToLowerInvariant() : "";

    /// all lots of a tile in world coordinates (rectangle rotated like the tile, smallest corner)
    public static IEnumerable<TileLot> Of(PlacedPrefab tile)
    {
        if (tile.Def == null) yield break;
        string district = District(tile);
        foreach (var m in tile.Def.Lots)
        {
            var (ax, az) = PlacedPrefab.Rotate(m.X, m.Z, tile.Rotation, tile.Def.SizeX, tile.Def.SizeZ);
            var (bx, bz) = PlacedPrefab.Rotate(m.X + m.SizeX, m.Z + m.SizeZ, tile.Rotation, tile.Def.SizeX, tile.Def.SizeZ);
            int x0 = (int)Math.Round(Math.Min(ax, bx)), z0 = (int)Math.Round(Math.Min(az, bz));
            yield return new TileLot(tile, m, district, tile.X + x0, tile.Z + z0,
                (int)Math.Round(Math.Abs(bx - ax)), (int)Math.Round(Math.Abs(bz - az)));
        }
    }

    /// lot on which the building stands (center of the footprint lies in it); null = none
    public static TileLot Under(WorldData w, PlacedPrefab p)
    {
        if (p.Def == null || p.IsTile || p.Def.Kind == PrefabKind.Part) return null;
        double cx = p.X + p.SizeX / 2.0, cz = p.Z + p.SizeZ / 2.0;
        foreach (var t in w.Prefabs)
        {
            if (!t.IsTile || !t.Contains(cx, cz)) continue;
            foreach (var lot in Of(t))
                if (cx > lot.X && cx < lot.X + lot.Width && cz > lot.Z && cz < lot.Z + lot.Depth) return lot;
        }
        return null;
    }

    /// rotation of a building on the lot (the front faces the street)
    public static int RotationFor(TileLot lot, PrefabDef d) => ((lot.Marker.Rotation + lot.Tile.Rotation + (d.RotationToFaceNorth ?? 0)) % 4 + 4) % 4;

    /// does the building fit the lot? district tag, additional tag and exactly the size of the lot (in its rotation)
    public static bool Fits(PrefabDef d, TileLot lot)
    {
        if (d == null || !d.Found || d.Kind is PrefabKind.Part or PrefabKind.Tile) return false;
        if (!d.Tags.Contains(lot.District)) return false;
        if (!string.IsNullOrEmpty(lot.Marker.Tag) && !d.Tags.Contains(lot.Marker.Tag)) return false;
        bool turned = RotationFor(lot, d) % 2 == 1;
        return (turned ? d.SizeZ : d.SizeX) == lot.Width && (turned ? d.SizeX : d.SizeZ) == lot.Depth;
    }

    /// new building on the lot: position = lot, rotation like the game, height = tile y + lot y + YOffset of the building
    public static PlacedPrefab Place(TileLot lot, string name, PrefabDef d) => new()
    {
        Name = name, Def = d, X = lot.X, Z = lot.Z, Rotation = RotationFor(lot, d),
        Y = lot.Tile.Y + lot.Marker.Y + d.YOffset,
    };
}
