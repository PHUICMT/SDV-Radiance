using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Author tool: write every buffer the pipeline produced this frame to disk as raw bytes,
    /// so two runs of the same scene can be compared away from the game (`tools/radiance-verify`).
    ///
    /// <para>
    /// This is the safety net a refactor needs. Splitting a 3,800-line pipeline is only safe if
    /// something can prove the pixels did not move, and the masks alone cannot prove that: a
    /// reordered stage, a dropped blend state or a render target that lost PreserveContents all
    /// leave every mask correct and the picture wrong. So the FRAME is dumped alongside them —
    /// the frame says whether anything broke, the masks say which stage broke it.
    /// </para>
    ///
    /// <para>
    /// Pair with <c>radiance_freeze</c>. Without it the animation clock and the eased presences
    /// keep moving, and two captures of the same spot differ for reasons that have nothing to do
    /// with the change under test. The metadata records whether the clock was frozen so the
    /// verifier can refuse to compare captures that were never comparable.
    /// </para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Name of the capture requested from the console, consumed by the next frame.</summary>
        private static string? _pendingDump;

        /// <summary>Where the last dump landed, for the console reply (the command returns before
        /// the frame that writes it).</summary>
        internal static string? LastDumpPath;

        internal static void RequestDump(string name) => _pendingDump = name;

        /// <summary>A capture is waiting for a frame. ModEntry reads this to keep the render path
        /// alive while the whole stack is switched off, which is the only way to capture the
        /// vanilla half of a comparison pair from inside the mod.</summary>
        internal static bool DumpPending => _pendingDump != null;

        /// <summary>Root for captures: Documents, for the same reason MapDump goes there — the mod
        /// folder can live under Program Files and is not reliably writable.</summary>
        private static string DumpRoot
        {
            get
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return Path.Combine(string.IsNullOrEmpty(docs) ? Path.GetTempPath() : docs, "Radiance-Dumps");
            }
        }

        /// <summary>One buffer in the capture: its bytes plus what they mean.</summary>
        private readonly record struct DumpBuffer(string Name, Texture2D Texture);

        /// <summary>
        /// Write the frame's buffers. Called at the end of <c>Apply</c> with the game's target
        /// still bound, and it hands the device back exactly as it found it.
        /// </summary>
        /// <summary>Window-sized scratch for the frame_out capture, allocated only while render
        /// scale keeps the ping buffers smaller than the window.</summary>
        private RenderTarget2D? _dumpCaptureRenderTarget;

        /// <summary>
        /// Capture the frame with the mod switched off. <c>Apply</c> returns before it reaches the
        /// normal capture when nothing is active, so a vanilla half of a comparison pair would
        /// otherwise be impossible to take from inside the mod. Everything downstream is the same
        /// path, so the two halves are read back and encoded identically: the only difference
        /// between them is the thing being demonstrated.
        /// </summary>
        private void WriteDisabledDump(SpriteBatch spriteBatch, ModConfig config)
        {
            try
            {
                RenderTargetBinding[] bindings = _device.GetRenderTargets();
                if (bindings.Length == 0 || bindings[0].RenderTarget is not RenderTarget2D target)
                    return;   // no buffer bound yet: keep the request and try the next frame
                // WriteDump blits through the full-size ping buffer, which nothing has allocated
                // this frame because the chain never ran.
                EnsureTargets(target.Width, target.Height, target.Format);
                _frameWidth = target.Width; _frameHeight = target.Height;
                WriteDump(spriteBatch, target, target.Width, target.Height, config);
                // Cleared only on the path that actually wrote. Clearing it up front loses the
                // capture on the frames before the buffer is bound, and the caller has already
                // moved on by the time anyone notices the file is missing.
                _pendingDump = null;
            }
            catch (Exception ex)
            {
                _monitor.Log($"radiance_dump (stack off): {ex.Message}", LogLevel.Warn);
                _pendingDump = null;
            }
        }

        private void WriteDump(SpriteBatch spriteBatch, RenderTarget2D target, int w, int h, ModConfig config)
        {
            string name = _pendingDump!;
            string dir = Path.Combine(DumpRoot, name);
            Directory.CreateDirectory(dir);

            // The composed frame lives in the game's own target, which cannot be read back while
            // it is bound. Blit it into a buffer we already own, then rebind — one extra
            // full-res copy, on capture frames only.
            // With render scale on, the ping buffers are SMALLER than the window: capturing
            // through them would file a shrunken frame as "what the player saw" and every
            // comparison would be measuring the wrong image. Use a full-size scratch instead.
            RenderTarget2D capture = _fullResolutionPingA!;
            if (capture.Width != w || capture.Height != h)
            {
                if (_dumpCaptureRenderTarget == null || _dumpCaptureRenderTarget.Width != w
                    || _dumpCaptureRenderTarget.Height != h || _dumpCaptureRenderTarget.Format != target.Format)
                {
                    _dumpCaptureRenderTarget?.Dispose();
                    _dumpCaptureRenderTarget = new RenderTarget2D(_device, w, h, false, target.Format, DepthFormat.None);
                }
                capture = _dumpCaptureRenderTarget;
            }
            _device.SetRenderTarget(capture);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            spriteBatch.Draw(target, new Rectangle(0, 0, w, h), Color.White);
            spriteBatch.End();
            _device.SetRenderTarget(target);

            var buffers = new List<DumpBuffer>();
            void Add(string bufName, Texture2D? texture)
            {
                if (texture != null && !texture.IsDisposed)
                    buffers.Add(new DumpBuffer(bufName, texture));
            }
            // frame_out is what the player sees; frame_in is what the game handed us. Both, because
            // a diff in frame_out with an identical frame_in is ours, and a diff in both is the
            // game's (a different save, a mod update, a cloud that moved) and the capture is void.
            Add("frame_out", capture);
            Add("frame_in", _sceneRenderTarget);
            Add("mask_water", _waterMask);
            Add("mask_water_sdf", _waterSignedDistanceTexture);
            Add("mask_water_sdf_realshore", _waterRealShoreDistanceTexture);
            Add("mask_water_churn", _waterPlungeChurnTexture);
            Add("mirror_selfdrawn_atlas", _selfDrawnMirrorAtlas);
            Add("mask_occluder", _occluderMask);
            Add("mask_sprite", _spriteMaskRenderTarget);
            // Only while it is still being BUILT. Once the cascades' cross-fade settles, the
            // pipeline stops calling _flood.Build (see the floodMapWanted gate), so this texture
            // holds whatever was in it when the fade finished, which is a different frame on every
            // launch. Dumped anyway it looked like a live buffer with 71% of its bytes moving
            // between two runs of the SAME build, which is a regression report waiting to happen.
            // A buffer nothing maintains is not evidence, and the honest way to say so is to leave
            // it out: a comparer already reports a missing side, and cannot mistake that for a diff.
            if (_cascadeBlend < 0.999f)
                Add("flood_lightmap", _flood.Texture);
            // The building shadows, as coverage, before the chain multiplies them into the
            // picture. "I cannot see the shadow" has two causes that look identical from the
            // finished frame - an empty mask, or a mask nothing applied - and this separates them.
            if (ShadowRenderer.BuildingSunShadowReady)
                Add("building_shadow_mask", ShadowRenderer.BuildingSunShadowMask);
            Add("cascade_lightmap", _cascades.Texture);
            // The cascades' inputs, because a collapsed cascade map has exactly three suspects
            // (the mask it marches, the softened copies, the emitters) and a capture that holds
            // the output without the inputs cannot say which one lied.
            Add("flood_occluder_mask", _floodOccluderMask);
            Add("flood_occluder_soft0", _floodOccluderSoft[0]);
            Add("flood_occluder_soft1", _floodOccluderSoft[1]);
            Add("flood_occluder_soft2", _floodOccluderSoft[2]);
            Add("cascade_emitters", _cascades.EmitterTexture);
            Add("normals", _normalRenderTarget);
            Add("reflect_entity", _reflectionRenderTarget);
            Add("mirror_source", _mirrorSourceRenderTarget);

            var manifest = new Dictionary<string, object?>();
            foreach (DumpBuffer buffer in buffers)
            {
                byte[] bytes = ReadTexture(buffer.Texture);
                string file = buffer.Name + ".raw.gz";
                using (FileStream fs = File.Create(Path.Combine(dir, file)))
                using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                    gz.Write(bytes, 0, bytes.Length);
                manifest[buffer.Name] = new Dictionary<string, object?>
                {
                    ["file"] = file,
                    ["width"] = buffer.Texture.Width,
                    ["height"] = buffer.Texture.Height,
                    ["format"] = buffer.Texture.Format.ToString(),
                    ["bytesPerPixel"] = BytesPerPixel(buffer.Texture.Format),
                    ["bytes"] = bytes.Length,
                };
            }

            var json = new JsonSerializerOptions { WriteIndented = true };
            json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            // The config is the one part of the metadata written by types this file does not own
            // (SMAPI keybinds among them). Serialize it on its own so a type that will not convert
            // costs the capture its config block instead of the whole metadata file.
            object? configJson;
            try { configJson = JsonSerializer.SerializeToElement(config, json); }
            catch (Exception ex) { configJson = "unserializable: " + ex.Message; }

            File.WriteAllText(Path.Combine(dir, "metadata.json"),
                JsonSerializer.Serialize(BuildMetadata(name, w, h, manifest, configJson), json));

            LastDumpPath = dir;
            _monitor.Log($"radiance_dump: wrote {buffers.Count} buffers to {dir}"
                + (Determinism.Frozen ? "" : " — NOT frozen, so this capture is not comparable to another run (radiance_freeze first)"),
                Determinism.Frozen ? LogLevel.Info : LogLevel.Warn);
        }

        /// <summary>
        /// Everything needed to decide whether two captures were taken of the same thing. A diff
        /// is only meaningful when the scene matched, so the scene is recorded in full: refusing
        /// a comparison is the verifier's job, and it can only do it from this.
        /// </summary>
        private Dictionary<string, object?> BuildMetadata(string name, int w, int h,
            Dictionary<string, object?> buffers, object? configJson)
        {
            GameLocation? location = Game1.currentLocation;
            return new Dictionary<string, object?>
            {
                ["name"] = name,
                ["modVersion"] = ModEntry.SVersion,
                ["capturedUtc"] = DateTime.UtcNow.ToString("o"),
                // Freeze state first: the verifier reads this before anything else.
                ["frozen"] = Determinism.Frozen,
                ["pinnedTicks"] = Determinism.Frozen ? Determinism.PinnedTicks : (int?)null,
                ["scene"] = new Dictionary<string, object?>
                {
                    ["location"] = location?.NameOrUniqueName,
                    ["outdoors"] = location?.IsOutdoors,
                    ["timeOfDay"] = Game1.timeOfDay,
                    ["season"] = Game1.season.ToString(),
                    ["dayOfMonth"] = Game1.dayOfMonth,
                    ["weather"] = Game1.isLightning ? "storm" : Game1.isRaining ? "rain" : Game1.isSnowing ? "snow" : "sun",
                    ["playerTile"] = Game1.player is { } p ? new[] { p.TilePoint.X, p.TilePoint.Y } : null,
                    ["viewport"] = new[] { Game1.viewport.X, Game1.viewport.Y, Game1.viewport.Width, Game1.viewport.Height },
                    ["zoom"] = Game1.options?.zoomLevel,
                },
                // The eased amounts are the state freeze mode is supposed to have settled. Recording
                // them turns "did freeze actually work" into something the verifier can answer
                // instead of something to argue about from the code.
                ["presence"] = new Dictionary<string, object?>
                {
                    ["master"] = _masterFade,
                    ["water"] = _fadeWater,
                    ["cloud"] = _fadeCloud,
                    ["lighting"] = _fadeLighting,
                    ["flood"] = _fadeFlood,
                    ["tilt"] = _fadeTilt,
                    ["godRays"] = _godRayAmount,
                    ["fogDay"] = _fogDayAmount,
                    ["fogMist"] = _fogMistAmount,
                    ["cloudWeather"] = _cloudWeatherAmount,
                    ["exposure"] = _meteredExposure,
                    ["wading"] = _pinFadeAmount,
                },
                ["mask"] = new Dictionary<string, object?>
                {
                    ["originTile"] = new[] { _lastWaterTileX, _lastWaterTileY },
                    ["anyWater"] = _hasWaterInMask,
                    ["shadowsReady"] = _shadowsReady,
                    ["labelVersion"] = LabelStore.Instance?.Version ?? 0,
                    ["jobInFlight"] = _pendingWaterMaskJob != null,
                },
                ["render"] = new Dictionary<string, object?> { ["width"] = w, ["height"] = h },
                // The whole config verbatim: a capture taken with a different preset is not a
                // regression, and this is the only way to tell the two apart after the fact.
                ["config"] = configJson,
                ["buffers"] = buffers,
            };
        }

        private static int BytesPerPixel(SurfaceFormat format) => format switch
        {
            SurfaceFormat.Alpha8 => 1,
            SurfaceFormat.Color or SurfaceFormat.Bgra32 or SurfaceFormat.Bgr32 => 4,
            SurfaceFormat.HalfVector4 => 8,
            SurfaceFormat.Vector4 => 16,
            _ => 4,
        };

        private static byte[] ReadTexture(Texture2D texture)
        {
            var data = new byte[texture.Width * texture.Height * BytesPerPixel(texture.Format)];
            texture.GetData(data);
            return data;
        }
    }
}
