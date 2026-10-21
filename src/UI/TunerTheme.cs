using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// The tuner's colours, for the game's wooden box and for the glass panel.
    ///
    /// <para>The glass follows the menu mock agreed on 18 Oct 2026: a dark blue frosted panel, pale
    /// ink, a sky blue for what is chosen and a warm gold for headings, rounded corners. The wooden
    /// box keeps the game's own brown ink and the washes the menu always used, so a player who
    /// keeps the box sees nothing change.</para>
    /// </summary>
    internal static class TunerTheme
    {
        /// <summary>Set by the tuner each frame it draws, from the player's choice of panel.</summary>
        internal static bool Glass;

        /// <summary>The pale glass: the same panel with the light and dark swapped, for a player who
        /// finds pale words on dark glass hard to read (asked for on 21 Oct 2026).</summary>
        internal static bool Light;

        // The pale glass's inks are darker than a straight swap of the dark ones: the world shows
        // through the glass, and the first cut's greys and gold were hard to read on it.
        internal static Color Ink => !Glass ? Game1.textColor : Light ? new Color(14, 18, 28) : new Color(238, 243, 255);
        internal static Color InkDim => !Glass ? Game1.textColor * 0.8f : Light ? new Color(36, 44, 60) : new Color(183, 194, 219);
        internal static Color InkFaint => !Glass ? Game1.textColor * 0.6f : Light ? new Color(70, 80, 100) : new Color(127, 138, 166);
        /// <summary>The chosen thing: a tab, a choice, a switch that is on.</summary>
        internal static Color Accent => Light ? new Color(28, 106, 196) : new Color(143, 211, 255);
        /// <summary>Ink on top of the accent.</summary>
        internal static Color AccentInk => Light ? Color.White : new Color(13, 22, 38);
        /// <summary>Headings on the glass.</summary>
        internal static Color Heading => Light ? new Color(146, 74, 0) : new Color(255, 210, 122);
        /// <summary>The panel's colour, laid over the blurred world.</summary>
        internal static Color PanelGlass => Light ? new Color(246, 248, 252) : new Color(16, 20, 32);
        /// <summary>What a plate on the glass is lifted with: white on the dark glass, black on the pale.</summary>
        internal static Color Overlay => Light ? Color.Black : Color.White;
        /// <summary>A wash that darkens on the box and lightens on the glass: hover, bands, tracks.</summary>
        internal static Color Wash => Glass ? Overlay : new Color(72, 38, 12);
        /// <summary>The bar beside the chosen tab and the hovered heading.</summary>
        internal static Color Marker => Glass ? Accent : new Color(96, 48, 14);
        /// <summary>A slider's filled part and a scroll thumb.</summary>
        internal static Color Fill => Glass ? Accent : new Color(196, 130, 66);
        /// <summary>The mark beside a row that is not at its default.</summary>
        internal static Color Changed => Glass ? Heading : new Color(214, 104, 40);
        /// <summary>The thin line round a plate on the glass.</summary>
        internal static Color Rim => Overlay * (Light ? 0.18f : 0.14f);

        /// <summary>How much of the panel colour covers the world: the player's Glass darkness, with
        /// the pale glass kept thicker, since busy colour behind dark words is what made them hard to read.</summary>
        internal static float GlassCover(float shade) => Light ? Math.Max(shade, 0.9f) : shade;

        /// <summary>Corner radius of the round texture, in its own texels.</summary>
        private const int RoundRadius = 16;
        private static Texture2D? _round;

        /// <summary>A white disc with soft edges, cut into quarters for the corners of a plate and
        /// drawn whole for a switch's knob and a slider's thumb.</summary>
        internal static Texture2D Round(GraphicsDevice device)
        {
            if (_round != null && !_round.IsDisposed)
                return _round;
            int size = RoundRadius * 2;
            var pixels = new Color[size * size];
            float centre = RoundRadius - 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float distance = MathF.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre));
                    float coverage = Math.Clamp(RoundRadius - distance, 0f, 1f);
                    pixels[y * size + x] = Color.White * coverage;
                }
            _round = new Texture2D(device, size, size);
            _round.SetData(pixels);
            return _round;
        }

        /// <summary>A filled rectangle with round corners of <paramref name="radius"/> pixels.</summary>
        internal static void Plate(SpriteBatch spriteBatch, Rectangle bounds, int radius, Color fill)
        {
            radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            if (radius == 0)
            {
                spriteBatch.Draw(Game1.staminaRect, bounds, fill);
                return;
            }
            Texture2D round = Round(spriteBatch.GraphicsDevice);
            spriteBatch.Draw(round, new Rectangle(bounds.X, bounds.Y, radius, radius), new Rectangle(0, 0, RoundRadius, RoundRadius), fill);
            spriteBatch.Draw(round, new Rectangle(bounds.Right - radius, bounds.Y, radius, radius), new Rectangle(RoundRadius, 0, RoundRadius, RoundRadius), fill);
            spriteBatch.Draw(round, new Rectangle(bounds.X, bounds.Bottom - radius, radius, radius), new Rectangle(0, RoundRadius, RoundRadius, RoundRadius), fill);
            spriteBatch.Draw(round, new Rectangle(bounds.Right - radius, bounds.Bottom - radius, radius, radius), new Rectangle(RoundRadius, RoundRadius, RoundRadius, RoundRadius), fill);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(bounds.X + radius, bounds.Y, bounds.Width - radius * 2, bounds.Height), fill);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y + radius, radius, bounds.Height - radius * 2), fill);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(bounds.Right - radius, bounds.Y + radius, radius, bounds.Height - radius * 2), fill);
        }

        /// <summary>A one-pixel rim all the way round a plate, its corners included.</summary>
        internal static void PlateRim(SpriteBatch spriteBatch, Rectangle bounds, int radius, Color rim)
        {
            radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(bounds.X + radius, bounds.Y, bounds.Width - radius * 2, 1), rim);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(bounds.X + radius, bounds.Bottom - 1, bounds.Width - radius * 2, 1), rim);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y + radius, 1, bounds.Height - radius * 2), rim);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(bounds.Right - 1, bounds.Y + radius, 1, bounds.Height - radius * 2), rim);
            RimCorners(spriteBatch, bounds, radius, rim);
        }

        /// <summary>The four rounded corners of a rim alone, for an outline whose sides are drawn some
        /// other way (the dashed add button).</summary>
        internal static void RimCorners(SpriteBatch spriteBatch, Rectangle bounds, int radius, Color rim)
        {
            radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            if (radius == 0)
                return;
            // A quarter of a one-pixel ring made at this radius, drawn at its own size, so the line
            // keeps the same weight round the bend as along the sides.
            Texture2D ring = Ring(spriteBatch.GraphicsDevice, radius);
            spriteBatch.Draw(ring, new Rectangle(bounds.X, bounds.Y, radius, radius), new Rectangle(0, 0, radius, radius), rim);
            spriteBatch.Draw(ring, new Rectangle(bounds.Right - radius, bounds.Y, radius, radius), new Rectangle(radius, 0, radius, radius), rim);
            spriteBatch.Draw(ring, new Rectangle(bounds.X, bounds.Bottom - radius, radius, radius), new Rectangle(0, radius, radius, radius), rim);
            spriteBatch.Draw(ring, new Rectangle(bounds.Right - radius, bounds.Bottom - radius, radius, radius), new Rectangle(radius, radius, radius, radius), rim);
        }

        private static readonly Dictionary<int, Texture2D> _rings = [];

        /// <summary>A white ring one pixel wide whose outer edge is <paramref name="radius"/> pixels out,
        /// one texture per radius the menu uses.</summary>
        private static Texture2D Ring(GraphicsDevice device, int radius)
        {
            if (_rings.TryGetValue(radius, out Texture2D? ring) && !ring.IsDisposed)
                return ring;
            int size = radius * 2;
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float distance = MathF.Sqrt((x + 0.5f - radius) * (x + 0.5f - radius) + (y + 0.5f - radius) * (y + 0.5f - radius));
                    pixels[y * size + x] = Color.White * Math.Clamp(1f - MathF.Abs(distance - (radius - 0.5f)), 0f, 1f);
                }
            ring = new Texture2D(device, size, size);
            ring.SetData(pixels);
            _rings[radius] = ring;
            return ring;
        }

        /// <summary>A disc of <paramref name="diameter"/> pixels centred on <paramref name="centre"/>.</summary>
        internal static void Disc(SpriteBatch spriteBatch, Vector2 centre, int diameter, Color colour)
            => spriteBatch.Draw(Round(spriteBatch.GraphicsDevice),
                new Rectangle((int)(centre.X - diameter / 2f), (int)(centre.Y - diameter / 2f), diameter, diameter), colour);
    }
}
