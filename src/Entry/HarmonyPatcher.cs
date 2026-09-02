using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Extensions;

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
        private static Harmony? _harmony;
        private static bool _loggedFreeze;

        /// <summary>Whether this mod's patches are on <c>SpriteBatch.Draw</c> right now. True from
        /// startup; only <c>radiance_hooks off</c> makes it false, and the report says so.</summary>
        internal static bool DrawHooksInstalled = true;

        /// <summary>
        /// The three <c>SpriteBatch.Draw</c> overloads this mod patches, between the sprite
        /// relief recorder, the sheet doubler and the location-art recorder. Kept in one place so
        /// the switch below takes off exactly what was put on, and nothing that was not.
        /// </summary>
        private static readonly Type[][] DrawOverloadSignatures =
        {
            new[] { typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(Vector2), typeof(SpriteEffects), typeof(float) },
            new[] { typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(float), typeof(SpriteEffects), typeof(float) },
            new[] { typeof(Texture2D), typeof(Rectangle), typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(SpriteEffects), typeof(float) },
        };

        /// <summary>
        /// Take this mod's patches off <c>SpriteBatch.Draw</c>, or put them back, while the game
        /// runs. A measuring instrument, not a setting.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Three of this mod's features prefix <c>SpriteBatch.Draw</c>, which is the most-called
        /// method in the game: every map tile, every sprite, every menu element and every shadow
        /// this mod draws goes through it, thousands of times a frame. Harmony folds all three
        /// prefixes and a finalizer into one replacement, so every one of those calls pays a
        /// trampoline, three early-out tests and a try/finally, whether or not the features are
        /// on. Two of the three (sprite relief, sheet doubling) ship OFF.
        /// </para>
        /// <para>
        /// None of that cost lands in any row of this mod's own report: it is spent inside the
        /// game's draw, outside every bracket FrameCost opens. The only line that can see it is
        /// WHOLE FRAME, and the only way to read it there is to take the patches off and compare,
        /// on the same launch, alternating rather than sweeping. This is that switch. While off,
        /// the three features are off with it, which is the point.
        /// </para>
        /// </remarks>
        internal static void SetDrawHooks(bool on, IMonitor monitor)
        {
            if (_harmony == null)
            {
                monitor.Log("draw hooks: no Harmony instance yet, nothing to take off or put back.", LogLevel.Warn);
                return;
            }
            if (on == DrawHooksInstalled)
            {
                monitor.Log($"draw hooks are already {(on ? "on" : "off")}.", LogLevel.Info);
                return;
            }
            if (!on)
            {
                int removed = 0;
                foreach (Type[] signature in DrawOverloadSignatures)
                {
                    MethodInfo? draw = AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw), signature);
                    if (draw == null)
                        continue;
                    _harmony.Unpatch(draw, HarmonyPatchType.All, _harmony.Id);
                    removed++;
                }
                DrawHooksInstalled = false;
                monitor.Log($"draw hooks REMOVED from {removed} SpriteBatch.Draw overload(s). Sprite relief, sheet "
                    + "doubling and the water's carve of a location's own art are all off until 'radiance_hooks on'. "
                    + "Read WHOLE FRAME in radiance_report, not this mod's own rows: the cost being measured was "
                    + "never in them.", LogLevel.Info);
                return;
            }
            SpriteDrawRecorder.Install(_harmony, monitor);
            SheetUpscaler.Install(_harmony, monitor);
            LocationDrawHook.InstallDrawRecorders(_harmony, monitor);
            DrawHooksInstalled = true;
            monitor.Log("draw hooks back on every SpriteBatch.Draw overload this mod patches.", LogLevel.Info);
        }

        /// <summary>Install all game patches: buffer-draw forcing, water frame freeze, and
        /// the vanilla-shadow suppression shims (see <see cref="ShadowSuppression"/>).</summary>
        internal static void InstallAll(Harmony harmony, IMonitor monitor)
        {
            _monitor = monitor;
            _harmony = harmony;

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

            // The mine's floor number is painted into the WORLD layer, not the HUD, so it lands
            // inside the frame the effect chain works on: the tilt-shift band blurs it, the grade
            // tints it, and a player reported not being able to read the floor. It also landed in
            // every captured frame: a badge in the corner of a gallery shot, and a caption that
            // CHANGES between two visits to the same floor, which is noise in a comparison the
            // harness is supposed to read as pixels. The game already knows to leave it out when a
            // picture is being taken - MineShaft.drawAboveAlwaysFrontLayer returns early on
            // takingMapScreenshot - so this borrows that flag for the length of that one call and
            // puts it back, then draws the number itself after the chain has run (see
            // DrawHoistedMineFloorNumber). Setting the flag for the whole frame is not the same
            // thing: this mod reads it in eight places, and one of them decides the render scale,
            // so a frame captured under it would come out a different size than the frame being
            // compared.
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
        private static bool _mapScreenshotFlagHeld;
        /// <summary>Set once a frame by ModEntry: whether the effect chain will run on this frame,
        /// so the mine's floor number is kept out of the world layer and drawn after the chain
        /// instead. With this false the game draws it where it always did.</summary>
        internal static bool HoistMineFloorNumber;

        /// <inheritdoc cref="Apply"/>
        private static void MineFloorNumber_Prefix()
        {
            _mapScreenshotFlagHeld = false;
            if (!(RenderPipeline.DumpPending || HoistMineFloorNumber) || Game1.game1 == null)
                return;
            _mapScreenshotFlagWas = Game1.game1.takingMapScreenshot;
            _mapScreenshotFlagHeld = true;
            Game1.game1.takingMapScreenshot = true;
        }

        /// <inheritdoc cref="Apply"/>
        private static void MineFloorNumber_Postfix()
        {
            if (!_mapScreenshotFlagHeld || Game1.game1 == null)
                return;
            Game1.game1.takingMapScreenshot = _mapScreenshotFlagWas;
            _mapScreenshotFlagHeld = false;
        }

        /// <summary>
        /// Draw the mine's floor number the way MineShaft.drawAboveAlwaysFrontLayer would have,
        /// after the effect chain, so it stays as crisp and as white as the HUD around it. Called
        /// from the RenderedWorld handler with the batch open the way the game opens it for that
        /// draw (deferred, alpha blend, point clamp), which is the state the chain leaves behind.
        /// The layout, the colour by mine area and the skull for a floor that must be cleared are
        /// the game's own, copied line for line from 1.6.15 so nothing moves.
        /// </summary>
        internal static void DrawHoistedMineFloorNumber(SpriteBatch spriteBatch)
        {
            if (!HoistMineFloorNumber || Game1.game1 == null || Game1.game1.takingMapScreenshot)
                return;
            if (Game1.currentLocation is not StardewValley.Locations.MineShaft mine || mine.isSideBranch())
                return;
            int area = mine.getMineArea();
            Color colour = area == 0 || (mine.isDarkArea() && area != 121) ? SpriteText.color_White
                : area == 10 ? SpriteText.color_Green
                : area == 40 ? SpriteText.color_Cyan
                : area == 80 ? SpriteText.color_Red
                : SpriteText.color_Purple;
            string floor = (mine.mineLevel + (area == 121 ? -120 : 0)).ToString();
            Rectangle titleSafeArea = Game1.game1.GraphicsDevice.Viewport.GetTitleSafeArea();
            int heightOfString = SpriteText.getHeightOfString(floor);
            SpriteText.drawString(spriteBatch, floor, titleSafeArea.Left + 16, titleSafeArea.Top + 16, 999999, -1, heightOfString,
                1f, 1f, junimoText: false, 2, "", colour);
            if (!mine.mustKillAllMonstersToAdvance())
                return;
            int widthOfString = SpriteText.getWidthOfString(floor);
            spriteBatch.Draw(Game1.mouseCursors,
                new Vector2(titleSafeArea.Left + 16 + widthOfString + 16, titleSafeArea.Top + 16) + new Vector2(4f, 6f) * 4f,
                new Rectangle(192, 324, 7, 10), Color.White, 0f, new Vector2(3f, 5f), 4f + Game1.dialogueButtonScale / 25f,
                SpriteEffects.None, 1f);
        }

        /// <summary>
        /// Name every mod that has patched this mod's own render clock, one line each. An empty
        /// list means nobody has, AT THE MOMENT IT WAS ASKED: this is cheap enough to ask live,
        /// so the report asks it live rather than trusting an answer from startup.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Determinism.Ticks"/> is a contract other mods reach for, and an uncapper has
        /// to: once the frame cap is off, the game's own counter stops being a clock, and the
        /// author of UltraSmooth patched this property from outside rather than let every ripple,
        /// flame, cloud and heat shimmer in this mod run at the frame rate. That was a kindness.
        /// It is also a fault line, because the patch is pinned to a name this mod is free to
        /// rename, it quietly overrides the clock this mod now keeps for itself, and until this
        /// method existed NOTHING SAID SO ANYWHERE. A whole night on 2026-09-01 went into a
        /// flicker that came down to a patch on this property still quantising our animation to
        /// sixty steps a second.
        /// </para>
        /// <para>
        /// So the report answers it without anyone having to know to ask. A player pastes a
        /// report, and the line either names the mod holding our clock or says nobody is.
        /// </para>
        /// </remarks>
        internal static List<string> ForeignClockPatches()
        {
            var found = new List<string>();
            try
            {
                AddForeignPatchOwners(found, "Determinism.Ticks",
                    AccessTools.PropertyGetter(typeof(Determinism), nameof(Determinism.Ticks)));
                AddForeignPatchOwners(found, "Determinism.Seconds",
                    AccessTools.PropertyGetter(typeof(Determinism), nameof(Determinism.Seconds)));
                AddForeignPatchOwners(found, "Determinism.FollowTheGamesTimeStep",
                    AccessTools.Method(typeof(Determinism), nameof(Determinism.FollowTheGamesTimeStep)));
            }
            catch (Exception ex)
            {
                // A line in a report must never be the thing that breaks the report.
                found.Add($"the check itself failed ({ex.GetType().Name}), so this line cannot answer");
            }
            return found;
        }

        /// <summary>
        /// Append one line per foreign patch on <paramref name="member"/>.
        /// </summary>
        /// <remarks>
        /// A member that cannot be looked up gets a line of its own rather than counting as clean.
        /// Renaming <see cref="Determinism.Ticks"/> would otherwise turn this whole check into a
        /// silent yes, which is the shape of failure this mod has been bitten by before: a zero
        /// that means "not measured" reads exactly like a zero that means "nothing wrong".
        /// </remarks>
        private static void AddForeignPatchOwners(List<string> found, string memberName, MethodBase? member)
        {
            if (member == null)
            {
                found.Add($"{memberName} could not be found to check, so this cannot answer for it");
                return;
            }
            var patches = Harmony.GetPatchInfo(member);
            if (patches == null)
                return;
            CollectForeignOwners(found, memberName, "runs before", patches.Prefixes);
            CollectForeignOwners(found, memberName, "runs after", patches.Postfixes);
            CollectForeignOwners(found, memberName, "rewrites", patches.Transpilers);
            CollectForeignOwners(found, memberName, "wraps", patches.Finalizers);
        }

        /// <summary>Add every owner in <paramref name="patches"/> that is not this mod.</summary>
        private static void CollectForeignOwners(List<string> found, string memberName, string verb,
            IEnumerable<Patch> patches)
        {
            foreach (Patch patch in patches)
                if (patch.owner != null && !patch.owner.Contains("Radiance", StringComparison.OrdinalIgnoreCase))
                    found.Add($"'{patch.owner}' {verb} {memberName}");
        }

        /// <summary>
        /// Say once in the log who holds our clock, so a shared log carries the answer even though
        /// the player has no console. Run at GameLaunched, like the weather check below, and
        /// worded for that: it reports what was true once every mod had had its Entry, which is
        /// not a promise about the rest of the session. The live answer is in the report.
        /// </summary>
        internal static void LogForeignClockPatches(IMonitor monitor)
        {
            List<string> found = ForeignClockPatches();
            if (found.Count == 0)
            {
                monitor.Log("render clock: ours alone, with every mod loaded. Animation speed in this "
                    + "mod is this mod's own answer.", LogLevel.Info);
                return;
            }
            foreach (string line in found)
                monitor.Log($"render clock: {line}.", LogLevel.Info);
            monitor.Log("A mod that patches this clock decides how fast every ripple, flame, cloud and "
                + "heat shimmer in this mod runs, so read that before reading any report of flicker or "
                + "of animation at the wrong speed.", LogLevel.Info);
        }

        /// <summary>
        /// Prove <see cref="ForeignClockPatches"/> can still see a patch, by making one under
        /// another name, asking, removing it, and asking again.
        /// </summary>
        /// <remarks>
        /// A detector that quietly answers "all clear" is worse than no detector, and this one has
        /// two ways to become that: the property could be renamed out from under the lookup, or
        /// Harmony could stop reporting a kind of patch. The rename is caught by the null branch in
        /// <see cref="AddForeignPatchOwners"/>; this catches the rest, and it catches it on the
        /// machine that has the doubt rather than on this one. Both directions are printed,
        /// because a check that can only ever say "found it" is the same failure wearing the other
        /// face.
        /// </remarks>
        internal static void SelfTestClockPatchDetection(IMonitor monitor)
        {
            const string imitationOwnerId = "test.ImitationUncapper";
            MethodInfo? clockTicks = AccessTools.PropertyGetter(typeof(Determinism), nameof(Determinism.Ticks));
            if (clockTicks == null)
            {
                monitor.Log("clock check: Determinism.Ticks could not even be found, so the report's clock "
                    + "line cannot answer for it either. That is a rename this mod made and did not follow "
                    + "through.", LogLevel.Error);
                return;
            }
            var imitation = new Harmony(imitationOwnerId);
            try
            {
                imitation.Patch(clockTicks,
                    postfix: new HarmonyMethod(typeof(HarmonyPatcher), nameof(ClockSelfTest_Postfix)));
                List<string> seen = ForeignClockPatches();
                bool named = seen.Exists(line => line.Contains(imitationOwnerId, StringComparison.Ordinal));
                monitor.Log(named
                    ? $"clock check: with a patch installed under '{imitationOwnerId}', the check named it. "
                      + "The report's clock line works."
                    : $"clock check: a patch was installed under '{imitationOwnerId}' and the check did NOT "
                      + "name it. The report's clock line is blind and must be fixed before it is trusted.",
                    named ? LogLevel.Info : LogLevel.Error);
                foreach (string line in seen)
                    monitor.Log($"  while patched: {line}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                monitor.Log($"clock check: could not install the test patch ({ex.GetType().Name}: {ex.Message}), "
                    + "so this run proves nothing either way.", LogLevel.Warn);
            }
            finally
            {
                try { imitation.UnpatchAll(imitationOwnerId); }
                catch (Exception ex)
                {
                    monitor.Log($"clock check: the test patch could not be removed ({ex.GetType().Name}). "
                        + "Restart the game before reading any clock line.", LogLevel.Error);
                }
            }
            List<string> after = ForeignClockPatches();
            monitor.Log(after.Count == 0
                ? "clock check: with the test patch removed, the check reports nobody, which is the answer a "
                  + "clean install should give."
                : "clock check: with the test patch removed, the check still reports somebody, listed above "
                  + "the previous line. On a clean install that would be wrong; with an uncapper installed "
                  + "it is the real answer.", LogLevel.Info);
            foreach (string line in after)
                monitor.Log($"  after removing: {line}", LogLevel.Info);
        }

        /// <summary>Does nothing. It exists so the self test has something to install.</summary>
        private static void ClockSelfTest_Postfix() { }

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
