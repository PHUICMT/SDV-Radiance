using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The camera pulled so far back that the world frame is bigger than this mod can safely hold.
    ///
    /// <para>The game does not shrink the world when you zoom out: it draws the same world pixels
    /// into a buffer the size of the window divided by the zoom, then scales that down to the
    /// window. A mod that lets the zoom go below the game's own 75 % (Infinite Zoom and the like)
    /// makes that buffer enormous: 1920x1080 at 5 % is a 38400x21600 frame. The water mask, the
    /// mirrors and the effect chain are all sized from that frame, and the report taken there
    /// showed this mod holding 26 GB of graphics memory. A card that runs out of memory fails to
    /// make its buffers or starts swapping them, and a player saw the screen flash white and shake.</para>
    ///
    /// <para>Past <see cref="MostWorldPixels"/> the mod stands aside the way it does for the game's
    /// map screenshot: the game draws its own frame, nothing of ours is drawn or allocated, and the
    /// idle release hands the buffers back after a few seconds. Coming back in, the master fade
    /// eases the effects in again. Going out it stops at once, because a fade would have to keep
    /// drawing into the very buffers that are the danger.</para>
    /// </summary>
    internal static class ZoomGuard
    {
        /// <summary>A 4K frame zoomed out to half: four times a 4K screen. The game's own zoom never
        /// gets near it (a 1920x1080 window at 75 % is 2560x1440); a 1080p window crosses it at
        /// 25 % zoom and a 4K window at 50 %.</summary>
        internal const long MostWorldPixels = 7680L * 4320L;

        private static bool _wasTooFarOut;

        /// <summary>Whether this screen's world frame is past the size the mod can hold.</summary>
        internal static bool TooFarOut
        {
            get
            {
                xTile.Dimensions.Rectangle viewport = Game1.viewport;
                return (long)viewport.Width * viewport.Height > MostWorldPixels;
            }
        }

        /// <summary>Says once in the log when the mod stands aside and when it comes back, so a
        /// report from a zoomed-out player shows why nothing of ours was drawn.</summary>
        internal static void NoteChange(IMonitor monitor)
        {
            bool now = TooFarOut;
            if (now == _wasTooFarOut)
                return;
            _wasTooFarOut = now;
            xTile.Dimensions.Rectangle viewport = Game1.viewport;
            monitor.Log(now
                ? $"Zoomed out to a {viewport.Width}x{viewport.Height} world frame, past the {MostWorldPixels / 1_000_000} million pixels this mod can hold: the effects stand aside until the zoom comes back in."
                : $"Zoomed back in to a {viewport.Width}x{viewport.Height} world frame: the effects return.", LogLevel.Info);
        }
    }
}
