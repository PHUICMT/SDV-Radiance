using System;
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
        /// <summary>The four art families, each with its own switch, set per frame from the config.
        /// Portraits and characters are named by their sheet's content path; the rest divides by
        /// WHEN the draw happens - the game draws menus, dialogue and the HUD in UI mode.</summary>
        internal static bool WorldEnabled = true;
        internal static bool CharactersEnabled = true;
        internal static bool PortraitsEnabled = true;
        internal static bool InterfaceEnabled;
        /// <summary>0 keeps the game's own pixels, 1 is the full Scale2x rounding. Baked into the
        /// doubled sheets, so a change re-makes the cache once instead of costing every frame.</summary>
        internal static float Smoothness = 1f;
        private static float _bakedSmoothness = 1f;
        internal static GraphicsDevice? Device;
        internal static Effect? Effect;
        private const int Scale = 2;
        /// <summary>Sheets up to 2048x2048 (16 MB) are doubled; a 4096 content-pack sheet would be 256 MB.</summary>
        internal static readonly SheetDerivedCache Cache = new("upscaled sheets", 384L * 1024 * 1024, 16L * 1024 * 1024, Scale, 4, "SheetScale",
            (effect, sheet, _) =>
            {
                effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / sheet.Width, 1f / sheet.Height));
                effect.Parameters["TargetSize"]?.SetValue(new Vector2(sheet.Width * Scale, sheet.Height * Scale));
                effect.Parameters["Smoothness"]?.SetValue(_bakedSmoothness);
            });
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

        internal static void Install(Harmony harmony, IMonitor monitor)
        {
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

        /// <summary>The doubled sheet for this draw, or null to leave the draw alone.</summary>
        private static Texture2D? Doubled(SpriteBatch batch, Texture2D texture)
        {
            if (!Active || SuspendedForOwnDraw || !ReferenceEquals(batch, Game1.spriteBatch) || texture == null || texture.IsDisposed)
                return null;
            // Only ART. A render target is a picture of the frame - the game's own screen being
            // presented, this mod's effect chain copying its buffers - and doubling those made
            // 300 MB of copies a frame and smoothed the whole picture ten times over. A texel or
            // two is a colour swatch, not art.
            if (texture is RenderTarget2D || texture.Width < 8 || texture.Height < 8)
                return null;
            if (!FamilyWantsDoubling(texture))
                return null;
            return Cache.For(Device!, Effect!, texture, 0);
        }

        /// <summary>Whether this sheet's art family has its switch on. The portrait check comes
        /// first because a portrait is drawn in UI mode too, and it has its own switch precisely
        /// so a player can keep the menus crisp while smoothing the faces, or the other way.</summary>
        private static bool FamilyWantsDoubling(Texture2D texture)
        {
            string name = texture.Name ?? "";
            if (name.StartsWith("Portraits", StringComparison.OrdinalIgnoreCase))
                return PortraitsEnabled;
            if (name.StartsWith("Characters", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Animals", StringComparison.OrdinalIgnoreCase))
                return CharactersEnabled;
            if (Game1.uiMode)
                return InterfaceEnabled;
            return WorldEnabled;
        }

        private static Rectangle DoubledSource(Texture2D original, Rectangle? sourceRectangle)
        {
            Rectangle source = sourceRectangle ?? original.Bounds;
            return new Rectangle(source.X * Scale, source.Y * Scale, source.Width * Scale, source.Height * Scale);
        }

        private static void DrawVectorScale_Prefix(SpriteBatch __instance, ref Texture2D texture, ref Rectangle? sourceRectangle, ref Vector2 origin, ref Vector2 scale)
        {
            Texture2D? doubled = Doubled(__instance, texture);
            if (doubled == null)
                return;
            sourceRectangle = DoubledSource(texture, sourceRectangle);
            texture = doubled;
            // The origin is in source texels, so it doubles with them, or every sprite hung from
            // its base (a tree from (24, 96)) slides by half its origin.
            origin *= Scale;
            scale /= Scale;
            RedirectedThisFrame++;
        }

        private static void DrawFloatScale_Prefix(SpriteBatch __instance, ref Texture2D texture, ref Rectangle? sourceRectangle, ref Vector2 origin, ref float scale)
        {
            Texture2D? doubled = Doubled(__instance, texture);
            if (doubled == null)
                return;
            sourceRectangle = DoubledSource(texture, sourceRectangle);
            texture = doubled;
            origin *= Scale;
            scale /= Scale;
            RedirectedThisFrame++;
        }

        private static void DrawDestination_Prefix(SpriteBatch __instance, ref Texture2D texture, ref Rectangle? sourceRectangle, ref Vector2 origin)
        {
            Texture2D? doubled = Doubled(__instance, texture);
            if (doubled == null)
                return;
            sourceRectangle = DoubledSource(texture, sourceRectangle);
            texture = doubled;
            // With a destination rectangle the origin is still in source texels (the batch scales
            // it by destination over source), so it doubles too.
            origin *= Scale;
            RedirectedThisFrame++;
        }

        /// <summary>Once a frame: reset the counter, sweep reloaded sheets' ghosts, and hand the
        /// sheets back once switched off.</summary>
        internal static void BeginFrame()
        {
            RedirectedThisFrame = 0;
            if (Enabled && _bakedSmoothness != Smoothness)
            {
                // The dial is baked into the sheets, so every held sheet is at the OLD value:
                // hand them all back and let the next draws re-make them at the new one.
                _bakedSmoothness = Smoothness;
                if (Cache.Count > 0)
                    Cache.Clear();
            }
            if (Enabled)
                Cache.SweepDisposed();
            else if (Cache.Count > 0)
                Cache.Clear();
        }

        internal static void Dispose() => Cache.Dispose();
    }
}
