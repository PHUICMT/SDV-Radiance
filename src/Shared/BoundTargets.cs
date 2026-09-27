using Microsoft.Xna.Framework.Graphics;

namespace SDVRadiance
{
    /// <summary>
    /// Whether switching away from the render target bound right now, and back, would lose what
    /// has been drawn into it.
    /// </summary>
    /// <remarks>
    /// <para>MonoGame empties a target made with <see cref="RenderTargetUsage.DiscardContents"/>
    /// every time it is bound. The game's own targets and all of this mod's are made to keep their
    /// contents, so a target that discards is another mod's, bound for a picture it is in the
    /// middle of drawing. Smooth art makes a sheet's smooth copy the first time a draw asks for it,
    /// which means binding a target of its own and then binding the old one again: done inside
    /// such a picture, it wiped everything the other mod had drawn so far.</para>
    /// <para>Dynamic Reflections draws the water's reflection that way, into a target of its own,
    /// with the map drawn upside down through Smooth art. The first time a stretch of river came
    /// into view the reflection was wiped halfway and the river turned a flat dark blue for a
    /// frame. Reported by a player walking along water with both mods.</para>
    /// </remarks>
    internal static class BoundTargets
    {
        /// <summary>How many smooth copies were put off because the draw was going into another
        /// mod's picture; each is made later, on a draw to the screen.</summary>
        internal static int BakesPutOff;

        internal static bool WouldBeWipedByRebinding(GraphicsDevice device)
        {
            foreach (RenderTargetBinding binding in device.GetRenderTargets())
                if (binding.RenderTarget is RenderTarget2D target && target.RenderTargetUsage == RenderTargetUsage.DiscardContents)
                {
                    BakesPutOff++;
                    return true;
                }
            return false;
        }
    }
}
