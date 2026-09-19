using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline: glass on art a location draws for itself.
    ///
    /// <para>The bus at the bus stop has windows, and none of them is a map tile: BusStop.draw paints
    /// the bus straight from LooseSprites/Cursors every frame, at a position that changes when it
    /// drives away. Map panes are found by walking the map's labelled tiles once, so the bus was
    /// never a pane and its windows showed nothing, which was reported alongside the bath house
    /// mirrors.</para>
    ///
    /// <para>What a location draws for itself is already recorded, draw by draw, for the water mask
    /// (<see cref="LocationDrawHook"/>): the sheet, the piece of it, where it landed in the world and
    /// how it was sorted. The glass on that piece is read from the sheet's own cell labels, exactly
    /// as an item's are (LabelStore.GetSheetCell), once per sheet piece, and becomes a pane for the
    /// frame at wherever the art was drawn. Nothing about the bus is known to this code: a mod's
    /// location that draws a car, a boat or a shop window gets the same treatment the moment its
    /// art carries glass labels.</para>
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        /// <summary>The glass of one sheet piece, in the piece's own pixels, or null when it has none.</summary>
        private sealed class SpriteGlass
        {
            internal readonly Rectangle[] Runs;
            /// <summary>Labelled mostly as mirror, which returns everything whatever the dial.</summary>
            internal readonly bool IsMirror;

            internal SpriteGlass(Rectangle[] runs, bool isMirror)
            {
                Runs = runs;
                IsMirror = isMirror;
            }

            /// <summary>How much of the picture this glass returns: a mirror all of it, glass what
            /// the vehicle glass dial says.</summary>
            internal float StrengthFor(ModConfig config)
                => IsMirror ? 1.0f : Math.Clamp(config.WindowVehicleGlassStrength, 0f, 1f);
        }

        /// <summary>Per sheet piece, read once. Cleared when the labels reload.</summary>
        private readonly Dictionary<(Texture2D Texture, Rectangle Source), SpriteGlass?> _spriteGlassByPiece = [];
        private LabelStore? _spriteGlassLabels;

        /// <summary>This frame's panes on self-drawn art, rebuilt every frame: the art can move.</summary>
        private readonly List<WindowPane> _spritePanes = [];

        /// <summary>How far in front of its art a sprite pane sorts: less than the gap the game leaves
        /// between the bus and its door (1e-5), so the door still covers the glass behind it.</summary>
        private const float SpritePaneDepthNudge = 2e-6f;

        private void CollectSpritePanes(GameLocation location, ModConfig config)
        {
            _spritePanes.Clear();
            LabelStore? labels = LabelStore.Instance;
            if (labels == null)
                return;
            if (!ReferenceEquals(labels, _spriteGlassLabels))
            {
                _spriteGlassByPiece.Clear();
                _spriteGlassLabels = labels;
            }
            foreach (LocationDrawHook.Stamp stamp in LocationDrawHook.Stamps)
            {
                if (!ReferenceEquals(stamp.Owner, location) || stamp.ArtTexture.IsDisposed)
                    continue;
                var key = (stamp.ArtTexture, stamp.ArtSource);
                if (!_spriteGlassByPiece.TryGetValue(key, out SpriteGlass? glass))
                {
                    glass = ReadSpriteGlass(labels, stamp.ArtTexture, stamp.ArtSource);
                    _spriteGlassByPiece[key] = glass;
                }
                if (glass == null)
                    continue;
                bool mirrored = (stamp.Effects & SpriteEffects.FlipHorizontally) != 0;
                float pixelWidth = stamp.Source.Width * stamp.Scale.X / Math.Max(1, stamp.ArtSource.Width);
                float pixelHeight = stamp.Source.Height * stamp.Scale.Y / Math.Max(1, stamp.ArtSource.Height);
                var worldRuns = new Rectangle[glass.Runs.Length];
                Rectangle union = Rectangle.Empty;
                for (int i = 0; i < glass.Runs.Length; i++)
                {
                    Rectangle run = glass.Runs[i];
                    // Runs are in the asked piece's pixels; the world size of one is the drawn size over the piece.
                    int sourceX = mirrored ? stamp.ArtSource.Width - run.Right : run.X;
                    var world = new Rectangle(
                        (int)MathF.Round(stamp.WorldTopLeft.X + sourceX * pixelWidth),
                        (int)MathF.Round(stamp.WorldTopLeft.Y + run.Y * pixelHeight),
                        (int)MathF.Round(run.Width * pixelWidth),
                        (int)MathF.Round(run.Height * pixelHeight));
                    worldRuns[i] = world;
                    union = i == 0 ? world : Rectangle.Union(union, world);
                }
                // Art a location paints for itself stands on the ground it is drawn over, so it is
                // one tile up, the height a shop front's glass is treated as.
                _spritePanes.Add(new WindowPane(union, worldRuns, glass.StrengthFor(config), tilesAboveGround: 1,
                    sortDepth: stamp.LayerDepth + SpritePaneDepthNudge, isMirror: glass.IsMirror));
            }
        }

        /// <summary>For the report's glass line: what this place drew for itself on the last frame and
        /// which of those pieces carry glass. "The bus has no reflection" is either no draw recorded,
        /// a draw recorded from a sheet nobody labelled, or glass that is there and covered.</summary>
        private string DescribeSpriteGlass(GameLocation location)
        {
            var pieces = new List<string>();
            int drawsHere = 0;
            foreach (LocationDrawHook.Stamp stamp in LocationDrawHook.Stamps)
            {
                if (!ReferenceEquals(stamp.Owner, location))
                    continue;
                drawsHere++;
                if (pieces.Count >= 4)
                    continue;
                _spriteGlassByPiece.TryGetValue((stamp.ArtTexture, stamp.ArtSource), out SpriteGlass? glass);
                pieces.Add($"{stamp.ArtTexture.Name ?? "(unnamed)"} {stamp.ArtSource.X},{stamp.ArtSource.Y} {stamp.ArtSource.Width}x{stamp.ArtSource.Height}"
                         + (glass == null ? " no glass" : $" {glass.Runs.Length} glass run(s) ({(glass.IsMirror ? "mirror" : "glass")})"));
            }
            return $"self-drawn art: {drawsHere} draw(s) recorded here{(LocationDrawHook.Enabled ? "" : " (recording off)")}"
                 + (pieces.Count > 0 ? " e.g. " + string.Join("; ", pieces) : "");
        }

        /// <summary>The glass pixels of one sheet piece as rows of runs merged down, like a map pane's
        /// (see GlassRuns), in the piece's own pixels. The piece need not sit on the sheet's 16 pixel
        /// grid: the bus starts fifteen rows into a cell, so each pixel asks the cell it falls in.</summary>
        private static SpriteGlass? ReadSpriteGlass(LabelStore labels, Texture2D texture, Rectangle source)
        {
            string? name = texture.Name;
            if (string.IsNullOrEmpty(name) || source.Width <= 0 || source.Height <= 0)
                return null;
            var cells = new Dictionary<Point, byte[]?>();
            byte ClassAt(int sheetX, int sheetY)
            {
                var cell = new Point(sheetX / 16, sheetY / 16);
                if (!cells.TryGetValue(cell, out byte[]? classes))
                {
                    classes = labels.GetSheetCell(name, texture, new Rectangle(cell.X * 16, cell.Y * 16, 16, 16));
                    cells[cell] = classes;
                }
                return classes == null ? (byte)0 : classes[(sheetY % 16) * 16 + sheetX % 16];
            }

            var runs = new List<Rectangle>();
            int windowCount = 0, glassCount = 0, mirrorCount = 0;
            for (int y = 0; y < source.Height; y++)
            {
                int x = 0;
                while (x < source.Width)
                {
                    byte first = ClassAt(source.X + x, source.Y + y);
                    if (first is not LabelClassWindow and not LabelClassGlass and not LabelClassMirror)
                    {
                        x++;
                        continue;
                    }
                    int runStart = x;
                    while (x < source.Width)
                    {
                        byte here = ClassAt(source.X + x, source.Y + y);
                        if (here is not LabelClassWindow and not LabelClassGlass and not LabelClassMirror)
                            break;
                        if (here == LabelClassWindow) windowCount++;
                        else if (here == LabelClassGlass) glassCount++;
                        else mirrorCount++;
                        x++;
                    }
                    var run = new Rectangle(runStart, y, x - runStart, 1);
                    int merged = runs.FindLastIndex(existing => existing.X == run.X && existing.Width == run.Width && existing.Bottom == run.Y);
                    if (merged >= 0)
                        runs[merged] = new Rectangle(run.X, runs[merged].Y, run.Width, runs[merged].Height + 1);
                    else
                        runs.Add(run);
                }
            }
            if (windowCount + glassCount + mirrorCount < 8)
                return null;
            // A mirror returns everything, as on the map (see TryPaneBox). Glass does not take the
            // map's ladder: its 0.35 is tuned for shop fronts, and on the bus it put the player's
            // reflection at 15 of 255 either side of off. It has its own dial instead.
            return new SpriteGlass([.. runs], isMirror: mirrorCount >= glassCount && mirrorCount >= windowCount);
        }
    }
}
