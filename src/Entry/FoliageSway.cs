using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace SDVRadiance
{
    /// <summary>
    /// Wind in the foliage: a tree's canopy and a bush lean with the wind, the crown most and the
    /// base not at all. Lives inside the Tree.draw / Bush.draw shim (see
    /// <see cref="ShadowSuppression.Draw_SkipVanillaShadow"/>), so it sees the draw the game made -
    /// texture, source, origin, the shake rotation of a tree being shaken - and only adds its own
    /// motion. Nothing is guessed about where the tree is.
    /// </summary>
    /// <remarks>
    /// The whole sprite TIPS about the point where its canopy meets its trunk, as one piece, on
    /// the ordinary rotation the draw already carries. That is the same thing the game itself does
    /// to a tree you shake, and it is the only shape of motion here that cannot come apart: there
    /// is one Draw, so there is no boundary anywhere in the sprite for the eye to catch.
    ///
    /// <para>Bending the crown further than the trunk - the more truthful motion, and the one this
    /// started as - was tried four ways and dropped. A shear has to be built out of rows that move
    /// by different amounts, and every seam between two such rows reads as a tear in the frame
    /// rather than as a bend in a tree: as eight fixed strips it was slabs, as sixteen it was
    /// cloth, as a run per distinct shift it still tore, and on the art's own pixel grid rather
    /// than the finer drawn grid it tore just the same. Reported each time, from a screenshot, in
    /// those words. A rigid tip of a fraction of a degree loses the bend and keeps the tree.</para>
    ///
    /// <para>Drawn on the GAME'S OWN batch. A version of this put a shear shader on a batch of its
    /// own; that batch submitted immediately while the world's deferred, depth-sorted batch had
    /// not flushed, so every tree sank behind whatever was drawn after it, and the sheet upscaler -
    /// which only redirects draws made on <see cref="Game1.spriteBatch"/> - skipped foliage
    /// entirely.</para>
    ///
    /// The wind is the one the rain already leans with (<see cref="PrecipitationSystem"/>): a gust
    /// front sweeps downwind across the map, one tree phase after another, and a slower sine of the
    /// tree's own keeps neighbours from swaying in step. Every clock is
    /// <see cref="Determinism.Seconds"/>, so a frozen capture stands still. Shadows keep their bake
    /// (a lean of a pixel or two is below what a cast shadow shows), and the reflection's own tree
    /// stamps do not sway - the mirror is a texture draw of its own, not Tree.draw.
    /// </remarks>
    internal static class FoliageSway
    {
        /// <summary>Set per frame by the mod: the switch, and how far the crown may lean (0..2).</summary>
        internal static bool Enabled;
        internal static float Strength = 1f;
        /// <summary>Tempo of every motion at once (0.25..2). 1 is a big tree's natural pace.</summary>
        internal static float Speed = 1f;
        /// <summary>How many tiles one gust spans as it sweeps across the map (4..40).</summary>
        internal static float GustSpanTiles = 14f;
        /// <summary>The rain's wind, signed, in world pixels per second (see PrecipitationSystem).</summary>
        internal static float WindPixelsPerSecond;

        /// <summary>How far the sprite tips on a calm day, in degrees, at Strength 1. A grown
        /// canopy is 96 art pixels tall, so a third of a degree carries its crown about two drawn
        /// pixels - a lean you can see without a tree that looks hinged.</summary>
        private const float CalmTiltDegrees = 0.35f;
        /// <summary>How much more a full wind adds, in degrees, at Strength 1.</summary>
        private const float WindTiltDegrees = 0.55f;
        /// <summary>The wind speed that counts as full (the rain's own ceiling is higher).</summary>
        private const float FullWindPixelsPerSecond = 200f;
        /// <summary>One gust front passes a given tree every this many seconds at Speed 1.</summary>
        private const float GustPeriodSeconds = 5f;

        /// <summary>The recorded count of foliage sprites swayed this frame, for the debug caption.</summary>
        internal static int StripDrawsThisFrame;

        /// <summary>The last canopy this frame swayed, so the trunk the game draws right after it
        /// can tip WITH it. Tree.draw makes two draws - the 48x96 top first, then the 16x32 trunk
        /// at the base - and tipping only the first one left a moving cut across the trunk of any
        /// art that draws most of its trunk inside the top block (the desert palms; reported from
        /// a screenshot with the seam circled). A chopped stump never has a canopy drawn before
        /// it, so it never matches this latch and stands still, which is right.</summary>
        private static Texture2D? _lastCanopyTexture;
        private static Vector2 _lastCanopyPivot;
        private static float _lastCanopyTilt;

        /// <summary>How far the foliage standing at this world position is leaning right now, in
        /// radians, about the point where its canopy meets its trunk.
        ///
        /// <para>Taken from the WORLD position and nothing else, so every pass that has to agree
        /// about one tree gets the same answer without passing it around: the draw itself, and the
        /// reflection, which paints the tree a second time from its own geometry and would
        /// otherwise show a still tree in the water beside a moving one on the bank.</para></summary>
        internal static float TiltAt(float worldColumn, float worldRow)
        {
            if (!Enabled || Strength <= 0.001f)
                return 0f;
            float treePhase = worldColumn * 1.7f + worldRow * 2.3f;
            double t = Determinism.Seconds * Speed;
            float windShare = Math.Min(1f, Math.Abs(WindPixelsPerSecond) / FullWindPixelsPerSecond);
            float windSign = WindPixelsPerSecond < 0f ? -1f : 1f;

            // The gust front: a wave GustSpanTiles long moving downwind, one pass per period, so a
            // row of trees leans one after the other; the tree's own slower sine keeps the row
            // from being copies. A steady share of the lean points downwind while the wind blows.
            float front = (float)Math.Sin(2.0 * Math.PI * (worldColumn / Math.Max(1f, GustSpanTiles) - windSign * t / GustPeriodSeconds));
            float personal = (float)Math.Sin(t * 2.2 + treePhase);
            float sway = 0.35f * windSign * windShare + 0.65f * (0.7f * front + 0.3f * personal);
            return MathHelper.ToRadians(Strength * (CalmTiltDegrees + WindTiltDegrees * windShare)) * sway;
        }

        /// <summary>The lean of the tree whose base stands on this tile, for a pass that knows the
        /// tile rather than the draw. Tree.draw hangs its canopy from (tile*64+32, tile*64+64), so
        /// that is the world point the lean is keyed to, and the reflection asking here gets the
        /// same number the canopy's own draw did.</summary>
        internal static float TiltForTileBase(float tileX, float tileY)
            => TiltAt(tileX + 0.5f, tileY + 1f);

        /// <summary>Draw this sprite swaying in the wind if it is foliage. False = not foliage, draw
        /// it the ordinary way.</summary>
        internal static bool TryDraw(SpriteBatch spriteBatch, Texture2D texture, Vector2 position, Rectangle? sourceRectangle,
            Color color, float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth)
        {
            if (!Enabled || Strength <= 0.001f || sourceRectangle is not Rectangle source || source.Height < 16)
                return false;
            // A grown tree's top: the 48x96 rectangle Tree.draw takes from the tree sheet, hung from
            // (24, 96). A bush: anything drawn from the bush sheet. Saplings and seeds are not
            // foliage and fall through; the TRUNK of a tree whose canopy just swayed is handled
            // below, so the tree tips as one piece.
            bool canopy = source.Width == Tree.treeTopSourceRect.Width && source.Height == Tree.treeTopSourceRect.Height
                && origin.Y >= source.Height - 1f;
            bool bush = Bush.texture.IsValueCreated && ReferenceEquals(texture, Bush.texture.Value);
            if (!canopy && !bush)
            {
                // Tree.draw's trunk: 16x32, origin zero, drawn right after the canopy, 32 world
                // pixels left of and 128 above the canopy's pivot (its x also carries the +-3px
                // shake wiggle). Re-anchor it on the canopy's own pivot and add the same tilt, so
                // the two draws stay one rigid tree and the seam between them cannot open. The
                // position match keys the latch to this one tree: a chopped stump, a bush-stage
                // sapling (same 16x32 shape) or a neighbour's trunk lands somewhere else.
                if (source.Width == 16 && source.Height == 32 && origin == Vector2.Zero
                    && ReferenceEquals(texture, _lastCanopyTexture)
                    && Math.Abs(position.Y - (_lastCanopyPivot.Y - 128f)) < 1f
                    && Math.Abs(position.X - (_lastCanopyPivot.X - 32f)) <= 4f)
                {
                    Vector2 pivotInSourcePixels = (_lastCanopyPivot - position) / scale;
                    spriteBatch.Draw(texture, _lastCanopyPivot, sourceRectangle, color,
                        rotation + _lastCanopyTilt, pivotInSourcePixels, scale, effects, layerDepth);
                    StripDrawsThisFrame++;
                    return true;
                }
                return false;
            }

            // Everything is keyed to where the sprite stands in the world, so the same tree sways
            // the same way from every camera and no two trees breathe in step.
            float tilt = TiltAt((position.X + Game1.viewport.X) / 64f, (position.Y + Game1.viewport.Y) / 64f);

            // The origin the game handed us is where the canopy sits on its trunk, so rotating
            // about it tips the tree from the ground rather than swinging it around its middle.
            // Added to the rotation already there, which is the game's own shake: a tree being
            // shaken while the wind blows does both.
            spriteBatch.Draw(texture, position, sourceRectangle, color, rotation + tilt, origin, scale, effects, layerDepth);
            if (canopy)
            {
                // Remember this canopy for the trunk draw that follows it (see the latch above).
                _lastCanopyTexture = texture;
                _lastCanopyPivot = position;
                _lastCanopyTilt = tilt;
            }
            StripDrawsThisFrame++;
            return true;
        }
    }
}
