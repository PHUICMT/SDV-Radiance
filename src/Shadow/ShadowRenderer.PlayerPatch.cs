using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// ShadowRenderer — the player's shadow as a PATCH: every cast of it composed into one small
    /// world-anchored render target before the game's sorted batch opens, cut by the map through
    /// <c>shadowmask.fx</c>, then drawn into that batch in floor-row strips.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A character's shadow lives in the game's own sprite batch so that depth sorts it against
    /// people and furniture, and that batch takes no effect of ours. So where the shadow ends
    /// against something the MAP paints could only be a rectangle cut at a distance walked out
    /// on the CPU, and four rounds of that on the saloon counter each left a different sliver on
    /// its front: the walk's first sample, the strip's rounding, the blur's reach. A guess with
    /// the numbers tuned is still a guess. Here the shadow is drawn once into a patch of our own,
    /// where a pixel shader can ask the map for every pixel, and the patch goes into the game's
    /// batch as a finished picture.
    /// </para>
    /// <para>
    /// The patch also answers the draw-call count measured on 2026-09-03: a live nine-tap blur
    /// on up to six strips of up to three casts is up to 162 draws for one player every frame.
    /// The taps are paid once into the patch, and the batch receives a handful of strips.
    /// </para>
    /// <para>
    /// The player only. Villagers keep the strip path: a patch is a render-target switch, and
    /// ten of them a frame is the stutter the bake caches exist to avoid.
    /// </para>
    /// </remarks>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>The map-cut effect, loaded by the pipeline from <c>assets/shadowmask.mgfxo</c>.
        /// Null leaves the player on the strip path exactly as before.</summary>
        internal static Effect? ShadowMaskEffect;

        /// <summary>The switch, for an A/B against the strip path (<c>radiance_shadowpatch</c>).</summary>
        internal static bool PlayerPatchEnabled = true;

        /// <summary>What the patch did this frame, for radiance_shadows.</summary>
        /// <summary>The fixed sentence for an outcome that is not a composed patch, or null when
        /// the patch WAS composed and <see cref="PlayerPatchLine"/> should describe it.</summary>
        internal static string? PlayerPatchReport = "not attempted";

        private const int PlayerPatchSize = 640;
        private RenderTarget2D? _playerPatch;
        /// <summary>One cast onto the patch. The scale's width is already POSITIVE and the sign it
        /// carried is in <c>facing</c>: the shader and the draw both want the same geometry, and a
        /// SpriteBatch cannot be handed a negative width (see <see cref="LaidDownWidth"/>).</summary>
        /// <remarks>The source is the upright bake for a lamp's cast and the laid-down one for
        /// the sun's, whose lean is then in its pixels and its rotation zero; <c>lean</c> says
        /// which way the shadow runs either way, for the choice of wall rule.</remarks>
        private readonly List<(Texture2D source, Rectangle? sourceRect, Vector2 origin, float rotation, Vector2 scale,
            float alpha, float blur, SpriteEffects facing, float lean)> _patchCasts = [];

        private bool _patchValid;
        private bool _patchDrawnThisFrame;
        private Vector2 _patchScreenTopLeft;
        private Vector2 _patchFeetInPatch;
        private float _patchAnchorWorldY;
        private float _patchFeetWorldX;
        private Rectangle _patchContent;

        /// <summary>The solid-tile texture this frame's patch reads: the one kept for the place the
        /// screen being drawn stands in.</summary>
        private Texture2D? _solidTiles;
        private Color[]? _solidTilesPixels;

        /// <summary>One solid-tile texture per place, a few places kept.
        ///
        /// <para>It was one slot, rebuilt whenever the place asked for was not the place it held. The
        /// same place from the other screen was already accepted (a different object with the same
        /// Buildings layer), but two screens in two DIFFERENT places took turns: every call walked
        /// the whole Buildings layer and uploaded a texture, for both screens, every frame. Measured
        /// 13/9 with one screen in Town and one on the mountain: "patch: solid tiles" 0.95 ms a frame
        /// in split screen against 0.001 and 0.000 for the same two places on one screen each.</para></summary>
        private sealed class SolidTilesForPlace
        {
            internal Texture2D? Texture;
            internal xTile.Map? Map;
            internal bool HasNoBuildingsLayer;
            internal long LastAskedFor;
        }

        private readonly Dictionary<string, SolidTilesForPlace> _solidTilesByPlace = [];
        private long _solidTilesAsks;
        /// <summary>Two screens in two places is two; the spare pair covers walking between them.</summary>
        private const int SolidTilesPlacesKept = 4;

        /// <summary>
        /// Compose every cast of the player's shadow into the patch, cut by the map. Runs from
        /// <see cref="PreparePlayer"/>, before the world batches open, so a render-target swap
        /// is safe.
        /// </summary>
        private void RenderPlayerShadowPatch(GraphicsDevice device, ModConfig config)
        {
            _patchValid = false;
            _patchDrawnThisFrame = false;
            PlayerPatchReport = "not attempted";
            if (!PlayerPatchEnabled || ShadowMaskEffect == null)
            {
                PlayerPatchReport = PlayerPatchEnabled ? "no shadowmask effect loaded" : "switched off (radiance_shadowpatch)";
                return;
            }
            if (!_playerReady || _playerRenderTarget == null)
                return;
            Farmer who = Game1.player;
            GameLocation? location = Game1.currentLocation;
            if (who == null || location == null || who.currentLocation != location || who.swimming.Value
                || who.isRidingHorse() || IsSeated(who))
            {
                PlayerPatchReport = "gated (seated, swimming or riding)";
                return;
            }
            if (!ShouldCast(config))
                return;
            if (!config.DirectionalShadowPlayer)
            {
                PlayerPatchReport = "switched off (Shadows for the player)";
                return;
            }
            float requestedStrength = MathHelper.Clamp(config.DirectionalShadowStrength, 0f, ModConfig.ShadowStrengthMax);
            float strength = Math.Min(requestedStrength, 1f);
            if (strength <= 0.01f)
                return;
            _patchDepthPower = Math.Max(1f, requestedStrength);
            float blur = Math.Max(0f, config.DirectionalShadowBlur);

            // The same casts the two draw paths would have made, from the same numbers. The
            // cross-fades read here are last frame's, one ease step behind what DrawInto will
            // advance them to later this frame; a frozen capture settles both, so the harness
            // sees no difference, and in play a sixtieth of a fade is not a picture.
            _patchCasts.Clear();
            _characterGroundForeshortening = config.ShadowCharacterGroundForeshortening;
            if (_sunBlend > 0.004f)
            {
                ComputeSun(out float rotation, out float stretch, out float alpha);
                alpha *= strength * _sunBlend * MathHelper.Lerp(1f, OvercastAlpha, _overcastBlend);
                if (alpha > 0.01f)
                {
                    float sunBlur = blur + OvercastExtraBlur * _overcastBlend;
                    float lengthScale = Math.Max(0.1f, config.DirectionalShadowLength)
                                      * MathHelper.Lerp(1f, OvercastLength, _overcastBlend);
                    stretch *= lengthScale;
                    if (_playerSunFresh && _playerSunRenderTarget != null)
                        // Laid down already, soft edge and skew in the pixels: no rotation, no
                        // blur taps, one scale. See LayDownPlayerSun.
                        _patchCasts.Add((_playerSunRenderTarget, _playerSunContent,
                            _playerSunFeet - new Vector2(_playerSunContent.X, _playerSunContent.Y), 0f,
                            new Vector2(_playerSunUnbake, _playerSunUnbake), alpha, 0f, SpriteEffects.None, rotation));
                    else
                    {
                        float patchWidth = LaidDownWidth(CharacterAcrossScale(rotation, stretch), SpriteEffects.None, out SpriteEffects patchFacing);
                        _patchCasts.Add((_playerRenderTarget, null, _playerFeetInRenderTarget, rotation,
                            new Vector2(patchWidth, stretch), alpha, sunBlur, patchFacing, rotation));
                    }
                }
            }
            if (_sunBlend < 0.996f)
            {
                float lightStrength = strength * (1f - _sunBlend);
                CollectCastingLights(location);
                TrimLightsToScreenBudget();
                _castsPerCaster = Math.Clamp(config.ShadowCastsPerCharacter, ModConfig.ShadowCastsMin, ModConfig.ShadowCastsMax);
                float lenCfg = Math.Max(0.1f, config.DirectionalShadowLength);
                float nightBoost = location.IsOutdoors ? GameClock.RampAt(TrulyDark()) : 0f;
                float castStrength = lightStrength * MathHelper.Lerp(1.0f, 1.9f, nightBoost);
                Vector2 feetScreen = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom - FeetLift));
                GatherCasts(feetScreen, castStrength, lenCfg);
                foreach (var (rotation, st, a, _) in _lightShadowCasts)
                    _patchCasts.Add((_playerRenderTarget, null, _playerFeetInRenderTarget, rotation, new Vector2(1f, st), a, blur,
                        SpriteEffects.None, rotation));
            }
            if (_patchCasts.Count == 0)
            {
                PlayerPatchReport = "no cast reaches the player this frame";
                return;
            }

            long solidStep = RenderPipeline.ChainStepBegin();

            EnsureSolidTiles(device, location);

            RenderPipeline.DrawingScreen?.ChainStepEnd(RenderPipeline.ChainStep.PatchSolidTiles, solidStep);
            if (_solidTiles == null)
            {
                PlayerPatchReport = "no Buildings layer to cut against";
                return;
            }

            var feetWorld = new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom - FeetLift);
            var patchOriginWorld = feetWorld - new Vector2(PlayerPatchSize / 2f, PlayerPatchSize / 2f);
            _patchFeetInPatch = feetWorld - patchOriginWorld;

            // PreserveContents: this target is unbound and then read in the world batch later in
            // the frame, and a DiscardContents target is undefined the moment it stops being the
            // target (the same note the water masks and the building mask carry).
            if (_playerPatch == null || _playerPatch.IsDisposed)
                _playerPatch = VramTally.Track(new RenderTarget2D(device, PlayerPatchSize, PlayerPatchSize, false,
                    SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "player shadow patch");

            Effect effect = ShadowMaskEffect;
            effect.Parameters["SolidTexture"]?.SetValue(_solidTiles);
            effect.Parameters["SolidMapTiles"]?.SetValue(new Vector2(_solidTiles.Width, _solidTiles.Height));
            effect.Parameters["FeetWorld"]?.SetValue(feetWorld);

            RenderTargetBinding[] previous = device.GetRenderTargets();
            var batch = _renderTargetSpriteBatch!;
            _patchContent = Rectangle.Empty;
            try
            {
                device.SetRenderTarget(_playerPatch);
                device.Clear(Color.Transparent);
                // Immediate, so each cast's lean and direction reach the shader before its taps.
                batch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.LinearClamp,
                    DepthStencilState.None, RasterizerState.CullNone, effect);
                foreach (var (source, sourceRect, origin, rotation, scale, alpha, castBlur, facing, lean) in _patchCasts)
                {
                    Rectangle area = sourceRect ?? source.Bounds;
                    // Per cast, because the sun's cast comes from the laid-down bake and a lamp's
                    // from the upright one, and the shader maps each texel back to the world
                    // through the size and origin of whichever it was handed.
                    effect.Parameters["SpriteOrigin"]?.SetValue(origin);
                    effect.Parameters["SpriteSize"]?.SetValue(new Vector2(area.Width, area.Height));
                    effect.Parameters["Scale"]?.SetValue(scale);
                    effect.Parameters["Rotation"]?.SetValue(rotation);
                    // Up the screen (cos > 0): the shadow climbs the wall it meets. Down the
                    // screen: it stops at the counter. See the shader for why. Asked of the lean
                    // the shadow really has, which a laid-down cast carries in its pixels.
                    effect.Parameters["KeepOnSolid"]?.SetValue(Math.Cos(lean) > 0.0 ? 1f : 0f);
                    ShadowDepthPower = _patchDepthPower;
                    try
                    {
                        DrawSoft(batch, Taps9, source, sourceRect, _patchFeetInPatch, Color.White, alpha, rotation,
                            origin, scale, 0f, facing, castBlur, shadowLengthPerHeight: scale.Y);
                    }
                    finally
                    {
                        ShadowDepthPower = 1f;
                    }
                    Rectangle castBounds = CastBounds(area.Width, area.Height, origin, rotation, scale, castBlur);
                    _patchContent = _patchContent.IsEmpty ? castBounds : Rectangle.Union(_patchContent, castBounds);
                }
                batch.End();
            }
            finally
            {
                device.SetRenderTargets(previous);
            }
            _patchContent = Rectangle.Intersect(_patchContent, new Rectangle(0, 0, PlayerPatchSize, PlayerPatchSize));
            _patchScreenTopLeft = Game1.GlobalToLocal(Game1.viewport, patchOriginWorld);
            _patchAnchorWorldY = feetWorld.Y;
            _patchFeetWorldX = feetWorld.X;
            _patchValid = !_patchContent.IsEmpty;
            _patchContentSize = new Point(_patchContent.Width, _patchContent.Height);
            // Counted, not written out: this runs on every frame the player has a shadow, and the
            // sentence is only ever read by radiance_shadows. Report() spells it out from these.
            PlayerPatchReport = null;
            _patchCastsComposed = _patchCasts.Count;
        }

        /// <summary>How many casts the last patch was composed from, for the diagnostic. The
        /// sentence that used to be built here is in <see cref="PlayerPatchLine"/>.</summary>
        private static int _patchCastsComposed;

        /// <summary>The player's shadow deepened past a strength of 1, the same as every other (see
        /// ShadowDepthPower), carried from where the casts are collected to where they are composed.</summary>
        private float _patchDepthPower = 1f;

        /// <summary>The size of the last composed patch's content, for the diagnostic.</summary>
        private static Point _patchContentSize;

        /// <summary>What the patch did last frame, in words. A composed patch leaves
        /// <see cref="PlayerPatchReport"/> null and is described from the numbers here; every
        /// other outcome is a fixed sentence set where it happened.</summary>
        internal static string PlayerPatchLine
            => PlayerPatchReport
               ?? $"composed {_patchCastsComposed} cast(s) into a {PlayerPatchSize}x{PlayerPatchSize} patch, "
                  + $"content {_patchContentSize.X}x{_patchContentSize.Y}";

        /// <summary>The patch pixels one cast can touch: the silhouette's quad under the draw's
        /// lean and scale, plus the blur's reach, so the strips cover no more than they must.</summary>
        private Rectangle CastBounds(float w, float h, Vector2 origin, float rotation, Vector2 scale, float blur)
        {
            float cs = (float)Math.Cos(rotation), sn = (float)Math.Sin(rotation);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (Vector2 corner in new[] { new Vector2(0, 0), new Vector2(w, 0), new Vector2(0, h), new Vector2(w, h) })
            {
                Vector2 scaled = (corner - origin) * scale;
                var p = _patchFeetInPatch + new Vector2(scaled.X * cs - scaled.Y * sn, scaled.X * sn + scaled.Y * cs);
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            int pad = (int)Math.Ceiling(blur) + 2;
            return new Rectangle((int)Math.Floor(minX) - pad, (int)Math.Floor(minY) - pad,
                (int)Math.Ceiling(maxX - minX) + 2 * pad, (int)Math.Ceiling(maxY - minY) + 2 * pad);
        }

        /// <summary>
        /// Draw the composed patch into the world batch, in floor-row strips. True when it was
        /// drawn (or already had been this frame), so the caller skips its own casts.
        /// </summary>
        private bool DrawPlayerPatch(SpriteBatch spriteBatch)
        {
            if (!_patchValid || _playerPatch == null)
                return false;
            if (_patchDrawnThisFrame)
                return true;
            _patchDrawnThisFrame = true;
            // Rows of the patch ARE floor rows: the patch is screen-aligned, so a strip of it is
            // the piece of every cast lying on that band of floor, sorted at that band, with the
            // building rule (GroundedPieceDepth) unchanged.
            int stripHeight = (int)GroundStripPixels;
            float stripCentreX = _patchContent.X + _patchContent.Width * 0.5f;
            float sideways = stripCentreX - _patchFeetInPatch.X;
            for (int y = _patchContent.Y; y < _patchContent.Bottom; y += stripHeight)
            {
                int h = Math.Min(stripHeight, _patchContent.Bottom - y);
                var strip = new Rectangle(_patchContent.X, y, _patchContent.Width, h);
                float upScreen = _patchFeetInPatch.Y - (y + h * 0.5f);
                float depth = GroundedPieceDepth(_patchAnchorWorldY, upScreen, _patchFeetWorldX, sideways);
                FrameCost.Count(FrameCost.Counter.ShadowDrawCalls);
                spriteBatch.Draw(_playerPatch, _patchScreenTopLeft + new Vector2(strip.X, strip.Y), strip, ShadowInk,
                    0f, Vector2.Zero, 1f, SpriteEffects.None, depth);
            }
            return true;
        }

        /// <summary>
        /// One texel per map tile: white where the Buildings layer holds a tile with no Passable
        /// property. Built once per map and kept, since a map's Buildings layer does not move.
        /// </summary>
        private void EnsureSolidTiles(GraphicsDevice device, GameLocation location)
        {
            xTile.Map? map = location.map;
            if (map == null)
            {
                _solidTiles = null;
                return;
            }
            // Keyed by the place's name, which is what LiveScreens.SamePlace compares: the same place
            // seen from the other screen is a different object with the same Buildings layer.
            string place = location.NameOrUniqueName;
            if (!_solidTilesByPlace.TryGetValue(place, out SolidTilesForPlace? kept))
                _solidTilesByPlace[place] = kept = new SolidTilesForPlace();
            kept.LastAskedFor = ++_solidTilesAsks;
            TrimSolidTilesPlaces();
            if (SDVRadiance.LiveScreens.SameMapSize(map, kept.Map)
                && (kept.HasNoBuildingsLayer || kept.Texture is { IsDisposed: false }))
            {
                _solidTiles = kept.HasNoBuildingsLayer ? null : kept.Texture;
                return;
            }
            var buildings = map.GetLayer("Buildings");
            if (buildings == null)
            {
                kept.Texture?.Dispose();
                kept.Texture = null;
                kept.HasNoBuildingsLayer = true;
                kept.Map = map;
                _solidTiles = null;
                return;
            }
            kept.HasNoBuildingsLayer = false;
            int width = buildings.LayerWidth, height = buildings.LayerHeight;
            if (_solidTilesPixels == null || _solidTilesPixels.Length != width * height)
                _solidTilesPixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    bool solid = buildings.Tiles[x, y] != null
                        && location.doesTileHaveProperty(x, y, "Passable", "Buildings") == null;
                    _solidTilesPixels[y * width + x] = solid ? Color.White : Color.Transparent;
                }
            if (kept.Texture == null || kept.Texture.IsDisposed || kept.Texture.Width != width || kept.Texture.Height != height)
            {
                kept.Texture?.Dispose();
                kept.Texture = VramTally.Track(new Texture2D(device, width, height, false, SurfaceFormat.Color), "player shadow solid tiles");
            }
            kept.Texture.SetData(_solidTilesPixels);
            kept.Map = map;
            _solidTiles = kept.Texture;
        }

        /// <summary>Drop the place nobody has asked about for longest once more are kept than
        /// <see cref="SolidTilesPlacesKept"/>. The place just asked for is stamped first, so it is
        /// never the one dropped.</summary>
        private void TrimSolidTilesPlaces()
        {
            while (_solidTilesByPlace.Count > SolidTilesPlacesKept)
            {
                string? leastWanted = null;
                long oldest = long.MaxValue;
                foreach (var pair in _solidTilesByPlace)
                    if (pair.Value.LastAskedFor < oldest)
                    {
                        oldest = pair.Value.LastAskedFor;
                        leastWanted = pair.Key;
                    }
                if (leastWanted == null)
                    break;
                _solidTilesByPlace[leastWanted].Texture?.Dispose();
                _solidTilesByPlace.Remove(leastWanted);
            }
        }
    }
}
