using System;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// ShadowRenderer, GROUNDED SHADOWS (ModConfig.ShadowGroundedLook, off by default).
    ///
    /// <para>A character's cast is the upright silhouette TURNED about the feet, so its feet row
    /// turns with it: a lamp beside a person lays the whole figure on the floor like a fallen card,
    /// and walking past a lamp swings that card round the feet. A real shadow starts where the body
    /// touches the ground and runs away from the light, so the feet row stays on the floor under the
    /// feet, lying across the light as a footprint of the body's own depth, and each row above it
    /// moves out along the shadow by its own height. A body off the ground casts off the ground.</para>
    ///
    /// <para>A SpriteBatch draw can only turn and scale, and that is a shear, so each cast is drawn
    /// as it always was and then its four corners are moved in the batch before it is sent: one
    /// quad, one draw, the same sort depth, nothing cut into strips that could come apart. The tip
    /// lands exactly where the turned cast put it; only the base stops swinging.</para>
    ///
    /// <para>The look eases in and out over half a second (<see cref="GroundedBlend"/>): every corner
    /// is the turned one moved that share of the way to the grounded one.</para>
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>How far the grounded look is in, 0 (turned) to 1 (grounded), eased toward the
        /// setting by <see cref="UpdateGroundedLook"/>.</summary>
        internal static float GroundedBlend;
        /// <summary>Whether any of the grounded look is in, so the paths that cost something can skip
        /// it entirely while the look is off.</summary>
        internal static bool GroundedLook => GroundedBlend > 0f && GroundedLookAvailable;
        /// <summary>How deep a body is against its width, as the ground sees it: the footprint the
        /// shadow starts from (ModConfig.ShadowGroundedDepth).</summary>
        internal static float GroundedDepth = 0.35f;
        /// <summary>Half a second from one look to the other, the time the other looks take to fade.</summary>
        private const float GroundedEaseSeconds = 0.5f;
        private static long _groundedEasedAtTicks;

        /// <summary>True while a character's cast is being drawn and the look is in.</summary>
        private static bool _groundingThisCast;

        private static readonly FieldInfo? BatcherField = AccessTools.Field(typeof(SpriteBatch), "_batcher");
        private static readonly Type? BatcherType = AccessTools.TypeByName("Microsoft.Xna.Framework.Graphics.SpriteBatcher");
        private static readonly Type? BatchItemType = AccessTools.TypeByName("Microsoft.Xna.Framework.Graphics.SpriteBatchItem");
        private static readonly FieldInfo? BatchItemListField = BatcherType == null ? null : AccessTools.Field(BatcherType, "_batchItemList");
        private static readonly AccessTools.FieldRef<object, int>? BatchItemCountOf = RefTo<int>(BatcherType, "_batchItemCount");
        private static readonly AccessTools.FieldRef<object, VertexPositionColorTexture>? TopLeftOf = RefTo<VertexPositionColorTexture>(BatchItemType, "vertexTL");
        private static readonly AccessTools.FieldRef<object, VertexPositionColorTexture>? TopRightOf = RefTo<VertexPositionColorTexture>(BatchItemType, "vertexTR");
        private static readonly AccessTools.FieldRef<object, VertexPositionColorTexture>? BottomLeftOf = RefTo<VertexPositionColorTexture>(BatchItemType, "vertexBL");
        private static readonly AccessTools.FieldRef<object, VertexPositionColorTexture>? BottomRightOf = RefTo<VertexPositionColorTexture>(BatchItemType, "vertexBR");

        /// <summary>Whether the batch internals were found. Without them the look does nothing and
        /// every cast is drawn turned, as before.</summary>
        internal static bool GroundedLookAvailable => BatcherField != null && BatchItemListField != null
            && BatchItemCountOf != null && TopLeftOf != null && TopRightOf != null && BottomLeftOf != null && BottomRightOf != null;

        private static AccessTools.FieldRef<object, TField>? RefTo<TField>(Type? owner, string name)
        {
            if (owner == null)
                return null;
            try
            {
                return AccessTools.FieldRefAccess<TField>(owner, name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Ease the look toward the setting, once a frame. Timed by the wall clock rather
        /// than counted per call: on a split screen this runs once for each screen, and a count
        /// would run the fade at twice the speed there.</summary>
        internal static void UpdateGroundedLook(ModConfig config)
        {
            GroundedDepth = Math.Clamp(config.ShadowGroundedDepth, ModConfig.ShadowGroundedDepthMin, ModConfig.ShadowGroundedDepthMax);
            float target = config.Enabled && config.ShadowGroundedLook ? 1f : 0f;
            long now = Environment.TickCount64;
            if (_groundedEasedAtTicks == 0)
            {
                // The first frame after launch starts where the setting is, with nothing to fade from.
                _groundedEasedAtTicks = now;
                GroundedBlend = target;
                UpdateFlierBlobs(config);
                return;
            }
            float seconds = Math.Min(0.25f, (now - _groundedEasedAtTicks) / 1000f);
            _groundedEasedAtTicks = now;
            float step = seconds / GroundedEaseSeconds;
            GroundedBlend = target > GroundedBlend ? Math.Min(target, GroundedBlend + step) : Math.Max(target, GroundedBlend - step);
            UpdateFlierBlobs(config);
        }

        /// <summary>The blend smoothed at both ends, so the look neither starts nor stops with a jolt.</summary>
        private static float GroundedShare => GroundedLook ? MathHelper.SmoothStep(0f, 1f, GroundedBlend) : 0f;

        /// <summary>How far a person's shadow width swings round under the sun. The dial's own value
        /// with the look out (1, a turn, is its default); the body's depth with it in, so the feet
        /// row stays on the ground by day as it does under a lamp. The sun path lays the silhouette
        /// down through the whole affine projection already, so this is all it needs.</summary>
        private static float PeopleGroundForeshortening(ModConfig config)
            => MathHelper.Lerp(config.ShadowCharacterGroundForeshortening, GroundedDepth, GroundedShare);

        /// <summary>The width and facing a character's upright cast is drawn with: the turned cast's
        /// own while the look is out, the sprite's own full width and facing with it in. Where the
        /// turned cast mirrored the sprite, the mirror is carried as a negative width, so over the
        /// fade the width runs through nothing and comes out the other side: the cast turns over
        /// like a card, and no frame swaps one picture for its mirror.</summary>
        private static (float Width, SpriteEffects Facing) CastWidth(float turnedWidth, SpriteEffects turnedFacing, SpriteEffects ownFacing)
        {
            float share = GroundedShare;
            if (share <= 0f)
                return (turnedWidth, turnedFacing);
            float signedWidth = turnedFacing == ownFacing ? turnedWidth : -turnedWidth;
            return (MathHelper.Lerp(signedWidth, 1f, share), ownFacing);
        }

        /// <summary>How much of the round pool under a horse or an animal is kept. With the grounded
        /// look its cast starts at the hooves already, and the pool, laid under the middle of a long
        /// body, read as the game's old round blob left behind. A person's small pool stays.</summary>
        private static float AnimalPoolKept => 1f - GroundedShare;

        /// <summary>A person's width, the size the footprint depth is set against.</summary>
        private const float PersonWidthPixels = 64f;
        /// <summary>The footprint depth of the cast being drawn, set by <see cref="WithGroundedCast"/>.</summary>
        private static float _castDepth = 0.35f;

        /// <summary>How deep a body's footprint is against its own width. The depth is a body's,
        /// set against a person, so a horse or a cow drawn twice as wide is not twice as deep: a
        /// long animal seen from the side is a thin body, and its shadow starts from a thin
        /// footprint under its hooves rather than a slab.</summary>
        private static float FootprintDepthFor(float widthPixels)
            => Math.Clamp(GroundedDepth * PersonWidthPixels / Math.Max(16f, Math.Abs(widthPixels)), ModConfig.ShadowGroundedDepthMin, 1f);

        /// <summary>The ground foreshortening an animal's or a horse's shadow is laid down with by
        /// day: the objects' own while the look is out, its footprint depth with it in.</summary>
        private static float AnimalGroundForeshortening(float objectForeshortening, float widthPixels)
            => MathHelper.Lerp(objectForeshortening, FootprintDepthFor(widthPixels), GroundedShare);

        /// <summary>Where one pixel of a cast's width lands, for the passes that have to follow a
        /// cast texel by texel (the player's patch mask): the lean's perpendicular for a turned
        /// cast, the footprint across the light for a grounded one.</summary>
        private static Vector2 CastAcrossAxis(float rotation)
        {
            var turned = new Vector2((float)Math.Cos(rotation), (float)Math.Sin(rotation));
            float share = GroundedShare;
            if (share <= 0f)
                return turned;
            ShadowProjection ground = ShadowProjection.ForSolid(rotation, 1f, GroundedDepth);
            return Vector2.Lerp(turned, new Vector2(ground.AcrossX, ground.AcrossY), share);
        }

        /// <summary>How high a body is off the ground, in world pixels, by the game's own jump. A
        /// farmer is drawn raised by twice its jump (once in getLocalPosition, once in Farmer.draw),
        /// everyone else once.</summary>
        private static float BodyLift(Character body)
            => Math.Max(0f, -(body is Farmer ? 2f : 1f) * body.yJumpOffset);

        /// <summary>The height past which a lifted shadow moves no further: two tiles. A jump is
        /// under one; this keeps a mod that throws a body high from throwing its shadow off screen.</summary>
        private const float LiftFadeHeight = 128f;

        /// <summary>Where a body's shadow moves when the body is off the ground: every point of it is
        /// that much higher, so the whole shadow moves out along the light by the height times the
        /// shadow's length, and leaves the feet. Nothing while the look is out.</summary>
        private static Vector2 LiftShift(float lift, float rotation, float stretch)
        {
            float share = GroundedShare;
            if (share <= 0f || lift <= 0f)
                return Vector2.Zero;
            return share * Math.Min(lift, LiftFadeHeight) * stretch
                * new Vector2((float)Math.Sin(rotation), -(float)Math.Cos(rotation));
        }

        /// <summary>How much of a shadow is left for a body this high: a jump's height takes a third
        /// of it, gradually, the way the game shrinks its own blob under a jumping body.</summary>
        private static float LiftFade(float lift)
        {
            float share = GroundedShare;
            if (share <= 0f || lift <= 0f)
                return 1f;
            return 1f - share * 0.35f * MathHelper.SmoothStep(0f, 1f, Math.Min(1f, lift / LiftFadeHeight));
        }

        /// <summary>Draw a character's cast with the grounded look when it is in. Resets itself even
        /// if the draw throws, so nothing drawn after a cast is ever moved.</summary>
        private static void WithGroundedCast(Action drawCast, float bodyWidthPixels = PersonWidthPixels)
        {
            _groundingThisCast = GroundedLook;
            _castDepth = FootprintDepthFor(bodyWidthPixels);
            try
            {
                drawCast();
            }
            finally
            {
                _groundingThisCast = false;
            }
        }

        /// <summary>How many sprites the batch holds right now, or -1 when this draw is not being
        /// grounded. Asked before the draw so the one after can tell that exactly one was added.</summary>
        private static int GroundedCountBefore(SpriteBatch spriteBatch)
        {
            if (!_groundingThisCast)
                return -1;
            object? batcher = BatcherField!.GetValue(spriteBatch);
            return batcher == null ? -1 : BatchItemCountOf!(batcher);
        }

        /// <summary>Move the four corners of the sprite just drawn from the turned cast toward the
        /// grounded one. Only when the batch grew by exactly one: an immediate batch has already
        /// sent it, and another mod drawing inside the call would make the last one not ours.</summary>
        private static void GroundLastDraw(SpriteBatch spriteBatch, int countBefore, Texture2D texture, Vector2 pivot, float rotation)
        {
            if (countBefore < 0)
                return;
            object? batcher = BatcherField!.GetValue(spriteBatch);
            if (batcher == null || BatchItemCountOf!(batcher) != countBefore + 1)
                return;
            if (BatchItemListField!.GetValue(batcher) is not Array items || countBefore >= items.Length
                || items.GetValue(countBefore) is not object item)
                return;
            float cos = (float)Math.Cos(rotation), sin = (float)Math.Sin(rotation);
            // The feet row lies on the ground across the light, as a solid's does (see
            // ShadowProjection.ForSolid), at the body's own depth: level while the light is in
            // front or behind, and a short footprint when the light stands beside the body.
            ShadowProjection ground = ShadowProjection.ForSolid(rotation, 1f, _castDepth);
            var across = new Vector2(ground.AcrossX, ground.AcrossY);
            float share = GroundedShare;
            ref VertexPositionColorTexture topLeft = ref TopLeftOf!(item);
            ref VertexPositionColorTexture topRight = ref TopRightOf!(item);
            ref VertexPositionColorTexture bottomLeft = ref BottomLeftOf!(item);
            ref VertexPositionColorTexture bottomRight = ref BottomRightOf!(item);
            Ground(ref topLeft, pivot, cos, sin, across, share);
            Ground(ref topRight, pivot, cos, sin, across, share);
            Ground(ref bottomLeft, pivot, cos, sin, across, share);
            Ground(ref bottomRight, pivot, cos, sin, across, share);
            // A shadow running down the screen is the upright silhouette mirrored top to bottom
            // (the turn made it upside down instead, which keeps the winding). Mirrored, its two
            // triangles face away and the batch's culling drops them: the whole cast vanished
            // under every lamp above the caster. Asked of the corners as they now stand, so the
            // fade between the two looks is covered too; swapping the top and bottom corners whole,
            // position and texture together, puts the winding back and leaves the picture as it is.
            float winding = (topRight.Position.X - topLeft.Position.X) * (bottomLeft.Position.Y - topLeft.Position.Y)
                          - (topRight.Position.Y - topLeft.Position.Y) * (bottomLeft.Position.X - topLeft.Position.X);
            if (winding < 0f)
            {
                (topLeft, bottomLeft) = (bottomLeft, topLeft);
                (topRight, bottomRight) = (bottomRight, topRight);
            }
            // Read point by point, a sheared soft silhouette snaps to a whole texel row by row.
            SheetUpscaler.ReadLinearlyThisFrame(texture);
            FrameCost.Count(FrameCost.Counter.GroundedCasts);
        }

        /// <summary>One corner: undo the turn to find where it sits on the upright silhouette, lay its
        /// place across the feet row on the ground, move it out along the shadow by its height, and
        /// go that share of the way from where the turn put it.</summary>
        private static void Ground(ref VertexPositionColorTexture vertex, Vector2 pivot, float cos, float sin, Vector2 across, float share)
        {
            float offsetX = vertex.Position.X - pivot.X, offsetY = vertex.Position.Y - pivot.Y;
            float acrossFeet = offsetX * cos + offsetY * sin;
            float heightAboveFeet = offsetX * sin - offsetY * cos;
            float groundedX = pivot.X + acrossFeet * across.X + heightAboveFeet * sin;
            float groundedY = pivot.Y + acrossFeet * across.Y - heightAboveFeet * cos;
            vertex.Position.X = MathHelper.Lerp(vertex.Position.X, groundedX, share);
            vertex.Position.Y = MathHelper.Lerp(vertex.Position.Y, groundedY, share);
        }
    }
}
