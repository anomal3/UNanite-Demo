using System;
using System.Threading.Tasks;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>
    /// Deterministic procedural heightfields for tests and sample scenes (M8): gradient-damped fBm
    /// ("eroded" look: detail fades on steep slopes, valleys stay smooth, after I. Quilez), ridged
    /// mountain ranges under a low-frequency mask, rolling hills elsewhere; normalised to [0, 1].
    /// </summary>
    public static class ProceduralTerrain
    {
        static float Noise(float x, float y) => Mathf.PerlinNoise(x, y) * 2f - 1f;

        /// <summary>GLSL smoothstep (Mathf.SmoothStep interpolates between its first two arguments instead).</summary>
        public static float Smooth(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        // fBm whose octaves are damped by the accumulated gradient (steep areas get less detail)
        static float ErodedFbm(float x, float y, int octaves, float gain)
        {
            float sum = 0f, amp = 0.5f, freq = 1f, dx = 0f, dy = 0f;
            const float e = 0.01f;
            for (int o = 0; o < octaves; ++o)
            {
                float px = x * freq, py = y * freq;
                float n = Noise(px, py);
                float gx = (Noise(px + e, py) - Noise(px - e, py)) / (2f * e);
                float gy = (Noise(px, py + e) - Noise(px, py - e)) / (2f * e);
                dx += gx * amp;
                dy += gy * amp;
                sum += amp * n / (1f + dx * dx + dy * dy);
                amp *= gain;
                freq *= 2.01f;
                // rotate the domain between octaves to break grid alignment
                float rx = 0.8f * x - 0.6f * y, ry = 0.6f * x + 0.8f * y;
                x = rx + 13.7f;
                y = ry + 7.1f;
            }
            return sum;
        }

        // Unnormalised height at terrain-space coordinates (u, v): one unit = one tile side / 3.
        static float RawHeight(float u, float v)
        {
            float wu = u + 0.35f * Noise(u * 0.7f + 17.3f, v * 0.7f);
            float wv = v + 0.35f * Noise(u * 0.7f, v * 0.7f + 41.7f);
            float hills = ErodedFbm(wu, wv, 9, 0.5f);
            float ridge = 0f, amp = 0.5f, freq = 0.8f, weight = 1f;
            for (int o = 0; o < 5; ++o)
            {
                float n = 1f - Mathf.Abs(Noise(wu * freq + 3.1f, wv * freq + 7.9f));
                n = n * n * weight;
                weight = Mathf.Clamp01(n * 1.5f);
                ridge += n * amp;
                amp *= 0.45f;
                freq *= 2.05f;
            }
            float mask = Smooth(0.3f, 0.8f, Mathf.PerlinNoise(u * 0.25f + 5f, v * 0.25f + 9f));
            float height = 0.35f + hills * 0.45f + mask * ridge * 0.5f;
            // broad flat valleys: compress the low range
            return height < 0.3f ? 0.3f - (0.3f - height) * 0.35f : height;
        }

        static float[] RawTile(int resolution, int seed, float featureScale, int tileX, int tileZ)
        {
            var h = new float[resolution * resolution];
            var rng = new System.Random(seed);
            float ox = (float)rng.NextDouble() * 1000f, oz = (float)rng.NextDouble() * 1000f;
            float inv = 1f / (resolution - 1);
            Parallel.For(0, resolution, z =>
            {
                for (int x = 0; x < resolution; ++x)
                    h[z * resolution + x] = RawHeight((tileX + x * inv) * 3f * featureScale + ox, (tileZ + z * inv) * 3f * featureScale + oz);
            });
            return h;
        }

        static void Normalise(float[] h, float min, float max)
        {
            float scale = 1f / Math.Max(max - min, 1e-6f);
            for (int i = 0; i < h.Length; ++i)
                h[i] = Mathf.Clamp01((h[i] - min) * scale * 0.98f + 0.01f);
        }

        public static float[] Heights(int resolution, int seed, float featureScale = 1f)
        {
            var h = RawTile(resolution, seed, featureScale, 0, 0);
            float min = float.MaxValue, max = float.MinValue;
            foreach (float v in h)
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
            Normalise(h, min, max);
            return h;
        }

        /// <summary>
        /// Heights of a `tiles` x `tiles` grid of neighbouring terrains (row-major [tz * tiles + tx]):
        /// one continuous field, shared border samples identical, normalised over the whole grid.
        /// </summary>
        public static float[][] HeightsGrid(int resolution, int seed, int tiles, float featureScale = 1f)
        {
            var result = new float[tiles * tiles][];
            float min = float.MaxValue, max = float.MinValue;
            for (int tz = 0; tz < tiles; ++tz)
                for (int tx = 0; tx < tiles; ++tx)
                {
                    var h = RawTile(resolution, seed, featureScale, tx, tz);
                    foreach (float v in h)
                    {
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                    }
                    result[tz * tiles + tx] = h;
                }
            foreach (var h in result)
                Normalise(h, min, max);
            return result;
        }

        /// <summary>A few round holes (cave entrances) as a per-quad hole mask (true = hole).</summary>
        public static bool[] Holes(int resolution, int seed, int count, float radiusQuads)
        {
            int q = resolution - 1;
            var holes = new bool[q * q];
            var rng = new System.Random(seed * 7919 + 1);
            for (int k = 0; k < count; ++k)
            {
                float cx = (float)rng.NextDouble() * q, cz = (float)rng.NextDouble() * q;
                int r = Mathf.CeilToInt(radiusQuads);
                for (int z = Mathf.Max(0, (int)cz - r); z < Mathf.Min(q, (int)cz + r + 1); ++z)
                    for (int x = Mathf.Max(0, (int)cx - r); x < Mathf.Min(q, (int)cx + r + 1); ++x)
                        if ((x + 0.5f - cx) * (x + 0.5f - cx) + (z + 0.5f - cz) * (z + 0.5f - cz) < radiusQuads * radiusQuads)
                            holes[z * q + x] = true;
            }
            return holes;
        }

        public static VgTerrainSource Source(int resolution, Vector3 size, int seed, int holeCount = 0, float holeRadius = 6f, float featureScale = 1f)
        {
            return new VgTerrainSource
            {
                resolution = resolution,
                size = size,
                heights = Heights(resolution, seed, featureScale),
                holes = holeCount > 0 ? Holes(resolution, seed, holeCount, holeRadius) : null,
            };
        }
    }
}
