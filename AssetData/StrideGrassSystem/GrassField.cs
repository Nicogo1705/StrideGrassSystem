using Stride.Core.Mathematics;
using Stride.Engine;

namespace StrideGrassSystem;

/// <summary>
/// Drop-in grass: attach this to an entity, press play, and a GPU grass field
/// appears centred on that entity. Every knob below is editable in Game Studio.
///
/// It owns a <see cref="GrassRenderer"/> internally and drives it each frame
/// with the camera position (for LOD/fade/wind) and a trample source (so the
/// player flattens a trail through the grass). For a heightmapped or voxel
/// world, ignore this component and drive <see cref="GrassRenderer"/> yourself
/// with your own seeds.
/// </summary>
[ComponentCategory("Grass")]
public sealed class GrassField : SyncScript
{
    /// <summary>Side length of the (square) grass field, in metres, centred on this entity.</summary>
    public float AreaSize { get; set; } = 60f;

    /// <summary>Distance between seeds. Smaller = denser (and more instances). Metres.</summary>
    public float CellSize { get; set; } = 1.0f;

    /// <summary>Render/fade radius around the camera, in metres.</summary>
    public float GrassDistance { get; set; } = 60f;

    /// <summary>LOD blade-count multiplier (higher = more blades further out).</summary>
    public float LodMultiplier { get; set; } = 1.0f;

    /// <summary>LOD falloff exponent (higher = blades thin out faster with distance).</summary>
    public float LodExponent { get; set; } = 1.5f;

    /// <summary>Maximum blade-instance budget (memory = value × 64 bytes on the GPU).</summary>
    public int MaxInstances { get; set; } = 1_000_000;

    /// <summary>Wind sway amplitude.</summary>
    public float WindStrength { get; set; } = 0.15f;

    /// <summary>Wind animation speed.</summary>
    public float WindSpeed { get; set; } = 1.5f;

    /// <summary>Spatial frequency of the wind gusts.</summary>
    public float WindFrequency { get; set; } = 0.8f;

    /// <summary>Overall blade brightness (1 = day). Drive this from a day/night cycle if you have one.</summary>
    public float ColorScale { get; set; } = 1.0f;

    /// <summary>Radius of the trail flattened around the trample target, in metres.</summary>
    public float TrampleRadius { get; set; } = 1.5f;

    /// <summary>
    /// Camera used for LOD/fade. If left empty the field falls back to this
    /// entity's position (fine for a fixed showcase, but LOD won't follow the view).
    /// </summary>
    public CameraComponent? Camera { get; set; }

    /// <summary>
    /// Entity that flattens the grass as it moves (usually the player). If empty,
    /// the camera (or this entity) is used.
    /// </summary>
    public Entity? TrampleTarget { get; set; }

    private GrassRenderer? _renderer;

    public override void Start()
    {
        _renderer = new GrassRenderer(Services, GraphicsDevice, MaxInstances);
        _renderer.SetGrassDistance(GrassDistance);
        _renderer.SetLodParams(LodMultiplier, LodExponent);
        _renderer.SetColorScale(ColorScale);
        _renderer.SetWind(WindStrength, WindSpeed, WindFrequency);

        var origin = Entity.Transform.WorldMatrix.TranslationVector;
        var seeds = GrassScatter.OverArea(origin, AreaSize, AreaSize, CellSize);
        _renderer.SetSeeds(seeds);

        Entity.Scene.Entities.Add(_renderer.GrassEntity);
    }

    public override void Update()
    {
        if (_renderer == null) return;

        Vector3 cameraPos = Camera?.Entity?.Transform.WorldMatrix.TranslationVector
                            ?? Entity.Transform.WorldMatrix.TranslationVector;

        var trampleEntity = TrampleTarget ?? Camera?.Entity ?? Entity;
        Vector3 tramplePos = trampleEntity.Transform.WorldMatrix.TranslationVector;

        _renderer.ClearTrampleSources();
        _renderer.AddTrampleSource(tramplePos, TrampleRadius);
        _renderer.Update(cameraPos, (Game)Game);
    }

    public override void Cancel()
    {
        _renderer?.Dispose();
        _renderer = null;
    }
}
