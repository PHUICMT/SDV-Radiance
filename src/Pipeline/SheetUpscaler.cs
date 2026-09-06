using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Sprites drawn from a sheet twice its size: a prefix on the game's SpriteBatch.Draw swaps the
    /// sheet for its Scale2x derivative (sheetscale.fx, kept per sheet by <see cref="SheetDerivedCache"/>),
    /// doubles the source rectangle and halves the scale, so the sprite lands exactly where it was
    /// with two texels where the game put one. What a texture upscaler mod does to every sheet at
    /// load, done here on the card, only for the sheets on screen, and only to the draw: the
    /// sheets themselves are untouched, so everything in this mod that reads sheet pixels back -
    /// the label fingerprints, the waterline map, the shadow bakes - reads what it always read.
    /// </summary>
    /// <remarks>
    /// Only the game's own batch is redirected. The mod's bakes and masks draw into targets sized
    /// for the original art, and a derivative of a derivative is refused by the cache. Off by
    /// default: it is a look, and it holds up to 384 MB of doubled sheets.
    /// </remarks>
    internal static class SheetUpscaler
    {
        /// <summary>Set per frame by the mod from the switch.</summary>
        internal static bool Enabled;
        /// <summary>The five art families, each with its own switch, set per frame from the config.
        /// Portraits, characters and items are named by their sheet's content path; the rest divides
        /// by WHEN the draw happens - the game draws menus, dialogue and the HUD in UI mode.</summary>
        internal static bool WorldEnabled = true;
        internal static bool CharactersEnabled = true;
        internal static bool PortraitsEnabled = true;
        internal static bool ItemsEnabled = true;
        internal static bool InterfaceEnabled;
        /// <summary>The art families, in the order the per-family dials and cache variants use.</summary>
        internal enum ArtFamily { World = 0, Characters = 1, Portraits = 2, Items = 3, Interface = 4 }
        internal const int FamilyCount = 5;
        /// <summary>0 keeps the game's own pixels, 1 is the full Scale2x rounding, one dial per art
        /// family (indexed by ArtFamily). Baked into the doubled sheets, whose cache variant is the
        /// family, so a change re-makes the sheets once instead of costing every frame.</summary>
        internal static readonly float[] SmoothnessByFamily = { 1f, 1f, 1f, 1f, 1f };
        private static readonly float[] _bakedSmoothnessByFamily = { 1f, 1f, 1f, 1f, 1f };
        /// <summary>The family whose soft sprite is being baked at this moment; the bake, which
        /// runs inside SoftSprites.For, reads that family's dial through it.</summary>
        private static ArtFamily _softBakeFamily;
        /// <summary>Which look the smoothing has (see <see cref="SheetSmoothingStyle"/>), set per
        /// frame from the config; a change hands every held sheet back.</summary>
        internal static SheetSmoothingStyle Style = SheetSmoothingStyle.Scale2x;
        private static SheetSmoothingStyle _bakedStyle = SheetSmoothingStyle.Scale2x;
        internal static GraphicsDevice? Device;
        internal static Effect? Effect;
        private const int Scale = 2;
        private const int SoftScale = 4;
        /// <summary>How wide the soft look's anti-aliased edge is, in source pixels: a quarter is one
        /// texel of the four-times sheet, which drawn at the game's 4x is one screen pixel of ramp.
        /// Baked, so a change re-makes the soft sheets (BeginFrame). radiance_softedge sets it live,
        /// for tuning by eye against a texture-upscaler capture.</summary>
        internal static float SoftEdgeSourcePixels = 0.25f;
        private static float _bakedSoftEdge = 0.25f;
        /// <summary>The tent that follows the kernel, in texels of the four-times sheet (see
        /// SheetSoften): what a texture upscaler gets by drawing a bigger sheet down with a linear
        /// filter. Three quarters was chosen beside a Clear Glasses capture of the same items, against
        /// a half (staircases still showing), one (softer than theirs) and one and a half. Baked;
        /// radiance_softblur sets it live for tuning by eye.</summary>
        internal static float SoftBlurTexels = 0.75f;
        private static float _bakedSoftBlur = 0.75f;
        /// <summary>Whether the soft sheets are sampled LINEARLY when the game draws them, whatever
        /// sampler the batch was begun with. This is the other half of the texture-upscaler look:
        /// their kernel rounds the outlines, and a linear read of the big sheet is what softens every
        /// colour boundary inside a sprite as well. The batch's own sampler (point, for the game's
        /// pixel art and lettering) is put back for every texture that is not a soft sheet, so
        /// nothing else in the batch goes soft. Done where MonoGame flushes a run of one texture,
        /// which is the only place a sampler can follow the texture rather than the batch.</summary>
        private static bool _linearForSoftSheets;
        /// <summary>Doubled sheets whose latest interface draw this frame was at a size the doubled
        /// texels cannot be read evenly with point sampling (below two screen pixels a doubled texel
        /// and not a whole number), so their run is read LINEARLY instead. The toolbar is the case: the
        /// game draws every item in it at 3.2 screen pixels a texel (Toolbar.draw, scaleSize 0.8,
        /// the held one 0.9), which left every tool and item there exactly as the game drew it
        /// while the same items on the ground were smoothed. Cleared each frame; a draw at an even
        /// size takes its sheet back out, so the inventory's 4x items stay point-read and crisp.</summary>
        private static readonly HashSet<Texture2D> _linearRuns = new();
        /// <summary>Two colours closer than this on the shader's luminance-plus-alpha scale (0 to
        /// 1.5) are the same colour to the edge rules: about a fifth of the way from black to white.</summary>
        private const float SoftEqualThreshold = 0.10f;
        /// <summary>Sheets up to 2048x2048 (16 MB) are doubled; a 4096 content-pack sheet would be 256 MB.</summary>
        internal static readonly SheetDerivedCache Cache = new("upscaled sheets", 384L * 1024 * 1024, 16L * 1024 * 1024, Scale, 4, "SheetScale",
            (effect, sheet, family) =>
            {
                effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / sheet.Width, 1f / sheet.Height));
                effect.Parameters["TargetSize"]?.SetValue(new Vector2(sheet.Width * Scale, sheet.Height * Scale));
                effect.Parameters["Smoothness"]?.SetValue(_bakedSmoothnessByFamily[family]);
                // A map tilesheet is doubled tile by tile: the kernel reads no texel outside the
                // 16-pixel cell it is rounding, so a floor tile keeps the edge it was painted with
                // rather than a corner borrowed from whatever tile sits beside it in the sheet.
                effect.Parameters["CellSize"]?.SetValue(DrawnAsMapTiles(sheet) ? MapTileSourcePixels : 0f);
            });
        /// <summary>The side of a map tile in source pixels; every Stardew tilesheet is on this grid.</summary>
        private const float MapTileSourcePixels = 16f;
        /// <summary>Whether a sheet is one of the map's own tilesheets, by the name the game gives them.</summary>
        private static bool DrawnAsMapTiles(Texture2D sheet)
        {
            string name = sheet.Name ?? "";
            return name.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Maps\\", StringComparison.OrdinalIgnoreCase);
        }
        /// <summary>The soft look's sprites, one target per (sheet, rectangle) drawn: four times the
        /// texels of each, made from its own rectangle alone (see <see cref="SoftSpriteCache"/> for
        /// the dark frame that baking whole sheets gave every sprite). Any sheet qualifies, since a
        /// sprite is small whatever its sheet is; eight bakes a frame, each two small passes.</summary>
        internal static readonly SoftSpriteCache SoftSprites = new("soft sprites", 192L * 1024 * 1024, 512, SoftScale, 8, SoftSpriteBake);
        internal static int PatchedOverloads { get; private set; }
        /// <summary>Draws redirected this frame, for the debug caption.</summary>
        internal static int RedirectedThisFrame;
        /// <summary>Set while this mod draws something of its own through the game's batch that
        /// does not want a smoothed sheet. Shadow silhouettes are the case: they are stamped in
        /// flat black and then blurred, so the rounded diagonal is thrown away a moment later,
        /// and the only thing left of it is four times the texels read. Measured at town-night
        /// with doubling on, the shadow draw was 0.021 ms before this pass existed and 0.322 ms
        /// after. The batch identity check below cannot tell our draws from the game's, because
        /// world sprites of ours are required to use Game1.spriteBatch, so the caller says so.</summary>
        internal static bool SuspendedForOwnDraw;

        private static bool Active => Enabled && Device != null && Effect != null;

        /// <summary>What a batch was begun with, per batcher, so a flush can put it back. Keyed by
        /// the batcher (SpriteBatch._batcher), which is the object the flush belongs to.</summary>
        private sealed class BatchSampling
        {
            public GraphicsDevice Device = null!;
            public SamplerState Sampler = SamplerState.PointClamp;
            public SamplerState? Applied;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, BatchSampling> _samplingByBatcher = new();
        private static AccessTools.FieldRef<SpriteBatch, object>? _batcherOf;
        private static System.Reflection.FieldInfo? _batcherDevice;
        /// <summary>The batcher behind Game1.spriteBatch, so a flush of the game's batch can be
        /// told from a flush of one of this mod's own.</summary>
        private static object? _gameBatcher;
        /// <summary>Frames left in which every run of the game's Point batch is flushed under a
        /// fresh Point sampler instance, alternating between two, so MonoGame's per-texture memory
        /// of the last sampler applied (glLastSamplerState) never matches and it writes the
        /// texture's GL filter again. radiance_resample sets it. A sprite that comes back crisp was
        /// a texture whose GL filter had drifted to linear behind MonoGame's back.</summary>
        internal static int ResampleFramesLeft;
        private static readonly SamplerState _pointAgainA = new() { Filter = TextureFilter.Point, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, Name = "PointAgainA" };
        private static readonly SamplerState _pointAgainB = new() { Filter = TextureFilter.Point, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, Name = "PointAgainB" };
        private static bool _pointAgainToggle;

        private static void Begin_Postfix(SpriteBatch __instance, SpriteSortMode sortMode, SamplerState? samplerState)
        {
            if (_batcherOf == null)
                return;
            object batcher = _batcherOf(__instance);
            if (batcher == null)
                return;
            BatchSampling sampling = _samplingByBatcher.GetValue(batcher, b => new BatchSampling
            {
                Device = (_batcherDevice?.GetValue(b) as GraphicsDevice) ?? __instance.GraphicsDevice,
            });
            // MonoGame's own default when none is given is LinearClamp; the game always passes one.
            sampling.Sampler = samplerState ?? SamplerState.LinearClamp;
            sampling.Applied = null;
            if (ReferenceEquals(__instance, Game1.spriteBatch))
            {
                _gameBatcher = batcher;
                if (SpriteDrawRecorder.InWorldStep)
                    SpriteDrawRecorder.NoteWorldBatchRestart(sortMode, samplerState);
            }
        }

        /// <summary>Before MonoGame draws one run of one texture: a soft sheet reads linearly,
        /// anything else reads as its batch asked.</summary>
        private static void FlushVertexArray_Prefix(object __instance, Texture texture)
        {
            if (!_samplingByBatcher.TryGetValue(__instance, out BatchSampling? sampling))
                return;
            if (_linearForSoftSheets || _linearRuns.Count > 0)
            {
                bool linear = texture is Texture2D sheet
                    && ((_linearForSoftSheets && SoftSprites.IsOwnOutput(sheet)) || _linearRuns.Contains(sheet));
                SamplerState wanted = linear ? SamplerState.LinearClamp : sampling.Sampler;
                if (!ReferenceEquals(sampling.Applied, wanted))
                {
                    sampling.Device.SamplerStates[0] = wanted;
                    sampling.Applied = wanted;
                }
            }
            // radiance_resample: break MonoGame's per-texture memory so the Point filter is written again.
            if (ResampleFramesLeft > 0 && ReferenceEquals(__instance, _gameBatcher) && sampling.Sampler.Filter == TextureFilter.Point)
            {
                _pointAgainToggle = !_pointAgainToggle;
                sampling.Device.SamplerStates[0] = _pointAgainToggle ? _pointAgainA : _pointAgainB;
                sampling.Applied = null;
            }
            // After a radiance_drawsat question: what this run of the game's batch is read with.
            if (SpriteDrawRecorder.FlushWatchOpen && ReferenceEquals(__instance, _gameBatcher) && texture is Texture2D flushed)
                SpriteDrawRecorder.NoteFlush(flushed, sampling.Device.SamplerStates[0], sampling.Sampler);
        }

        internal static void Install(Harmony harmony, IMonitor monitor)
        {
            // Re-entered by radiance_hooks on, after an off: count the overloads afresh.
            PatchedOverloads = 0;
            // The sampler that follows the texture (see _linearForSoftSheets): the batch's Begin
            // to learn what it asked for, and the batcher's flush to apply it per texture.
            try
            {
                Type? batcherType = AccessTools.TypeByName("Microsoft.Xna.Framework.Graphics.SpriteBatcher");
                var flush = batcherType == null ? null : AccessTools.Method(batcherType, "FlushVertexArray");
                var begin = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Begin));
                if (batcherType != null && flush != null && begin != null)
                {
                    _batcherOf = AccessTools.FieldRefAccess<SpriteBatch, object>("_batcher");
                    _batcherDevice = AccessTools.Field(batcherType, "_device");
                    harmony.Patch(begin, postfix: new HarmonyMethod(typeof(SheetUpscaler), nameof(Begin_Postfix)));
                    harmony.Patch(flush, prefix: new HarmonyMethod(typeof(SheetUpscaler), nameof(FlushVertexArray_Prefix)));
                }
                else
                    monitor.Log("SpriteBatcher.FlushVertexArray not found; the soft look will be sampled as the batch is.", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                _batcherOf = null;
                monitor.Log($"Could not patch the sprite batcher's flush ({ex.GetType().Name}: {ex.Message}); the soft look will be sampled as the batch is.", LogLevel.Warn);
            }
            (Type[] signature, string handler)[] overloads =
            {
                (new[] { typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(Vector2), typeof(SpriteEffects), typeof(float) },
                    nameof(DrawVectorScale_Prefix)),
                (new[] { typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(float), typeof(SpriteEffects), typeof(float) },
                    nameof(DrawFloatScale_Prefix)),
                (new[] { typeof(Texture2D), typeof(Rectangle), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(SpriteEffects), typeof(float) },
                    nameof(DrawDestination_Prefix)),
            };
            foreach ((Type[] signature, string handler) in overloads)
            {
                var draw = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), signature);
                if (draw == null)
                {
                    monitor.Log($"SpriteBatch.Draw overload for {handler} not found; sheet upscaling will miss those draws.", LogLevel.Warn);
                    continue;
                }
                harmony.Patch(draw, prefix: new HarmonyMethod(typeof(SheetUpscaler), handler));
                PatchedOverloads++;
            }
        }

        /// <summary>One sprite of the soft look, baked: the xBR kernel (see SheetXbr in sheetscale.fx)
        /// over its own rectangle of the sheet to four times its texels, then the tent. Baked once
        /// per (sheet, rectangle) and kept.</summary>
        private static bool SoftSpriteBake(GraphicsDevice device, SpriteBatch batch, Effect effect, Texture2D sheet, Rectangle rect, RenderTarget2D target)
        {
            RenderTarget2D? kernelOutput = null;
            try
            {
                bool soften = SoftBlurTexels > 0.01f;
                // With a tent to follow, the kernel draws into a scratch of the target's size and
                // the tent reads it into the target; without one it draws into the target itself.
                RenderTarget2D kernelTarget = target;
                if (soften)
                    kernelTarget = kernelOutput = new RenderTarget2D(device, target.Width, target.Height, false, SurfaceFormat.Color, DepthFormat.None);
                device.SetRenderTarget(kernelTarget);
                device.Clear(Color.Transparent);
                effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / sheet.Width, 1f / sheet.Height));
                effect.Parameters["TargetSize"]?.SetValue(new Vector2(target.Width, target.Height));
                effect.Parameters["SourceRect"]?.SetValue(new Vector4(rect.X, rect.Y, rect.Width, rect.Height));
                effect.Parameters["Smoothness"]?.SetValue(_bakedSmoothnessByFamily[(int)_softBakeFamily]);
                effect.Parameters["EdgeSoftness"]?.SetValue(SoftEdgeSourcePixels);
                effect.Parameters["EqualThreshold"]?.SetValue(SoftEqualThreshold);
                effect.CurrentTechnique = effect.Techniques["SheetXbr"];
                batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                    DepthStencilState.None, RasterizerState.CullNone, effect);
                batch.Draw(sheet, new Rectangle(0, 0, target.Width, target.Height), Color.White);
                batch.End();
                if (soften)
                {
                    device.SetRenderTarget(target);
                    device.Clear(Color.Transparent);
                    effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / target.Width, 1f / target.Height));
                    effect.Parameters["SoftRadius"]?.SetValue(SoftBlurTexels);
                    effect.CurrentTechnique = effect.Techniques["SheetSoften"];
                    batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                        DepthStencilState.None, RasterizerState.CullNone, effect);
                    batch.Draw(kernelOutput!, new Rectangle(0, 0, target.Width, target.Height), Color.White);
                    batch.End();
                }
                return true;
            }
            catch
            {
                try { batch.End(); } catch { }
                return false;
            }
            finally
            {
                kernelOutput?.Dispose();
            }
        }

        /// <summary>Whether a draw at this many screen pixels per DERIVED texel is worth redirecting.
        /// The doubled sheet wants two, or a whole number (see MinimumDoubledScale). A soft sprite
        /// is already soft, so a dropped row here and there is invisible: one pixel a texel is
        /// enough, which keeps the hover pulse (4x to 4.4x) on the soft sprite the whole way.</summary>
        private static bool DrawnLargeEnough(float pixelsPerTexel, bool soft)
        {
            if (soft)
                return pixelsPerTexel >= 1f - 0.001f;
            if (pixelsPerTexel >= MinimumDoubledScale - 0.001f)
                return true;
            return pixelsPerTexel >= 1f - 0.001f && Math.Abs(pixelsPerTexel - (float)Math.Round(pixelsPerTexel)) < 0.001f;
        }

        /// <summary>The derived texture for this draw, the source rectangle to read it with, and how
        /// many texels it holds per texel of the original; null leaves the draw alone. The soft
        /// look answers with a sprite of its own (the whole target is the sprite); the doubled look
        /// answers with the doubled sheet and the rectangle doubled.</summary>
        private static Texture2D? Derived(SpriteBatch batch, Texture2D texture, Rectangle? sourceRectangle, float drawScale,
            out Rectangle derivedSource, out int factor)
        {
            factor = 1;
            derivedSource = default;
            if (!Active || SuspendedForOwnDraw || !ReferenceEquals(batch, Game1.spriteBatch) || texture == null || texture.IsDisposed)
                return null;
            // Only ART. A render target is a picture of the frame - the game's own screen being
            // presented, this mod's effect chain copying its buffers - and doubling those made
            // 300 MB of copies a frame and smoothed the whole picture ten times over. A texel or
            // two is a colour swatch, not art.
            if (texture is RenderTarget2D || texture.Width < 8 || texture.Height < 8)
                return null;
            ArtFamily family = FamilyOf(texture);
            if (!FamilyEnabled(family))
                return null;
            Rectangle source = sourceRectangle ?? texture.Bounds;
            if (Style == SheetSmoothingStyle.Soft4x && DrawnLargeEnough(drawScale / SoftScale, soft: true))
            {
                // A rectangle that reaches past the sheet is drawn by the game with the sheet
                // clamped; it is left to the game rather than baked from pixels that are not there.
                if (source.X >= 0 && source.Y >= 0 && source.Right <= texture.Width && source.Bottom <= texture.Height)
                {
                    _softBakeFamily = family;
                    Texture2D? sprite = SoftSprites.For(Device!, Effect!, texture, source, (int)family);
                    if (sprite != null)
                    {
                        derivedSource = new Rectangle(0, 0, sprite.Width, sprite.Height);
                        factor = SoftScale;
                        return sprite;
                    }
                }
                // Refused or capped this frame: the doubled sheet stands in, as it did before.
            }
            float pixelsPerDoubledTexel = drawScale / Scale;
            bool evenRead = DrawnLargeEnough(pixelsPerDoubledTexel, soft: false);
            // Not even, but at least one screen pixel a doubled texel, and in the interface: the
            // draw still goes to the doubled sheet and its run is read linearly (see _linearRuns),
            // which is even at any size. Only the interface, where the game draws at such sizes
            // all the time (the toolbar's 3.2x): in the world an odd size is an animation, a slime
            // squashing or a tree shaking, and a sprite that went soft while it moved and crisp
            // when it stopped would be its own report. Below one pixel a texel the sheet would be
            // minified and is left to the game either way.
            bool linearRead = !evenRead && Game1.uiMode && pixelsPerDoubledTexel >= 1f - 0.001f;
            if (!evenRead && !linearRead)
                return null;
            Texture2D? doubled = Cache.For(Device!, Effect!, texture, (int)family);
            if (doubled == null)
                return null;
            if (linearRead)
                _linearRuns.Add(doubled);
            else
                _linearRuns.Remove(doubled);
            derivedSource = new Rectangle(source.X * Scale, source.Y * Scale, source.Width * Scale, source.Height * Scale);
            factor = Scale;
            return doubled;
        }

        /// <summary>Which art family a sheet belongs to. The portrait check comes first because a
        /// portrait is drawn in UI mode too, and it has its own switch and dial precisely so a
        /// player can keep the menus crisp while smoothing the faces, or the other way.</summary>
        private static ArtFamily FamilyOf(Texture2D texture)
        {
            string name = texture.Name ?? "";
            if (name.StartsWith("Portraits", StringComparison.OrdinalIgnoreCase))
                return ArtFamily.Portraits;
            if (name.StartsWith("Characters", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Animals", StringComparison.OrdinalIgnoreCase))
                return ArtFamily.Characters;
            // Everything drawn in UI mode is the interface, items included: a tool in the toolbar
            // or the inventory follows the Menus switch and dial, and the same tool lying on the
            // ground follows Items. The families are named for where the player sees them, and the
            // author chose that over "the same sheet reads the same everywhere" once the toolbar
            // could be smoothed at all (see _linearRuns).
            if (Game1.uiMode)
                return ArtFamily.Interface;
            // Items lying in the world are their own family, known by their sheet, so a placed
            // object, tool or piece of furniture can be rounded differently from the terrain.
            if (IsItemSheet(name))
                return ArtFamily.Items;
            return ArtFamily.World;
        }

        /// <summary>Whether the family's switch is on.</summary>
        private static bool FamilyEnabled(ArtFamily family) => family switch
        {
            ArtFamily.Portraits => PortraitsEnabled,
            ArtFamily.Characters => CharactersEnabled,
            ArtFamily.Items => ItemsEnabled,
            ArtFamily.Interface => InterfaceEnabled,
            _ => WorldEnabled,
        };

        /// <summary>The game's own item sheets by content path: objects, the second object sheet,
        /// tools, weapons, big craftables and the furniture sheets. A content pack's own item
        /// sheet under Mods/ is not one of these and counts as the world.</summary>
        private static bool IsItemSheet(string name)
        {
            string path = name.Replace('\\', '/');
            return path.StartsWith("Maps/springobjects", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("TileSheets/Objects_2", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("TileSheets/tools", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("TileSheets/weapons", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("TileSheets/Craftables", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("TileSheets/furniture", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The least a DOUBLED sheet may be drawn at, in screen pixels per doubled texel.
        ///
        /// <para>The game draws its menus with point sampling, and a doubled sheet at scale 2 is
        /// exact under it: every doubled texel is a 2x2 block of pixels, the picture is the
        /// original with its corners rounded. But not everything is drawn at 4x. The toolbar
        /// draws each item at 0.8 or 0.9 of that (3.2x and 3.6x), a quality star and a stack
        /// count at 3x, a small icon here and there at 2x. Halved for the doubled sheet those are
        /// 1.6, 1.8, 1.5 and 1 pixel per texel, and under point sampling a texel is then one
        /// pixel wide or two, with no pattern the eye forgives: a stack count wobbles, a star has
        /// a chunk out of it, the toolbar's items look torn. Vanilla at 3.2x has texels three or
        /// four pixels wide, which nobody notices. This was reported as "the vast majority of items
        /// in the inventory look pixelated and glitched", by two people in one thread, with the
        /// items themselves - drawn at 4x in the grid - measured unchanged.</para>
        ///
        /// <para>At two pixels per doubled texel and above the worst unevenness is two against
        /// three, no worse than the game's own at the same size, so the doubled sheet stays.
        /// Below it the draw is left as the game made it, which is exactly what the player saw
        /// before switching the smoothing on. The pulse an item makes when hovered (4x to 4.4x)
        /// stays above the line, so nothing flips as it grows.</para></summary>
        private const float MinimumDoubledScale = 2f;

        private static void DrawVectorScale_Prefix(SpriteBatch __instance, ref Texture2D texture, ref Rectangle? sourceRectangle, ref Vector2 origin, ref Vector2 scale)
        {
            Texture2D? derived = Derived(__instance, texture, sourceRectangle, Math.Min(scale.X, scale.Y), out Rectangle derivedSource, out int factor);
            if (derived == null)
                return;
            sourceRectangle = derivedSource;
            texture = derived;
            // The origin is in source texels, so it scales with them, or every sprite hung from
            // its base (a tree from (24, 96)) slides by half its origin.
            origin *= factor;
            scale /= factor;
            RedirectedThisFrame++;
        }

        private static void DrawFloatScale_Prefix(SpriteBatch __instance, ref Texture2D texture, ref Rectangle? sourceRectangle, ref Vector2 origin, ref float scale)
        {
            Texture2D? derived = Derived(__instance, texture, sourceRectangle, scale, out Rectangle derivedSource, out int factor);
            if (derived == null)
                return;
            sourceRectangle = derivedSource;
            texture = derived;
            origin *= factor;
            scale /= factor;
            RedirectedThisFrame++;
        }

        private static void DrawDestination_Prefix(SpriteBatch __instance, ref Texture2D texture, Rectangle destinationRectangle, ref Rectangle? sourceRectangle, ref Vector2 origin)
        {
            // The scale is implied here: the destination over the source, per axis.
            if (texture == null)
                return;
            Rectangle impliedSource = sourceRectangle ?? texture.Bounds;
            float impliedScale = Math.Min(destinationRectangle.Width / (float)Math.Max(1, impliedSource.Width),
                                          destinationRectangle.Height / (float)Math.Max(1, impliedSource.Height));
            Texture2D? derived = Derived(__instance, texture, sourceRectangle, impliedScale, out Rectangle derivedSource, out int factor);
            if (derived == null)
                return;
            sourceRectangle = derivedSource;
            texture = derived;
            // With a destination rectangle the origin is still in source texels (the batch scales
            // it by destination over source), so it scales too.
            origin *= factor;
            RedirectedThisFrame++;
        }

        /// <summary>Once a frame: reset the counter, sweep reloaded sheets' ghosts, and hand the
        /// sheets back once switched off.</summary>
        internal static void BeginFrame()
        {
            RedirectedThisFrame = 0;
            _linearRuns.Clear();
            if (ResampleFramesLeft > 0)
                ResampleFramesLeft--;
            _linearForSoftSheets = Enabled && Style == SheetSmoothingStyle.Soft4x && _batcherOf != null;
            bool dialsMoved = false;
            for (int family = 0; family < FamilyCount; family++)
                dialsMoved |= _bakedSmoothnessByFamily[family] != SmoothnessByFamily[family];
            if (Enabled && (dialsMoved || _bakedStyle != Style || _bakedSoftEdge != SoftEdgeSourcePixels || _bakedSoftBlur != SoftBlurTexels))
            {
                // The dials, the style, the edge width and the tent are baked into the sheets, so
                // every held sheet is at the OLD value: hand them back and let the next draws re-make them.
                Array.Copy(SmoothnessByFamily, _bakedSmoothnessByFamily, FamilyCount);
                _bakedStyle = Style;
                _bakedSoftEdge = SoftEdgeSourcePixels;
                _bakedSoftBlur = SoftBlurTexels;
                if (Cache.Count > 0)
                    Cache.Clear();
                if (SoftSprites.Count > 0)
                    SoftSprites.Clear();
            }
            if (Enabled)
            {
                Cache.SweepDisposed();
                SoftSprites.SweepDisposed();
            }
            else
            {
                if (Cache.Count > 0)
                    Cache.Clear();
                if (SoftSprites.Count > 0)
                    SoftSprites.Clear();
            }
        }

        internal static void Dispose()
        {
            Cache.Dispose();
            SoftSprites.Dispose();
        }
    }
}
