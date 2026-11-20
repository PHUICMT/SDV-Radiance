using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace SDVRadiance
{
    /// <summary>
    /// Sunlight through the leaves: small patches of sun inside a tree's shadow that move with
    /// the wind.
    ///
    /// <para>Every patch is an image of the sun let through a gap in the leaves, so it is round,
    /// a pixel or two across at this art's scale, and stretched along the sun when the sun is low;
    /// the shape of the gap does not matter. So the patches are not cut from the canopy's holes.
    /// Each starts at a point inside the canopy art and is laid on the ground by the same
    /// projection the tree's shadow is laid down with, so it always lands inside that shadow.</para>
    ///
    /// <para>They are drawn into the game's sorted batch as light ADDED to what is under them
    /// (a premultiplied colour with no alpha adds), in the sun's colour, so they warm the shade
    /// and never paint over it, and never come out brighter than the open ground beside them.
    /// The wind sways them by the same lean the canopy has, one art pixel at a time, and each
    /// brightens and dims slowly on its own, never a blink.</para>
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>Set around the sun's pass over terrain features: only a sun has gaps to shine through.</summary>
        private bool _dapplesThisCast;
        private float _dappleStrength, _dappleAmount;
        private Vector3 _dappleSunColour;

        /// <summary>Patches tried per tree at amount 1. Some land on leaf-free art and are dropped.</summary>
        private const int DappleTriesPerTree = 22;
        /// <summary>How much light a patch adds at strength 1, in the sun's colour.</summary>
        private const float DappleLight = 0.55f;

        /// <summary>Which art pixels of a canopy are leaves, read once per tree art.</summary>
        private readonly Dictionary<(Texture2D Sheet, Rectangle Canopy), bool[]?> _canopyLeaves = [];

        /// <summary>Called by the sun's character pass before the objects are cast.</summary>
        private void BeginDapples(ModConfig config, float sunAlpha)
        {
            _dapplesThisCast = config.SunDapples && sunAlpha > 0.01f;
            if (!_dapplesThisCast)
                return;
            WindowDaylight(out Vector3 colour, out float daylight);
            _dappleSunColour = colour;
            _dappleStrength = MathHelper.Clamp(config.SunDapplesStrength, 0f, 2f) * daylight;
            _dappleAmount = MathHelper.Clamp(config.SunDapplesAmount, 0f, 2f);
            if (_dappleStrength <= 0.01f || _dappleAmount <= 0f)
                _dapplesThisCast = false;
        }

        private bool[]? CanopyLeaves(Texture2D sheet, Rectangle canopy)
        {
            if (_canopyLeaves.TryGetValue((sheet, canopy), out bool[]? leaves))
                return leaves;
            leaves = null;
            try
            {
                Rectangle inside = Rectangle.Intersect(canopy, sheet.Bounds);
                if (inside == canopy)
                {
                    var pixels = new Color[canopy.Width * canopy.Height];
                    sheet.GetData(0, canopy, pixels, 0, pixels.Length);
                    leaves = new bool[pixels.Length];
                    for (int i = 0; i < pixels.Length; i++)
                        leaves[i] = pixels[i].A > 200;
                }
            }
            catch (Exception)
            {
                // A sheet that cannot be read back (a mod's render target, a lost device): no dapples
                // for this art, rather than a crash.
                leaves = null;
            }
            _canopyLeaves[(sheet, canopy)] = leaves;
            return leaves;
        }

        /// <summary>The sun patches of one grown tree, laid down with its shadow's projection.</summary>
        private void DrawTreeDapples(SpriteBatch spriteBatch, Tree tree, Vector2 tile, Rectangle canopy, float rotation, float stretch, float shadowAlpha)
        {
            if (!_dapplesThisCast || shadowAlpha <= 0.01f || Game1.staminaRect == null)
                return;
            bool[]? leaves = CanopyLeaves(tree.texture.Value, canopy);
            if (leaves == null)
                return;
            ShadowProjection projection = ShadowProjection.ForSolid(rotation, stretch, _groundForeshortening);
            Vector2 feetWorld = new(tile.X * 64f + 32f, tile.Y * 64f + 64f);
            float depth = MathHelper.Clamp((tree.getBoundingBox().Bottom + 2f) / 10000f - tile.X / 1000000f - ShadowDepthBias + 0.000001f, 0f, 1f);
            bool flipped = tree.flipped.Value;
            // The canopy's own lean in the wind, so the patches move with the leaves overhead.
            float lean = FoliageSway.TiltForTileBase(tile.X, tile.Y) + tree.shakeRotation;
            // A long low-sun shadow stretches each patch along it.
            bool lengthwise = stretch > 1.6f;
            bool alongIsSideways = Math.Abs(projection.AlongX) > Math.Abs(projection.AlongY);
            double seconds = Determinism.Seconds;
            int tries = (int)MathF.Round(DappleTriesPerTree * _dappleAmount);
            uint seed = (uint)((int)tile.X * 73856093 ^ (int)tile.Y * 19349663);
            for (int i = 0; i < tries; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                int u = (int)(seed >> 8) % canopy.Width;
                seed = seed * 1664525u + 1013904223u;
                // The crown, not the trunk: the top two thirds of the art.
                int v = (int)(seed >> 8) % (canopy.Height * 2 / 3);
                if (!LeafAround(leaves, canopy.Width, canopy.Height, u, v))
                    continue;
                seed = seed * 1664525u + 1013904223u;
                float phase = (seed >> 8) % 1000 / 1000f;
                // Slow, each on its own clock: brighter and dimmer over three to six seconds.
                float period = 3f + phase * 3f;
                float glow = 0.6f + 0.4f * (float)Math.Sin((seconds / period + phase) * Math.PI * 2.0);
                // Up the canopy the shadow fades (see ObjectHeadFade), and the patches with it.
                float heightShare = 1f - v / (float)canopy.Height;
                float fade = 1f - 0.5f * ObjectHeadFade * heightShare;
                float sideways = (u - canopy.Width / 2f) * 4f * (flipped ? -1f : 1f);
                float up = (v - canopy.Height) * 4f;
                sideways += lean * -up;
                Vector2 world = feetWorld + projection.Apply(sideways, up);
                // On the art's grid: a patch steps a pixel at a time as the leaves sway.
                int artX = (int)MathF.Floor(world.X / 4f), artY = (int)MathF.Floor(world.Y / 4f);
                float light = DappleLight * _dappleStrength * shadowAlpha * fade * glow;
                var colour = new Color(_dappleSunColour.X * light, _dappleSunColour.Y * light, _dappleSunColour.Z * light, 0f);
                int big = (seed >> 20) % 3 == 0 ? 2 : 1;
                int width = big, height = big;
                if (lengthwise)
                {
                    if (alongIsSideways) width++;
                    else height++;
                }
                Vector2 local = Game1.GlobalToLocal(Game1.viewport, new Vector2(artX * 4f, artY * 4f));
                spriteBatch.Draw(Game1.staminaRect, new Rectangle((int)local.X, (int)local.Y, width * 4, height * 4), null,
                    colour, 0f, Vector2.Zero, SpriteEffects.None, depth);
            }
        }

        /// <summary>A leaf pixel with leaves two pixels round it, so a patch sits well inside the
        /// shadow and not on its ragged edge.</summary>
        private static bool LeafAround(bool[] leaves, int width, int height, int u, int v)
        {
            if (u < 2 || v < 2 || u >= width - 2 || v >= height - 2)
                return false;
            return leaves[v * width + u] && leaves[v * width + u - 2] && leaves[v * width + u + 2]
                && leaves[(v - 2) * width + u] && leaves[(v + 2) * width + u];
        }
    }
}
