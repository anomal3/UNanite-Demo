#ifndef UNANITE_VG_RVT_INCLUDED
#define UNANITE_VG_RVT_INCLUDED

// M12: runtime virtual texture of virtual geometry terrains (VgWorld.Rvt.cs).
//
//   virtual texture   2^k texels over the terrain's UV square; mip m has (2^k >> m) / 128 tiles per
//                     side (k - 7 + 1 mips, the last one a single tile)
//   page table        VG_RvtPageTable, per terrain from `base`, mip-major, row-major inside a mip:
//                     atlas tile + 1, 0 = not resident (the top mips always are)
//   atlas             _VgRvtAlbedo (RGBA8 sRGB: albedo, AO) + _VgRvtNormal (RGBA8: world normal x / z
//                     * 0.5 + 0.5, smoothness, metallic); tiles of VG_RVT_TILE + 2 * VG_RVT_BORDER texels
//
// Terrain parameters (a terrain's resolve material, UnityPerMaterial._VgRvtParams):
//   x: page-table base, y: k (log2 of the virtual size), z: top mip (k - 7), w: 1 = RVT on

#define VG_RVT_TILE 128
#define VG_RVT_TILE_LOG2 7
#define VG_RVT_BORDER 4
#define VG_RVT_STRIDE (VG_RVT_TILE + 2 * VG_RVT_BORDER)
#define VG_RVT_MAX_ANISO_LOG2 2.0 // 4:1, within the tile border

// First page-table entry of mip `m` (sum of the finer mips' tile counts): L = log2(tiles per side at mip 0)
uint VgRvtMipOffset(uint L, uint m)
{
    // sum_{j < m} 4^(L - j) = (4^(L + 1) - 4^(L + 1 - m)) / 3
    return ((1u << (2u * (L + 1u))) - (1u << (2u * (L + 1u - m)))) / 3u;
}

// Page-table index of tile (tx, ty) of mip m.
uint VgRvtTileIndex(uint L, uint m, uint2 t)
{
    return VgRvtMipOffset(L, m) + t.y * (1u << (L - m)) + t.x;
}

// Level of detail (mip 0 = finest) from the UV footprint of a pixel in mip-0 texels: the ellipse's
// minor axis, but at most VG_RVT_MAX_ANISO_LOG2 finer than the major axis (anisotropic taps cover
// that ratio inside the tile border).
float VgRvtLod(float2 dx, float2 dy)
{
    float major2 = max(dot(dx, dx), dot(dy, dy));
    float area = abs(dx.x * dy.y - dx.y * dy.x);
    float lodMajor = 0.5 * log2(max(major2, 1e-20));
    float lodMinor = log2(max(area, 1e-20)) - lodMajor;
    return max(lodMinor, lodMajor - VG_RVT_MAX_ANISO_LOG2);
}

#ifndef VG_RVT_NO_SAMPLING

StructuredBuffer<uint> VG_RvtPageTable;
TEXTURE2D(_VgRvtAlbedo);
SAMPLER(sampler_VgRvtAlbedo);
TEXTURE2D(_VgRvtNormal);
float4 _VgRvtAtlas; // x: tiles per atlas row, y: 1 / atlas size (texels), z: atlas size

struct VgRvtSurface
{
    float3 albedo;
    float ao;
    float3 normalWS;
    float smoothness;
    float metallic;
    bool valid;   // false until the terrain's top mips are baked (first frames): use the direct blend
};

// Samples mip `m` (or its nearest resident ancestor) at terrain UV `uv`; dx / dy = UV derivatives.
VgRvtSurface VgRvtSampleMip(float4 params, float2 uv, float2 dx, float2 dy, uint m)
{
    uint base = (uint)params.x, k = (uint)params.y, top = (uint)params.z;
    uint L = top;
    uint entry = 0;
    [loop] for (; m <= top; ++m)
    {
        uint side = 1u << (L - m);
        uint2 t = min((uint2)(saturate(uv) * side), side - 1u);
        entry = VG_RvtPageTable[base + VgRvtTileIndex(L, m, t)];
        if (entry != 0)
            break;
    }
    m = min(m, top);
    VgRvtSurface s;
    s.valid = entry != 0;
    uint slot = s.valid ? entry - 1u : 0u;
    float size = (float)(1u << (k - m)); // texels of mip m per side
    float2 texel = uv * size;
    float2 tile = min(floor(texel / VG_RVT_TILE), (float)((1u << (L - m)) - 1u));
    float2 origin = float2(slot % (uint)_VgRvtAtlas.x, slot / (uint)_VgRvtAtlas.x) * VG_RVT_STRIDE + VG_RVT_BORDER;
    float2 local = clamp(texel - tile * VG_RVT_TILE, -VG_RVT_BORDER * 0.5, VG_RVT_TILE + VG_RVT_BORDER * 0.5);
    float2 atlasUV = (origin + local) * _VgRvtAtlas.y;
    float scale = size * _VgRvtAtlas.y;
    float4 a = SAMPLE_TEXTURE2D_GRAD(_VgRvtAlbedo, sampler_VgRvtAlbedo, atlasUV, dx * scale, dy * scale);
    float4 n = SAMPLE_TEXTURE2D_GRAD(_VgRvtNormal, sampler_VgRvtAlbedo, atlasUV, dx * scale, dy * scale);
    s.albedo = a.rgb;
    s.ao = a.a;
    float2 nxz = n.xy * 2.0 - 1.0;
    s.normalWS = float3(nxz.x, sqrt(saturate(1.0 - dot(nxz, nxz))), nxz.y);
    s.smoothness = n.z;
    s.metallic = n.w;
    return s;
}

// Trilinear lookup at level of detail `lod` (>= 0).
VgRvtSurface VgRvtSample(float4 params, float2 uv, float2 dx, float2 dy, float lod)
{
    uint top = (uint)params.z;
    lod = clamp(lod, 0.0, (float)top);
    uint m0 = (uint)lod;
    float f = lod - m0;
    VgRvtSurface s0 = VgRvtSampleMip(params, uv, dx, dy, m0);
    if (f <= 1.0 / 64.0 || m0 >= top)
        return s0;
    VgRvtSurface s1 = VgRvtSampleMip(params, uv, dx, dy, m0 + 1u);
    s0.valid = s0.valid && s1.valid;
    s0.albedo = lerp(s0.albedo, s1.albedo, f);
    s0.ao = lerp(s0.ao, s1.ao, f);
    s0.normalWS = normalize(lerp(s0.normalWS, s1.normalWS, f));
    s0.smoothness = lerp(s0.smoothness, s1.smoothness, f);
    s0.metallic = lerp(s0.metallic, s1.metallic, f);
    return s0;
}

#endif // VG_RVT_NO_SAMPLING

#endif
