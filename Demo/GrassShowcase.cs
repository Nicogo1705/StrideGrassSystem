using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Input;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.ProceduralModels;
using StrideGrassSystem;
using System;
using GpuBuffer = Stride.Graphics.Buffer;

namespace Demo;

/// <summary>
/// Richer demo of the grass system: a rolling, hilly terrain (the grass follows the
/// ground via a height sampler), a growing render distance, and a ball that rolls
/// across the field on a loop and flattens a trail through the grass (trample).
///
/// It drives the lower-level <see cref="GrassRenderer"/> API directly — a good example
/// of scattering seeds over a real surface and pushing your own trample sources.
/// </summary>
public sealed class GrassShowcase : SyncScript
{
    public float AreaSize { get; set; } = 100f;
    public float CellSize { get; set; } = 0.5f;
    public float GrassDistance { get; set; } = 130f;
    public float BallRadius { get; set; } = 2.5f;
    public float BallSpeed { get; set; } = 10f;

    /// <summary>Camera used for LOD/fade (wire it in the scene). Falls back to this entity.</summary>
    public CameraComponent? Camera { get; set; }

    private GrassRenderer? _grass;
    private Entity? _ball;
    private float _ballDist;
    private float _loopLength;

    // Terrain height: a slope along +X plus gentle rolling hills.
    private static float Height(float x, float z)
        => x * 0.12f
         + 3.5f * MathF.Sin(x * 0.06f) * MathF.Cos(z * 0.05f)
         + 1.5f * MathF.Sin(z * 0.08f);

    public override void Start()
    {
        // Benchmark overrides (see Demo.Windows/Program.cs GRASS_BENCH mode) — inert otherwise.
        if (float.TryParse(Environment.GetEnvironmentVariable("GRASS_AREA"), out var areaOverride))
        {
            AreaSize = areaOverride;
        }

        if (float.TryParse(Environment.GetEnvironmentVariable("GRASS_CELL"), out var cellOverride))
        {
            CellSize = cellOverride;
        }

        BuildGround();
        BuildBall();

        // Bench override: the instance buffer must cover seeds × 32 sub-blades, or only a
        // stripe of a big field gets grass (the buffer fills in seed order).
        var maxInstances = 1_500_000;
        if (int.TryParse(Environment.GetEnvironmentVariable("GRASS_MAXINSTANCES"), out var maxOverride))
        {
            maxInstances = maxOverride;
        }

        _grass = new GrassRenderer(Services, GraphicsDevice, maxInstances);
        _grass.SetGrassDistance(GrassDistance);
        _grass.SetLodParams(1.2f, 1.5f);
        _grass.SetColorScale(0.85f); // slightly tone down the vivid green
        _grass.SetWind(0.2f, 1.6f, 0.8f); // a touch above the defaults — visible breeze, not a storm

        var origin = Entity.Transform.WorldMatrix.TranslationVector;
        var seeds = GrassScatter.OverArea(origin, AreaSize, AreaSize, CellSize, (x, z) => Height(x, z));
        _grass.SetSeeds(seeds);
        Entity.Scene.Entities.Add(_grass.GrassEntity);

        _loopLength = AreaSize;
    }

    private bool _showFps;

    public override void Update()
    {
        if (_grass == null) return;
        float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;

        // FPS overlay — Ctrl+Shift+P to toggle.
        if ((Input.IsKeyDown(Keys.LeftCtrl) || Input.IsKeyDown(Keys.RightCtrl))
            && (Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift))
            && Input.IsKeyPressed(Keys.P))
        {
            _showFps = !_showFps;
        }

        if (_showFps)
        {
            DebugText.Print($"{Game.UpdateTime.FramePerSecond:F0} FPS", new Int2(12, 12));
        }

        // Roll the ball across the field (downhill along -X), looping.
        _ballDist += BallSpeed * dt;
        if (_ballDist > _loopLength) _ballDist -= _loopLength;
        float bx = AreaSize * 0.5f - _ballDist;
        float bz = 8f * MathF.Sin(_ballDist * 0.06f);
        float groundY = Height(bx, bz);

        _ball!.Transform.Position = new Vector3(bx, groundY + BallRadius, bz);
        _ball.Transform.Rotation = Quaternion.RotationZ(-_ballDist / BallRadius); // rolling look

        Vector3 camPos = Camera?.Entity?.Transform.WorldMatrix.TranslationVector
                         ?? Entity.Transform.WorldMatrix.TranslationVector;

        _grass.ClearTrampleSources();
        // Crush the grass in a circle under the ball (trample is sampled in world XZ).
        _grass.AddTrampleSource(new Vector3(bx, groundY, bz), BallRadius * 1.2f);
        _grass.Update(camPos, (Game)Game);
    }

    private void BuildGround()
    {
        const int res = 80;
        float half = AreaSize * 0.5f;
        float step = AreaSize / res;

        var verts = new VertexPositionNormalTexture[(res + 1) * (res + 1)];
        for (int i = 0; i <= res; i++)
        {
            for (int j = 0; j <= res; j++)
            {
                float x = -half + i * step;
                float z = -half + j * step;
                float y = Height(x, z);
                // Normal from the height gradient (finite differences).
                float hL = Height(x - 0.5f, z), hR = Height(x + 0.5f, z);
                float hD = Height(x, z - 0.5f), hU = Height(x, z + 0.5f);
                var n = Vector3.Normalize(new Vector3(hL - hR, 1f, hD - hU));
                verts[i * (res + 1) + j] = new VertexPositionNormalTexture(
                    new Vector3(x, y, z), n, new Vector2(i / (float)res, j / (float)res));
            }
        }

        var indices = new int[res * res * 6];
        int k = 0;
        for (int i = 0; i < res; i++)
        {
            for (int j = 0; j < res; j++)
            {
                int a = i * (res + 1) + j;
                int b = (i + 1) * (res + 1) + j;
                int c = (i + 1) * (res + 1) + j + 1;
                int d = i * (res + 1) + j + 1;
                indices[k++] = a; indices[k++] = b; indices[k++] = c;
                indices[k++] = a; indices[k++] = c; indices[k++] = d;
            }
        }

        var vbo = GpuBuffer.Vertex.New(GraphicsDevice, verts, GraphicsResourceUsage.Immutable);
        var ibo = GpuBuffer.Index.New(GraphicsDevice, indices, GraphicsResourceUsage.Immutable);
        var meshDraw = new MeshDraw
        {
            PrimitiveType = PrimitiveType.TriangleList,
            VertexBuffers = new[] { new VertexBufferBinding(vbo, VertexPositionNormalTexture.Layout, verts.Length) },
            IndexBuffer = new IndexBufferBinding(ibo, true, indices.Length),
            DrawCount = indices.Length,
        };

        var mat = Material.New(GraphicsDevice, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0.22f, 0.40f, 0.16f, 1f))),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
            }
        });
        mat.Passes[0].CullMode = CullMode.None; // double-sided: never disappears from any angle

        var model = new Model();
        model.Add(new MaterialInstance(mat));
        model.Add(new Mesh
        {
            Draw = meshDraw,
            MaterialIndex = 0,
            BoundingBox = new BoundingBox(new Vector3(-half, -50, -half), new Vector3(half, 50, half)),
        });

        var e = new Entity("ShowcaseGround");
        e.Add(new ModelComponent { Model = model });
        Entity.Scene.Entities.Add(e);
    }

    private void BuildBall()
    {
        var model = new SphereProceduralModel { Radius = BallRadius, Tessellation = 24 }.Generate(Services);

        var mat = Material.New(GraphicsDevice, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0.90f, 0.35f, 0.10f, 1f))),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
            }
        });
        if (model.Materials.Count > 0) model.Materials[0] = new MaterialInstance(mat);
        else model.Add(new MaterialInstance(mat));

        _ball = new Entity("Ball");
        _ball.Add(new ModelComponent { Model = model });
        Entity.Scene.Entities.Add(_ball);
    }

    public override void Cancel()
    {
        _grass?.Dispose();
        _grass = null;
    }
}
