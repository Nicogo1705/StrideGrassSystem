using Stride.Core;
using Stride.Core.Mathematics;
using System.Runtime.InteropServices;

namespace StrideGrassSystem;

/// <summary>
/// A single grass "cell" root. One seed becomes up to <c>MaxBladesPerCell</c>
/// sub-blades on the GPU, jittered deterministically from <see cref="Variation"/>.
/// This is the only input the renderer needs — where the seeds come from
/// (a flat area, a heightmap, a voxel surface, a mesh…) is entirely up to you.
///
/// 16 bytes, laid out to match the <c>GrassSeed</c> struct in GrassCull.sdsl.
/// </summary>
[DataContract]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GrassSeed
{
    /// <summary>World-space position of the blade root.</summary>
    public Vector3 Position;

    /// <summary>Per-cell hash driving the deterministic sub-blade jitter/rotation/scale.</summary>
    public uint Variation;

    public GrassSeed(Vector3 position, uint variation)
    {
        Position = position;
        Variation = variation;
    }
}
