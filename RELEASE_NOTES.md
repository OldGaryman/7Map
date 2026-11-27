# YAMEE – Yet Another Map Editor Extra

Map editor for 7 Days to Die (Windows). Author: Old Garyman 2026.

## 2.0.0 – 2026-11-27
### New
- New menu: Home, Map, Terrain, Water, Roads, Spawn point, View, Settings
- New project: "New map" (with a random name) or "Open map"
- Renaming the map also renames the project
- Game versions 2.6, 2.6 + Undead Legacy and 3.4 are recognized
- Convert: adapt a project to the installed game version, including "Replace everything automatically"
- Rivers in the map generator, tools "River" and "Connect lakes"
- Bridges (mod YAMEE_Bridges): "Bridge" tool, bridges for roads, routes, wilderness paths and river towns
- Bridge mods: choose bridges, swap bridges with a click
- The installer can install the bridge mod into the game
- Volcano in the burnt forest with serpentine road, two POIs and landslides
- Map generator: snow terrain, desert terrain (badlands), biome layout, biome coherence, sizes 2k to 12k
- Road generation: asphalt/gravel route with variants, close dead end, re-lay road, delete road
- Generate roads: node tiles (T, crossing, straight gateway), roads to the map edge
- Wasteland: roads and paths are broken at craters and cracks
- Generate cities: T and crossing gateways, "Earthwork for towns", city sizes per biome and map size
- New lake tool: natural shores, eyedropper, lake bed, island
- Export: map as .png (size selectable) and map as .zip for sharing (incl. overview image)
- View: show dead ends
- Progress bar with "Cancel" (Esc)
- Info windows can be turned off
- Spawn points: seed and number selectable

### Improved
- Roads: smoother curves, round crests, smooth surface, slight cross slope
- Junctions and crossings without steps or ditches
- Swap bridge: road, surface and river stay intact, height changes fade out smoothly
- Swap bridge & re-lay road: seamless joints with banking, crossings and side branches
- Wilderness paths: smoother connections, distance to city tiles, cross links instead of dead ends
- Rivers and lakes: more natural shores, no more water over land in the distance
- Cities: districts closer, away from the map edge, no two identical tiles side by side
- All windows look the same, also in the dark design
- One project folder for projects, backups and error log
- Update a savegame: safer, checks game version and water
- YAMEE starts only once (running window is brought to the front)
- Portable version: no portable.txt needed, does not copy settings from an installed version

### Fixed
- Double driveways at some buildings
- Error messages in the road network ("Collection was modified", "Object reference …")
- Crash with narrow rivers in "Connect lakes"
- Windows of the old map stayed open after loading another map

## 1.3.0 – 2026-11-13
### New
- Generate roads: edit the plan before building (type, width, delete, reroute, draw a new road)
- Move junctions and crossings in the plan
- The road plan is saved in the project
- List "Places to check" with jump to the map
- Towns without a free exit are connected via their own streets

### Improved
- Town roads in wide curves
- Roads: straight connection pieces, round crests, level verge, slight cross slope
- Flatter junctions and crossings
- Short steep pieces allowed when they save a lot of earthwork
- Short connections as a smooth curve

### Fixed
- Earthwork lowered lake shores, lakes ran out
- Wrong warning "already built"

## 1.2.0 – 2026-11-12
- Less memory for large steps and long roads
- Planning the road network about twice as fast
- Smoother panning and zooming of the map
- Cancelled creating or opening of projects leaves nothing behind
- Own values (user.ini) are no longer lost

## 1.1.0 – 2026-11-11
- One project folder for projects, backups and settings
- Driveways next to new buildings were 1 m too high; fixed

## 1.0.2 – 2026-11-11
- Note on the GPL-3.0 license with links to the license and source code

## 1.0.1 – 2026-11-11
- Generate world: smoother water edge at lakes

## 1.0.0 – 2026-11-11
- Generate roads: roads stay close to the ground instead of long embankments
- Generate world: biome shares are met exactly
- New default values (e.g. asphalt 16 m, 8 %)

## 0.9.9 – 2026-11-10
- Generate world: the center of the map is always pine forest
- All windows and messages come in light and dark
- Grades are shown in percent everywhere
- Notices can no longer be switched off (every message appears again)

## 0.9.8 – 2026-11-10
- Every tool has its values on the left and saves them at once; "Reset values" exists for each tool separately
- Settings: only paths and language are left; all changeable values are in default.ini and user.ini
- Generate world: one spawn point in the middle of the map

## 0.9.7 – 2026-11-10
- A map without a world shows what you can do: open a project, create one from a game map or generate a new world
- World tab: the creation tools (world, towns, roads, wilderness POIs) form their own group
- Shorter messages with a hint for the next step; a hint to generate roads before placing wilderness POIs

## 0.9.6 – 2026-11-10
- Wilderness paths: 3–5 m wide depending on the building, with a level verge, cut into the ground on slopes

## 0.9.5 – 2026-11-10
- New: Generate wilderness POIs – cabins, camps, caves etc. outside the towns, following the game's rules, with gravel paths to the nearest road
- Buildings with randomly placed parts (e.g. signs) now get just one part instead of all stacked on top of each other

## 0.9.4 – 2026-11-09
- Towns: number per biome adjustable
- Towns also find room in steep mountains (main part cut into the slope as a terrace)

## 0.9.3 – 2026-11-08
- Towns: in about every second town the trader sits near the center

## 0.9.2 – 2026-11-08
- Generate roads: fewer dead ends
- Roads that run close to a town street are joined with a short link

## 0.9.1 – 2026-11-08
- Generate roads: roads run side by side less often
- Free town exits are joined to nearby roads

## 0.9.0 – 2026-11-08
- Generate roads: at least 80 m distance to the map edge

## 0.8.9 – 2026-11-07
- Towns: every town has 2–4 open connections for roads, pointing in different directions

## 0.8.8 – 2026-11-07
- Generate towns on a map that already has towns: choose between Replace, Add and Cancel
- Some German notices that were still in English are now translated

## 0.8.7 – 2026-11-07
- Towns: open connections no longer point to the map edge

## 0.8.6 – 2026-11-07
- Generate roads: the "Build roads" button writes the planned network into the map (cuts, embankments, slopes, connection to the town exits)

## 0.8.5 – 2026-11-07
- Generate roads: a road no longer crosses the road it branches from; round curves at town exits

## 0.8.4 – 2026-11-07
- Generate roads: wide curves instead of dense zigzags

## 0.8.3 – 2026-11-07
- New: Generate roads – plans a road network between the towns and shows it as a preview on the map

## 0.8.2 – 2026-11-07
- Towns: every town lies within one biome; roads no longer cross or touch each other
- Free space in front of the gateway; transition piece to the wide road (part_highway_transition)

## 0.8.1 – 2026-11-07
- Towns: location selectable (plains and valleys, mixed, hilltops); hilltops are leveled
- Steps between roads are smoothed out

## 0.8.0 – 2026-11-07
- Towns: the connecting roads run in calm curves instead of hooks and zigzags

## 0.7.9 – 2026-11-07
- Towns rebuilt: level main part plus districts at their natural height, gateway with a trader per biome, winding roads

## 0.7.8 – 2026-11-07
- New: Generate towns – places towns like the game does (town types per biome, number and size by map size)

## 0.7.7 – 2026-11-07
- Generate world: striking rock walls, rock towers and rock domes in the pine forest

## 0.7.6 – 2026-11-07
- Generate world: desert plateaus a little higher and partly lower, more variety

## 0.7.5 – 2026-11-07
- Generate world: hillier wasteland, rougher desert with single pointed mountains, more variety in steepness

## 0.7.4 – 2026-11-06
- Generate world: bomb craters and earthquake cracks in the wasteland, steep walls in the desert, rare anomalies (meteor crater, sinkhole, rock needles …)
- More natural lake shores

## 0.7.3 – 2026-11-06
- Generate world: mountains from the game's height stamps, rockier slopes, erosion by rain

## 0.7.2 – 2026-11-06
- Generate world: higher mountains, valleys and gorges, irregular lakes also in the mountains

## 0.7.1 – 2026-11-06
- New: Generate world – a completely new world (size 2–10 km, seed, biome shares, mountains, hills, lakes), for now without towns, roads and buildings

## 0.7.0 – 2026-11-06
- Water: all ground below the water level and a strip along the shore gets water (no more bushes in the middle of shallow water)

## 0.6.9 – 2026-11-06
- Lakes, rivers and "Fill craters": biome under water selectable

## 0.6.8 – 2026-11-06
- Remove water: removes only the water, the terrain stays

## 0.6.7 – 2026-11-06
- Water follows the ground in every editing step (eraser, roads, buildings, tiles)

## 0.6.6 – 2026-11-06
- Water follows the ground when you change the terrain with the brush, mountains or mountain ranges

## 0.6.5 – 2026-11-05
- The status bar shows the water level under the mouse

## 0.6.4 – 2026-11-05
- Fill craters: you only set the water height above the clicked point

## 0.6.3 – 2026-11-05
- Fill craters: water level and minimum depth selectable

## 0.6.2 – 2026-11-05
- Fill craters: water flows over thresholds; a click runs downhill to the lowest point

## 0.6.1 – 2026-11-05
- New: Fill craters (Water tab)

## 0.6.0 – 2026-11-05
- River: adjustable irregularity of the banks

## 0.5.9 – 2026-11-05
- River in two steps: first click the path, then edit the bank line

## 0.5.8 – 2026-11-05
- New: River tool; new bodies of water take over the water level of adjoining water

## 0.5.7 – 2026-11-05
- Lakes: water depth separate from the digging depth, shore width also works outside the water

## 0.5.6 – 2026-11-05
- Lakes: angular outline, rougher shores and lake bed

## 0.5.5 – 2026-11-05
- New: Water tab with the tools "Lake" and "Remove water"

## 0.5.4 – 2026-11-05
- New: "Grass edge" for asphalt and gravel – a level strip next to the road without surface

## 0.5.3 – 2026-11-05
- Building list: road connections with automatic width

## 0.5.2 – 2026-11-05
- Building list: "Biome" column and filter (what the game builds in which biome)
- Buildings can be placed even while a brush is switched on

## 0.5.1 – 2026-11-05
- Mountain ranges: separate options for roads and buildings underneath

## 0.5.0 – 2026-11-05
- Settings window in the new layout

## 0.4.9 – 2026-11-05
- The height profile at the bottom only appears when it is needed

## 0.4.8 – 2026-11-05
- The box above the map (road, edit road) in the uniform layout

## 0.4.7 – 2026-11-05
- Uniform layout: same row heights, label and field widths in all tools

## 0.4.6 – 2026-11-05
- Rulers easy to read, terrain brush neatly aligned

## 0.4.5 – 2026-11-05
- The left side can be resized and disappears when no tool is selected

## 0.4.4 – 2026-11-05
- All values on the left directly visible (no "More …"), slightly wider

## 0.4.3 – 2026-11-04
- The values of the selected tool are on the left, the tool toolbar is gone
- Building list on the right can be collapsed, lighter green like the logo, sharper text

## 0.4.2 – 2026-11-04
- Ribbon: each tab shows all its tools; new tabs Spawn points, View and Settings
- New: rulers at the map edge and dark theme

## 0.4.1 – 2026-11-04
- New interface with a ribbon (World, Terrain, Mountain ranges, Roads, Settings)
- Brushes, mountains and eraser take effect at once; a question appears if you switch tools while a road is unfinished

## 0.4.0 – 2026-11-04
- Bug fixed: clicking the text in the box above the map caused an error message

## 0.3.9 – 2026-11-04
- Mountain range: new value "Ruggedness" (pointed peaks, rocky ridges), jagged edges rarer, gentle foothills also beyond the line

## 0.3.8 – 2026-11-04
- Mountain range: the drawn area can be edited before applying (drag, insert, delete points)
- Stronger roughness, more distinct hill ridges

## 0.3.7 – 2026-11-04
- New: Mountain range – draw an area with the mouse and a mountain range, hill chain or highland is built inside it; foothills gentle, steep, jagged or mixed

## 0.3.6 – 2026-11-04
- Roads start and end at town tiles and building entrances level for 4 m, exactly at the game's height
- Warnings while building roads in a yellow box

## 0.3.5 – 2026-11-04
- New: place mountains and hills with one click (mountain range, plateau, hill, dome) – using the game's height stamps

## 0.3.4 – 2026-11-03
- New icons (Bootstrap Icons) in all buttons and building groups

## 0.3.3 – 2026-11-03
- Terrain brush: more shapes (diamond, hexagon, ellipse, cross, star, blob, random)

## 0.3.2 – 2026-11-03
- Edit road: crossing roads follow smoothly; entrances and tiles keep their height
- Automatic banking in curves
- Roads can become narrower or wider (width per point); connection to tiles in the right width
- All connections show their road width; more buildings have connection points
- Town tiles snap to neighboring tiles and show their road exits (green = matches)
- Bug fixed: a road sometimes joined a tile one step too low

## 0.3.1 – 2026-11-02
- New: edit an existing road – change height, grade and banking (cross slope) between markers

## 0.3.0 – 2026-11-02
- The height readout at the bottom left updates at once when the terrain changes

## 0.2.9 – 2026-11-02
- Terrain brush: town tiles always keep their height

## 0.2.8 – 2026-11-02
- Terrain brush: buildings can be taken along, height limits (min/max)

## 0.2.7 – 2026-11-02
- Terrain brush: leave roads fixed, affect them slightly or take them along; grade limit

## 0.2.6 – 2026-11-02
- New: terrain brush (smooth, raise, lower, level, dents, weather)

## 0.2.5 – 2026-11-02
- Insert, move, rotate, swap and delete town tiles
- New logo and program icon; new, calmer look in all windows
- Building list: tier as stars, sortable; biome legend on the map
- More stability: safer saving, backups and settings, error log, less memory use

## 0.2.0 – 2026-10-30
- Apply map changes to an existing saved game (preview, full backup)
- Notice when the world was changed outside of YAMEE
- Town tiles show roads even if the world was never loaded in the game
- Installer: notice about updates and version history

## 0.1.0 – 2026-10-30 (first release)
- Load game worlds as projects, undo/redo, backups
- Draw asphalt and gravel roads, eraser, biome brush
- Place, move, rotate, swap and delete buildings
- Spawn points for play and test
- German and English, automatic detection of the game
