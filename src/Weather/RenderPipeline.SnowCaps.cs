using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline - SNOW CAPS. In winter, snow lies along the tops of the things a player
    /// places: fences, chests, machines, a scarecrow, a building's sprite. The game paints snow into
    /// its own winter trees, bushes and roofs, but everything placed keeps its summer colours, and on
    /// a white farm a row of bare brown fence posts reads as cut out and pasted on.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        private Effect? _snowCapsEffect;
        private System.Action<SpriteBatch, Texture2D, RenderTarget2D, ModConfig>? _snowCapsStageDelegate;
        /// <summary>True while the caps want the sprite mask, which in winter nothing else asks for:
        /// the wet ground stands down under snow.</summary>
        private bool _snowCapsWantSpriteMask;

        /// <summary>Winter, outdoors, where the game lies snow on the ground.</summary>
        private static bool SnowLiesHere(GameLocation? location)
            => location is { IsOutdoors: true } && location.GetSeason() == Season.Winter;

        private void RenderSnowCaps(SpriteBatch spriteBatch, Texture2D source, RenderTarget2D dest, ModConfig config)
        {
            var effect = _snowCapsEffect!;
            float tilesWide = Game1.viewport.Width / 64f, tilesHigh = Game1.viewport.Height / 64f;
            GetParam(effect, "TilesPerScreen")?.SetValue(new Vector2(tilesWide, tilesHigh));
            GetParam(effect, "WorldTileOffset")?.SetValue(new Vector2(Game1.viewport.X / 64f, Game1.viewport.Y / 64f));
            GetParam(effect, "ArtPixelUv")?.SetValue(new Vector2(1f / (tilesWide * 16f), 1f / (tilesHigh * 16f)));
            GetParam(effect, "Strength")?.SetValue(MathHelper.Clamp(config.SnowCapsStrength, 0f, 1f) * _fadeSnowCaps);
            // Fresh snow in the light the scene is lit by: the same ambient the wet ground takes, so
            // the caps dim at dusk with everything round them instead of glowing white at night.
            Vector3 ambient = Vector3.Lerp(Vector3.One, ComputeLightingAmbient(config), _fadeLighting);
            GetParam(effect, "SnowLit")?.SetValue(new Vector3(0.93f, 0.96f, 1f) * ambient);
            GetParam(effect, "SpriteMaskTexture")?.SetValue(_spriteMaskRenderTarget);
            DrawFull(spriteBatch, source, dest, effect);
        }
    }
}
