#ifndef UNANITE_VG_SUN_CLIPMAP_INCLUDED
#define UNANITE_VG_SUN_CLIPMAP_INCLUDED

// M13b sun shadow clipmap (VgWorld.SunClipmap.cs): shared by UNanite's page marking (VgVsm.compute)
// and by the optional HDRP patch, which copies this file into HDRP as
// Runtime/Lighting/Shadow/UNaniteSunShadow.hlsl and calls VgSunShadow from
// GetDirectionalShadowAttenuation (HdrpPatch~/README.md). Self-contained on purpose: the patched HDRP
// must compile without the UNanite package.
//
//   level k      orthographic view along the sun, texel size t0 * 2^k, `res` x `res` texels around the
//                camera; one M13 cache entry (VgVsmEntry layout, VG_VsmEntries) per level, entries
//                firstEntry .. firstEntry + levels - 1
//   level choice the first level whose texel is at least the pixel's world footprint (x LOD bias);
//                a point outside its window or on a page that is not resident falls back to coarser levels
//   depth        stored ordered-float keys of d = -dot(sun forward, p) * s + 0.5 (larger = nearer the sun)

#define VG_SUN_PAGE 128
#define VG_SUN_PAGE_LOG2 7
#define VG_SUN_POOL_ROW 64
#define VG_SUN_VALID (1u << 16)

struct VgSunEntry // = VgVsmEntry (VgVsm.hlsl), 192 bytes
{
    float4 viewProj[4];
    float4 anchorDepth;
    int4 window;          // xy: global texel of local (0, 0), z: resolution
    uint4 table;          // x: first slot, y: W
    float4 depth;
    float4 cullToLocal0;
    float4 cullToLocal1;
    float4 localToCull0;
    float4 localToCull1;
};

StructuredBuffer<VgSunEntry> _UNaniteSunEntries;
StructuredBuffer<uint4> _UNaniteSunPages;
Texture2D<uint> _UNaniteSunPool;
float4 _UNaniteSunParams;  // x: mode (0 off, 1 clipmap, 2 min(clipmap, HDRP cascades)), y: levels, z: first entry, w: texel size of level 0 (m)
float4 _UNaniteSunCamera;  // xyz: camera position (absolute world), w: world size of a pixel per metre of depth x LOD bias
float4 _UNaniteSunForward; // xyz: camera forward, w: normal offset (texels of the level)
float4 _UNaniteSunBias;    // x: depth bias (texels of the level), y: filter radius (texels), z: device depth per metre (s)

int VgSunMod(int a, int b) { return (int)((uint)(a + 32768 * b) % (uint)b); }
uint VgSunKey(int2 page) { return (uint)(page.x + 32768) | ((uint)(page.y + 32768) << 16); }

float VgSunOrderedFloat(uint k)
{
    return asfloat((k & 0x80000000u) != 0 ? (k & 0x7FFFFFFFu) : ~k);
}

// level of a point by its pixel footprint (the marking pass and the lookup agree on it)
int VgSunLevelFor(float3 positionAbs)
{
    float depth = max(dot(positionAbs - _UNaniteSunCamera.xyz, _UNaniteSunForward.xyz), 1e-3);
    float footprint = depth * _UNaniteSunCamera.w;
    int level = (int)ceil(log2(max(footprint / _UNaniteSunParams.w, 1e-8)));
    return clamp(level, 0, (int)_UNaniteSunParams.y - 1);
}

// continuous global texel of a point in a level (texel centres at i + 0.5)
float2 VgSunGlobalTexel(VgSunEntry e, float3 p)
{
    float4 q = float4(p, 1.0);
    float2 clip = float2(dot(e.viewProj[0], q), dot(e.viewProj[1], q));
    float res = (float)e.window.z;
    return float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5) * res + (float2)e.window.xy;
}

bool VgSunInside(VgSunEntry e, float2 g, float margin)
{
    float2 l = g - (float2)e.window.xy;
    return all(l >= margin) && all(l < (float)e.window.z - margin);
}

// slot index (absolute in the pages buffer) and page of a global texel
uint VgSunSlot(VgSunEntry e, int2 texel, out int2 page)
{
    page = texel >> VG_SUN_PAGE_LOG2;
    int W = (int)e.table.y;
    return e.table.x + (uint)(VgSunMod(page.y, W) * W + VgSunMod(page.x, W));
}

bool VgSunResident(VgSunEntry e, int2 texel)
{
    int2 page;
    uint4 p = _UNaniteSunPages[VgSunSlot(e, texel, page)];
    return p.y == VgSunKey(page) && (p.x & VG_SUN_VALID) != 0 && (p.x & 0xFFFFu) != 0xFFFFu;
}

// 1 = lit by the sun at this texel, receiver depth dr; missing pages count as lit
float VgSunTap(VgSunEntry e, int2 texel, float dr)
{
    int2 page;
    uint4 p = _UNaniteSunPages[VgSunSlot(e, texel, page)];
    if (p.y != VgSunKey(page) || (p.x & VG_SUN_VALID) == 0)
        return 1.0;
    uint phys = p.x & 0xFFFFu;
    uint2 origin = uint2(phys % VG_SUN_POOL_ROW, phys / VG_SUN_POOL_ROW) * VG_SUN_PAGE;
    uint key = _UNaniteSunPool.Load(int3(origin + (uint2)(texel - page * VG_SUN_PAGE), 0));
    if (key == 0)
        return 1.0; // no caster
    return VgSunOrderedFloat(key) > dr ? 0.0 : 1.0; // a caster nearer the sun
}

// Sun visibility (1 = lit) of an absolute world position with its (light-facing) normal; false when
// the clipmap has no resident page for it (the caller keeps HDRP's own cascades). `filtered`: tent
// filter over 4 x 4 taps, else one tap (volumetric fog: HDRP's DIRECTIONAL_SHADOW_ULTRA_LOW contexts).
bool VgSunShadowImpl(float3 positionAbs, float3 normalWS, bool filtered, out float visibility)
{
    visibility = 1.0;
    if (_UNaniteSunParams.x < 0.5)
        return false;
    int levels = (int)_UNaniteSunParams.y;
    int first = (int)_UNaniteSunParams.z;
    float radius = _UNaniteSunBias.y;
    for (int level = VgSunLevelFor(positionAbs); level < levels; ++level)
    {
        VgSunEntry e = _UNaniteSunEntries[first + level];
        float texelSize = _UNaniteSunParams.w * exp2((float)level);
        float3 p = positionAbs + normalWS * (_UNaniteSunForward.w * texelSize);
        float2 g = VgSunGlobalTexel(e, p);
        if (!VgSunInside(e, g, radius + 2.0) || !VgSunResident(e, (int2)floor(g)))
            continue;
        float dr = dot(e.anchorDepth.xyz, p) + e.anchorDepth.w + _UNaniteSunBias.x * texelSize * _UNaniteSunBias.z;
        if (!filtered)
        {
            visibility = VgSunTap(e, (int2)floor(g), dr);
            return true;
        }
        // tent filter of `radius` texels over 4 x 4 taps (texel centres at i + 0.5)
        float2 c = g - 0.5;
        int2 base = (int2)floor(c) - 1;
        float wx[4], wy[4];
        float sx = 0.0, sy = 0.0;
        [unroll]
        for (int i = 0; i < 4; ++i)
        {
            wx[i] = max(0.0, radius - abs(c.x - (float)(base.x + i)));
            wy[i] = max(0.0, radius - abs(c.y - (float)(base.y + i)));
            sx += wx[i];
            sy += wy[i];
        }
        float sum = 0.0;
        [unroll]
        for (int y = 0; y < 4; ++y)
        {
            [unroll]
            for (int x = 0; x < 4; ++x)
            {
                float w = wx[x] * wy[y];
                if (w > 0.0)
                    sum += w * VgSunTap(e, base + int2(x, y), dr);
            }
        }
        visibility = sum / max(sx * sy, 1e-6);
        return true;
    }
    return false;
}

bool VgSunShadow(float3 positionAbs, float3 normalWS, out float visibility)
{
    return VgSunShadowImpl(positionAbs, normalWS, true, visibility);
}

bool VgSunShadowPoint(float3 positionAbs, float3 normalWS, out float visibility)
{
    return VgSunShadowImpl(positionAbs, normalWS, false, visibility);
}

#endif
