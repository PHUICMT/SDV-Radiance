using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// ShadowRenderer - THE OTHER PLAYERS. Everything else in this class knows about exactly one
    /// farmer, <see cref="Game1.player"/>, because for most of this mod's life there was only ever
    /// one. In multiplayer there are more, they live in <c>location.farmers</c>, and nothing here
    /// ever read that list: co-op partners cast no shadow at all, online or on a split screen.
    /// <para>
    /// They cannot go through the NPC path. An NPC's silhouette is baked from one texture and one
    /// source rectangle, which is why those bakes can be shared by every NPC wearing the same
    /// sprite; a farmer is composed at draw time by FarmerRenderer out of body, clothes, hair and
    /// whatever appearance mods have patched in, so the only way to get their shape is to render
    /// them, and the result belongs to that farmer alone. Hence one small target each, kept
    /// between frames and re-baked only when the pose changes, exactly as the local player's is.
    /// </para>
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>One remote farmer's baked silhouette and the state needed to reuse it.</summary>
        private sealed class FarmerBake
        {
            internal RenderTarget2D? Mask;
            /// <summary>Full-colour twin of <see cref="Mask"/>: same pose, same feet anchor, no
            /// colour scrub and no head fade. The water reflection flips this below the feet, the
            /// same way the local player's is used.</summary>
            internal RenderTarget2D? Color;
            internal Vector2 FeetInRenderTarget;
            internal (int Frame, int Facing, Rectangle Src) Signature;
            internal bool HasSignature;
            /// <summary>Baked and worth drawing: not swimming, not seated, not on a horse.</summary>
            internal bool Ready;
            /// <summary>The colour twin holds the current pose. It is skipped entirely when there
            /// is no water to reflect into, so this is what makes that skip safe to reverse.</summary>
            internal bool ColorFresh;
            internal int LastSeenTick;
        }

        private readonly Dictionary<long, FarmerBake> _otherFarmerBakes = new();
        private readonly List<long> _farmerBakeEvictions = new();

        /// <summary>The renderer the game is using, for the diagnostic report, which is static
        /// because a console command has no instance to start from. Claimed on the first bake pass;
        /// there is exactly one of these per game.</summary>
        internal static ShadowRenderer? Current;

        /// <summary>What the shadow pass currently holds for each remote farmer, for the report.
        /// Returns nothing at all when no renderer exists yet, which is itself the answer.</summary>
        private static IEnumerable<(Farmer Who, bool HasBake, bool Ready, bool HasMask, bool HasColour, bool ColourFresh)>
            DescribeRemoteFarmers(GameLocation location)
        {
            ShadowRenderer? renderer = Current;
            foreach (Farmer who in OtherFarmersIn(location))
            {
                FarmerBake? bake = null;
                bool has = renderer != null && renderer._otherFarmerBakes.TryGetValue(who.UniqueMultiplayerID, out bake);
                yield return (who, has, has && bake!.Ready, has && bake!.Mask != null,
                              has && bake!.Color != null, has && bake!.ColorFresh);
            }
        }

        /// <summary>How many remote farmer bakes are being held, for the report.</summary>
        private static int RemoteFarmerBakeCount => Current?._otherFarmerBakes.Count ?? 0;

        /// <summary>How many times the bake pass has been entered, and why it last gave up before
        /// looking at anyone. Both exist because "no bake for this farmer" has two very different
        /// causes and the report could not tell them apart: never asked, or asked and refused.</summary>
        internal static int RemoteFarmerPreparePasses;
        internal static string? RemoteFarmerLastSkip;
        private bool _remoteFarmerBakeErrorLogged;

        /// <summary>What the last bake pass actually saw, as opposed to what the report sees when
        /// it asks the same question later. The two disagreed - the report found a farmer to draw
        /// and the pass held no bake for anyone - and no amount of reading the code explained it,
        /// so the pass now writes down its own view and the two can be compared directly.</summary>
        internal static string RemoteFarmerLastPassView = "(the bake pass has not run yet)";

        /// <summary>What a remote farmer looks like this frame, for anything outside this class
        /// that needs their image. Published exactly as <see cref="PlayerColor"/> is, and for the
        /// same reason: the water passes run in another class and cannot reach in here.</summary>
        internal readonly struct RemoteFarmerImage
        {
            internal RemoteFarmerImage(Farmer who, Texture2D? mask, Texture2D? colour)
            {
                Who = who;
                Mask = mask;
                Colour = colour;
            }

            internal Farmer Who { get; }
            internal Texture2D? Mask { get; }
            internal Texture2D? Colour { get; }
        }

        /// <summary>This frame's remote farmers, rebuilt by <see cref="PrepareOtherFarmers"/>. On a
        /// split screen that is once per screen, so it always describes the screen being drawn.</summary>
        internal static readonly List<RemoteFarmerImage> OtherFarmerImages = new();

        /// <summary>A generous ceiling on remote farmers we will hold a target for. Vanilla co-op
        /// caps at four; the extra room is for servers that raise it, and past this the newcomers
        /// simply go unshadowed rather than the memory growing without a bound.</summary>
        private const int MaxRemoteFarmerBakes = 8;
        /// <summary>Ticks a farmer can go unseen before their target is released. Long enough to
        /// cover a walk through a doorway and back, short enough that a full server does not keep
        /// targets for people who left the map an hour ago.</summary>
        private const int RemoteFarmerBakeTtl = 600;

        /// <summary>
        /// The farmers in this location OTHER than the one whose screen we are drawing. On a split
        /// screen this is asked once per screen, and each screen's own player is excluded from its
        /// own pass because that one already has a dedicated bake and draw path.
        /// </summary>
        private static IEnumerable<Farmer> OtherFarmersIn(GameLocation? location)
        {
            if (location == null)
                yield break;
            long localId = Game1.player?.UniqueMultiplayerID ?? 0L;
            foreach (Farmer f in location.farmers)
            {
                if (f == null || f.UniqueMultiplayerID == localId)
                    continue;
                yield return f;
            }
        }

        /// <summary>
        /// Bake every remote farmer in the current location whose pose has changed. Runs from the
        /// same place the local player's bake does, during RenderingWorld, because a render-target
        /// swap is only legal there.
        /// </summary>
        internal void PrepareOtherFarmers(GraphicsDevice graphicsDevice, GameLocation? location, ModConfig config)
        {
            Current = this;
            RemoteFarmerPreparePasses++;
            EvictStaleFarmerBakes();
            OtherFarmerImages.Clear();
            if (location == null || _renderTargetSpriteBatch == null)
            {
                // Not a detail. The batch belongs to the local player's bake and is created there,
                // so a null one means that pass returned before reaching it and this whole feature
                // is off with nothing said. That is how the first split-screen test came back with
                // no partner shadow and no error anywhere.
                RemoteFarmerLastSkip = location == null
                    ? "no location"
                    : "no render-target batch yet (the local player's bake has not run)";
                return;
            }
            RemoteFarmerLastSkip = null;

            // Same gate the local player's colour twin uses: a second FarmerRenderer draw per
            // farmer is only worth paying for when there is water on this screen to mirror them in.
            bool reflectionNeedsFarmers = config.Enabled && config.WaterReflection && WaterOnScreen;

            int seen = 0, skippedNoSprite = 0;
            RenderTargetBinding[]? previous = null;   // fetched lazily: only a re-bake pays for it
            try
            {
                foreach (Farmer who in OtherFarmersIn(location))
                {
                    seen++;
                    // Ask for what the bake actually uses. This guard used to read
                    // who.FarmerSprite.Texture, copied from the NPC path where that IS the source
                    // art. A farmer is not drawn from it: BakeFarmerSilhouette calls
                    // FarmerRenderer.draw, which composes body, clothes, hair and whatever an
                    // appearance mod patched in. On a remote farmer that texture is null, so the
                    // guard protected nothing and switched the whole feature off: co-op partners
                    // had no shadow, no error, and a bake pass that ran seven thousand times
                    // without ever baking anybody.
                    if (who.FarmerRenderer == null || who.FarmerSprite == null)
                    {
                        skippedNoSprite++;
                        continue;
                    }
                    if (!_otherFarmerBakes.TryGetValue(who.UniqueMultiplayerID, out FarmerBake? bake))
                    {
                        if (_otherFarmerBakes.Count >= MaxRemoteFarmerBakes)
                            continue;
                        bake = new FarmerBake();
                        _otherFarmerBakes[who.UniqueMultiplayerID] = bake;
                    }
                    bake.LastSeenTick = Game1.ticks;

                    // Same three questions the local player's bake asks, and the same answers: a
                    // rider is covered by the horse's own shadow, and a swimmer casts none.
                    if (who.isRidingHorse() || who.swimming.Value)
                    {
                        bake.Ready = false;
                        continue;
                    }

                    Rectangle src = who.FarmerSprite.SourceRect;
                    var sig = (who.FarmerSprite.CurrentFrame, (int)who.FacingDirection, src);
                    // Accessory layers that animate on their own clock get the same periodic
                    // refresh the local player gets, and only when a mod that has them is loaded.
                    bool accessoryRefreshDue = PlayerAccessoriesAnimate && Game1.ticks % 8 == 0;
                    if (bake.HasSignature && bake.Signature == sig && !accessoryRefreshDue && bake.Mask != null
                        && (!reflectionNeedsFarmers || bake.ColorFresh))
                    {
                        bake.Ready = !IsSeated(who);
                        PublishRemoteFarmer(who, bake);
                        continue;
                    }

                    // PreserveContents, for the same reason every persistent bake target here
                    // needs it: a cached target on DiscardContents decays into garbage between
                    // frames instead of holding the pose it was baked with.
                    bake.Mask ??= VramTally.Track(new RenderTarget2D(graphicsDevice, PlayerRtW, PlayerRtH, false,
                        SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "farmer bakes (co-op)");
                    previous ??= graphicsDevice.GetRenderTargets();
                    BakeFarmerSilhouette(graphicsDevice, who, src, bake.Mask, out Vector2 feetInRt);
                    if (reflectionNeedsFarmers)
                    {
                        bake.Color ??= VramTally.Track(new RenderTarget2D(graphicsDevice, PlayerRtW, PlayerRtH, false,
                            SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), "farmer bakes (co-op)");
                        BakeFarmerColour(graphicsDevice, who, src, bake.Color);
                    }
                    bake.ColorFresh = reflectionNeedsFarmers;
                    bake.FeetInRenderTarget = feetInRt;
                    bake.Signature = sig;
                    bake.HasSignature = true;
                    bake.Ready = !IsSeated(who);
                    PublishRemoteFarmer(who, bake);
                }
            }
            catch (Exception ex)
            {
                try { _renderTargetSpriteBatch.End(); } catch { }
                // Reported whether or not diagnostic logging is on, once per session. A feature
                // that silently does nothing is the worst outcome available: the first split-screen
                // test came back with no partner shadow, no error, and nothing to go on.
                RemoteFarmerLastSkip = $"the bake threw: {ex.GetType().Name}: {ex.Message}";
                if (!_remoteFarmerBakeErrorLogged)
                {
                    _remoteFarmerBakeErrorLogged = true;
                    (DiagnosticMonitor ?? SharedMonitor)?.Log($"[shadow] remote farmer bake threw, so co-op partners "
                        + $"will have no shadow this session: {ex}", LogLevel.Error);
                }
            }
            finally
            {
                if (previous != null)
                    graphicsDevice.SetRenderTargets(previous);
                RemoteFarmerLastPassView =
                    $"screen {StardewModdingAPI.Context.ScreenId} at {location.NameOrUniqueName}: "
                    + $"location.farmers={location.farmers.Count}, local={Game1.player?.UniqueMultiplayerID}, "
                    + $"others seen={seen}, skipped for no sprite={skippedNoSprite}, "
                    + $"bakes after this pass={_otherFarmerBakes.Count}";
            }
        }

        /// <summary>
        /// Render one farmer's shape into <paramref name="target"/>: draw them, scrub the colour
        /// out so only the shape survives whatever an appearance mod painted, then fade feet to
        /// head so the stretched far end reads as penumbra rather than a hard clone.
        /// </summary>
        /// <remarks>
        /// This is the local player's bake, and the duplication is deliberate for now: that path
        /// was measured and tuned recently and is the single most expensive thing the mod does, so
        /// it keeps its own copy. Anything changed here has to be changed there.
        /// </remarks>
        private void BakeFarmerSilhouette(GraphicsDevice graphicsDevice, Farmer who, Rectangle src,
            RenderTarget2D target, out Vector2 feetInRenderTarget)
        {
            float w = src.Width * 4f, h = src.Height * 4f;
            Vector2 pos = new Vector2((PlayerRtW - w) / 2f, PlayerRtH - h - 8f);
            feetInRenderTarget = new Vector2(PlayerRtW / 2f, PlayerRtH - 8f);

            graphicsDevice.SetRenderTarget(target);
            graphicsDevice.Clear(Color.Transparent);
            _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            who.FarmerRenderer.draw(_renderTargetSpriteBatch, who.FarmerSprite.CurrentAnimationFrame,
                who.FarmerSprite.CurrentFrame, src, pos, Vector2.Zero, 0f, who.FacingDirection,
                Color.Black, 0f, 1f, who);
            _renderTargetSpriteBatch.End();

            _gradientTexture ??= BuildGradient(graphicsDevice);
            _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, ZeroColor, SamplerState.PointClamp);
            _renderTargetSpriteBatch.Draw(_gradientTexture, new Rectangle(0, 0, PlayerRtW, PlayerRtH), Color.White);
            _renderTargetSpriteBatch.End();

            _renderTargetSpriteBatch.Begin(SpriteSortMode.Deferred, MultiplyAlpha, SamplerState.PointClamp);
            _renderTargetSpriteBatch.Draw(_gradientTexture, new Rectangle(0, 0, PlayerRtW, PlayerRtH), Color.White);
            _renderTargetSpriteBatch.End();
        }

        /// <summary>
        /// The full-colour twin: the same pose at the same feet anchor, with no colour scrubbed out
        /// and no fade toward the head, because this one is a picture of the person rather than a
        /// shape. The mirror flips it below their feet, so whatever their appearance mods drew is
        /// what appears in the water.
        /// </summary>
        private void BakeFarmerColour(GraphicsDevice graphicsDevice, Farmer who, Rectangle src, RenderTarget2D target)
        {
            float w = src.Width * 4f, h = src.Height * 4f;
            Vector2 pos = new Vector2((PlayerRtW - w) / 2f, PlayerRtH - h - 8f);

            graphicsDevice.SetRenderTarget(target);
            graphicsDevice.Clear(Color.Transparent);
            _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            who.FarmerRenderer.draw(_renderTargetSpriteBatch, who.FarmerSprite.CurrentAnimationFrame,
                who.FarmerSprite.CurrentFrame, src, pos, Vector2.Zero, 0f, who.FacingDirection,
                Color.White, 0f, 1f, who);
            _renderTargetSpriteBatch.End();
        }

        /// <summary>Offer this farmer's images to the water passes. A swimmer is left out for the
        /// same reason the local player is: half the body is under the surface, and a whole
        /// mirrored copy of them reads as a glitch.</summary>
        private static void PublishRemoteFarmer(Farmer who, FarmerBake bake)
        {
            if (who.swimming.Value)
                return;
            OtherFarmerImages.Add(new RemoteFarmerImage(who, bake.Mask, bake.ColorFresh ? bake.Color : null));
        }

        /// <summary>Release targets for farmers who have not been seen for a while.</summary>
        private void EvictStaleFarmerBakes()
        {
            if (_otherFarmerBakes.Count == 0)
                return;
            _farmerBakeEvictions.Clear();
            foreach (var kv in _otherFarmerBakes)
                if (Game1.ticks - kv.Value.LastSeenTick > RemoteFarmerBakeTtl)
                    _farmerBakeEvictions.Add(kv.Key);
            foreach (long id in _farmerBakeEvictions)
            {
                if (_otherFarmerBakes.TryGetValue(id, out FarmerBake? bake))
                {
                    bake.Mask?.Dispose();
                    bake.Color?.Dispose();
                }
                _otherFarmerBakes.Remove(id);
            }
        }

        /// <summary>
        /// Remote farmers under the SUN, through the same anchor and the same guards the local
        /// player and every NPC use. House rule: a body is a body, and only the image differs.
        /// </summary>
        private void DrawOtherFarmerSunShadows(SpriteBatch spriteBatch, GameLocation location,
            float rot, float stretch, float alpha, float blur)
        {
            foreach (Farmer who in OtherFarmersIn(location))
            {
                if (!_otherFarmerBakes.TryGetValue(who.UniqueMultiplayerID, out FarmerBake? bake))
                    continue;
                // The same three the local player and every NPC are asked, in the same order. The
                // per-light sibling below already asked all three; this one asked only about water,
                // so a partner on a horse got a pool of their own laid over the horse's shadow, and
                // a swimmer got one on the surface. House rule: a body is a body.
                if (who.swimming.Value || who.isRidingHorse() || OnOpenWater(location, who.TilePoint))
                    continue;
                if (IsSeated(who))
                {
                    // A seated body gets the grounding pool and no cast, the same trade every
                    // seated NPC gets: the silhouette is the part that cannot describe a sitter.
                    DrawContactBlob(spriteBatch, SeatedAnchor(who), 20f, 10f, alpha * 0.8f, SeatedDepth(who), blur);
                    continue;
                }
                if (!bake.Ready || bake.Mask == null)
                    continue;
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom - FeetLift));
                float depth = MathHelper.Clamp(who.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                DrawSoft(spriteBatch, Taps9, bake.Mask, null, feet, Color.White, alpha, rot,
                    bake.FeetInRenderTarget, new Vector2(1f, stretch), depth, SpriteEffects.None, blur);
            }
        }

        /// <summary>Remote farmers indoors and after dark, one cast per light that reaches them.</summary>
        private void DrawOtherFarmerLightShadows(SpriteBatch spriteBatch, GameLocation location,
            float castStrength, float lenCfg, float ambAlpha, float blur)
        {
            foreach (Farmer who in OtherFarmersIn(location))
            {
                if (!_otherFarmerBakes.TryGetValue(who.UniqueMultiplayerID, out FarmerBake? bake))
                    continue;
                if (who.swimming.Value || who.isRidingHorse() || OnOpenWater(location, who.TilePoint))
                    continue;
                if (IsSeated(who))
                {
                    DrawContactBlob(spriteBatch, SeatedAnchor(who), 20f, 10f, ambAlpha * 0.8f, SeatedDepth(who), blur);
                    continue;
                }
                Vector2 feet = Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(who.GetBoundingBox().Center.X, who.GetBoundingBox().Bottom - FeetLift));
                float depth = MathHelper.Clamp(who.StandingPixel.Y / 10000f - ShadowDepthBias, 0f, 1f);
                GatherCasts(feet, castStrength, lenCfg);
                DrawContactBlob(spriteBatch, feet, 22f, 11f,
                    ambAlpha * (_lightShadowCasts.Count > 0 ? 0.45f : 1f), depth, blur);
                if (!bake.Ready || bake.Mask == null)
                    continue;
                foreach (var (rot, st, a, _) in _lightShadowCasts)
                    DrawSoft(spriteBatch, Taps9, bake.Mask, null, feet, Color.White, a, rot,
                        bake.FeetInRenderTarget, new Vector2(1f, st), depth, SpriteEffects.None, blur);
            }
        }
    }
}
