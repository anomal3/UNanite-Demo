# Quarry Scene (UNanite sample)

Builds a dense scanned environment - a terraced slate quarry with rock-clad walls, scree, slabs, spoil heaps,
shrubs, a brick ruin, a worker camp and a sandbag barrier - and renders it as virtual geometry.

**Tools > UNanite > Samples > Quarry Scene**

1. **Import downloads** (optional): point the window at a folder of Megascans downloads (Fab `..._high.zip`,
   Quixel Bridge `..._3d_ms.zip`, collection zips with one folder per asset). Every asset becomes an HDRP/Lit
   prefab (all LODs of all variants, a mask map packed from metalness / AO / roughness), surfaces become terrain
   layers. Texture settings are written into the `.meta` files before the import, so nothing is imported twice
   and no project-wide asset postprocessor is involved. Meshes without LOD files get Unity's Mesh LOD (the
   baseline a Unity 6 project would use; UNanite builds from LOD0 either way).
2. **Build scene**: the quarry from the library - or from procedural rocks when there is no library. Placement is
   rule based on each asset's own geometry (size, openness, the direction its scanned surface faces), so any set
   of scans works: open rock-face scans clad the walls, thin scans lie as ground patches, closed rocks become
   boulders, stones and scree; props are recognised by name (table, barrel, sandbag, ruin walls, ...).

In Play Mode: right mouse + WASD / QE to fly, Shift faster, wheel for speed; **V** switches between virtual
geometry and Unity's renderers (Mesh LOD, LODGroups, Unity terrain) for comparisons; **P** plays the flythrough;
**H** hides the overlay (frame times, VG triangles, clusters, instances, streaming residency).

**Megascans license**: the downloads are licensed to you by Fab and are never part of this package; the sample
only contains the code that turns them into a scene.

## Reference scene

The scene measured in UNanite's notes (`Documentation~/Milestones.md`, "M14 quarry scene": 9 246 objects,
249 M triangles at LOD0) was built with the default options (seed 7, density 1) from 57 downloads, all free
on Fab in September 2026 (search fab.com for the names, the `_high` / `8K` variants were used):

* the collection **South African Slate Quarry** (82 assets: 45 meshes, 35 surfaces, 2 plants);
* rocks: Huge Icelandic Lava Cliff, Massive Tundra Rock Formation, Nordic Beach Rocks, Nordic Forest Ledge Rock
  Large, Nordic Forest Rock Small, Rock (shopk), Rock Sandstone, Military Trenches Scatter Rock S 01-10,
  Military Trenches Scatter Flint Rock S 01, Military Trenches Debris Patch Rock S 01-02;
* ruin: Industrial BrickRuin Door Arch Brick Straight 02, Wall Brick Straight 02, Wall Chimney Brick Straight 01,
  Window Brick Straight 01 and 04, Broken Wall, Castle Wall (sbxuw, sctv3), Bricks Rubble, Street Curbs;
* camp: Metal Table, Wooden Table (uc1kebzfa, ulzrcgoaw), Dirty Metal Chair, Old Metal Stool, Metal Pot,
  Pumpkin, Rusty Differential Cog, Wooden Floor Lamp, Rusty Metal Barrel, Wooden Barrel (tl0vafqfa, tmgpcg2fa),
  Rusty Gas Tank, Trash Can (ucynedrfa, ufghbgyfa), Wooden Wheelbarrow, Round Hay Bale;
* barrier: Military Trenches Barrier Sandbag Canvas Square 02, Barrier Sandbag Canvas Worn, Cloth Sandbag Canvas
  Torn 02, Pile Sandbag Canvas 01, Wall Wood 01 and 05, Wall Dirt Corner 05, Storage Crate Wood S 02, Storage
  Box Metal Rusted.

Any other set of scans works too; the builder logs what it recognised (shells, patches, boulders, stones, props,
plants, surfaces).
