using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// ShadowRenderer, the SIZE of every player-sized target: the silhouette, its colour twin, the
    /// pose library, the co-op bakes and the window bake.
    ///
    /// <para>
    /// The game's farmer is a 16 by 32 frame, drawn at 64 by 128, and a target of 96 by 176 holds it
    /// with room for the game's own hats and tools. An outfit mod can put wings, a tail or a tall hat on a
    /// farmer that reach well outside that, and a target sized for the game cut them off, so the
    /// shadow had a farmer with no wings. The outfit mod says how far its drawing reached
    /// (<see cref="Integrations.OutfitAppearance.DrawnBoundsOf"/>); the size grows to hold that,
    /// centred on the feet as always, and never shrinks back, so a wing that flaps in and out of the
    /// frame does not rebuild every target on every flap.
    /// </para>
    ///
    /// <para>
    /// Every target is made at the size wanted at the moment it is made, and everything that reads
    /// one reads its own width and height rather than these numbers, so a target made before the
    /// size grew is still read correctly until it is made again. Without the outfit mod the size never moves
    /// from 96 by 176, which is exactly what it was.
    /// </para>
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        /// <summary>The size a player-sized target is made at when nothing asks for more.</summary>
        private const int PlayerRtStartWidth = 96, PlayerRtStartHeight = 176;
        /// <summary>The most it may grow to: a farmer with wings three times their own width fits.</summary>
        private const int PlayerRtMostWidth = 256, PlayerRtMostHeight = 320;
        /// <summary>Sizes move in whole steps, so a reach one pixel wider does not rebuild everything.</summary>
        private const int PlayerRtStep = 16;
        /// <summary>The game's own farmer frame inside a bake, in its pixels: 16 by 32 at four each.</summary>
        private const int FarmerFrameWidth = 64, FarmerFrameHeight = 128;
        /// <summary>The rows every bake keeps below the feet.</summary>
        private const int RowsUnderTheFeet = 8;

        /// <summary>Width a player-sized target is made at now. Read a made target's own Width instead.</summary>
        internal static int PlayerRtW { get; private set; } = PlayerRtStartWidth;
        /// <summary>Height a player-sized target is made at now. Read a made target's own Height instead.</summary>
        internal static int PlayerRtH { get; private set; } = PlayerRtStartHeight;

        /// <summary>
        /// Grow the size to hold what the outfit mod last drew of every farmer here. Called before the bakes
        /// each frame; costs one API call a farmer, and nothing at all without the outfit mod.
        /// </summary>
        private void GrowPlayerTargetsToFit()
        {
            if (!Integrations.OutfitAppearance.Connected)
                return;
            foreach (Farmer farmer in Game1.getOnlineFarmers())
            {
                if (Integrations.OutfitAppearance.DrawnBoundsOf(farmer) is { } reach)
                    GrowToHold(reach);
            }
        }

        /// <summary>Grow the size to hold a reach measured from the top left of the farmer's own frame.</summary>
        private void GrowToHold(Rectangle reach)
        {
            // Centred on the feet, so the wider of the two sides decides the width.
            int side = Math.Max(0, Math.Max(-reach.Left, reach.Right - FarmerFrameWidth));
            int above = Math.Max(0, -reach.Top);
            int width = Math.Clamp(Math.Max(PlayerRtW, RoundUpToStep(FarmerFrameWidth + 2 * side)), PlayerRtStartWidth, PlayerRtMostWidth);
            int height = Math.Clamp(Math.Max(PlayerRtH, RoundUpToStep(FarmerFrameHeight + RowsUnderTheFeet + above)), PlayerRtStartHeight, PlayerRtMostHeight);
            if (width == PlayerRtW && height == PlayerRtH)
                return;

            DiagnosticMonitor?.Log($"[shadow] player targets grow from {PlayerRtW}x{PlayerRtH} to {width}x{height} to hold what the outfit mod draws.", LogLevel.Trace);
            PlayerRtW = width;
            PlayerRtH = height;

            // Whatever was baked at the old size is baked again at the new one. The targets are
            // remade where they are next made (MakePlayerSized); the library's poses go now.
            _playerMaskFresh = false;
            _playerColorFresh = false;
            ClearPoseLibrary();
            foreach (FarmerBake bake in _otherFarmerBakes.Values)
                bake.HasSignature = false;
        }

        private static int RoundUpToStep(int size) => (size + PlayerRtStep - 1) / PlayerRtStep * PlayerRtStep;

        /// <summary>
        /// Make a player-sized target, or make it again when the size has grown since. True when it
        /// was made just now, so the caller knows it holds nothing yet.
        /// </summary>
        internal static bool MakePlayerSized(GraphicsDevice graphicsDevice, [NotNull] ref RenderTarget2D? target, string tallyName)
        {
            if (target != null && !target.IsDisposed && target.Width == PlayerRtW && target.Height == PlayerRtH)
                return false;

            target?.Dispose();
            // PreserveContents, like every persistent bake target: it is read frames after it is drawn.
            target = VramTally.Track(new RenderTarget2D(graphicsDevice, PlayerRtW, PlayerRtH, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents), tallyName);
            return true;
        }

        /// <summary>Where the farmer's own frame sits inside an upright bake of this size: centred,
        /// feet eight rows above the bottom. The layout every bake and every reader shares.</summary>
        internal static Vector2 FrameTopLeftInBake(Rectangle sourceRect, int bakeWidth, int bakeHeight) =>
            new((bakeWidth - sourceRect.Width * 4f) / 2f, bakeHeight - sourceRect.Height * 4f - RowsUnderTheFeet);

        /// <summary>The feet inside an upright bake of this size.</summary>
        internal static Vector2 FeetInBake(int bakeWidth, int bakeHeight) => new(bakeWidth / 2f, bakeHeight - RowsUnderTheFeet);

        /// <summary>
        /// The feet-to-head fade over an upright bake, inside a batch already begun with the
        /// multiplying blend. The ramp keeps the rows from the feet it was tuned over, and anything a
        /// grown bake holds above those takes the ramp's tip, so a farmer's own shadow fades the same
        /// in a bake of any size. At the size the ramp was tuned on this is the one draw it always was.
        /// </summary>
        private void DrawFeetToHeadFade(int bakeWidth, int bakeHeight)
        {
            int rampRows = Math.Min(bakeHeight, PlayerRtStartHeight);
            int rowsAbove = bakeHeight - rampRows;
            if (rowsAbove > 0)
                _renderTargetSpriteBatch!.Draw(_gradientTexture, new Rectangle(0, 0, bakeWidth, rowsAbove), new Rectangle(0, 0, 1, 1), Color.White);
            _renderTargetSpriteBatch!.Draw(_gradientTexture, new Rectangle(0, rowsAbove, bakeWidth, rampRows), Color.White);
        }

        /// <summary>
        /// What of an upright bake holds the farmer: their own frame, and with the outfit mod everything its
        /// drawing reached around it, cut to the bake. The sun lays down this much of the bake.
        /// </summary>
        private static Rectangle FarmerInBake(Farmer who, Rectangle sourceRect, int bakeWidth, int bakeHeight)
        {
            Vector2 frameTopLeft = FrameTopLeftInBake(sourceRect, bakeWidth, bakeHeight);
            var frame = new Rectangle((int)frameTopLeft.X, (int)frameTopLeft.Y, sourceRect.Width * 4, sourceRect.Height * 4);
            if (Integrations.OutfitAppearance.DrawnBoundsOf(who) is not { } reach)
                return frame;

            reach.Offset(frame.X, frame.Y);
            return Rectangle.Intersect(Rectangle.Union(frame, reach), new Rectangle(0, 0, bakeWidth, bakeHeight));
        }
    }
}
