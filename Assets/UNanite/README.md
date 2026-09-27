# UNanite — virtual geometry for Unity 6 (HDRP)

Nanite-style virtualized geometry: offline cluster DAG builder, GPU-driven culling and LOD selection,
rendering through HDRP's own passes via BatchRendererGroup. Status and measured numbers per
milestone: [Documentation~/Milestones.md](Documentation~/Milestones.md). Design:
[Documentation~/Architecture.md](Documentation~/Architecture.md). Binary layout:
[Documentation~/DataFormat.md](Documentation~/DataFormat.md).

## Install

- **.unitypackage** (`UNanite_<version>.unitypackage` from the
  [releases](https://github.com/anomal3/UNanite-Demo/releases)): installs to `Assets/UNanite`, the native
  builder DLL comes as `Plugins/win-x64` (no platform enabled; loaded by UNanite from a shadow copy).
- **UPM package**: Package Manager → Add package from tarball / from disk / git URL.

Use one way only. Requirements: Unity 6000.4, HDRP 17.4, Windows. A welcome window opens after install
(checkbox *Show this window at every startup*; **Tools/UNanite/Welcome**). Player builds need nothing
extra: page files go to `StreamingAssets/UNanite`, the shader variants of the runtime material twins are
kept by temporary twin materials (`VgShaderVariantBuildStep`), the native builder is copied to the player.

## Quick start

1. Select a model or mesh asset → tick **Virtual Geometry** in the inspector header (writes a
   `.vgmesh` next to it; the importer builds the DAG, cached by the AssetDatabase).
2. Select scene objects → **Tools/UNanite/Convert Selection to Virtual Geometry** (revert with
   **Revert Selection to Mesh Renderers**). Or add **UNanite/Virtual Geometry Renderer** manually.
3. Scene View overlay **UNanite** (or **Tools/UNanite/Debug View/**): Triangles, Clusters, Groups,
   LOD Level, Instances, Materials; pixel error; visibility-buffer toggle; **Freeze** (culling stays
   at the camera's current position while it moves: shows what was culled); per-view statistics
   (`[VB]` marks views rendered through the visibility buffer, `[SR]` shadow splits drawn by the
   shadow raster, `[RC -n]` shadow splits receiver-culled against the camera).

Existing HDRP/Lit and Shader Graph materials work unmodified (they need the `DOTS_INSTANCING_ON`
variant, which HDRP materials have). Main cameras (HDRP deferred) rasterise VG into a visibility
buffer and shade opaque HDRP/Lit materials with a per-material resolve inside HDRP's GBuffer pass;
other materials and views (forward cameras, reflection probes) use GPU vertex expansion. Those
cameras also get two-phase HZB occlusion culling (previous-frame HZB, exact re-test of deferred work
against the current frame; regular MeshRenderers occlude VG too) and a software rasteriser for
small clusters (compute, 64-bit atomics, D3D12/Vulkan). Shadow maps draw VG from indices only with
a vertex-pulling ShadowCaster pass, and shadow casters whose shadows cannot reach anything the
camera sees are culled with the camera depth of the same frame (receiver culling).

Geometry streams (M7): the GPU reports which pages its LOD cut wants, they are read from disk
(`AsyncReadManager`) into a fixed page pool (`VirtualGeometrySettings.streamingPoolMB`, default
512 MB) and evicted least-recently-used; only the small root pages of each mesh are always
resident, and a missing page shows coarser detail, never a hole. Imported `.vgmesh` assets keep the
streamable pages in a side file of the import artifact (copied to StreamingAssets for player
builds). A `.vgmesh` can also describe a procedural rock (`VirtualGeometryImporter.CreateProceduralRock`)
for test content without source meshes.

Terrains (M8): select a Unity Terrain → **Tools/UNanite/Convert Selected Terrains to Virtual
Geometry** (revert: **Revert Selected Terrains to Unity Terrain**). A `.vgterrain` sidecar next to
the TerrainData builds an HLOD quadtree of VG tiles (borders locked at full resolution: crack-free
across tiles and levels; the GPU picks one node per quadtree path per view), shaded with HDRP
TerrainLit's layer blending and per-pixel heightmap normals. Trees, details and the TerrainCollider
stay Unity's. `VirtualGeometryTerrain.SetHeights` / `ApplyCrater` edit at runtime: the affected
tiles are rebuilt on a worker thread and swapped in together with the collider.

Moving objects, destruction and ray tracing (M9):
* Moving `VirtualGeometryRenderer`s get object motion vectors (TAA, motion blur) automatically.
* *Ray Tracing Proxy Triangles* on a renderer: a hidden proxy mesh (DAG cut) in HDRP's ray tracing
  acceleration structure.
* A `.vgfracture` sidecar (JSON: source mesh or procedural rock, piece count, seed) imports a
  Voronoi-fractured mesh; add **Virtual Geometry Destructible** next to the renderer and call
  `Break(point, impulse)` (or let collisions break it). The source mesh must be closed.
* Baked lightmaps: converted renderers keep the lightmap of their (disabled) MeshRenderer. Bake
  before converting; all lightmaps must have one size and format.

Shader Graphs and transparency (M10): **Tools/UNanite/Generate Shader Variants for Scene** writes
a VG variant of every Shader Graph used by VG renderers (all passes pull their vertices from the page
pool with the instance's object matrices; opaque graphs are shaded by the visibility-buffer resolve).
Transparent instances are sorted like MeshRenderers.

Foliage (M11): **Virtual Geometry Terrain Trees** / **Virtual Geometry Terrain Details** on a
converted terrain draw its trees (the prefab's LODs with SpeedTree smooth LOD and LODGroup's animated
crossfade into the billboard, SpeedTree 8 wind under the scene's WindZones) and its mesh details
(density LOD: instances the details' shaders have faded out are dropped; `densityLodFadePoint`
trades draw distance for speed) as virtual geometry. Alpha-tested and vertex-animated materials
rasterise into the visibility buffer with their own vertex graph and alpha test; on D3D12 / Vulkan /
Metal the raster also writes hardware barycentrics, so the resolve shades each foliage pixel without
re-evaluating the vertex graph, and the motion vectors of the moved vertices (TAA, motion blur of
swaying leaves).

Scene foliage (M14): converting a LODGroup whose LOD0 is alpha-tested keeps every LOD: foliage is built
without simplification (the cluster DAG would drop whole leaves), and **Virtual Geometry LOD Group** draws the
group's own LODs as discrete virtual geometry LODs, switched where Unity's LODGroup switches (at 1080 lines).

Sample (M14, Package Manager > UNanite > Samples > **Quarry Scene**, HDRP): *Tools > UNanite > Samples >
Quarry Scene* imports a folder of Megascans downloads and builds a dense scanned quarry from them (or from
procedural rocks), converted to virtual geometry; in Play Mode V switches to Unity's renderers for comparisons.

Experimental, off by default (M12, M13, M13b; `VirtualGeometrySettings`) - validated by render tests, but
measured no faster than the default path in the test scenes (see `Documentation~/Milestones.md`):
* `terrainVirtualTexture`: VG terrains shade from a runtime virtual texture - their blended layers
  are baked into cached 128 × 128 tiles on demand (GPU feedback from the visibility buffer) instead
  of blending up to 8 layers per pixel every frame; close-up detail keeps the direct blend.
* `virtualShadowMaps`: VG shadow casters of every-frame lights are cached in 128 × 128 pages; only
  pages whose casters moved, appeared or were never rendered are rasterised again, the rest is copied
  into HDRP's shadow atlas (directional cascades scroll their pages with the camera).
* `sunShadowClipmap` (M13b prototype): the sun's shadow from UNanite's own clipmap of cached pages
  (texel about one pixel near the camera), read by HDRP through an optional patch (`HdrpPatch~/README.md`);
  VG casters with plain materials only for now - foliage and MeshRenderers stay in HDRP's cascades.

## Native builder

`Native~/` (C++17, meshoptimizer 1.2, MIT). Build (Windows, VS 2019+ / CMake 3.20+):

```
cmake -S Native~ -B Native~/build -G "Visual Studio 16 2019" -A x64
cmake --build Native~/build --config Release
```

The DLL is copied to `Plugins~/win-x64/` and loaded from a shadow copy, so it can be rebuilt while
the editor runs; every `.vgmesh` re-imports automatically when the DLL changes.
`Native~/build/Release/unanite_cli.exe rock 8 --runs 2` builds, decodes and crack-tests a 1.3M
triangle procedural rock outside Unity.

## Tests

EditMode tests (`UNanite.Tests.Editor`): determinism across runs/thread counts, DAG monotonicity,
per-level reduction, lossless source level, watertight LOD cuts (incl. partial page residency),
1M-triangle build time, resolve-capability rules, visibility buffer vs expansion image comparison,
occlusion image equality (moving camera and MeshRenderer occluder), software vs hardware raster
image equality, shadow raster and receiver culling image equality, format v2 compression and
tangent accuracy, the page residency manager under random feedback (with crack-free cuts of the
resident sets), streamed rendering converging to the all-resident image, page files read from
import artifacts, and (M8) terrain HLOD switches (monotonic, exactly one node per path), watertight
terrain cuts across tiles and levels, runtime edits identical to a full rebuild, terrain rendering
(1 px cut vs full detail, edits swapped in) and mesh replacement without dropping streamed pages.

## License

Copyright (C) 2026 Roman Koscheev (anomal3). GPL-3.0-only ([LICENSE](LICENSE)) with an additional
permission for Unity and additional terms (GPL v3 section 7), see [NOTICE.md](NOTICE.md):

- free for everyone, in any project, free or paid, open or closed source: your own game code and
  assets stay yours, under your own terms;
- UNanite itself, and every modified version of it, stays GPL-3.0 with its source available;
- keep "UNanite by Roman Koscheev (anomal3)" in your credits or About screen;
- don't present UNanite as your own work, and don't call your own version "UNanite".
The native builder uses meshoptimizer (MIT).
