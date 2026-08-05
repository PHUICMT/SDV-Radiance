using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Per-frame gates that hide the game's own baked blob shadows while this mod's
    /// directional shadows are casting, plus the Harmony shims that do the hiding.
    /// ModEntry refreshes the flags every tick (see OnUpdateTicked); HarmonyPatcher
    /// installs the shims once at startup.
    /// </summary>
    internal static class ShadowSuppression
    {
        /// <summary>The layerDepth vanilla uses for a grown tree/bush canopy blob shadow —
        /// exactly 1E-06f, an intentional fingerprint the tree/bush shims key on.</summary>
        private const float VanillaCanopyShadowDepth = 1E-06f;

        /// <summary>When true, the vanilla blob shadow is skipped (we draw a directional one instead).</summary>
        internal static bool SuppressVanillaShadows;

        /// <summary>When true, vanilla tree/bush baked blob shadows are skipped (our object shadows replace them).</summary>
        internal static bool SuppressVanillaObjectShadows;

        /// <summary>When true, vanilla <see cref="Game1.shadowTexture"/> blob shadows (big craftables) are
        /// skipped. Gated on ShadowsActiveNow so it also covers the indoor/night ambient path.</summary>
        internal static bool SuppressVanillaBlobShadows;

        /// <summary>When true, critters' vanilla blob shadows are skipped (our directional critter
        /// shadows replace them — sun path only, so rainy days keep the vanilla blob).</summary>
        internal static bool SuppressVanillaCritterShadows;

        /// <summary>When true, the vanilla drifting Cloud critter shadow is hidden.</summary>
        internal static bool SuppressVanillaClouds;

        /// <summary>Skip the game's blob shadow while our directional shadow is active.</summary>
        /// <remarks>
        /// Seated characters are NOT exempted here. Handing them back to vanilla was the first
        /// attempt and it read as "no shadow at all when sitting": vanilla draws its blob at the
        /// standing feet, which for a sitter is behind the seat art, so nothing is visible.
        /// Seated NPCs get our own grounding pool at the DRAWN position instead
        /// (<see cref="ShadowRenderer.IsSeated"/>); the player keeps their normal silhouette.
        /// </remarks>
        internal static bool DrawShadow_Prefix() => !SuppressVanillaShadows;

        /// <summary>Skip the vanilla <c>Cloud</c> critter's drifting shadow draw.</summary>
        internal static bool Cloud_Draw_Prefix() => !SuppressVanillaClouds;

        /// <summary>
        /// Transpiler shim for Tree.draw / Bush.draw: swallow the vanilla blob shadow while our
        /// object shadows are active, and forward everything else.
        /// <para>
        /// The grown-canopy shadow is the one drawn at layerDepth exactly 1E-06f, and keying on
        /// that alone is what left saplings and stumps with a vanilla blob while every grown tree
        /// beside them wore ours — they draw their shadow at their own depth, so the test never
        /// matched them. Inside a tree/bush draw, a <see cref="Game1.shadowTexture"/> draw IS the
        /// shadow whatever depth it carries, which is the same rule the craftable shim uses.
        /// </para>
        /// </summary>
        public static void Draw_SkipVanillaShadow(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, float scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaObjectShadows && (layerDepth == VanillaCanopyShadowDepth || ReferenceEquals(texture, Game1.shadowTexture)))
                return;
            spriteBatch.Draw(texture, pos, src, color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Vector2-scale twin of <see cref="Draw_SkipVanillaShadow"/>.</summary>
        public static void Draw_SkipVanillaShadowV(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, Vector2 scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaObjectShadows && (layerDepth == VanillaCanopyShadowDepth || ReferenceEquals(texture, Game1.shadowTexture)))
                return;
            spriteBatch.Draw(texture, pos, src, color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Shim for Object.draw: drop the vanilla <see cref="Game1.shadowTexture"/> blob (big
        /// craftables draw it at an object-specific depth, so we key on the texture, not layerDepth).</summary>
        public static void Draw_SkipBlobShadow(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, float scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaBlobShadows && ReferenceEquals(texture, Game1.shadowTexture))
                return;
            spriteBatch.Draw(texture, pos, src, color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Vector2-scale twin of <see cref="Draw_SkipBlobShadow"/>.</summary>
        public static void Draw_SkipBlobShadowV(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, Vector2 scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaBlobShadows && ReferenceEquals(texture, Game1.shadowTexture))
                return;
            spriteBatch.Draw(texture, pos, src, color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Shim for Critter draw methods: drop only their Game1.shadowTexture blob.</summary>
        public static void Draw_SkipCritterShadow(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, float scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaCritterShadows && ReferenceEquals(texture, Game1.shadowTexture))
                return;
            spriteBatch.Draw(texture, pos, src, color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Vector2-scale twin of <see cref="Draw_SkipCritterShadow"/>.</summary>
        public static void Draw_SkipCritterShadowV(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, Vector2 scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaCritterShadows && ReferenceEquals(texture, Game1.shadowTexture))
                return;
            spriteBatch.Draw(texture, pos, src, color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Redirect a method's 9-arg SpriteBatch.Draw calls through <paramref name="shimName"/>.</summary>
        private static System.Collections.Generic.IEnumerable<CodeInstruction> RedirectDraws(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions, string shimName)
        {
            // SpriteBatch.Draw has TWO overloads with this shape, differing only in whether scale
            // is a float or a Vector2, and redirecting just the float one leaves every draw that
            // used the other invisible to the shim — which is a hole, not a filter: a shadow drawn
            // through the Vector2 overload was never offered for suppression, so it survived
            // alongside ours as a second shadow.
            var drawWithFloatScale = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), new[]
            {
                typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color),
                typeof(float), typeof(Vector2), typeof(float), typeof(SpriteEffects), typeof(float)
            });
            var drawWithVectorScale = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), new[]
            {
                typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color),
                typeof(float), typeof(Vector2), typeof(Vector2), typeof(SpriteEffects), typeof(float)
            });
            var floatScaleShim = AccessTools.Method(typeof(ShadowSuppression), shimName);
            var vectorScaleShim = AccessTools.Method(typeof(ShadowSuppression), shimName + "V");
            foreach (var instruction in instructions)
            {
                if (drawWithFloatScale != null && instruction.Calls(drawWithFloatScale))
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, floatScaleShim) { labels = instruction.labels, blocks = instruction.blocks };
                else if (drawWithVectorScale != null && vectorScaleShim != null && instruction.Calls(drawWithVectorScale))
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, vectorScaleShim) { labels = instruction.labels, blocks = instruction.blocks };
                else
                    yield return instruction;
            }
        }

        /// <summary>Tree/Bush: drop the depth==1E-06 blob draws.</summary>
        internal static System.Collections.Generic.IEnumerable<CodeInstruction> DrawShadow_Transpiler(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
            => RedirectDraws(instructions, nameof(Draw_SkipVanillaShadow));

        /// <summary>Object.draw: drop the Game1.shadowTexture blob draws.</summary>
        internal static System.Collections.Generic.IEnumerable<CodeInstruction> BlobShadow_Transpiler(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
            => RedirectDraws(instructions, nameof(Draw_SkipBlobShadow));

        /// <summary>Critter draw methods: drop the Game1.shadowTexture blob draws.</summary>
        internal static System.Collections.Generic.IEnumerable<CodeInstruction> CritterShadow_Transpiler(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
            => RedirectDraws(instructions, nameof(Draw_SkipCritterShadow));
    }
}
