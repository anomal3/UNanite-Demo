#ifndef UNANITE_DEBUG_COLORS_INCLUDED
#define UNANITE_DEBUG_COLORS_INCLUDED

// Stable pseudo-random colour for an id (PCG hash), kept away from black for readability.
float3 VgHashColor(uint id)
{
    uint h = id * 747796405u + 2891336453u;
    h = ((h >> ((h >> 28u) + 4u)) ^ h) * 277803737u;
    h = (h >> 22u) ^ h;
    return float3(h & 0xFFu, (h >> 8) & 0xFFu, (h >> 16) & 0xFFu) / 255.0 * 0.75 + 0.25;
}

// Blue (finest) -> green -> yellow -> red (coarsest).
float3 VgLevelColor(uint level, uint levelCount)
{
    float t = levelCount > 1 ? saturate(level / (float)(levelCount - 1)) : 0;
    float3 c0 = float3(0.10, 0.30, 1.00);
    float3 c1 = float3(0.10, 0.90, 0.30);
    float3 c2 = float3(1.00, 0.90, 0.10);
    float3 c3 = float3(1.00, 0.15, 0.10);
    if (t < 1.0 / 3.0) return lerp(c0, c1, t * 3.0);
    if (t < 2.0 / 3.0) return lerp(c1, c2, t * 3.0 - 1.0);
    return lerp(c2, c3, t * 3.0 - 2.0);
}

#endif
