using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace SDVRadiance
{
    /// <summary>
    /// The horse's sheet has its shadow painted into the art: a grey oval under the hooves of every
    /// frame, part of the picture rather than a draw of its own, so no switch that takes the game's
    /// shadows away can reach it, and it stays under the horse with the mod off too. With shadows
    /// that start at the feet the horse casts a shadow of its own from the hooves, and the painted
    /// oval sat under it as a second one. While that look is on, the sheet is loaded with those
    /// pixels taken out.
    /// </summary>
    /// <remarks>
    /// The painted shadow is dark and see-through; the horse's own outline is dark but solid, and
    /// everything else on the sheet is solid. A pixel is taken out when it is see-through and
    /// dark, which leaves every part of the horse, a pack's recolour included, since the edit runs
    /// after other mods' edits.
    /// </remarks>
    internal static class HorseArtShadow
    {
        private const string HorseSheet = "Animals/horse";
        /// <summary>Whether the sheet is loaded without its painted shadow right now.</summary>
        private static bool _takenOut;
        private static IModHelper? _helper;

        internal static void Install(IModHelper helper)
        {
            _helper = helper;
            helper.Events.Content.AssetRequested += OnAssetRequested;
        }

        /// <summary>Once a frame: take the painted shadow out or put it back when the look changes.</summary>
        internal static void Update(ModConfig config)
        {
            bool wanted = config.Enabled && config.DirectionalShadowsEnabled && config.ShadowGroundedLook
                && ShadowRenderer.GroundedLookAvailable;
            if (wanted == _takenOut || _helper == null)
                return;
            _takenOut = wanted;
            _helper.GameContent.InvalidateCache(HorseSheet);
        }

        private static void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            if (!_takenOut || !e.NameWithoutLocale.IsEquivalentTo(HorseSheet))
                return;
            e.Edit(asset => TakeOutPaintedShadow(asset.AsImage().Data), AssetEditPriority.Late);
        }

        /// <summary>The brightest a see-through pixel may paint and still be the painted shadow. The
        /// game's own is one dark brown at a little over half cover, (38, 26, 21) premultiplied at
        /// 136, the only see-through colour on the sheet: 71 out of 255 once the cover is undone.</summary>
        private const int PaintedShadowBrightest = 110;

        private static void TakeOutPaintedShadow(Texture2D sheet)
        {
            var pixels = new Color[sheet.Width * sheet.Height];
            sheet.GetData(pixels);
            bool changed = false;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color pixel = pixels[i];
                if (pixel.A is 0 or > 220)
                    continue;
                // The sheet is premultiplied, so a pixel's colour is scaled by its own cover; the
                // colour it paints with is that scale undone.
                int brightest = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) * 255 / pixel.A;
                if (brightest <= PaintedShadowBrightest)
                {
                    pixels[i] = Color.Transparent;
                    changed = true;
                }
            }
            if (changed)
                sheet.SetData(pixels);
        }
    }
}
