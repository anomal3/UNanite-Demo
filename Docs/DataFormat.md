> Copy of `Documentation~/DataFormat.md` of the UNanite package (0.13.2-preview). Paths are relative to the
> package folder (`Assets/UNanite` in this repository); the sources of the native builder (`Native~`) are not
> part of this repository.

# VG blob format (version 2)

Source of truth: `Native~/src/vg_format.h`. Mirrors: `Runtime/Scripts/Data/VgFormat.cs`,
`Runtime/ShaderLibrary/VgFormat.hlsl` (M2). Any change bumps `UNVG_FORMAT_VERSION`; the editor
refuses a native builder with a different version and the importer re-imports every `.vgmesh`.

All values little-endian, all tables 4-byte aligned (16-byte aligned starts), offsets in bytes
from the start of the blob unless stated otherwise.

```
+-------------------+ 0
| MeshHeader  256 B |
+-------------------+ groupTableOffset
| Group[groupCount]      48 B each   (always resident)
+-------------------+ nodeTableOffset
| Node[nodeCount]        32 B each   (always resident)
+-------------------+ pageTableOffset
| Page[pageCount]        32 B each   (always resident)
+-------------------+ pageDepsOffset
| uint32[pageDepsCount]              (page dependency lists)
+-------------------+ levelTableOffset
| Level[levelCount]      16 B each   (statistics)
+-------------------+ pageDataOffset (256-aligned)
| root pages [0, rootPageCount)      (always resident)
+-------------------+ streamDataOffset
| page rootPageCount | ...           (streamable, each <= pageSize, 16-aligned)
+-------------------+ blobSize
```

Everything before `streamDataOffset` is the *resident prefix*. Imported assets keep only that prefix
(`VirtualGeometryMesh.Blob`); the streamable part is stored verbatim as the **page file**
(`<index>.vgpages`, a side file of the `.vgmesh` import artifact in the editor,
`StreamingAssets/UNanite/<content hash>.vgpages` in players, M7). Page `p` sits at
`pageDataOffset + page.dataOffset - streamDataOffset` in that file. Meshes built at runtime keep the
whole blob (embedded pages).

## MeshHeader (256 B)

| Offset | Field | Notes |
|---|---|---|
| 0 | magic | `0x47564E55` ("UNVG") |
| 4 | version | 2 |
| 8 | headerSize | 256 |
| 12 | meshFlags | bit0 tangents, bit1 uv1, bit2 colours, bit3 separate x/z quantisation (`positionPrecisionXZ`, M8) |
| 16..28 | clusterCount, groupCount, nodeCount, pageCount | |
| 32..44 | levelCount, materialCount, rootPageCount, pageSize | pages `[0, rootPageCount)` always resident |
| 48 | positionPrecision (int) | position = q · 2^-precision |
| 52 | uvPrecision | uv = q · 2^-uvPrecision |
| 56 | normalBits | octahedral bits per component |
| 60 | maxClusterTriangles | |
| 64 | aabbMin.xyz, lodRootError | |
| 80 | aabbMax.xyz, boundsRadius | |
| 96 | sourceTriangles, sourceVertices, totalTriangles, totalVertices | |
| 112 | groupTableOffset, nodeTableOffset, pageTableOffset, pageDepsOffset | |
| 128 | levelTableOffset, pageDataOffset, pageDepsCount, blobSize | |
| 144 | rootNodeCount | nodes `[0, rootNodeCount)` are per-level BVH roots |
| 148 | streamDataOffset | blob offset of the first streamable page (= blobSize if every page is a root page) |
| 152 | tangentAngleBits | 8 (tangent angle, see the vertex stream) |
| 156 | positionPrecisionXZ | M8: x and z stored as `round(p * 2^positionPrecisionXZ)` when meshFlags bit3 (heightfield grids: the coarsest power of two that represents every sample position exactly, 1 m spacing → 2^0), else equal to positionPrecision |
| 160 | reserved[24] | zero |

**Root pages.** Groups are ordered *terminal groups first* (the top group and groups whose
simplification got stuck: nothing coarser represents them), then coarsest level first, spatial
order inside a level. Page 0 is closed at 32 KB (it is resident for every registered mesh), later
pages at `pageSize` (≤ 128 KB). `rootPageCount` = pages up to the one holding the last terminal
group, so the root pages alone always form a complete coarse mesh. Every page dependency points to
an earlier page (the builder checks it), so any page prefix is upward-closed.

## Group (48 B) — always resident

`center.xyz, radius` (LOD sphere, conservative, monotonic; also a valid culling sphere),
`error` (error of the clusters simplified *from* this group; `FLT_MAX` = never coarsen),
`depth` (DAG level), `pageIndex`, `firstCluster` (global), `clusterCount`,
`pageClusterOffset` (index inside the page), `flags` (bit0 terminal), `reserved`.

Groups are numbered in page order: each page owns a contiguous group range, each group a
contiguous cluster range.

## Node (32 B) — always resident

`center.xyz, radius, error` (max over subtree), `childOffset, childCount`, `group` (leaf: group
index; internal: −1). One BVH per DAG level (width 8); roots are nodes `0..levelCount-1`.

## Page (32 B)

`dataOffset` (from `pageDataOffset`), `dataSize`, `firstGroup, groupCount, firstCluster,
clusterCount, depsOffset, depsCount`. Dependencies are the pages holding the coarser groups that
contain clusters simplified from this page's groups; a page may only be resident if all its
dependencies are (upward closure → crack-free cuts under streaming).

## Page contents

```
PageHeader (16 B): clusterCount, clusterHeaderOffset (=16), geometryOffset, dataSize
ClusterHeader[clusterCount] (64 B each)
per cluster: [uv1Min int2 if meshFlags bit1] vertex stream (4-byte padded), index stream (4-byte padded)
```

Page bytes are uploaded verbatim into a GPU page-pool slot; all offsets inside are page-relative.

### ClusterHeader (64 B = 4 × uint4)

| Word | Field |
|---|---|
| 0–3 | `cullCenter.xyz, cullRadius` — precise bounds (culling only; not monotonic) |
| 4 | `counts`: (vertexCount−1) \| (triangleCount−1) << 7 \| material << 14 \| lodLevel << 22 \| flags << 28 |
| 5 | `bits`: bx \| by << 5 \| bz << 10 \| index base bits << 15 \| index delta bits << 18 \| vertex stride bits << 21 |
| 6 | `offsets`: vertex stream offset / 4 \| index stream offset / 4 << 15 (page-relative) |
| 7–9 | `posMin.xyz` (int, quantised) |
| 10–11 | `uv0Min.xy` (int) |
| 12 | `uvBits`: u0x \| u0y << 5 \| u1x << 10 \| u1y << 15 |
| 13 | `cone`: axis s8 x, y, z, cutoff s8 (meshopt); backface-cull if `dot(normalize(c - eye), axis) >= cutoff + r / \|c - eye\|` |
| 14 | `group` (mesh-relative group index) |
| 15 | `refinedGroup` (`0xFFFFFFFF` for source clusters) |

Words 4–9 hold everything the rasterisers need for positions and triangles (one `Load4` + one
`Load2`). v1 had a float AABB (dropped: derivable from `posMin` and the bit widths), the uv1
minimum (moved in front of the vertex stream, only for meshes with lightmap UVs) and 32-bit
counts/offset fields: 112 → 64 bytes.

### Vertex stream

Vertices are numbered in Cuthill–McKee order (breadth-first from a pseudo-peripheral vertex,
neighbours by increasing degree), which keeps index distances inside triangles small. Fixed stride
per cluster (`stride` bits), vertex *i* at bit `i * stride` from the vertex stream offset, fields in
order:

| Field | Bits | Decode |
|---|---|---|
| position x, y, z | bx, by, bz | `(posMin + q) * 2^-positionPrecision` (x, z: `2^-positionPrecisionXZ` with meshFlags bit3) |
| normal oct x, y | normalBits × 2 | octahedral, unsigned `[0, 2^n-1] → [-1, 1]` |
| tangent angle, sign | tangentAngleBits (8), 1 (if meshFlags bit0) | see below; sign bit set → w = −1 |
| uv0 u, v | u0x, u0y | `(uv0Min + q) * 2^-uvPrecision` |
| uv1 u, v | u1x, u1y (if bit1) | `(uv1Min + q)`, same grid as uv0; `uv1Min` = the 8 bytes before the vertex stream |
| colour | 16 + 16 (if bit2) | RGBA8, low half first |

Bit fields are read LSB-first from little-endian 32-bit words; a field may straddle two words.

**Tangent.** `t = cos(φ)·b1 + sin(φ)·b2`, `φ = angle · 2π / 2^tangentAngleBits`, with the
orthonormal basis of Duff et al. 2017 around the *decoded* normal `n`:
`s = lower ? −1 : 1, a = −1 / (s + n.z), b = n.x·n.y·a, b1 = (1 + s·n.x²·a, s·b, −s·n.x),
b2 = (b, s + n.y²·a, −n.y)`. The hemisphere `lower` is decided on the integer octahedral code
(`|2·ox − M| + |2·oy − M| > M`, `M = 2^normalBits − 1`), identically in the builder, the C# reader
and the shaders. The encoder projects the source tangent onto the plane of the decoded normal.
Measured on the 327K rock: mean error 0.35°, max 0.70° (v1 octahedral 8 + 8 bits: 17 bits → 9).

### Index stream

```
word 0:  anchors: 7 bits per 32 triangles (anchor k = smallest index of triangles [32k, 32k + 32))
bit 32+: per triangle t, recBits = baseBits + 2 * deltaBits:
         (m - anchor[t >> 5]) | (a - m - 1) << baseBits | (b - m - 1) << (baseBits + deltaBits)
triangle t = (m, a, b)
```

Every triangle is rotated so that its smallest index `m` comes first (the winding is kept) and the
triangles are sorted by it, so `m` grows slowly inside a 32-triangle block. Decoding one triangle is
two loads (anchor word + record), random access for the vertex shader, the software raster and the
resolve. Measured: 20.9 → 13.5 bits per triangle.

## LOD rule

```
projectedError(G) = G.error * scale / max(distance(eye, G.center) - G.radius * scale, near) * P11 * 0.5 * screenHeight
coarsen(G)        = projectedError(G) <= thresholdPixels
render(C)         = resident(C.group) && !coarsen(C.group)
                    && (C.refinedGroup == ~0 || coarsen(C.refinedGroup) || !resident(C.refinedGroup))
resident(G)       = VG_PageOffsets[mesh.pageBase + G.pageIndex] != ~0     (M7)
```

## Runtime GPU buffers (VgWorld)

Layouts shared by `VgGpuTypes.cs`, `VgFormat.hlsl`, `VgCull.compute`, `VgClassify.compute`,
`VgVisBufferRaster.shader`, `VgResolve.hlsl`, `VgShadowRaster.shader` and
`VgShadowReceivers.compute`.

| Buffer | Element | Content |
|---|---|---|
| `VG_Meshes` | `VgMeshGpu` 80 B | table bases, quantisation (y and x/z), bounds, material count, tangent angle bits; M8: runtime flag bit16 `VG_MESH_UV_FROM_XZ` with `uvScaleX/Z` (terrain tiles: `uv0 = position.xz * uvScale`, no stored UVs) and bit17 `VG_MESH_HW_RASTER` (terrain tiles: never the software raster). Mesh slots are reused; tables live at ranges of the global tables (first-fit allocators with coalescing, M8) |
| `VG_PagePool` | raw bytes | M7: root region (every registered mesh's root pages, packed, 16-aligned per mesh) followed by fixed streaming slots of `max pageSize` bytes; the pool size is the budget (`streamingPoolMB`). Streaming off: every page in the root region |
| `VG_PageOffsets` | uint per global page (`mesh.pageBase + page`) | pool byte address of the page, `0xFFFFFFFF` = not resident (M7) |
| `VG_Feedback` | uint | M7: [0] list count, [1, 1 + capacity) global pages reported this frame (first report only), then per global page at `1 + capacity + page` the max priority reported this frame (float bits, projected error of the group item; 0 = none). One buffer because `CullClusters` is at the 8-UAV limit of SM 5.0 |
| `VG_FeedbackOut` | uint | M7: gathered at the end of a context render: [0] count (bit31: list overflow), then (page, priority bits) pairs; read back with `AsyncGPUReadback` (a window of about twice the last count) |
| `VG_Instances` | `VgInstanceGpu` 192 B | 3×4 local-to-world and world-to-local rows, mesh, material base, flags (enabled, shadows, mirrored, occludable, M9: bit4 moving), max scale, world sphere; M8: `lodSelf`, `lodParent` (HLOD switch records, ~0 = none); M9: `prevLocalToWorld` rows (transform of the previous rendered frame, object motion vectors), `lightmapIndex` (slice of the lightmap texture arrays; lightmapped bins only); M11: flags bit5 density LOD (thinned with the distance, `VgDensityLodScale`), flags bits 16–31 its own thinning start in metres (0: the view's `densityLodStart`, which stays the minimum), `errorScale` (former padding: LOD error multiplier, 1 = the view's pixel error, per-terrain pixel error) |
| `VG_InstanceLightmaps` | float4 | M9: lightmap uv scale (xy) / offset (zw) per instance (`MeshRenderer.lightmapScaleOffset`) |
| `VG_LodSwitches` | `VgLodSwitch` 32 B | M8 HLOD switch records: world sphere, error. `CullInstances` draws an instance iff `¬coarsen(lodParent) ∧ coarsen(lodSelf)`; a terrain node and its children reference the same record, so the decisions are exact complements. M11: `morphStart` (former padding): instances whose LOD the record ends (`lodParent`) morph toward the next LOD from the distance where `morphStart` projects to one unit (SpeedTree smooth LOD, `unity_LODFade.x`); `fadeStart` (crossfade band, `fadeTransitionWidth`: the coarse side from where it projects to one unit), `fadeDuration` (animated crossfade, seconds) |
| `VG_LodFadeState` | uint2 per camera slot (4) × record | M11 animated crossfade: x = side the camera last switched to (1 coarse) \| initialised << 1, y = time of that switch (float bits); written by `UpdateLodFade` per camera view, read by `CullInstances` and the programmable raster (`VG_LodFadeParams`: slot base, record count, time) |
| per-instance BRG batch buffer | raw | M10: 64 zero bytes, then `unity_ObjectToWorld` / `unity_WorldToObject` / previous matrices as per-instance float3x4 arrays; M11: then per wind slot 16 current + 16 previous-frame float4 (`_ST_Wind*` of SpeedTree8Wind.hlsl), read through one BRG batch per slot as per-batch `DOTS_ST_WindParam*` / `DOTS_ST_WindHistoryParam*` |
| `_VgBaryBuffer` | R32_UInt, screen | M11: barycentrics of corners 1 and 2 of programmable pixels (15 bits each) + front face (bit 30); valid where the visibility buffer shows a bin with `VG_BIN_BARYCENTRICS` (32; `VG_BIN_PROGRAMMABLE` = 16) |
| `_VgMotionBuffer` | R16G16_SFloat, screen | M11: motion vector (HDRP encoding: NDC delta × 0.5, y flipped where UVs start at the top) of programmable pixels of vertex-animated bins (`VG_BIN_RASTER_MOTION` = 64), SV_Target2 of the programmable raster |
| `VG_InstanceBins` | uint | material bin of (instance material base + material slot) |
| `VG_BinFlags` | uint per bin | bit0 `VG_BIN_RESOLVE` (visibility-buffer resolve), bit1 `VG_BIN_DOUBLE_SIDED` (no cone culling, `Cull Off` raster), bit2 `VG_BIN_SHADOW` (M6: vertex-pulled shadow raster) |
| `VG_Visible` | uint4 | (instance, page address, cluster in page, bin); region 0 = shadow/expansion views, regions 1..N = visibility-buffer cameras (capacity each = `visibleClusterCapacity`), records appended sequentially |
| `VG_RasterLists` | uint | M5: per region 3 lists × visible capacity of absolute `VG_Visible` indices: 0 hardware single-sided, 1 hardware double-sided, 2 software. Region 0 (shadow / expansion views) uses lists 0 / 1 for the M6 shadow raster |
| `VG_RasterArgs` | 32 uints per view slot | camera: 0/4 phase-1 HW single/double `IndirectDrawArgs` (384, clusters, 0, 0); 8/12 phase-2 HW; 16/20 phase-1/phase-2 SW dispatch args (one group per cluster). Shadow-raster split (M6): 0/8 `IndirectDrawIndexedArgs` (triangles × 3, 1, first index, 0, 0) single / double-sided; 16 compaction dispatch (one group per record) |
| `VG_ShadowRecords` | uint4 | M6: visible records (`VG_Visible` layout) of all shadow-raster splits of the frame, `shadowRecordCapacity` entries; each split owns a contiguous range |
| `VG_ShadowViews` | uint4 per view slot | M6: (first record, single-sided records, double-sided records, first index in the arena index buffer) |
| arena index buffer (shadow raster) | uint | M6: `shadowRecord << 7 \| vertex in cluster` per triangle corner, written by `CompactShadow`; the shadow raster decodes the position of `SV_VertexID` from the record's cluster (no vertex data) |
| `VG_PhaseState` | 16 uints per camera region | 0–2 deferred instances / nodes / clusters, 3 visible records after phase 1, 4–6 raster list sizes after phase 1, 7–9 list entries added by phase 2 (phase-2 draws start at 4–6) |
| `VG_SwVisBuffer` | uint64 per pixel | M5: `asuint(depthKey) << 32 \| visibility id`, depthKey = device depth (reversed Z) or 1 − depth; 0 = empty |
| `VG_SwTileMask` / `VG_SwTiles` | uint | M5: 8×8 tiles touched by the software raster / compacted list drawn by `VG.MergeSW` |
| `VG_OccludedInstances` / `Nodes` / `Clusters` | uint / uint2 / uint4 | per camera region, `occlusionListCapacity` entries each: instance index, (instance, node), visible-record layout |
| `VG_Hzb` | float | farthest-depth pyramid, see below |
| `VG_Stats` | 16 uints per view slot | instances, groups, clusters, triangles, overflow (bit5: shadow records / indices, M6), double-sided, raster bin mask (1 visibility buffer, 4 shadow raster), deferred I/N/C, phase-2 clusters, phase-2 triangles, SW clusters phase 1, SW clusters phase 2, receiver-culled instances + nodes + clusters, receiver-culled split flag |
| `VG_Counters` | 40 uints | per-view working counters (`VgCull.compute` `C_*`); M6 adds 32–33 shadow triangles of lists 0 / 1 and 34–35 their index cursors |
| `VG_ArenaCounters` | 4 uints | frame-wide: 0 vertices, 1 indices, 2 shadow records (M6) |
| `VG_DrawArgs` | 5 uints per (view slot, bin) | `IndirectDrawIndexedArgs` of the expansion draws |
| `VG_BinTileCount` / `Offset` / `Cursor` | uint per bin | classification counters and tile-list ranges |
| `VG_ResolveArgs` | 4 uints per bin | `IndirectDrawArgs` (tiles × 6, tiles > 0, 0, 0) of the resolve draws |
| `VG_TileList` | uint2 | (tileX \| tileY << 16, bin), `resolveTileListFactor` × screen tiles entries |
| BRG visible instances | uint per bin | all 0 (one identity instance); resolve draws use `visibleOffset = bin` |
| `VG_TileDepth` | float4 | M6, per camera: 16 px tiles (min, max, opaque min, opaque max) linear depth with sky = far plane, followed by 64 px and 256 px nodes (min, 0, 0, 0) |
| `VG_RcvSplat` | uint per texel | M6: splat pyramids of up to 16 splits (receiver pyramid layout below), ordered-uint encoded light depth, 0 = empty |

### Visibility buffer

`R32G32_UInt`, camera resolution (`TextureXR` array), cleared to 0 before the raster:

```
x = ((visibleRecord << 7) | triangle) + 1      0 = no virtual geometry
y = asuint(SV_Position.z)                      the rasterised device depth, bit-exact
```

`visibleRecord` is the absolute index in `VG_Visible` (camera region included), so 25 bits allow
32 M records. Memory: 8 B per pixel (16.6 MB at 1920×1080).

### HZB (M4)

Structured buffer of floats, levels stored consecutively, row-major. Level 0 is
`nextPow2(ceil(W/2)) × nextPow2(ceil(H/2))`, each further level halves both sizes (minimum 1) down
to 1×1. Texel `t` of level `L` holds the farthest device depth (minimum with reversed Z) of pixels
`[t·2^(L+1), (t+1)·2^(L+1))` in both axes; pixels outside the screen count as the far plane. The
test projects the 8 corners of the bounds' AABB with the HZB frame's unjittered GPU
view-projection, grows the pixel rect by one pixel, picks the level where it spans at most 2×2
texels, and culls when the nearest corner depth is farther than the farthest HZB depth.
Memory at 1920×1080: 1024×1024 + mips ≈ 5.6 MB per camera history + one scratch.

### Shadow receiver pyramid (M6)

Same layout as the HZB with a 512×512 "screen": level 0 is 256×256 texels over the split's
light-space NDC square (`x = (ndc.x · 0.5 + 0.5) · 512`, `y = (0.5 − ndc.y · 0.5) · 512`), 9
levels down to 1×1, 87 381 texels per split. A texel holds the **farthest** light depth (split
`cullingMatrix` clip z / w, larger = farther from the light) of any camera-visible point whose
dilated footprint covers it; `-3e38` = no receiver. Up to 16 splits are stored back to back
(`split × 87 381`, 5.6 MB for the pyramids and as much for the splat buffer); `_OccConfig.z` gives
the cull pass its split's base. The cull test is the HZB test with `_OccConfig.x = 3`: a caster
is rejected when its nearest light depth is farther than every receiver in its footprint.
Footprints are clamped to the split instead of being kept at the border.

Splat encoding: `f ≥ 0 → asuint(f) | 0x80000000`, `f < 0 → ~asuint(f)` (unsigned order = float
order); a slab is written with `InterlockedMax` into the level where it covers at most 2×2 texels,
`Finalize` takes the max over a level-0 texel's ancestors, `Pyramid` builds the max pyramid.

### Terrain virtual texture (M12)

* **Virtual texture** of a terrain: `2^k` texels over its UV square (`k` from `rvtTexelsPerMeter`,
  capped at 17), tiles of 128 × 128 texels; mip `m` has `2^(k − 7 − m)` tiles per side, the last mip
  one tile.
* **`VG_RvtPageTable`** (`StructuredBuffer<uint>`, all terrains): a terrain's entries start at its
  `pageBase`, mip-major, row-major inside a mip (`VgRvtMipOffset(L, m) = (4^(L+1) − 4^(L+1−m)) / 3`,
  `L = k − 7`); value = atlas tile + 1, 0 = not resident. The top two mips are always resident; the
  resolve walks up to the nearest resident ancestor.
* **Atlas**: two `rvtAtlasTiles²`-tile textures of 136 × 136 texels per tile (4-texel border):
  `_VgRvtAlbedo` RGBA8 sRGB (albedo, AO), `_VgRvtNormal` RGBA8 (world normal x / z · 0.5 + 0.5,
  smoothness, metallic; y reconstructed, terrains face up).
* **Feedback**: `VG_RvtBits` (one bit per page-table entry, cleared every frame) and
  `VG_RvtRequests` (`[0]` count, then keys `terrain << 25 | mip << 20 | ty << 10 | tx`), read back
  asynchronously.
* The terrain's resolve material carries `_VgRvtParams = (pageBase, k, L, 1)` in `UnityPerMaterial`.

### Virtual shadow maps (M13)

* **Pool** `_VgVsmPool`: `R32_UInt`, 64 pages of 128 × 128 texels per row (`vsmPoolPages / 64`
  rows). A texel holds the ordered key of the nearest caster's biased depth (splat encoding above;
  reversed Z: `InterlockedMax`), 0 = no caster.
* **`VG_VsmPages`** (`uint4` per table slot; an entry owns `W × W` slots, `W = res / 128` for
  perspective splits, `+ 1` for cascades): x = physical page (`0xFFFF` none) | valid bit 16 | dirty
  bit 17, y = global page key `(px + 32768) | (py + 32768) << 16`, z = last frame needed, w = content
  epoch. Slot of a page = `(py mod W) · W + (px mod W)`.
* **`VG_VsmEntries`** (`VgVsmEntry`, 192 B): rows of HDRP's render matrix of the split (absolute
  world), the anchor frame's depth row (cascades), window (global texel of local (0, 0), resolution,
  flags ortho / z-clip / reset), table (base, W, epoch, max page age), depth map (`a · stored + b`,
  slope bias), cull-NDC ↔ local-texel affine maps (`cullToLocal0.w / 1.w`: the sub-texel offset of
  the atlas grid from the anchor grid, cascades).
* **`VG_VsmBatch`** (`uint4` per entry of a dispatch): x = entry, y = receiver batch index of its
  split (pyramid base = y · texels per pyramid); `Release` / `Mark` / `DirtyPyramid` take the entry
  from the dispatch z, `Invalidate` from y, `Finalize` loops over them.
* **`VG_VsmList`** (per entry, from its table base): slots of this frame's needed pages (composite
  quads); **`VG_VsmArgs`**: `[0..2]` page-clear dispatch, then 4 uints per entry (composite draw).
* **`VG_VsmFree`**: `[0]` count, then free physical pages (pushed by `Release`, popped by `Mark`).
