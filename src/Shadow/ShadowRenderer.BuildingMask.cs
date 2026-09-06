using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;

namespace SDVRadiance
{
    /// <summary>
    /// ShadowRenderer — a building's sun shadow, stamped into a screen-space coverage mask instead
    /// of drawn into the world's sorted batch.
    /// </summary>
    /// <remarks>
    /// Every other caster in this mod is small enough to be a sprite among sprites. A building is
    /// not: its shadow covers dozens of tiles, and drawn as a sprite it has to pick ONE side of
    /// every sort question at once. There is no depth that answers them all, because the game
    /// draws a building BELOW the grass and bushes standing around it: put the shadow over the
    /// grass and it also goes over the building, put it under the building and every tuft of grass
    /// punches a hole in it.
    /// <para>
    /// So it stops being a sprite. The silhouettes go into a mask here, and the effect chain
    /// multiplies the finished picture down where the mask is set, the way a cloud shadow already
    /// does. Grass standing in the shadow is then DARKENED rather than sorted in front of it,
    /// which is what a shadow does to grass.
    /// </para>
    /// </remarks>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>Coverage of this frame's building shadows, white where shadowed, in the same
        /// screen space as the buffer the effect chain works in. Null when nothing casts.
        ///
        /// <para>Static, and handed across the same way <see cref="PlayerMask"/> is: the effect
        /// chain and the shadow renderer are two systems that meet at a texture, and the chain has
        /// no instance of this to ask.</para></summary>
        internal static RenderTarget2D? BuildingSunShadowMask { get; private set; }
        /// <summary>Whether <see cref="BuildingSunShadowMask"/> holds this frame's answer. False
        /// means the reader must leave the picture alone rather than reuse a stale mask.</summary>
        internal static bool BuildingSunShadowReady { get; private set; }

        private SpriteBatch? _buildingMaskSpriteBatch;
        private RenderTarget2D? _buildingMaskRenderTarget;

        /// <summary>
        /// Stamp every building's sun shadow into the mask. Runs in the same phase as the player
        /// bake, before the world batches open, so a render-target swap is safe here.
        /// </summary>
        internal void BuildBuildingSunShadowMask(GraphicsDevice graphicsDevice, ModConfig config)
        {
            BuildingSunShadowReady = false;
            GameLocation? location = Game1.currentLocation;
            if (location == null || !ShouldCast(config) || !config.DirectionalShadowObjects
                || !config.DirectionalShadowBuildings || location.buildings.Count == 0)
                return;
            // The sun path is the only one that casts these. A lamp lights a building from a few
            // tiles away and the honest shadow of one at that range is the whole map.
            if (!SunCasts())
                return;

            ComputeSun(out float rot, out float stretch, out float alpha);
            alpha *= MathHelper.Clamp(config.DirectionalShadowStrength, 0f, 1f)
                   * MathHelper.Lerp(1f, OvercastAlpha, _overcastBlend);
            if (alpha <= 0.01f)
                return;
            _sunLengthScale = Math.Max(0.1f, config.DirectionalShadowLength)
                            * MathHelper.Lerp(1f, OvercastLength, _overcastBlend);
            CaptureKindTuning(config);
            stretch *= _sunLengthScale;

            RenderTargetBinding[] previousTargets = graphicsDevice.GetRenderTargets();
            int width = previousTargets.Length > 0 && previousTargets[0].RenderTarget is RenderTarget2D bound
                ? bound.Width : Game1.viewport.Width;
            int height = previousTargets.Length > 0 && previousTargets[0].RenderTarget is RenderTarget2D bound2
                ? bound2.Height : Game1.viewport.Height;
            if (width <= 0 || height <= 0)
                return;
            if (_buildingMaskRenderTarget == null || _buildingMaskRenderTarget.Width != width
                || _buildingMaskRenderTarget.Height != height)
            {
                _buildingMaskRenderTarget?.Dispose();
                _buildingMaskRenderTarget = VramTally.Track(
                    new RenderTarget2D(graphicsDevice, width, height, false, SurfaceFormat.Color, DepthFormat.None),
                    "building shadow mask");
            }
            _buildingMaskSpriteBatch ??= new SpriteBatch(graphicsDevice);

            // Nothing stamped here is looked at as art: it is a coverage shape, so the smoothed
            // diagonal a doubled sheet buys is worth nothing to it, exactly as with the water mask.
            bool upscalerWasSuspended = SheetUpscaler.SuspendedForOwnDraw;
            bool recorderWasSuppressed = SpriteDrawRecorder.SuppressRecording;
            _renderDepth++;
            try
            {
                SheetUpscaler.SuspendedForOwnDraw = true;
                SpriteDrawRecorder.SuppressRecording = true;
                graphicsDevice.SetRenderTarget(_buildingMaskRenderTarget);
                graphicsDevice.Clear(Color.Transparent);
                SpriteBatch spriteBatch = _buildingMaskSpriteBatch;
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                int stamped = StampBuildings(spriteBatch, location, rot, stretch, alpha,
                                             Math.Max(0f, config.DirectionalShadowBlur));
                spriteBatch.End();
                // Then take the buildings themselves back out. A screen-space multiply has no
                // depth at all, so without this a building is darkened by its own shadow: the
                // silhouette hangs from the footprint line and runs up the screen, which is
                // exactly where the building's own art is drawn. The building is the thing IN the
                // sun; only the ground behind it is not.
                if (stamped > 0)
                {
                    spriteBatch.Begin(SpriteSortMode.Deferred, EraseCoverage, SamplerState.PointClamp);
                    CarveBuildings(spriteBatch, location);
                    spriteBatch.End();
                }
                BuildingSunShadowReady = stamped > 0;
                BuildingSunShadowMask = stamped > 0 ? _buildingMaskRenderTarget : null;
            }
            catch (Exception ex)
            {
                BuildingSunShadowReady = false;
                BuildingSunShadowMask = null;
                if (DiagnosticMonitor != null && !_errorLogged)
                {
                    _errorLogged = true;
                    DiagnosticMonitor.Log($"[shadow] building mask threw: {ex}", LogLevel.Warn);
                }
            }
            finally
            {
                _renderDepth--;
                SheetUpscaler.SuspendedForOwnDraw = upscalerWasSuspended;
                SpriteDrawRecorder.SuppressRecording = recorderWasSuppressed;
                graphicsDevice.SetRenderTargets(previousTargets);
            }
        }

        /// <summary>Stamp each on-screen building's silhouette. Returns how many were drawn, so a
        /// screen with none tells the reader to leave the picture alone rather than multiply it by
        /// an empty mask.</summary>
        /// <summary>Multiplies what is already in the mask by one minus the source's alpha, so
        /// drawing a sprite through it ERASES coverage where that sprite is opaque. The mask
        /// carries its shape in alpha, so only the alpha channel is written.</summary>
        private static readonly BlendState EraseCoverage = new()
        {
            ColorWriteChannels = ColorWriteChannels.Alpha,
            AlphaSourceBlend = Blend.Zero,
            AlphaDestinationBlend = Blend.InverseSourceAlpha,
            ColorSourceBlend = Blend.Zero,
            ColorDestinationBlend = Blend.InverseSourceAlpha,
        };

        /// <summary>Erase each building's own art from the mask, drawn where the game draws it:
        /// bottom-left of the art on the bottom-left of the footprint, at 4x, upright, moved by the
        /// DrawOffset the building's data declares, exactly as the stamp is. The stamp took the
        /// offset in 86a699b and this did not, so a content-pack house with an offset was carved
        /// out of its own shadow at the wrong place and kept a strip of that shadow, one offset
        /// wide, down the side of its wall (reported 2026-09-06 as a faint dark band on the
        /// farmhouse that went away with building shadows off). The vanilla house declares none,
        /// which is why nobody saw it on a vanilla farm.</summary>
        private void CarveBuildings(SpriteBatch spriteBatch, GameLocation location)
        {
            xTile.Dimensions.Rectangle viewport = Game1.viewport;
            foreach (Building bld in location.buildings)
            {
                if (bld == null)
                    continue;
                Texture2D? texture = null;
                try { texture = bld.texture?.Value; } catch { /* a content pack can throw while loading its art */ }
                if (texture == null)
                    continue;
                Rectangle src = bld.getSourceRect();
                if (src.Width <= 0 || src.Height <= 0)
                    continue;
                Vector2 drawOffset = (bld.GetData()?.DrawOffset ?? Vector2.Zero) * 4f;
                float baseY = (bld.tileY.Value + bld.tilesHigh.Value) * 64f + drawOffset.Y;
                Vector2 corner = Game1.GlobalToLocal(viewport, new Vector2(bld.tileX.Value * 64f + drawOffset.X, baseY));
                spriteBatch.Draw(texture, corner, src, Color.White, 0f, new Vector2(0f, src.Height),
                    4f, SpriteEffects.None, 0f);
            }
        }

        private int StampBuildings(SpriteBatch spriteBatch, GameLocation location, float rot, float stretch,
                                   float alpha, float blur)
        {
            xTile.Dimensions.Rectangle viewport = Game1.viewport;
            int tileX0 = viewport.X / 64 - 12, tileX1 = (viewport.X + viewport.Width) / 64 + 4;
            int tileY0 = viewport.Y / 64 - 12, tileY1 = (viewport.Y + viewport.Height) / 64 + 4;
            int stamped = 0;
            foreach (Building bld in location.buildings)
            {
                if (bld == null || bld.tileX.Value > tileX1 || bld.tileX.Value + bld.tilesWide.Value < tileX0
                    || bld.tileY.Value > tileY1 || bld.tileY.Value + bld.tilesHigh.Value < tileY0)
                    continue;
                Texture2D? texture = null;
                try { texture = bld.texture?.Value; } catch { /* a content pack can throw while loading its art */ }
                if (texture == null || bld.isUnderConstruction())
                    continue;
                Rectangle src = bld.getSourceRect();
                if (src.Width <= 0 || src.Height <= 0)
                    continue;
                // Same anchor as the world draw: the art hangs from its own centre, because a
                // barn's roof overhangs the footprint it is standing on. The game draws a
                // building at its tile plus the DrawOffset its data declares (times four, the
                // sprite scale), and a content pack's house can declare one the vanilla house
                // does not: without it the shadow sat that far to the right of the wall that cast
                // it, which is how it was reported, with the sun on either side. The mirror and
                // the water mask already anchor buildings this way.
                Vector2 drawOffset = (bld.GetData()?.DrawOffset ?? Vector2.Zero) * 4f;
                float baseY = (bld.tileY.Value + bld.tilesHigh.Value) * 64f + drawOffset.Y;
                float artCentreX = bld.tileX.Value * 64f + drawOffset.X + src.Width * 2f;
                Vector2 feet = Game1.GlobalToLocal(viewport, new Vector2(artCentreX, baseY));
                // Depth means nothing in a mask - every stamp is coverage - so the whole thing goes
                // in flat, and WHITE, which is what the compositing pass reads as "shadowed".
                //
                // The softening happens HERE and not in the chain, and the order is the whole
                // point: the buildings are erased from this mask afterwards, so a blur applied
                // later would spread that hole outwards and eat the shadow hugging the wall,
                // leaving a bright gap in the shape of the building. Soft first, then cut.
                EmitObject(spriteBatch, texture, src, feet, new Vector2(src.Width / 2f, src.Height),
                    alpha, LeanOf(rot, ShadowKind.Buildings), LengthOf(stretch, ShadowKind.Buildings), 0f,
                    SoftnessOf(blur, ShadowKind.Buildings), ObjectHeadFade, SpriteEffects.None,
                    ShadowGeometry.Card, groundAnchorWorldY: null, shadowColor: Color.White);
                stamped++;
            }
            return stamped;
        }
    }
}
