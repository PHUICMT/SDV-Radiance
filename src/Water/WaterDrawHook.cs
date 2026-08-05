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
    /// Draw-call-accurate water discovery. The game's own <c>GameLocation.drawWaterTile</c>
    /// is the single point every water overlay tile passes through — vanilla, custom
    /// locations, and mods that manage water dynamically all end up here. A postfix
    /// records each drawn tile into a STICKY per-location set, so the water mask can
    /// trust "the game drew water here" over data heuristics, and tiles stay known
    /// after they scroll off screen (the mask window is padded past the viewport,
    /// but the game only draws visible tiles).
    ///
    /// The set is consumed by BuildWaterMask as an ADDITIVE nomination: it only adds
    /// tiles that <c>isWaterTile</c> does NOT already report (a tile the game drew but
    /// the data missed). Tiles isWaterTile knows about keep their existing pipeline,
    /// including Height Framework's deck-over-water veto — the hook can never
    /// resurrect a reflection on a pier deck.
    /// </summary>
    internal static class WaterDrawHook
    {
        /// <summary>Live gate, mirrored from config each frame. When false the postfix is a single branch.</summary>
        internal static bool Enabled;

        /// <summary>Bumped whenever the sticky set gains a tile (or resets). BuildWaterMask folds this
        /// into its cache key so newly discovered water triggers a rebuild on the existing throttle.</summary>
        internal static int Version;

        private static GameLocation? _currentLocation;
        private static readonly HashSet<int> _waterTileIndices = new();

        private static int Key(int x, int y) => (y << 16) | (x & 0xFFFF);

        /// <summary>Patch the base method plus every DECLARED override in loaded assemblies.
        /// Harmony patches method bodies, so a subclass override (VolcanoDungeon's lava,
        /// custom mod locations) is invisible to a base-only patch — each declared override
        /// needs its own. Call at GameLaunched so mod assemblies are loaded.</summary>
        internal static void Install(Harmony harmony, IMonitor monitor)
        {
            var postfix = new HarmonyMethod(typeof(WaterDrawHook), nameof(DrawWaterTile_Postfix));
            var sigs = new[]
            {
                new[] { typeof(SpriteBatch), typeof(int), typeof(int) },
                new[] { typeof(SpriteBatch), typeof(int), typeof(int), typeof(Color) },
            };
            int patched = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }   // dynamic/reflection-only assemblies
                foreach (var t in types)
                {
                    if (!typeof(GameLocation).IsAssignableFrom(t))
                        continue;
                    foreach (var sig in sigs)
                    {
                        var mi = t.GetMethod(nameof(GameLocation.drawWaterTile),
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly,
                            null, sig, null);
                        if (mi == null || mi.IsAbstract)
                            continue;
                        try { harmony.Patch(mi, postfix: postfix); patched++; }
                        catch (Exception ex) { monitor.Log($"drawWaterTile patch skipped for {t.FullName}: {ex.Message}", LogLevel.Trace); }
                    }
                }
            }
            monitor.Log($"Water draw hook installed ({patched} drawWaterTile method(s) patched).", LogLevel.Trace);
        }

        /// <summary>Both overloads funnel here; the base 3-arg calls the 4-arg, so a vanilla tile
        /// records twice — HashSet.Add makes the second a cheap no-op.</summary>
        private static void DrawWaterTile_Postfix(GameLocation __instance, int x, int y)
        {
            if (!Enabled)
                return;
            if (!ReferenceEquals(__instance, _currentLocation))
            {
                _currentLocation = __instance;
                _waterTileIndices.Clear();
                Version++;
            }
            // Version only bumps for tiles the DATA doesn't know (they change the mask) —
            // ordinary isWaterTile tiles entering view must not add rebuilds on top of the
            // tile-crossing cadence, or walking near water would rebuild twice as often.
            if (_waterTileIndices.Add(Key(x, y)) && !__instance.isWaterTile(x, y))
                Version++;
        }

        /// <summary>True when the game has drawn water at this tile of the CURRENT location.</summary>
        internal static bool WasDrawn(GameLocation location, int x, int y)
            => ReferenceEquals(location, _currentLocation) && (uint)x <= 0xFFFF && y >= 0 && _waterTileIndices.Contains(Key(x, y));
    }
}
