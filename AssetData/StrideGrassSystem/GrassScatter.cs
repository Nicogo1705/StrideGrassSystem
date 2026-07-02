using Stride.Core.Mathematics;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace StrideGrassSystem;

/// <summary>
/// Helpers that turn a surface into a list of <see cref="GrassSeed"/>.
/// These are pure CPU utilities with no Stride-scene dependency, so you can
/// scatter grass over anything: a flat plane, a procedural heightmap, a mesh,
/// or a voxel terrain. Feed the result to <see cref="GrassRenderer.SetSeeds"/>
/// (static field) or <see cref="GrassRenderer.SetChunkSeeds"/> (streamed world).
/// </summary>
public static class GrassScatter
{
    /// <summary>
    /// Scatters one seed per <paramref name="cellSize"/> cell over an axis-aligned
    /// rectangle centered on <paramref name="center"/> (XZ plane).
    /// </summary>
    /// <param name="heightSampler">
    /// Optional. Given world (x, z) it returns the ground Y at which the blade
    /// should sit, or <c>null</c> to skip that cell (no grass there — e.g. off
    /// the terrain, on rock, under water…). When omitted, every cell sits at
    /// <c>center.Y</c> (a flat field).
    /// </param>
    public static GrassSeed[] OverArea(
        Vector3 center, float sizeX, float sizeZ, float cellSize = 1.0f,
        Func<float, float, float?>? heightSampler = null, int seed = 0)
    {
        cellSize = MathF.Max(cellSize, 0.05f);
        int nx = Math.Max(1, (int)(sizeX / cellSize));
        int nz = Math.Max(1, (int)(sizeZ / cellSize));

        float startX = center.X - sizeX * 0.5f;
        float startZ = center.Z - sizeZ * 0.5f;

        var result = new List<GrassSeed>(nx * nz);
        for (int ix = 0; ix < nx; ix++)
        {
            float wx = startX + (ix + 0.5f) * cellSize;
            for (int iz = 0; iz < nz; iz++)
            {
                float wz = startZ + (iz + 0.5f) * cellSize;

                float wy;
                if (heightSampler != null)
                {
                    float? y = heightSampler(wx, wz);
                    if (y is null) continue;      // no grass in this cell
                    wy = y.Value;
                }
                else
                {
                    wy = center.Y;
                }

                result.Add(new GrassSeed(new Vector3(wx, wy, wz), HashCell(ix, iz, seed)));
            }
        }
        return result.ToArray();
    }

    /// <summary>Deterministic per-cell hash (same mix the GPU uses to seed jitter).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HashCell(int x, int z, int seed = 0)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093 ^ z * 19349663 ^ seed * 83492791 ^ 0x47524153);
            h ^= h >> 16;
            h *= 0x45d9f3bu;
            h ^= h >> 16;
            return h;
        }
    }
}
