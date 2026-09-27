#ifndef UNANITE_VG_VSM_INCLUDED
#define UNANITE_VG_VSM_INCLUDED

// M13 virtual shadow maps: cached shadow pages of VG casters (VgWorld.Vsm.cs, VgVsm.compute,
// VgVsmRaster.shader, VgVsmComposite.shader).
//
//   entry        one cached shadow split (light, split, owner camera): HDRP's render matrices and
//                resolution for the frame, a page table of W x W slots (VG_VsmPages)
//   local texel  texel of the split's atlas viewport in the frame (x right, y = row, 0 at the top)
//   global texel local + window.xy: directional cascades keep their global texel grid while they
//                follow the camera in texel steps (anchor frame); perspective splits: window.xy = 0
//   page         128 x 128 global texels; slot = page mod W (toroidal for cascades)
//   pool         R32_UInt texture of 128 x 128 physical pages (64 per row): ordered-float depth keys
//                of the anchor frame (reversed Z: larger = nearer, InterlockedMax), 0 = no caster
//
// VG_VsmPages (uint4 per slot): x phys page (0xFFFF none) | flags << 16, y global page key,
// z last frame the page was needed, w content epoch.

#define VSM_PAGE 128
#define VSM_PAGE_LOG2 7
#define VSM_POOL_ROW 64         // physical pages per pool row
#define VSM_NONE 0xFFFFu
#define VSM_VALID (1u << 16)
#define VSM_DIRTY (1u << 17)
#define VSM_NO_KEY 0xFFFFFFFFu

#define VSM_ENTRY_ORTHO 1
#define VSM_ENTRY_ZCLIP 2
#define VSM_ENTRY_RESET 4

struct VgVsmEntry
{
    float4 viewProj[4];       // rows of this frame's render matrix, world -> clip (HDRP's device projection, y flipped)
    float4 anchorDepth;       // directional: anchor-frame device depth = dot(xyz, p) + w; perspective: unused
    int4 window;              // xy: global texel of local (0, 0), z: resolution, w: flags (VSM_ENTRY_*)
    uint4 table;              // x: first slot in VG_VsmPages, y: W, z: epoch, w: max page age (frames)
    float4 depth;             // x, y: stored depth -> device depth of this frame (a * d + b), z: slope bias, w: frame
    float4 cullToLocal0;      // local texel x = dot(xy, cull NDC) + z (receiver / dirty pyramids use the culling matrix); w: sub-texel offset x of the atlas grid (cascades)
    float4 cullToLocal1;      // local texel y; w: sub-texel offset y
    float4 localToCull0;      // cull NDC x = dot(xy, local texel) + z
    float4 localToCull1;      // cull NDC y
};

float4 VsmClip(VgVsmEntry e, float3 p)
{
    float4 q = float4(p, 1.0);
    return float4(dot(e.viewProj[0], q), dot(e.viewProj[1], q), dot(e.viewProj[2], q), dot(e.viewProj[3], q));
}

int VsmPageOf(int texel) { return texel >> VSM_PAGE_LOG2; } // floor division (arithmetic shift)
// non-negative modulus (page coordinates are within +-32768, b > 0): shifted into uint range first
int VsmMod(int a, int b) { return (int)((uint)(a + 32768 * b) % (uint)b); }

uint VsmKey(int2 page) { return (uint)(page.x + 32768) | ((uint)(page.y + 32768) << 16); }
int2 VsmKeyPage(uint key) { return int2((int)(key & 0xFFFFu) - 32768, (int)(key >> 16) - 32768); }

// slot (table index relative to the entry's base) of a global page
uint VsmSlot(VgVsmEntry e, int2 page)
{
    int W = (int)e.table.y;
    return (uint)(VsmMod(page.y, W) * W + VsmMod(page.x, W));
}

// first global page of the frame's window
int2 VsmFirstPage(VgVsmEntry e) { return int2(VsmPageOf(e.window.x), VsmPageOf(e.window.y)); }

uint2 VsmPhysOrigin(uint phys) { return uint2(phys % VSM_POOL_ROW, phys / VSM_POOL_ROW) * VSM_PAGE; }

// Monotonic float <-> uint (every float ordered as unsigned; 0 below everything = no caster).
uint VsmOrderedKey(float f)
{
    uint u = asuint(f);
    return (u & 0x80000000u) != 0 ? ~u : (u | 0x80000000u);
}

float VsmOrderedFloat(uint k)
{
    return asfloat((k & 0x80000000u) != 0 ? (k & 0x7FFFFFFFu) : ~k);
}

#endif
