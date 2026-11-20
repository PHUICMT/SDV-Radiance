using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// ShadowRenderer, the silhouettes of the people standing in a room, handed to the window light
    /// so a body standing in the patch of daylight on the floor takes its own shape out of it.
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>One body standing in the room: the white silhouette the shadow pass already
        /// baked for it, the feet inside that silhouette, the feet in the world, and which way the
        /// game draws it.</summary>
        internal readonly record struct StandingSilhouette(Texture2D Mask, Vector2 FeetInMask, Vector2 WorldFeet, SpriteEffects Facing);

        private readonly List<StandingSilhouette> _standingSilhouettes = [];

        /// <summary>
        /// The player and every character standing in <paramref name="location"/> whose silhouette
        /// is baked this frame, at the feet the shadow pass casts them from.
        /// </summary>
        /// <remarks>
        /// Nothing is baked for this: these are the silhouettes the shadows were drawn from, so a
        /// body with no shadow this frame (seated, swimming, switched off) blocks no light either.
        /// One feet rule for the player and the villagers, the same the per-light pass uses: the
        /// middle of the body over the bottom of its box, less <see cref="FeetLift"/>.
        /// </remarks>
        internal IReadOnlyList<StandingSilhouette> StandingSilhouettesIn(GameLocation location)
        {
            _standingSilhouettes.Clear();
            Farmer? who = Game1.player;
            if (_castPlayer && who != null && who.currentLocation == location && _playerReady && _playerMaskFresh
                && _playerRenderTarget != null && !_playerRenderTarget.IsDisposed)
            {
                Rectangle box = who.GetBoundingBox();
                _standingSilhouettes.Add(new StandingSilhouette(_playerRenderTarget, _playerFeetInRenderTarget,
                    new Vector2(box.Center.X, box.Bottom - FeetLift - FarmerFeetRaise), SpriteEffects.None));
            }
            foreach (NPC npc in CharactersIn(location))
            {
                if (npc?.Sprite?.Texture == null || npc.IsInvisible || ShadowHiddenFor(npc) || npc.swimming.Value
                    || IsSeated(npc) || !CharacterCasts(npc)
                    || !_casterBakeCache.TryGetValue((npc.Sprite.Texture, npc.Sprite.SourceRect), out SpriteBake? baked)
                    || baked.Rt == null || baked.Rt.IsDisposed)
                    continue;
                var feet = new Vector2(npc.Position.X + npc.drawOffset.X + npc.GetSpriteWidthForPositioning() * 4 / 2f,
                    npc.GetBoundingBox().Bottom - FeetLift);
                _standingSilhouettes.Add(new StandingSilhouette(baked.Rt, baked.FeetInRt, feet, SpriteFacing(npc)));
            }
            return _standingSilhouettes;
        }
    }
}
