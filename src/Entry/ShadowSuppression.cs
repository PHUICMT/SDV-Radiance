using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
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

        /// <summary>Character classes from other mods that draw a <see cref="Game1.shadowTexture"/>
        /// blob of their own inside their own draw. Custom Companions does this for every companion:
        /// it sets HideShadow so the game draws no blob, then draws one itself, scaled to its model.
        /// Read by the shadow pass, which treats such a class as a caster (it wants a shadow, it just
        /// paints its own), while the shim below swallows the blob it paints. Filled by the draw
        /// itself rather than by class name, so a companion configured to have no shadow never
        /// draws one and never lands here.</summary>
        internal static readonly System.Collections.Generic.HashSet<System.Type> SelfShadowedCharacterTypes = new();

        /// <summary>How many blob shadows painted by another mod's creature we swallowed, and how
        /// many reached the screen anyway, counted for the frame radiance_shadows is asked about.
        ///
        /// <para>A round patch under a creature can be the blob its own mod paints or our own
        /// silhouette laid flat, and at four times scale on sand the two are the same picture. The
        /// only difference that can be READ is whether the draw came through this shim at all: a
        /// mod that paints its blob from a method which does not take a SpriteBatch never passes
        /// through here, and its blob is one we cannot see, let alone drop. A blob on the screen
        /// with nothing in "let through" is the signature of that hole.</para></summary>
        internal static int BlobDrawsSwallowed;

        /// <inheritdoc cref="BlobDrawsSwallowed"/>
        internal static int BlobDrawsLetThrough;

        /// <summary>Start a fresh count, so the numbers belong to one frame rather than to the
        /// whole session.</summary>
        internal static void ResetBlobTally()
        {
            BlobDrawsSwallowed = 0;
            BlobDrawsLetThrough = 0;
            VanillaShadowSuppressed.Clear();
        }

        /// <summary>The class whose own draw is running right now, set by the prefix on every
        /// patched draw, so the shim can say whose blob it just saw.</summary>
        private static System.Type? _characterDrawing;

        /// <summary>Every draw method the patch above took, for radiance_shadows.</summary>
        internal static readonly System.Collections.Generic.List<string> PatchedCharacterDraws = new();

        /// <summary>
        /// Art the game asked to read past the edge of, by texture name and how many times.
        /// See <see cref="RepairSourceRect"/>; reported by radiance_report so the pack can be
        /// named rather than guessed at.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, int> _artReadPastItsEdge = new();

        /// <summary>
        /// Keep a draw inside its own texture.
        ///
        /// <para>
        /// Stardew 1.6 added COLUMNS to several sheets - a tree gained a mossy variant at x=96,
        /// for one - and an art pack written before 1.6 simply does not have them. The game still
        /// asks for the new column, the graphics card reads past the right edge of the sheet, and
        /// what comes back is the clamped edge pixel: a transparent margin, so the tree vanishes
        /// entirely, or a smear of the last column, which reads as one tile at a quarter of the
        /// resolution of its neighbours. A mossy oak in winter under one popular recolour is
        /// invisible until the moss is knocked off it.
        /// </para>
        ///
        /// <para>
        /// Nothing about that is this mod's doing, and nothing about it is the player's to fix.
        /// But every one of those draws passes through our shims on its way to the screen, so
        /// stepping the rectangle back by whole columns until it lands inside the sheet costs a
        /// couple of comparisons and turns an invisible tree into the same tree without its moss.
        /// The name of the offending texture goes in the report either way: a repair that hides
        /// the problem from the pack's author would be worse than the bug.
        /// </para>
        /// </summary>
        internal static Rectangle? RepairSourceRect(Texture2D? texture, Rectangle? source)
        {
            if (texture == null || texture.IsDisposed || !source.HasValue)
                return source;
            Rectangle wanted = source.Value;
            if (wanted.X >= 0 && wanted.Y >= 0
                && wanted.Right <= texture.Width && wanted.Bottom <= texture.Height)
                return source;

            string name = string.IsNullOrEmpty(texture.Name) ? "an unnamed texture" : texture.Name;
            _artReadPastItsEdge.TryGetValue(name, out int seen);
            _artReadPastItsEdge[name] = seen + 1;

            // Step back by whole rectangle widths first: the missing thing is a VARIANT column,
            // so the column to its left is the same sprite without the variant, which is exactly
            // what should be drawn instead. Clamping alone would have sliced one sprite in half.
            int repairedWidth = System.Math.Min(wanted.Width, texture.Width);
            int repairedHeight = System.Math.Min(wanted.Height, texture.Height);
            int x = wanted.X;
            while (x + repairedWidth > texture.Width && x - repairedWidth >= 0)
                x -= repairedWidth;
            int y = wanted.Y;
            while (y + repairedHeight > texture.Height && y - repairedHeight >= 0)
                y -= repairedHeight;
            x = System.Math.Clamp(x, 0, System.Math.Max(0, texture.Width - repairedWidth));
            y = System.Math.Clamp(y, 0, System.Math.Max(0, texture.Height - repairedHeight));
            return new Rectangle(x, y, repairedWidth, repairedHeight);
        }

        /// <summary>What the repair above has had to do this session, for radiance_report.</summary>
        internal static string DescribeRepairedArt()
        {
            if (_artReadPastItsEdge.Count == 0)
                return "art bounds: every draw stayed inside its own sheet";
            var report = new System.Text.StringBuilder("art bounds: ");
            report.Append(_artReadPastItsEdge.Count).Append(" sheet(s) drawn past their edge and snapped back ");
            report.Append("(art made before a game update that added sprite columns - not this mod, and not fixable in it):");
            foreach (var pair in _artReadPastItsEdge)
                report.Append("\n    ").Append(pair.Key).Append("  x").Append(pair.Value);
            return report.ToString();
        }

        /// <summary>Skip the game's blob shadow while our directional shadow is active.</summary>
        /// <remarks>
        /// Seated characters are NOT exempted here. Handing them back to vanilla was the first
        /// attempt and it read as "no shadow at all when sitting": vanilla draws its blob at the
        /// standing feet, which for a sitter is behind the seat art, so nothing is visible.
        /// Seated NPCs get our own grounding pool at the DRAWN position instead
        /// (<see cref="ShadowRenderer.IsSeated"/>); the player keeps their normal silhouette.
        /// </remarks>
        internal static bool DrawShadow_Prefix(Character __instance)
        {
            if (!SuppressVanillaShadows)
                return true;
            // Which classes we actually took a blob away from this frame. A round patch that
            // survives with the whole mod switched off is the game's own, and the only way to know
            // whether we were ever offered it is to count the offers: a horse that never appears
            // here is a horse whose blob is being drawn by something that does not go through
            // Character.DrawShadow at all, and no amount of reading Horse.cs will show that.
            string name = __instance?.GetType().Name ?? "null";
            VanillaShadowSuppressed.TryGetValue(name, out int seen);
            VanillaShadowSuppressed[name] = seen + 1;
            return false;
        }

        /// <inheritdoc cref="BlobDrawsSwallowed"/>
        internal static readonly System.Collections.Generic.Dictionary<string, int> VanillaShadowSuppressed = new();

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
            FrameCost.Count(FrameCost.Counter.ShimDraws);
            Rectangle? repaired = RepairSourceRect(texture, src);
            // The wind, if this is a canopy or a bush and the switch is on (see FoliageSway).
            if (FoliageSway.TryDraw(spriteBatch, texture, pos, repaired, color, rotation, origin, scale, effects, layerDepth))
                return;
            spriteBatch.Draw(texture, pos, repaired, color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Vector2-scale twin of <see cref="Draw_SkipVanillaShadow"/>.</summary>
        public static void Draw_SkipVanillaShadowV(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, Vector2 scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaObjectShadows && (layerDepth == VanillaCanopyShadowDepth || ReferenceEquals(texture, Game1.shadowTexture)))
                return;
            FrameCost.Count(FrameCost.Counter.ShimDraws);
            spriteBatch.Draw(texture, pos, RepairSourceRect(texture, src), color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Shim for Object.draw: drop the vanilla <see cref="Game1.shadowTexture"/> blob (big
        /// craftables draw it at an object-specific depth, so we key on the texture, not layerDepth).</summary>
        public static void Draw_SkipBlobShadow(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, float scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaBlobShadows && ReferenceEquals(texture, Game1.shadowTexture))
                return;
            FrameCost.Count(FrameCost.Counter.ShimDraws);
            spriteBatch.Draw(texture, pos, RepairSourceRect(texture, src), color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Vector2-scale twin of <see cref="Draw_SkipBlobShadow"/>.</summary>
        public static void Draw_SkipBlobShadowV(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, Vector2 scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaBlobShadows && ReferenceEquals(texture, Game1.shadowTexture))
                return;
            FrameCost.Count(FrameCost.Counter.ShimDraws);
            spriteBatch.Draw(texture, pos, RepairSourceRect(texture, src), color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Shim for Critter draw methods: drop only their Game1.shadowTexture blob.</summary>
        public static void Draw_SkipCritterShadow(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, float scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaCritterShadows && ReferenceEquals(texture, Game1.shadowTexture))
                return;
            FrameCost.Count(FrameCost.Counter.ShimDraws);
            spriteBatch.Draw(texture, pos, RepairSourceRect(texture, src), color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Vector2-scale twin of <see cref="Draw_SkipCritterShadow"/>.</summary>
        public static void Draw_SkipCritterShadowV(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, Vector2 scale,
            SpriteEffects effects, float layerDepth)
        {
            if (SuppressVanillaCritterShadows && ReferenceEquals(texture, Game1.shadowTexture))
                return;
            FrameCost.Count(FrameCost.Counter.ShimDraws);
            spriteBatch.Draw(texture, pos, RepairSourceRect(texture, src), color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Shim for the draw of a character class from another mod: remember that the class
        /// paints its own blob, and drop it while our directional shadows are casting.</summary>
        public static void Draw_SkipCharacterBlobShadow(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, float scale,
            SpriteEffects effects, float layerDepth)
        {
            if (ReferenceEquals(texture, Game1.shadowTexture))
            {
                if (_characterDrawing != null)
                    SelfShadowedCharacterTypes.Add(_characterDrawing);
                if (SuppressVanillaShadows)
                {
                    BlobDrawsSwallowed++;
                    return;
                }
                BlobDrawsLetThrough++;
            }
            FrameCost.Count(FrameCost.Counter.ShimDraws);
            spriteBatch.Draw(texture, pos, RepairSourceRect(texture, src), color, rotation, origin, scale, effects, layerDepth);
        }

        /// <summary>Vector2-scale twin of <see cref="Draw_SkipCharacterBlobShadow"/>.</summary>
        public static void Draw_SkipCharacterBlobShadowV(SpriteBatch spriteBatch, Texture2D texture, Vector2 pos,
            Rectangle? src, Color color, float rotation, Vector2 origin, Vector2 scale,
            SpriteEffects effects, float layerDepth)
        {
            if (ReferenceEquals(texture, Game1.shadowTexture))
            {
                if (_characterDrawing != null)
                    SelfShadowedCharacterTypes.Add(_characterDrawing);
                if (SuppressVanillaShadows)
                {
                    BlobDrawsSwallowed++;
                    return;
                }
                BlobDrawsLetThrough++;
            }
            FrameCost.Count(FrameCost.Counter.ShimDraws);
            spriteBatch.Draw(texture, pos, RepairSourceRect(texture, src), color, rotation, origin, scale, effects, layerDepth);
        }

        internal static void CharacterDraw_Prefix(NPC __instance) => _characterDrawing = __instance.GetType();

        internal static void CharacterDraw_Postfix() => _characterDrawing = null;

        /// <summary>Character draw methods declared by other mods: drop their Game1.shadowTexture blob draws.</summary>
        internal static System.Collections.Generic.IEnumerable<CodeInstruction> CharacterBlobShadow_Transpiler(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
            => RedirectDraws(instructions, nameof(Draw_SkipCharacterBlobShadow));

        /// <summary>
        /// Route every method a mod's character class declares that takes a SpriteBatch through the
        /// shim above. A mod creature that hides the vanilla blob to paint its own (Custom
        /// Companions) kept that blob under our directional shadows, and got none of ours because
        /// HideShadow read as "wants no shadow". Run at GameLaunched, when every mod's classes exist.
        /// Only classes outside the game's own assembly, and every method of theirs that is handed a
        /// SpriteBatch rather than only the ones called draw: Custom Companions' draw hands the batch
        /// to a DoDraw of its own and paints the blob there, so patching by name saw nothing.
        /// Vanilla NPCs already go through the DrawShadow prefix.
        /// </summary>
        internal static void PatchSelfDrawnCharacters(Harmony harmony, IMonitor monitor)
        {
            int patched = 0;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == typeof(NPC).Assembly)
                    continue;
                System.Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(ex.Types, t => t != null))!; }
                catch (Exception) { continue; }
                foreach (var type in types)
                {
                    if (type == null || !typeof(NPC).IsAssignableFrom(type))
                        continue;
                    System.Reflection.MethodInfo[] declared;
                    try
                    {
                        declared = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                    }
                    catch (Exception) { continue; }
                    foreach (var method in declared)
                    {
                        if (method.IsAbstract || method.IsGenericMethodDefinition || method.ContainsGenericParameters)
                            continue;
                        bool takesBatch = false;
                        foreach (var parameter in method.GetParameters())
                            if (parameter.ParameterType == typeof(SpriteBatch)) { takesBatch = true; break; }
                        if (!takesBatch)
                            continue;
                        try
                        {
                            harmony.Patch(method,
                                prefix: new HarmonyMethod(typeof(ShadowSuppression), nameof(CharacterDraw_Prefix)),
                                postfix: new HarmonyMethod(typeof(ShadowSuppression), nameof(CharacterDraw_Postfix)),
                                transpiler: new HarmonyMethod(typeof(ShadowSuppression), nameof(CharacterBlobShadow_Transpiler)));
                            patched++;
                            PatchedCharacterDraws.Add($"{type.Name}.{method.Name}");
                        }
                        catch (Exception ex)
                        {
                            monitor.Log($"Own-shadow patch skipped for {type.FullName}.{method.Name}: {ex.Message}", LogLevel.Trace);
                        }
                    }
                }
            }
            monitor.Log($"Watching {patched} character draw(s) from other mods for a blob shadow of their own.", LogLevel.Trace);
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
