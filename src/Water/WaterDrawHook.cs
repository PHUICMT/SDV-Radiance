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

        /// <summary>Bumped whenever a location's sticky set gains a tile the data did not know, or a
        /// set is thrown away. BuildWaterMask folds this into its cache key so newly discovered
        /// water triggers a rebuild on the existing throttle.</summary>
        internal static int Version;

        /// <summary>The tiles the game has drawn water on in ONE location.</summary>
        private sealed class DrawnWaterTiles
        {
            public GameLocation Location = null!;
            public readonly HashSet<int> Indices = new();
            public int LastTouchedTick;
        }

        /// <summary>One set per location, a handful of them, least recently drawn evicted first.
        ///
        /// <para>This was ONE set for "the current location", cleared and re-versioned whenever a
        /// drawWaterTile call came from a different location than the last one. Fine with one
        /// screen: the location changes on a warp and nowhere else. In split screen the two
        /// screens draw in turn, so with one player on the farm and the other indoors the "current
        /// location" flipped twice a frame, the set was emptied twice a frame, and Version climbed
        /// twice a frame. Version is in the water mask's cache key, so every screen rebuilt its
        /// whole window mask on every frame (the 26 ms worst frame in a split-screen report), and
        /// the map-wide waterline anchor, keyed on it too, was never fresh for a single frame and
        /// re-gathered the entire 156x65 farm each time a player stood still. Per location there
        /// is nothing to flip: each screen reads and extends its own location's set.</para></summary>
        private static readonly List<DrawnWaterTiles> _byLocation = new();
        private const int LocationsRemembered = 8;

        private static int Key(int x, int y) => (y << 16) | (x & 0xFFFF);

        /// <summary>The set for a location, made on first sight; the oldest goes when there are too
        /// many. A location's set outlives leaving it (a mine floor comes back, a farmhouse is
        /// re-entered), which is what "sticky" always meant for the tiles inside it.</summary>
        private static DrawnWaterTiles ForLocation(GameLocation location)
        {
            for (int i = 0; i < _byLocation.Count; i++)
                if (ReferenceEquals(_byLocation[i].Location, location))
                    return _byLocation[i];
            if (_byLocation.Count >= LocationsRemembered)
            {
                int oldest = 0;
                for (int i = 1; i < _byLocation.Count; i++)
                    if (_byLocation[i].LastTouchedTick < _byLocation[oldest].LastTouchedTick)
                        oldest = i;
                _byLocation.RemoveAt(oldest);
                Version++;
            }
            var made = new DrawnWaterTiles { Location = location, LastTouchedTick = Game1.ticks };
            _byLocation.Add(made);
            return made;
        }

        /// <summary>Throw away what was drawn in one location: its map was reloaded under the
        /// player, and water the old map drew may not be on the new one.</summary>
        internal static void Forget(GameLocation? location)
        {
            if (location == null)
                return;
            for (int i = 0; i < _byLocation.Count; i++)
            {
                if (!ReferenceEquals(_byLocation[i].Location, location))
                    continue;
                _byLocation.RemoveAt(i);
                Version++;
                return;
            }
        }

        /// <summary>A new day: every set starts empty, the way a single set used to on every warp.</summary>
        internal static void ForgetAll()
        {
            if (_byLocation.Count == 0)
                return;
            _byLocation.Clear();
            Version++;
        }

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
            DrawnWaterTiles drawn = ForLocation(__instance);
            drawn.LastTouchedTick = Game1.ticks;
            // Version only bumps for tiles the DATA doesn't know (they change the mask) —
            // ordinary isWaterTile tiles entering view must not add rebuilds on top of the
            // tile-crossing cadence, or walking near water would rebuild twice as often.
            if (drawn.Indices.Add(Key(x, y)) && !__instance.isWaterTile(x, y))
                Version++;
        }

        /// <summary>True when the game has drawn water at this tile of this location.</summary>
        internal static bool WasDrawn(GameLocation location, int x, int y)
        {
            if ((uint)x > 0xFFFF || y < 0)
                return false;
            for (int i = 0; i < _byLocation.Count; i++)
                if (ReferenceEquals(_byLocation[i].Location, location))
                    return _byLocation[i].Indices.Contains(Key(x, y));
            return false;
        }
    }
}
