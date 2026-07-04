using System;
using System.IO;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;

namespace Demo.Windows
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var game = new Game())
            {
                // Scripted showcase-video camera (set RECORD_VARIANT=1|2|3). No effect otherwise.
                if (int.TryParse(Environment.GetEnvironmentVariable("RECORD_VARIANT"), out var variant))
                {
                    game.WindowCreated += (_, _) => game.Window.Title = "StrideDemoRecord";
                    game.Script.AddTask(() => DriveCameraAsync(game, variant));
                }

                // FPS benchmark for the README's performance table (GRASS_BENCH=1, optional
                // GRASS_AREA/GRASS_CELL overrides, result appended to GRASS_BENCH_OUT).
                if (Environment.GetEnvironmentVariable("GRASS_BENCH") == "1")
                {
                    if (game.Services.GetService<Stride.Games.IGraphicsDeviceManager>() is GraphicsDeviceManager gdm)
                    {
                        gdm.SynchronizeWithVerticalRetrace = false; // measure the GPU, not the monitor
                    }

                    game.Script.AddTask(() => BenchAsync(game));
                }

                game.Run();
            }
        }

        private static async Task BenchAsync(Game game)
        {
            Entity? camera = null;
            while (camera is null)
            {
                await game.Script.NextFrame();
                var scene = game.SceneSystem.SceneInstance?.RootScene;
                if (scene is null)
                {
                    continue;
                }

                foreach (var entity in scene.Entities)
                {
                    if (entity.Get<CameraComponent>() is not null) camera = entity;
                }
            }

            if (camera.Get<BasicCameraController>() is { } controller)
            {
                camera.Remove(controller);
            }

            game.Window.SetSize(new Int2(1920, 1080)); // bench at 1080p (matches the README table)

            // Same fixed grass-filled view for every config, so numbers are comparable.
            camera.Transform.Position = new Vector3(50f, 12f, 0f);
            var dir = Vector3.Normalize(new Vector3(-70f, -10f, 0f));
            camera.Transform.Rotation = Quaternion.RotationYawPitchRoll(
                MathF.Atan2(-dir.X, -dir.Z), MathF.Asin(dir.Y), 0f);

            const float warmupSeconds = 15f;  // shader compile + steady state
            const float measureSeconds = 15f;
            var frames = 0;

            while (game.IsRunning)
            {
                await game.Script.NextFrame();
                var total = (float)game.UpdateTime.Total.TotalSeconds;
                if (total <= warmupSeconds)
                {
                    continue;
                }

                if (total <= warmupSeconds + measureSeconds)
                {
                    frames++;
                    continue;
                }

                var fps = frames / measureSeconds;
                var output = Environment.GetEnvironmentVariable("GRASS_BENCH_OUT") ?? "bench.txt";
                File.AppendAllText(output,
                    $"area={Environment.GetEnvironmentVariable("GRASS_AREA") ?? "100"};cell={Environment.GetEnvironmentVariable("GRASS_CELL") ?? "0.5"};fps={fps:F0}\n");
                game.Exit();
                return;
            }
        }

        // Same terrain function as GrassShowcase.Height — keeps the scripted camera above ground.
        private static float Height(float x, float z)
            => x * 0.12f
             + 3.5f * MathF.Sin(x * 0.06f) * MathF.Cos(z * 0.05f)
             + 1.5f * MathF.Sin(z * 0.08f);

        private static async Task DriveCameraAsync(Game game, int variant)
        {
            // Wait for the scene, the camera and the showcase ball to exist.
            Entity? camera = null, ball = null;
            while (camera is null || ball is null)
            {
                await game.Script.NextFrame();
                var scene = game.SceneSystem.SceneInstance?.RootScene;
                if (scene is null)
                {
                    continue;
                }

                foreach (var entity in scene.Entities)
                {
                    if (entity.Get<CameraComponent>() is not null) camera = entity;
                    if (entity.Name == "Ball") ball = entity;
                }
            }

            // The fly controller would fight the scripted transform.
            if (camera.Get<BasicCameraController>() is { } controller)
            {
                camera.Remove(controller);
            }

            game.Window.SetSize(new Int2(1920, 1080)); // capture the showcase in 1080p

            var time = 0f;
            while (game.IsRunning)
            {
                time += (float)game.UpdateTime.Elapsed.TotalSeconds;
                var ballPos = ball.Transform.Position;

                Vector3 position, target;
                switch (variant)
                {
                    case 1: // chase cam: ball + the flattened trail it leaves behind (+X side)
                        position = ballPos + new Vector3(-7f, 5f, 13f);
                        target = ballPos + new Vector3(9f, -1.5f, -2f);
                        break;
                    case 2: // low in the grass, slow pan — wind up close, ball crosses the frame
                        position = new Vector3(12f, Height(12f, 14f) + 1.6f, 14f);
                        var yaw = 2.4f + 0.09f * time;
                        target = position + new Vector3(MathF.Sin(yaw), -0.38f, -MathF.Cos(yaw));
                        break;
                    case 4: // fully static close-up (wind A/B test — any pixel change IS the wind)
                        position = new Vector3(12f, Height(12f, 14f) + 1.6f, 14f);
                        target = position + new Vector3(0.6f, -0.30f, -0.75f);
                        break;
                    default: // wide slow orbit of the whole field
                        var angle = 0.09f * time;
                        position = new Vector3(46f * MathF.Cos(angle), 15f, 46f * MathF.Sin(angle));
                        target = new Vector3(0f, 1f, 0f);
                        break;
                }

                camera.Transform.Position = position;
                var dir = Vector3.Normalize(target - position);
                camera.Transform.Rotation = Quaternion.RotationYawPitchRoll(
                    MathF.Atan2(-dir.X, -dir.Z), MathF.Asin(dir.Y), 0f);

                await game.Script.NextFrame();
            }
        }
    }
}
