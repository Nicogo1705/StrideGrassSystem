using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GpuBuffer = Stride.Graphics.Buffer;

namespace StrideGrassSystem;

/// <summary>
/// GPU-driven grass renderer, fully decoupled from any terrain system.
///
/// You feed it a set of <see cref="GrassSeed"/> (one per grass cell) via
/// <see cref="SetSeeds"/> (a static field) or <see cref="SetChunkSeeds"/> /
/// <see cref="RemoveChunk"/> (a streamed, chunked world). Every frame a single
/// compute dispatch (<c>GrassCull</c>) reads the seed buffer and writes the
/// world matrices for SeedCount × <see cref="MaxBladesPerCell"/> blade instances
/// into the buffer backing an <see cref="InstancingUserBuffer"/>. Sub-blades
/// beyond the current LOD count are written as degenerate (zero) matrices, so
/// the rasterizer culls those triangles for free — no indirect draws.
///
/// A second compute pass (<c>GrassTrampleUpdate</c>) maintains a scrolling R8
/// "trample field": add sources each frame (<see cref="AddTrampleSource"/>) and
/// blades near them bend away and flatten, recovering after a few seconds.
///
/// CPU per-frame cost is ~constant (one seed upload when dirty + two dispatches);
/// camera movement does no per-blade work.
///
/// This class owns its own <see cref="Entity"/> (<see cref="GrassEntity"/>) —
/// add it to a scene once. It is <see cref="IDisposable"/>.
/// </summary>
public sealed class GrassRenderer : IDisposable
{
    private const float FadeRatio = 0.8f;

    // Default output-matrix budget. Memory = MaxInstances × 64 B.
    // 1M ≈ 64 MB, a safe default on any dGPU; raise it for dense open worlds.
    public const int DefaultMaxInstances = 1_000_000;

    // Upper bound on sub-blades per grass cell. Each seed reserves this many
    // matrix slots in the output buffer, so this trades blade density near the
    // camera against the seed budget at a given MaxInstances cap.
    private const int MaxBladesPerCell = 32;

    // Camera-move threshold beyond which the GPU bounding box is recentered
    // (frustum culling only — the compute pass re-evaluates LOD every frame).
    private const float BBoxRecenterDistSq = 256f * 256f;

    // ── Runtime-string ParameterKeys ────────────────────────────────────────
    // Every shader parameter is bound by name so the library needs no generated
    // *Keys classes. Names must match the SDSL variable identifiers exactly.
    private static readonly ObjectParameterKey<GpuBuffer?> KeySeeds            = ParameterKeys.NewObject<GpuBuffer?>(null, "GrassCull.Seeds");
    private static readonly ObjectParameterKey<GpuBuffer?> KeyOutWorld         = ParameterKeys.NewObject<GpuBuffer?>(null, "GrassCull.OutWorld");
    private static readonly ValueParameterKey<int>         KeySeedCount        = ParameterKeys.NewValue<int>(0, "GrassCull.SeedCount");
    private static readonly ValueParameterKey<int>         KeyMaxBladesPerCell = ParameterKeys.NewValue<int>(0, "GrassCull.MaxBladesPerCell");
    private static readonly ValueParameterKey<Vector3>     KeyCameraPos        = ParameterKeys.NewValue<Vector3>(default, "GrassCull.CameraPos");
    private static readonly ValueParameterKey<float>       KeyGrassRadius      = ParameterKeys.NewValue<float>(0f, "GrassCull.GrassRadius");
    private static readonly ValueParameterKey<float>       KeyGrassRadiusSq    = ParameterKeys.NewValue<float>(0f, "GrassCull.GrassRadiusSq");
    private static readonly ValueParameterKey<float>       KeyFadeStart        = ParameterKeys.NewValue<float>(0f, "GrassCull.FadeStart");
    private static readonly ValueParameterKey<float>       KeyFadeStartSq      = ParameterKeys.NewValue<float>(0f, "GrassCull.FadeStartSq");
    private static readonly ValueParameterKey<float>       KeyFadeRange        = ParameterKeys.NewValue<float>(0f, "GrassCull.FadeRange");
    private static readonly ValueParameterKey<float>       KeyBaseBladesPerCell = ParameterKeys.NewValue<float>(0f, "GrassCull.BaseBladesPerCell");
    private static readonly ValueParameterKey<float>       KeyLodMultiplier    = ParameterKeys.NewValue<float>(0f, "GrassCull.LodMultiplier");
    private static readonly ValueParameterKey<float>       KeyLodExponent      = ParameterKeys.NewValue<float>(0f, "GrassCull.LodExponent");
    // GrassCull trample field bindings (sampling side).
    private static readonly ObjectParameterKey<Texture?>      KeyTrampleField        = ParameterKeys.NewObject<Texture?>(null, "GrassCull.TrampleField");
    private static readonly ObjectParameterKey<SamplerState?> KeyTrampleSampler      = ParameterKeys.NewObject<SamplerState?>(null, "GrassCull.TrampleSampler");
    private static readonly ValueParameterKey<Vector2>        KeyTrampleOriginWorld  = ParameterKeys.NewValue<Vector2>(default, "GrassCull.TrampleOriginWorld");
    private static readonly ValueParameterKey<float>          KeyTrampleTexelSize    = ParameterKeys.NewValue<float>(0f, "GrassCull.TrampleTexelSize");
    private static readonly ValueParameterKey<uint>           KeyTrampleTextureSize  = ParameterKeys.NewValue<uint>(0u, "GrassCull.TrampleTextureSize");
    private static readonly ValueParameterKey<Int2>           KeyTrampleOffset       = ParameterKeys.NewValue<Int2>(default, "GrassCull.TrampleOffset");

    // GrassTrampleUpdate bindings (writing side).
    private static readonly ObjectParameterKey<Texture?>   KeyTUField       = ParameterKeys.NewObject<Texture?>(null, "GrassTrampleUpdate.Field");
    private static readonly ObjectParameterKey<GpuBuffer?> KeyTUSources     = ParameterKeys.NewObject<GpuBuffer?>(null, "GrassTrampleUpdate.Sources");
    private static readonly ValueParameterKey<int>         KeyTUSourceCount  = ParameterKeys.NewValue<int>(0, "GrassTrampleUpdate.SourceCount");
    private static readonly ValueParameterKey<float>       KeyTUDecay        = ParameterKeys.NewValue<float>(0f, "GrassTrampleUpdate.DecayPerFrame");
    private static readonly ValueParameterKey<Vector2>     KeyTUOriginWorld  = ParameterKeys.NewValue<Vector2>(default, "GrassTrampleUpdate.OriginWorld");
    private static readonly ValueParameterKey<float>       KeyTUTexelSize    = ParameterKeys.NewValue<float>(0f, "GrassTrampleUpdate.TexelWorldSize");
    private static readonly ValueParameterKey<uint>        KeyTUTextureSize  = ParameterKeys.NewValue<uint>(0u, "GrassTrampleUpdate.TextureSize");
    private static readonly ValueParameterKey<Int2>        KeyTUOffset       = ParameterKeys.NewValue<Int2>(default, "GrassTrampleUpdate.Offset");

    // Material shader keys (bound by name — no generated GrassDiffuseKeys needed).
    private static readonly ValueParameterKey<float> KeyGrassColorScale = ParameterKeys.NewValue<float>(1f, "GrassDiffuse.GrassColorScale");
    private static readonly ValueParameterKey<float> KeyWindStrength    = ParameterKeys.NewValue<float>(0.15f, "GrassWind.WindStrength");
    private static readonly ValueParameterKey<float> KeyWindSpeed       = ParameterKeys.NewValue<float>(1.5f, "GrassWind.WindSpeed");
    private static readonly ValueParameterKey<float> KeyWindFrequency   = ParameterKeys.NewValue<float>(0.8f, "GrassWind.WindFrequency");

    // LOD / distance settings.
    private float _lodMultiplier = 1.0f;
    private float _lodExponent = 1.5f;
    private float _grassRadius = 60f;
    private float _grassRadiusSq = 60f * 60f;
    private float _fadeStart = 48f;
    private float _fadeRange = 12f;
    private float _fadeStartSq = 48f * 48f;

    private readonly GraphicsDevice _graphicsDevice;
    private readonly Entity _grassEntity;
    private readonly InstancingUserBuffer _instancing;
    private readonly Material _material;
    private readonly GpuBuffer _vertexBuffer;
    private readonly GpuBuffer _indexBuffer;

    private readonly ComputeEffectShader _cullShader;
    private RenderDrawContext? _drawContext;

    private int _maxInstances;
    private int _maxSeeds;                    // _maxInstances / MaxBladesPerCell
    private GpuBuffer? _seedBuffer;           // Buffer<GrassSeed>
    private GpuBuffer? _outWorldBuffer;       // Buffer<Matrix>, UAV+SRV, bound to instancing
    private GrassSeed[] _seedStaging = Array.Empty<GrassSeed>();

    private Vector3 _lastBBoxPos = new(float.MaxValue, 0, float.MaxValue);

    // ── Seed sources ────────────────────────────────────────────────────────
    // Seeds are grouped by an arbitrary chunk key so a streamed world can add
    // and drop regions without touching the rest. SetSeeds() uses a single
    // implicit chunk for the simple, static-field case.
    private readonly Dictionary<Int3, GrassSeed[]> _chunks = new();
    private int _liveSeedCount;
    private bool _layoutDirty;                // seed set changed → repack staging
    private bool _gpuSeedDirty;               // staging changed → re-upload to GPU

    // ── Trample field (R8 texture sampled by GrassCull) ─────────────────────
    private const int   TrampleTextureSize    = 256;
    private const float TrampleTexelWorldSize = 0.25f;   // 256 × 0.25 = 64 m window
    private const int   MaxTrampleSources     = 64;
    private const float TrampleDecayPerSecond = 0.4f;    // ~2.5 s to fully recover

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TrampleSource
    {
        public Vector3 Position;
        public float   Radius;
    }

    private Texture?             _trampleTexture;
    private SamplerState?        _trampleSampler;
    private GpuBuffer?           _trampleSourceBuffer;
    private readonly TrampleSource[] _trampleSourceStaging = new TrampleSource[MaxTrampleSources];
    private int                  _trampleSourceCount;
    private ComputeEffectShader? _trampleShader;
    private Vector2              _trampleOriginWorld;
    private Int2                 _trampleOffset;
    private bool                 _trampleInitialized;

    public Entity GrassEntity => _grassEntity;

    public GrassRenderer(IServiceRegistry services, GraphicsDevice graphicsDevice, int maxInstances = DefaultMaxInstances)
    {
        _graphicsDevice = graphicsDevice;
        _maxInstances = Math.Max(1000, maxInstances);
        _maxSeeds = Math.Max(64, _maxInstances / MaxBladesPerCell);

        // --- Cross-billboard mesh: 2 quads at 90° (8 vertices, 4 triangles) ---
        var vertices = new GrassVertex[]
        {
            new(new Vector3(-0.5f, 0f, 0f), Vector3.UnitZ, new Vector2(0f, 1f)),
            new(new Vector3( 0.5f, 0f, 0f), Vector3.UnitZ, new Vector2(1f, 1f)),
            new(new Vector3( 0.5f, 1f, 0f), Vector3.UnitZ, new Vector2(1f, 0f)),
            new(new Vector3(-0.5f, 1f, 0f), Vector3.UnitZ, new Vector2(0f, 0f)),
            new(new Vector3(0f, 0f, -0.5f), Vector3.UnitX, new Vector2(0f, 1f)),
            new(new Vector3(0f, 0f,  0.5f), Vector3.UnitX, new Vector2(1f, 1f)),
            new(new Vector3(0f, 1f,  0.5f), Vector3.UnitX, new Vector2(1f, 0f)),
            new(new Vector3(0f, 1f, -0.5f), Vector3.UnitX, new Vector2(0f, 0f)),
        };
        var indices = new ushort[]
        {
            0, 1, 2, 0, 2, 3,
            4, 5, 6, 4, 6, 7,
        };

        _vertexBuffer = GpuBuffer.Vertex.New(graphicsDevice, vertices, GraphicsResourceUsage.Immutable);
        _indexBuffer  = GpuBuffer.Index.New(graphicsDevice, indices,  GraphicsResourceUsage.Immutable);

        var meshDraw = new MeshDraw
        {
            PrimitiveType = PrimitiveType.TriangleList,
            VertexBuffers = new[] { new VertexBufferBinding(_vertexBuffer, GrassVertex.Layout, 8) },
            IndexBuffer   = new IndexBufferBinding(_indexBuffer, false, 12),
            DrawCount     = 12,
        };

        var grassColor = new ComputeShaderClassColor { MixinReference = "GrassDiffuse" };
        var descriptor = new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(grassColor),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                Displacement = new MaterialDisplacementMapFeature(
                    new ComputeShaderClassScalar { MixinReference = "GrassWind" })
                {
                    Stage = DisplacementMapStage.Vertex,
                    ScaleAndBias = false,
                    Intensity = new ComputeFloat(1.0f),
                },
                Transparency = new MaterialTransparencyCutoffFeature
                {
                    Alpha = new ComputeFloat(0.3f),
                },
            }
        };
        _material = Material.New(graphicsDevice, descriptor);
        _material.Passes[0].CullMode = CullMode.None;
        _material.Passes[0].Parameters.Set(KeyGrassColorScale, 1.0f);
        _material.Passes[0].Parameters.Set(KeyWindStrength, 0.15f);
        _material.Passes[0].Parameters.Set(KeyWindSpeed, 1.5f);
        _material.Passes[0].Parameters.Set(KeyWindFrequency, 0.8f);

        var largeBBox = new BoundingBox(
            new Vector3(-1000f, -500f, -1000f),
            new Vector3(1000f, 500f, 1000f));

        var model = new Model();
        model.Add(new MaterialInstance(_material));
        model.Add(new Mesh
        {
            Draw           = meshDraw,
            MaterialIndex  = 0,
            BoundingBox    = largeBBox,
            BoundingSphere = new BoundingSphere(Vector3.Zero, 1500f),
        });

        AllocateGpuBuffers();

        _instancing = new InstancingUserBuffer
        {
            BoundingBox = largeBBox,
            InstanceCount = 0,
            InstanceWorldBuffer = _outWorldBuffer,
            // Alias world/world-inverse: grass is alpha-cutoff foliage with no
            // meaningful use of the inverse, so we skip the second buffer.
            InstanceWorldInverseBuffer = _outWorldBuffer,
        };

        _grassEntity = new Entity("GrassPool");
        _grassEntity.Add(new ModelComponent
        {
            Model = model,
            IsShadowCaster = false,
        });
        _grassEntity.Add(new InstancingComponent { Type = _instancing });

        var renderContext = RenderContext.GetShared(services);
        _cullShader = new ComputeEffectShader(renderContext)
        {
            ShaderSourceName = "GrassCull",
            ThreadNumbers    = new Int3(64, 1, 1),
        };
        _trampleShader = new ComputeEffectShader(renderContext)
        {
            ShaderSourceName = "GrassTrampleUpdate",
            ThreadNumbers    = new Int3(8, 8, 1),
        };

        AllocateTrampleResources();
    }

    // ── Public configuration ────────────────────────────────────────────────

    /// <summary>Set the outer render radius (metres). Blades fade out over the last 20%.</summary>
    public void SetGrassDistance(float radius)
    {
        radius = MathF.Max(radius, 1f);
        if (MathF.Abs(radius - _grassRadius) < 0.5f) return;

        _grassRadius = radius;
        _grassRadiusSq = radius * radius;
        _fadeStart = radius * FadeRatio;
        _fadeRange = MathF.Max(radius - _fadeStart, 0.01f);
        _fadeStartSq = _fadeStart * _fadeStart;
    }

    /// <summary>LOD curve: higher <paramref name="multiplier"/> = more blades; higher <paramref name="exponent"/> = faster falloff.</summary>
    public void SetLodParams(float multiplier, float exponent)
    {
        _lodMultiplier = MathF.Max(multiplier, 0f);
        _lodExponent   = MathF.Max(exponent, 0.1f);
    }

    /// <summary>Day/night dimming applied to the blade color (1 = day, ~0.25 = night).</summary>
    public void SetColorScale(float scale) => _material.Passes[0].Parameters.Set(KeyGrassColorScale, scale);

    /// <summary>Wind sway parameters (world-space).</summary>
    public void SetWind(float strength, float speed, float frequency)
    {
        _material.Passes[0].Parameters.Set(KeyWindStrength, strength);
        _material.Passes[0].Parameters.Set(KeyWindSpeed, speed);
        _material.Passes[0].Parameters.Set(KeyWindFrequency, frequency);
    }

    /// <summary>Resize the per-frame instance budget (metres² of dense grass you can afford).</summary>
    public void SetMaxInstances(int maxInstances)
    {
        maxInstances = Math.Max(1000, maxInstances);
        if (maxInstances == _maxInstances) return;
        _maxInstances = maxInstances;
        _maxSeeds = Math.Max(64, _maxInstances / MaxBladesPerCell);
        AllocateGpuBuffers();
        _instancing.InstanceWorldBuffer = _outWorldBuffer;
        _instancing.InstanceWorldInverseBuffer = _outWorldBuffer;
        _layoutDirty = true;
    }

    // ── Seed input ───────────────────────────────────────────────────────────

    /// <summary>Replace the entire grass field with <paramref name="seeds"/> (static/finite case).</summary>
    public void SetSeeds(ReadOnlySpan<GrassSeed> seeds)
    {
        _chunks.Clear();
        if (seeds.Length > 0) _chunks[default] = seeds.ToArray();
        _layoutDirty = true;
    }

    /// <summary>Add or replace the seeds for one streamed chunk. Pass an empty span to clear it.</summary>
    public void SetChunkSeeds(Int3 key, ReadOnlySpan<GrassSeed> seeds)
    {
        if (seeds.Length == 0) _chunks.Remove(key);
        else _chunks[key] = seeds.ToArray();
        _layoutDirty = true;
    }

    /// <summary>Drop a streamed chunk's seeds (region left view distance).</summary>
    public void RemoveChunk(Int3 key)
    {
        if (_chunks.Remove(key)) _layoutDirty = true;
    }

    /// <summary>Remove all seeds.</summary>
    public void ClearSeeds()
    {
        if (_chunks.Count > 0) { _chunks.Clear(); _layoutDirty = true; }
    }

    // ── Trample sources ──────────────────────────────────────────────────────

    /// <summary>Clear the trample source list. Call once per frame before pushing sources.</summary>
    public void ClearTrampleSources() => _trampleSourceCount = 0;

    /// <summary>Add a trample source (player/enemy foot position). Radius in metres. Up to 64 per frame.</summary>
    public void AddTrampleSource(Vector3 worldPosition, float radius)
    {
        if (_trampleSourceCount >= MaxTrampleSources) return;
        _trampleSourceStaging[_trampleSourceCount++] = new TrampleSource
        {
            Position = worldPosition,
            Radius   = radius,
        };
    }

    // ── Per-frame driver ─────────────────────────────────────────────────────

    /// <summary>
    /// Uploads any changed seeds and dispatches the trample + cull compute passes.
    /// Call once per frame with the camera (or player) world position.
    /// </summary>
    public void Update(Vector3 cameraPosition, Game game)
    {
        // 1. Repack the seed set into the flat staging array when it changed.
        if (_layoutDirty)
        {
            int total = 0;
            foreach (var arr in _chunks.Values)
            {
                int room = _maxSeeds - total;
                if (room <= 0) break;
                int n = Math.Min(arr.Length, room);
                Array.Copy(arr, 0, _seedStaging, total, n);
                total += n;
            }
            _liveSeedCount = total;
            _layoutDirty = false;
            _gpuSeedDirty = true;
        }

        // 2. Recenter the GPU bounding box periodically (frustum culling).
        float bdx = cameraPosition.X - _lastBBoxPos.X;
        float bdz = cameraPosition.Z - _lastBBoxPos.Z;
        if (bdx * bdx + bdz * bdz > BBoxRecenterDistSq)
        {
            const float bboxHalfXZ = 1000f;
            const float bboxHalfY  = 500f;
            var c = cameraPosition;
            _instancing.BoundingBox = new BoundingBox(
                new Vector3(c.X - bboxHalfXZ, c.Y - bboxHalfY, c.Z - bboxHalfXZ),
                new Vector3(c.X + bboxHalfXZ, c.Y + bboxHalfY, c.Z + bboxHalfXZ));
            _lastBBoxPos = cameraPosition;
        }

        // Nothing to draw — skip the dispatch entirely.
        if (_liveSeedCount <= 0)
        {
            _instancing.InstanceCount = 0;
            return;
        }

        _drawContext ??= EnsureDrawContext(game);
        var cl = _drawContext.CommandList;

        if (_gpuSeedDirty)
        {
            _seedBuffer!.SetData(cl, new ReadOnlySpan<GrassSeed>(_seedStaging, 0, _liveSeedCount));
            _gpuSeedDirty = false;
        }

        // 3. Trample field: advance origin + dispatch decay/stamp.
        float dt = (float)game.UpdateTime.Elapsed.TotalSeconds;
        DispatchTrampleUpdate(cameraPosition, dt);

        // 4. Cull/build pass.
        _cullShader.Parameters.Set(KeySeeds,            _seedBuffer);
        _cullShader.Parameters.Set(KeyOutWorld,         _outWorldBuffer);
        _cullShader.Parameters.Set(KeySeedCount,        _liveSeedCount);
        _cullShader.Parameters.Set(KeyMaxBladesPerCell, MaxBladesPerCell);
        _cullShader.Parameters.Set(KeyCameraPos,        cameraPosition);
        _cullShader.Parameters.Set(KeyGrassRadius,      _grassRadius);
        _cullShader.Parameters.Set(KeyGrassRadiusSq,    _grassRadiusSq);
        _cullShader.Parameters.Set(KeyFadeStart,        _fadeStart);
        _cullShader.Parameters.Set(KeyFadeStartSq,      _fadeStartSq);
        _cullShader.Parameters.Set(KeyFadeRange,        _fadeRange);
        _cullShader.Parameters.Set(KeyBaseBladesPerCell, 12f);
        _cullShader.Parameters.Set(KeyLodMultiplier,    _lodMultiplier);
        _cullShader.Parameters.Set(KeyLodExponent,      _lodExponent);
        _cullShader.Parameters.Set(KeyTrampleField,       _trampleTexture);
        _cullShader.Parameters.Set(KeyTrampleSampler,     _trampleSampler);
        _cullShader.Parameters.Set(KeyTrampleOriginWorld, _trampleOriginWorld);
        _cullShader.Parameters.Set(KeyTrampleTexelSize,   TrampleTexelWorldSize);
        _cullShader.Parameters.Set(KeyTrampleTextureSize, (uint)TrampleTextureSize);
        _cullShader.Parameters.Set(KeyTrampleOffset,      _trampleOffset);

        int groups = (_liveSeedCount + 63) / 64;
        _cullShader.ThreadGroupCounts = new Int3(groups, 1, 1);
        ((RendererBase)_cullShader).Draw(_drawContext);

        _instancing.InstanceCount = _liveSeedCount * MaxBladesPerCell;
    }

    private RenderDrawContext EnsureDrawContext(Game game)
    {
        var renderContext = RenderContext.GetShared(game.Services);
        return new RenderDrawContext(game.Services, renderContext, game.GraphicsContext);
    }

    // ── Trample compute ──────────────────────────────────────────────────────

    private void DispatchTrampleUpdate(Vector3 cameraPosition, float dt)
    {
        if (!_trampleInitialized || _trampleShader == null || _drawContext == null) return;

        // Snap the origin to texel multiples so the world→texel mapping is
        // stable across frames (avoids sub-texel jitter in the trail).
        float halfWorld    = TrampleTextureSize * TrampleTexelWorldSize * 0.5f;
        float targetXBase  = cameraPosition.X - halfWorld;
        float targetZBase  = cameraPosition.Z - halfWorld;
        int   targetTexelX = (int)MathF.Floor(targetXBase / TrampleTexelWorldSize);
        int   targetTexelZ = (int)MathF.Floor(targetZBase / TrampleTexelWorldSize);

        if (_trampleOriginWorld == Vector2.Zero && _trampleOffset.X == 0 && _trampleOffset.Y == 0)
        {
            _trampleOriginWorld = new Vector2(targetTexelX * TrampleTexelWorldSize,
                                              targetTexelZ * TrampleTexelWorldSize);
        }
        else
        {
            int currentTexelX = (int)MathF.Round(_trampleOriginWorld.X / TrampleTexelWorldSize);
            int currentTexelZ = (int)MathF.Round(_trampleOriginWorld.Y / TrampleTexelWorldSize);
            int dx = targetTexelX - currentTexelX;
            int dz = targetTexelZ - currentTexelZ;
            if (dx != 0 || dz != 0)
            {
                _trampleOffset.X += dx;
                _trampleOffset.Y += dz;
                _trampleOriginWorld = new Vector2(targetTexelX * TrampleTexelWorldSize,
                                                  targetTexelZ * TrampleTexelWorldSize);
            }
        }

        if (_trampleSourceCount > 0)
        {
            _trampleSourceBuffer!.SetData(_drawContext.CommandList,
                new ReadOnlySpan<TrampleSource>(_trampleSourceStaging, 0, _trampleSourceCount));
        }

        float decayPerFrame = TrampleDecayPerSecond * MathF.Max(dt, 0.0001f);

        _trampleShader.Parameters.Set(KeyTUField,       _trampleTexture);
        _trampleShader.Parameters.Set(KeyTUSources,     _trampleSourceBuffer);
        _trampleShader.Parameters.Set(KeyTUSourceCount, _trampleSourceCount);
        _trampleShader.Parameters.Set(KeyTUDecay,       decayPerFrame);
        _trampleShader.Parameters.Set(KeyTUOriginWorld, _trampleOriginWorld);
        _trampleShader.Parameters.Set(KeyTUTexelSize,   TrampleTexelWorldSize);
        _trampleShader.Parameters.Set(KeyTUTextureSize, (uint)TrampleTextureSize);
        _trampleShader.Parameters.Set(KeyTUOffset,      _trampleOffset);

        int groups = (TrampleTextureSize + 7) / 8;
        _trampleShader.ThreadGroupCounts = new Int3(groups, groups, 1);
        ((RendererBase)_trampleShader).Draw(_drawContext);
    }

    // ── Allocation ───────────────────────────────────────────────────────────

    private void AllocateGpuBuffers()
    {
        _seedBuffer?.Dispose();
        _outWorldBuffer?.Dispose();

        _seedBuffer     = GpuBuffer.Structured.New<GrassSeed>(_graphicsDevice, _maxSeeds,     unorderedAccess: false);
        _outWorldBuffer = GpuBuffer.Structured.New<Matrix>(_graphicsDevice,    _maxInstances, unorderedAccess: true);

        if (_seedStaging.Length < _maxSeeds)
            _seedStaging = new GrassSeed[_maxSeeds];
    }

    private void AllocateTrampleResources()
    {
        _trampleTexture = Texture.New2D(
            _graphicsDevice,
            TrampleTextureSize, TrampleTextureSize,
            PixelFormat.R8_UNorm,
            TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);

        var samplerDesc = new SamplerStateDescription
        {
            Filter   = TextureFilter.Linear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
        };
        _trampleSampler = SamplerState.New(_graphicsDevice, in samplerDesc);

        _trampleSourceBuffer = GpuBuffer.Structured.New<TrampleSource>(_graphicsDevice, MaxTrampleSources, unorderedAccess: false);
        _trampleInitialized = true;
    }

    public void Dispose()
    {
        _grassEntity.Scene?.Entities.Remove(_grassEntity);
        _seedBuffer?.Dispose();
        _outWorldBuffer?.Dispose();
        _trampleTexture?.Dispose();
        _trampleSourceBuffer?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
    }

    // ── Vertex format ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct GrassVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TexCoord;

        public static readonly VertexDeclaration Layout = new(
            VertexElement.Position<Vector3>(),
            VertexElement.Normal<Vector3>(),
            VertexElement.TextureCoordinate<Vector2>());

        public GrassVertex(Vector3 position, Vector3 normal, Vector2 texCoord)
        {
            Position = position;
            Normal   = normal;
            TexCoord = texCoord;
        }
    }
}
