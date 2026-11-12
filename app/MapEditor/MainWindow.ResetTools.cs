using System.Windows;

namespace MapEditor;

/// "Reset values" in every tool (the user's request, 2026-11-10): reloads the values of this tool from default.ini
/// (if it is missing, the built-in values apply); the deviations in user.ini are dropped by this.
public partial class MainWindow
{
    void ResetTool_Click(object sender, RoutedEventArgs e)
    {
        string tool = (string)((FrameworkElement)sender).Tag;
        var std = StandardValues.Load();   // read default.ini anew each time
        switch (tool)
        {
            case "terrain":
                UserValues.ResetTools(settings, nameof(AppSettings.TerrainSize), nameof(AppSettings.TerrainSquare), nameof(AppSettings.TerrainShape),
                    nameof(AppSettings.TerrainStrengths), nameof(AppSettings.TerrainSoftness), nameof(AppSettings.TerrainRoads), nameof(AppSettings.TerrainRoadTilt),
                    nameof(AppSettings.TerrainPois), nameof(AppSettings.TerrainHeightLimit), nameof(AppSettings.TerrainMinHeight), nameof(AppSettings.TerrainMaxHeight),
                    nameof(AppSettings.TerrainLimitGrade), nameof(AppSettings.TerrainMaxGrade));
                break;
            case "mountain":
                UserValues.ResetTools(settings, nameof(AppSettings.MountainVariant), nameof(AppSettings.MountainHeight), nameof(AppSettings.MountainSize),
                    nameof(AppSettings.MountainRotation), nameof(AppSettings.MountainMirror), nameof(AppSettings.MountainSteep), nameof(AppSettings.MountainRough),
                    nameof(AppSettings.MountainRoads), nameof(AppSettings.MountainPois));
                break;
            case "range":
                UserValues.ResetTools(settings, nameof(AppSettings.RangeEdge), nameof(AppSettings.RangeHeight), nameof(AppSettings.RangeFoot),
                    nameof(AppSettings.RangeSpacing), nameof(AppSettings.RangeRough), nameof(AppSettings.RangeCrag), nameof(AppSettings.RangeRoads), nameof(AppSettings.RangePois));
                break;
            case "water":
                UserValues.ResetTools(settings, nameof(AppSettings.WaterLevel), nameof(AppSettings.WaterDepth), nameof(AppSettings.WaterWDepth), nameof(AppSettings.WaterWidth),
                    nameof(AppSettings.WaterIrregular), nameof(AppSettings.WaterBelow), nameof(AppSettings.WaterCraterLevel), nameof(AppSettings.WaterMinDepth),
                    nameof(AppSettings.WaterCraterHeight), nameof(AppSettings.WaterShore), nameof(AppSettings.WaterBumps));
                break;
            case "eraser":
                settings.EraserSize = std.EraserSize; settings.EraserSquare = std.EraserSquare; settings.EraserBumps = std.EraserBumps;
                break;
            case "biome":
                settings.BiomeBrushSize = std.BiomeBrushSize; settings.BiomeBrushSquare = std.BiomeBrushSquare;
                break;
            case "roadedit":
                UserValues.ResetTools(settings, nameof(AppSettings.EditEven), nameof(AppSettings.EditKeepJunctions), nameof(AppSettings.EditAutoCross), nameof(AppSettings.EditAutoCrossMax));
                break;
            case "asphalt": settings.Asphalt = null; break;
            case "gravel": settings.Gravel = null; break;
            default: return;
        }
        settings.Save();
        switch (tool)
        {
            case "terrain": ShowTerrainValues(); break;
            case "mountain": ShowMountainValues(); break;
            case "range": ShowRangeValues(); break;
            case "water": ShowWaterValues(); break;
            case "roadedit": ShowEditValues(); if (roadEditMode) RecomputeRoadEdit(); break;
            case "eraser": ShowEraserValues(); break;
            case "biome": ShowBiomeValues(); break;
            default: ShowToolValues(); ToolValue_Changed(null, null); break;
        }
    }
}
