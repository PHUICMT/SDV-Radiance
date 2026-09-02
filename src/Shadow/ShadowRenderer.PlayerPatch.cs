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
        internal static string PlayerPatchReport = "not attempted";

        private const int PlayerPatchSize = 640;
        private RenderTarget2D? _playerPatch;
        private readonly List<(float rot, Vector2 scale, float alpha, float blur)> _patchCasts = new();

        private bool _patchValid;
        private bool _patchDrawnThisFrame;
        private Vector2 _patchScreenTopLeft;
        private Vector2 _patchFeetInPatch;
        private float _patchAnchorWorldY;
        private float _patchFeetWorldX;
        private Rectangle _patchContent;

        private Texture2D? _solidTiles;
        private GameLocation? _solidTilesFor;
        private xTile.Map? _solidTilesMap;
        private Color[]? _solidTilesPixels;

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
                || who.isRidingHorse() || IsSeated(who) || OnOpenWater(location, who.TilePoint))
            {
                PlayerPatchReport = "gated (seated, swimming, riding or on open water)";
                return;
            }
            if (!ShouldCast(config))
                return;
            float strength = MathHelper.Clamp(config.DirectionalShadowStrength, 0f, 1f);
            if (strength <= 0.01f)
                return;
            float blur = Math.Max(0f, config.DirectionalShadowBlur);

            // The same casts the two draw paths would have made, from the same numbers. The
            // cross-fades read here are last frame's, one ease step behind what DrawInto will
            // advance them to later this frame; a frozen capture settles both, so the harness
            // sees no difference, and in play a sixtieth of a fade is not a picture.
            _patchCasts.Clear();
            _characterGroundForeshortening = config.ShadowCharacterGroundForeshortening;
            if (_sunBlend > 0.004f)
            {
                ComputeSun(out float rot, out float stretch, out float alpha);
                alpha *= strength * _sunBlend * MathHelper.Lerp(1f, OvercastAlpha, _overcastBlend);
                if (alpha > 0.01f)
                {
                    float sunBlur = blur + OvercastExtraBlur * _overcastBlend;
                    float lengthScale = Math.Max(0.1f, config.DirectionalShadowLength)
                                      * MathHelper.Lerp(1f, OvercastLength, _overcastBlend);
                    stretch *= lengthScale;
                    _patchCasts.Add((rot, new Vector2(CharacterAcrossScale(rot, stretch), stretch), alpha, sunBlur));
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
                foreach (var (rot, st, a, _) in _lightShadowCasts)
                    _patchCasts.Add((rot, new Vector2(1f, st), a, blur));
            }
            if (_patchCasts.Count == 0)
            {
                PlayerPatchReport = "no cast reaches the player this frame";
                return;
            }

            EnsureSolidTiles(device, location);
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
            effect.Parameters["SpriteOrigin"]?.SetValue(_playerFeetInRenderTarget);
            effect.Parameters["SpriteSize"]?.SetValue(new Vector2(_playerRenderTarget.Width, _playerRenderTarget.Height));

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
                foreach (var (rot, scale, alpha, castBlur) in _patchCasts)
                {
                    effect.Parameters["Scale"]?.SetValue(scale);
                    effect.Parameters["Rotation"]?.SetValue(rot);
                    // Up the screen (cos > 0): the shadow climbs the wall it meets. Down the
                    // screen: it stops at the counter. See the shader for why.
                    effect.Parameters["KeepOnSolid"]?.SetValue(Math.Cos(rot) > 0.0 ? 1f : 0f);
                    DrawSoft(batch, Taps9, _playerRenderTarget, null, _patchFeetInPatch, Color.White, alpha, rot,
                        _playerFeetInRenderTarget, scale, 0f, SpriteEffects.None, castBlur);
                    _patchContent = Rectangle.Union(_patchContent.IsEmpty ? CastBounds(rot, scale, castBlur) : _patchContent,
                        CastBounds(rot, scale, castBlur));
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
            PlayerPatchReport = $"composed {_patchCasts.Count} cast(s) into a {PlayerPatchSize}x{PlayerPatchSize} patch, content {_patchContent.Width}x{_patchContent.Height}";
        }

        /// <summary>The patch pixels one cast can touch: the silhouette's quad under the draw's
        /// lean and scale, plus the blur's reach, so the strips cover no more than they must.</summary>
        private Rectangle CastBounds(float rot, Vector2 scale, float blur)
        {
            float w = _playerRenderTarget!.Width, h = _playerRenderTarget.Height;
            Vector2 origin = _playerFeetInRenderTarget;
            float cs = (float)Math.Cos(rot), sn = (float)Math.Sin(rot);
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
                spriteBatch.Draw(_playerPatch, _patchScreenTopLeft + new Vector2(strip.X, strip.Y), strip, Color.White,
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
            if (ReferenceEquals(location, _solidTilesFor) && ReferenceEquals(map, _solidTilesMap)
                && _solidTiles is { IsDisposed: false })
                return;
            var buildings = map.GetLayer("Buildings");
            if (buildings == null)
            {
                _solidTiles = null;
                _solidTilesFor = location;
                _solidTilesMap = map;
                return;
            }
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
            if (_solidTiles == null || _solidTiles.IsDisposed || _solidTiles.Width != width || _solidTiles.Height != height)
            {
                _solidTiles?.Dispose();
                _solidTiles = VramTally.Track(new Texture2D(device, width, height, false, SurfaceFormat.Color), "player shadow solid tiles");
            }
            _solidTiles.SetData(_solidTilesPixels);
            _solidTilesFor = location;
            _solidTilesMap = map;
        }
    }
}
