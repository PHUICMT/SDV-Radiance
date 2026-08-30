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
            HoldCrittersWhileFrozen(harmony, monitor);
            HoldMapAnimationWhileFrozen(harmony, monitor);
            HoldTemporarySpritesWhileFrozen(harmony, monitor);
            SpriteDrawRecorder.Install(harmony, monitor);
            SheetUpscaler.Install(harmony, monitor);
            // Replace the vanilla rain/snow draw on the days the player asked for ours. The
            // prefix skips vanilla only when the PrecipitationSystem gate says this exact frame
            // is ours; the postfix draws the replacement in the same slot (before the lightmap,
            // under the effect chain) so it darkens at night and grades with the world.
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.drawWeather)),
                prefix: new HarmonyMethod(typeof(PrecipitationSystem), nameof(PrecipitationSystem.DrawWeather_Prefix)),
                postfix: new HarmonyMethod(typeof(PrecipitationSystem), nameof(PrecipitationSystem.DrawWeather_Postfix)));
            PrecipitationSystem.Monitor = monitor;
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

            // The mine's floor number is painted into the WORLD layer, not the HUD, so it landed
            // in every captured frame: a badge in the corner of a gallery shot, and a caption that
            // CHANGES between two visits to the same floor, which is noise in a comparison the
            // harness is supposed to read as pixels. The game already knows to leave it out when a
            // picture is being taken - MineShaft.drawAboveAlwaysFrontLayer returns early on
            // takingMapScreenshot - so a capture borrows that flag for the length of that one call
            // and puts it back. Setting it for the whole frame is not the same thing: this mod
            // reads it in eight places, and one of them decides the render scale, so a frame
            // captured under it would come out a different size than the frame being compared.
            try
            {
                harmony.Patch(
                    original: AccessTools.Method(typeof(StardewValley.Locations.MineShaft),
                                                 nameof(StardewValley.Locations.MineShaft.drawAboveAlwaysFrontLayer),
                                                 new[] { typeof(SpriteBatch) }),
                    prefix: new HarmonyMethod(typeof(HarmonyPatcher), nameof(MineFloorNumber_Prefix)),
                    postfix: new HarmonyMethod(typeof(HarmonyPatcher), nameof(MineFloorNumber_Postfix)));
            }
            catch (Exception ex)
            {
                monitor.Log($"Mine floor number will stay in captured frames: {ex.Message}", LogLevel.Trace);
            }
        }

        /// <summary>What takingMapScreenshot was before a capture borrowed it.</summary>
        private static bool _mapScreenshotFlagWas;

        /// <inheritdoc cref="Apply"/>
        private static void MineFloorNumber_Prefix()
        {
            if (!RenderPipeline.DumpPending || Game1.game1 == null)
                return;
            _mapScreenshotFlagWas = Game1.game1.takingMapScreenshot;
            Game1.game1.takingMapScreenshot = true;
        }

        /// <inheritdoc cref="Apply"/>
        private static void MineFloorNumber_Postfix()
        {
            if (!RenderPipeline.DumpPending || Game1.game1 == null)
                return;
            Game1.game1.takingMapScreenshot = _mapScreenshotFlagWas;
        }

        /// <summary>
        /// Look for anyone else's prefix or transpiler on Game1.drawWeather and stand down if one
        /// exists. Run at GameLaunched, after every mod has had its Entry: a mod that patches
        /// later than that (SaveLoaded patching exists in the wild) is caught the hard way, by
        /// the postfix's own failure trap.
        /// </summary>
        internal static void DetectForeignWeatherPatches(IMonitor monitor)
        {
            var patches = Harmony.GetPatchInfo(AccessTools.Method(typeof(Game1), nameof(Game1.drawWeather)));
            if (patches == null)
                return;
            foreach (var patch in patches.Prefixes)
                if (patch.owner != null && !patch.owner.Contains("Radiance", StringComparison.OrdinalIgnoreCase))
                {
                    PrecipitationSystem.AnotherModOwnsWeatherDraw = true;
                    monitor.Log($"'{patch.owner}' also patches the weather draw; Radiance precipitation is yielding to it for this session.", LogLevel.Info);
                    return;
                }
            foreach (var patch in patches.Transpilers)
                if (patch.owner != null && !patch.owner.Contains("Radiance", StringComparison.OrdinalIgnoreCase))
                {
                    PrecipitationSystem.AnotherModOwnsWeatherDraw = true;
                    monitor.Log($"'{patch.owner}' rewrites the weather draw; Radiance precipitation is yielding to it for this session.", LogLevel.Info);
                    return;
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
        /// <summary>
        /// Hold every critter still while the capture clock is frozen. A seagull crossing the
        /// beach is the game's own animation and runs straight through the freeze, and since the
        /// mod started recording the art a location draws for itself, that gull lands in the
        /// sprite mask, in the self-drawn atlas and in its own reflection: two dumps of one
        /// frozen frame differed in all three and the verification harness could certify nothing
        /// at any water spot. Author-only, since nothing freezes the clock during play.
        /// </summary>
        private static void HoldCrittersWhileFrozen(Harmony harmony, IMonitor monitor)
        {
            Type critterBase = typeof(StardewValley.BellsAndWhistles.Critter);
            var prefix = new HarmonyMethod(typeof(HarmonyPatcher), nameof(CritterUpdate_Prefix));
            Type[] signature = { typeof(Microsoft.Xna.Framework.GameTime), typeof(GameLocation) };
            int patched = 0;
            foreach (Type type in critterBase.Assembly.GetTypes())
            {
                if (!critterBase.IsAssignableFrom(type))
                    continue;
                var update = type.GetMethod("update",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly,
                    null, signature, null);
                if (update == null || update.IsAbstract)
                    continue;
                try
                {
                    harmony.Patch(update, prefix: prefix);
                    patched++;
                }
                catch (Exception ex)
                {
                    monitor.Log($"Critter freeze patch skipped for {type.FullName}: {ex.Message}", LogLevel.Trace);
                }
            }
            monitor.Log($"Holding {patched} critter update(s) still while the capture clock is frozen.", LogLevel.Trace);
        }

        /// <summary>Skip a critter's update while frozen, and tell the location to keep it:
        /// a critter that returns true is removed, and a beach that loses its gulls between two
        /// dumps has changed as surely as one whose gulls moved.</summary>
        internal static bool CritterUpdate_Prefix(ref bool __result)
        {
            if (!Determinism.Frozen)
                return true;
            __result = false;
            return false;
        }

        /// <summary>Hold every NPC still while the capture clock is frozen: villagers on their
        /// routes, and the creatures other mods derive from NPC (Custom Companions walks its animals
        /// from an update of its own). A frozen beach still had a crab and a gull walking between
        /// its two dumps, and Town at any hour has villagers moving, which is why town-night could
        /// never certify reflect_entity. Patched by signature on every loaded assembly, at
        /// GameLaunched so the other mods' classes exist. Author-only, like the critter hold.</summary>
        internal static void HoldCharactersWhileFrozen(Harmony harmony, IMonitor monitor)
        {
            var prefix = new HarmonyMethod(typeof(HarmonyPatcher), nameof(CharacterUpdate_Prefix));
            Type[] signature = { typeof(Microsoft.Xna.Framework.GameTime), typeof(GameLocation) };
            int patched = 0;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(ex.Types, t => t != null))!; }
                catch (Exception) { continue; }
                foreach (Type type in types)
                {
                    if (type == null || !typeof(NPC).IsAssignableFrom(type))
                        continue;
                    System.Reflection.MethodInfo? update;
                    try
                    {
                        update = type.GetMethod("update",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly,
                            null, signature, null);
                    }
                    catch (Exception) { continue; }
                    if (update == null || update.IsAbstract)
                        continue;
                    try
                    {
                        harmony.Patch(update, prefix: prefix);
                        patched++;
                    }
                    catch (Exception ex)
                    {
                        monitor.Log($"Character freeze patch skipped for {type.FullName}: {ex.Message}", LogLevel.Trace);
                    }
                }
            }
            monitor.Log($"Holding {patched} character update(s) still while the capture clock is frozen.", LogLevel.Trace);
        }

        /// <summary>Skip a character's update while frozen. Nothing to keep here: an NPC is not
        /// removed for standing still, unlike a critter.</summary>
        internal static bool CharacterUpdate_Prefix() => !Determinism.Frozen;

        /// <summary>Hold the map's own animated tiles while frozen. The surf along the beach and the
        /// waterfalls are frame-cycled by xTile from the elapsed time the game hands it, which is the
        /// game's clock and not ours: two dumps of one frozen beach differed along the whole tide
        /// line. Author-only.</summary>
        private static void HoldMapAnimationWhileFrozen(Harmony harmony, IMonitor monitor)
        {
            var update = AccessTools.Method(typeof(xTile.Map), nameof(xTile.Map.Update), new[] { typeof(long) });
            if (update == null)
            {
                monitor.Log("xTile.Map.Update(long) not found; animated tiles will run through a frozen capture.", LogLevel.Trace);
                return;
            }
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(HarmonyPatcher), nameof(MapUpdate_Prefix)));
        }

        internal static bool MapUpdate_Prefix() => !Determinism.Frozen;

        /// <summary>Hold the location's temporary sprites while frozen: the flame on a campfire, the
        /// smoke off a chimney, a splash, all frame-cycled in update from the game's clock. The
        /// beach gate still differed by a few hundred pixels at a campfire the player had left there
        /// after the draw-time clock was pinned, because the flame is one of these and advances in
        /// update, not draw. Kept rather than removed: a sprite that returns true is deleted, and a
        /// scene that lost its flame between two dumps has changed as much as one whose flame moved.
        /// Author-only.</summary>
        private static void HoldTemporarySpritesWhileFrozen(Harmony harmony, IMonitor monitor)
        {
            var update = AccessTools.Method(typeof(TemporaryAnimatedSprite), nameof(TemporaryAnimatedSprite.update),
                new[] { typeof(Microsoft.Xna.Framework.GameTime) });
            if (update == null)
            {
                monitor.Log("TemporaryAnimatedSprite.update(GameTime) not found; temporary sprites will run through a frozen capture.", LogLevel.Trace);
                return;
            }
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(HarmonyPatcher), nameof(TemporarySpriteUpdate_Prefix)));
        }

        internal static bool TemporarySpriteUpdate_Prefix(ref bool __result)
        {
            if (!Determinism.Frozen)
                return true;
            __result = false;
            return false;
        }

        internal static void UpdateWater_Postfix(GameLocation __instance)
        {
            // The scroll is the game's motion, not ours, so a frozen render clock does not stop
            // it: two dumps of one frozen frame at the beach differed across the whole sea, and
            // the verification harness read that as a scene too unsteady to conclude anything
            // from. While the clock is pinned the scroll is pinned with it, at the phase the
            // frozen tick would have asked for, so the sea holds as still as everything else.
            if (Determinism.Frozen)
                __instance.waterPosition = 0f;
            if (!FreezeGameWater)
                return;
            __instance.waterAnimationIndex = 0;
            if (!_loggedFreeze) { _monitor?.Log("Water frame-cycle frozen (shader ripple active); vertical scroll left running.", LogLevel.Info); _loggedFreeze = true; }
        }
    }
}
