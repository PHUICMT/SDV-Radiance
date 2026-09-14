using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The game's tick counter as ONE clock for every screen, for state this mod shares between
    /// screens.
    ///
    /// <para>Split screen runs one Game1 per screen and Game1.ticks is one of the statics swapped
    /// with it: measured 13/9 with two screens up, the host read 2361 to 2420 while the farmhand read
    /// 1356 to 1415, a thousand apart. Anything kept once for the whole game and stamped or stepped
    /// by that counter read two clocks in turn. The shared bake caches ordered "coldest first" across
    /// both counters; the once-per-tick guards on the wind, the wet ground, the lightning and the
    /// uncapped clock ran once per screen instead of once per frame; and a readback written on one
    /// screen's tick was judged old enough to read on the other's, which is a read that waits for
    /// the whole GPU queue.</para>
    ///
    /// <para>The host screen writes its own counter here whenever it reads it and on its update
    /// tick, which SMAPI runs before any other screen's, and every other screen reads the host's. On
    /// one screen, and in a network farmhand's own game, which is screen 0 of its process, this is
    /// Game1.ticks and nothing else.</para>
    ///
    /// <para>State that already belongs to one screen (RenderPipeline.ScreenState, the per-screen
    /// ink ease, the tool bake keyed by screen) keeps Game1.ticks: its own screen's counter is
    /// consistent with itself, and a guard meant to run once per screen per frame, like the sampler
    /// guard, needs exactly that.</para>
    /// </summary>
    internal static class SharedTicks
    {
        private static int _hostTicks;

        internal static int Now
        {
            get
            {
                if (Context.ScreenId == 0)
                    _hostTicks = Game1.ticks;
                return _hostTicks;
            }
        }

        /// <summary>Called first thing on every update tick, so a farmhand screen whose turn comes
        /// before anything on the host read the clock still reads this tick's value.</summary>
        internal static void NoteHostTick()
        {
            if (Context.ScreenId == 0)
                _hostTicks = Game1.ticks;
        }
    }
}
