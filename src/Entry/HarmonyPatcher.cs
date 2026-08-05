using System;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// Installs every Harmony patch this mod uses, and hosts the two whole-game gates
    /// those patches read: <see cref="ForceBufferDraw"/> (make the game render the world
    /// into its buffer so RenderedWorld always has a target to capture) and
    /// <see cref="FreezeGameWater"/> (pin the vanilla water frame-cycle while the shader
    /// ripple supplies the surface motion). ModEntry refreshes both gates from live config.
    /// </summary>
    internal static class HarmonyPatcher
    {
        /// <summary>
        /// Mirrors <see cref="ModConfig.Enabled"/> for the static Harmony postfix.
        /// When true, the game is forced to render the world into its buffer
        /// (Game1.screen) so we always have a target to capture during RenderedWorld.
        /// </summary>
        internal static bool ForceBufferDraw;

        /// <summary>
        /// When true, the game's jerky water FRAME-cycle (waterAnimationIndex, a
        /// ~5fps 10-frame gif) is pinned so our shader ripple supplies the surface
        /// motion. The smooth 1px vertical scroll (waterPosition) is left running.
        /// </summary>
        internal static bool FreezeGameWater;

        private static IMonitor? _monitor;
        private static bool _loggedFreeze;

        /// <summary>Install all game patches: buffer-draw forcing, water frame freeze, and
        /// the vanilla-shadow suppression shims (see <see cref="ShadowSuppression"/>).</summary>
        internal static void InstallAll(Harmony harmony, IMonitor monitor)
        {
            _monitor = monitor;

            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.ShouldDrawOnBuffer)),
                postfix: new HarmonyMethod(typeof(HarmonyPatcher), nameof(ShouldDrawOnBuffer_Postfix)));
            harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.updateWater)),
                postfix: new HarmonyMethod(typeof(HarmonyPatcher), nameof(UpdateWater_Postfix)));
            // Suppress the vanilla blob shadow while our directional shadow is casting,
            // so casters don't show both. Farmer overrides DrawShadow, so patch both.
            harmony.Patch(
                original: AccessTools.Method(typeof(Character), nameof(Character.DrawShadow)),
                prefix: new HarmonyMethod(typeof(ShadowSuppression), nameof(ShadowSuppression.DrawShadow_Prefix)));
            harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.DrawShadow)),
                prefix: new HarmonyMethod(typeof(ShadowSuppression), nameof(ShadowSuppression.DrawShadow_Prefix)));
            // Trees and bushes bake their blob shadow inline in draw() at a FIXED direction that
            // fights our directional cast; route their Draw calls through a shim that drops just
            // the depth==1E-06 (shadow) draws while our object shadows are active.
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.TerrainFeatures.Tree), nameof(StardewValley.TerrainFeatures.Tree.draw)),
                transpiler: new HarmonyMethod(typeof(ShadowSuppression), nameof(ShadowSuppression.DrawShadow_Transpiler)));
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.TerrainFeatures.Bush), nameof(StardewValley.TerrainFeatures.Bush.draw), new[] { typeof(SpriteBatch) }),
                transpiler: new HarmonyMethod(typeof(ShadowSuppression), nameof(ShadowSuppression.DrawShadow_Transpiler)));
            // Big craftables draw a vanilla Game1.shadowTexture blob in Object.draw(b,x,y,alpha);
            // drop it while our object shadows are active so it doesn't double up.
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.Object), nameof(StardewValley.Object.draw), new[] { typeof(SpriteBatch), typeof(int), typeof(int), typeof(float) }),
                transpiler: new HarmonyMethod(typeof(ShadowSuppression), nameof(ShadowSuppression.BlobShadow_Transpiler)));
            // The vanilla drifting cloud shadow is a Cloud critter drawn in drawAboveFrontLayer;
            // skip it (opt-out) so it doesn't compete with our own cloud-shadow effect.
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.BellsAndWhistles.Cloud), nameof(StardewValley.BellsAndWhistles.Cloud.drawAboveFrontLayer), new[] { typeof(SpriteBatch) }),
                prefix: new HarmonyMethod(typeof(ShadowSuppression), nameof(ShadowSuppression.Cloud_Draw_Prefix)));
            // Critters draw their own Game1.shadowTexture blob inside draw()/drawAboveFrontLayer()
            // (base class + several overrides). Route every Critter subclass's declared draw
            // methods through a shim that drops just those blob draws while ours are casting.
            foreach (var critterType in typeof(StardewValley.BellsAndWhistles.Critter).Assembly.GetTypes())
            {
                if (!typeof(StardewValley.BellsAndWhistles.Critter).IsAssignableFrom(critterType)
                    || critterType == typeof(StardewValley.BellsAndWhistles.Cloud))
                    continue;
                foreach (string methodName in new[] { "draw", "drawAboveFrontLayer" })
                {
                    var declaredDraw = critterType.GetMethod(methodName,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly,
                        null, new[] { typeof(SpriteBatch) }, null);
                    if (declaredDraw == null || declaredDraw.IsAbstract)
                        continue;
                    try
                    {
                        harmony.Patch(declaredDraw, transpiler: new HarmonyMethod(typeof(ShadowSuppression), nameof(ShadowSuppression.CritterShadow_Transpiler)));
                    }
                    catch (Exception ex)
                    {
                        monitor.Log($"Critter shadow patch skipped for {critterType.Name}.{methodName}: {ex.Message}", LogLevel.Trace);
                    }
                }
            }
        }

        /// <summary>Force the game to draw the world into its buffer so a render target is bound during graphics events.</summary>
        internal static void ShouldDrawOnBuffer_Postfix(ref bool __result)
        {
            if (ForceBufferDraw && Game1.gameMode == Game1.playingGameMode)
                __result = true;
        }

        /// <summary>
        /// Pin the game's jerky water frame-cycle (waterAnimationIndex) while our
        /// ripple is active. waterPosition (the smooth 1px vertical scroll) is left
        /// running so the water still gently rises and falls.
        /// </summary>
        internal static void UpdateWater_Postfix(GameLocation __instance)
        {
            if (!FreezeGameWater)
                return;
            __instance.waterAnimationIndex = 0;
            if (!_loggedFreeze) { _monitor?.Log("Water frame-cycle frozen (shader ripple active); vertical scroll left running.", LogLevel.Info); _loggedFreeze = true; }
        }
    }
}
