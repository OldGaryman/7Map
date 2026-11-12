# YAMEE – Yet Another Map Editor Extra

Map editor for the game **7 Days to Die** (Windows): view, edit and generate worlds
(terrain, biomes, water, roads, cities, buildings, spawn points).

## Build

Requirements / Voraussetzungen: Windows 10/11, [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```
dotnet build app\MapEditor\MapEditor.csproj -c Release
```

or double-click `start_editor.bat` (builds and starts), or open `app\YAMEE.sln` in Visual Studio 2022
(see `app\VISUAL_STUDIO.md`). `publish.bat` builds a single self-contained `YAMEE.exe`.

## License

GNU General Public License v3.0 or later – see [LICENSE](LICENSE) and [COPYRIGHT.txt](COPYRIGHT.txt).
Bootstrap Icons (MIT) is included, see `app/MapEditor/Assets`.
