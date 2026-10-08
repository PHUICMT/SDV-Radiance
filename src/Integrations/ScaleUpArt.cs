using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// Character art that Scale Up Unofficial holds at more texels than the game thinks it has,
    /// drawn into this mod's own shadow targets where it belongs.
    /// </summary>
    /// <remarks>
    /// <para>Scale Up lets a pack load a character sheet at four times the texels while the game
    /// keeps working in the original frame size, and it patches SpriteBatch.Draw to swap in the
    /// bigger frame. For a sheet it treats as a character sprite (a pack's "Sprite" entry) it does
    /// more than scale the frame: it throws away the origin and size the caller passed and puts in
    /// the ones the game's own NPC draw uses. That is right for the game's draw and wrong for every
    /// other: the shadow bakes draw a frame into a small target at their own origin and scale, and
    /// Scale Up moved it out of the target, so the bake came back empty and a scaled villager had no
    /// shadow at all while the game's own round one was taken away (Mud's Bountiful Beauties on
    /// Haley: her bake held about 340 opaque texels of a shifted silhouette, and about 1,270 once
    /// drawn here, against Marnie's ordinary sheet beside her).</para>
    /// <para>So for those sheets the mod scales the frame, the origin and the scale itself, and
    /// raises Scale Up's own guard against drawing twice (the one it sets around its own redrawn
    /// call) for the one draw, so it passes the draw through untouched. Every other sheet, and
    /// everything when Scale Up is not installed, draws exactly as before. The other kinds of Scale
    /// Up entry (farmers, explicit frame sizes, padded or pixel-replaced sheets) are left to Scale
    /// Up: their layouts are its own business and none was seen to lose a shadow.</para>
    /// <para>Everything about Scale Up is read by name, once, and each sheet's answer is kept for
    /// as long as the sheet lives, so a frame costs one table lookup.</para>
    /// </remarks>
    internal static class ScaleUpArt
    {
        private sealed class Answer
        {
            public float Scale;
            public Texture2D? Sheet;
            public int OriginalWidth;
        }

        private static readonly ConditionalWeakTable<Texture2D, Answer> _answers = [];
        private static readonly Answer _notScaled = new();
        private static FieldInfo? _drawGuard;
        private static MethodInfo? _shouldProcess;
        private static bool _found;
        private static IMonitor? _monitor;

        /// <summary>Look for Scale Up once the mods are loaded. Silent when it is not there.</summary>
        internal static void Find(IMonitor monitor)
        {
            _monitor = monitor;
            Type? patches = AccessTools.TypeByName("ScaleUpUnofficial.HarmonyPatches");
            if (patches == null)
                return;
            _drawGuard = AccessTools.Field(patches, "_spriteAlreadyDrawn");
            _shouldProcess = AccessTools.Method(patches, "ShouldProcessTexture");
            _found = _drawGuard != null && _shouldProcess != null;
            monitor.Log(_found
                ? "Scale Up Unofficial is here: shadows draw its scaled character sheets at their own size."
                : "Scale Up Unofficial is here, but not in a shape this mod knows; its scaled characters may have no shadow.",
                LogLevel.Trace);
        }

        /// <summary>SpriteBatch.Draw for a caster's frame into one of this mod's targets: the same
        /// arguments the game's sizes are in, whatever Scale Up holds the sheet at.</summary>
        internal static void Draw(SpriteBatch batch, Texture2D texture, Vector2 position, Rectangle? frame, Color color,
            float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float depth)
        {
            Answer answer = _found && frame.HasValue ? _answers.GetValue(texture, Ask) : _notScaled;
            if (answer.Sheet == null || frame is not Rectangle source)
            {
                batch.Draw(texture, position, frame, color, rotation, origin, scale, effects, depth);
                return;
            }
            float factor = answer.Scale;
            int sourceX = answer.OriginalWidth > 0 ? source.X % answer.OriginalWidth : source.X;
            var scaledSource = new Rectangle((int)(sourceX * factor), (int)(source.Y * factor),
                (int)(source.Width * factor), (int)(source.Height * factor));
            _drawGuard!.SetValue(null, true);
            try
            {
                batch.Draw(answer.Sheet, position, scaledSource, color, rotation, origin * factor, scale / factor, effects, depth);
            }
            finally
            {
                _drawGuard.SetValue(null, false);
            }
        }

        /// <summary>The float-scale form of <see cref="Draw(SpriteBatch, Texture2D, Vector2, Rectangle?, Color, float, Vector2, Vector2, SpriteEffects, float)"/>.</summary>
        internal static void Draw(SpriteBatch batch, Texture2D texture, Vector2 position, Rectangle? frame, Color color,
            float rotation, Vector2 origin, float scale, SpriteEffects effects, float depth)
            => Draw(batch, texture, position, frame, color, rotation, origin, new Vector2(scale), effects, depth);

        private static Answer Ask(Texture2D texture)
        {
            try
            {
                object?[] arguments = [texture, null];
                if (_shouldProcess!.Invoke(null, arguments) is not true || arguments[1] is not object data)
                    return _notScaled;
                Type type = data.GetType();
                object? sprite = AccessTools.Property(type, "Sprite")?.GetValue(data);
                bool isFarmer = AccessTools.Property(type, "IsFarmer")?.GetValue(data) is true;
                bool sized = AccessTools.Property(type, "SpriteWidth")?.GetValue(data) != null
                    || AccessTools.Property(type, "SpriteHeight")?.GetValue(data) != null;
                if (sprite == null || isFarmer || sized)
                    return _notScaled;
                // A character sprite entry is always four times (Scale Up's GetTextureScaleMultiplier).
                object? replaced = AccessTools.Property(texture.GetType(), "NewTexture")?.GetValue(texture);
                int originalWidth = AccessTools.Property(type, "OrgWidth")?.GetValue(data) is int width ? width : 0;
                _monitor?.Log($"Scale Up holds '{texture.Name}' at four times: its shadows are drawn from the full sheet.", LogLevel.Trace);
                return new Answer { Scale = 4f, Sheet = replaced as Texture2D ?? texture, OriginalWidth = originalWidth };
            }
            catch (Exception exception)
            {
                QuietFailures.Note("Scale Up: reading how a sheet is scaled", exception);
                return _notScaled;
            }
        }
    }
}
