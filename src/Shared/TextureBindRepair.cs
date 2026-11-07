using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// Puts the real texture bind back when another mod has replaced MonoGame's.
    ///
    /// <para>Clear Glasses (and SpriteMaster, which it comes from) swaps the delegate
    /// <c>MonoGame.OpenGL.GL.BindTexture</c> for its own, which remembers one texture for the whole
    /// context and skips the call when asked for that texture again. OpenGL holds a binding per
    /// texture unit, so a bind on another unit is lost and that unit keeps the texture it had.
    /// Nothing in the game sets one texture on two units in a row. This mod does it all the time:
    /// the settled lightmap on both of the flood's lightmap samplers, the water mask on its point and
    /// its linear sampler. The second sampler then reads some earlier pass's texture, and the picture
    /// is bright and dark blocks on the ground and a reflection in the wrong place, both of which
    /// slide with the player because the window they are read through does.</para>
    ///
    /// <para>The swap is a plain field write, not a Harmony patch, so taking that mod's patches away
    /// changes nothing. The real bind is chained after the other mod's delegate rather than in place
    /// of it, so its record of the last texture stays right for its own code.</para>
    ///
    /// <para>Only a delegate from a mod's assembly counts as a replacement, and the real
    /// bind is chained once. 2.4.6 took any delegate whose method was not declared in MonoGame for a
    /// replacement. On Android the game's own native bind is declared elsewhere, so the real bind
    /// was chained on every frame, each texture bind ran once more per frame played, and the game
    /// slowed until a step took seconds, with this mod switched off too.</para>
    /// </summary>
    internal static class TextureBindRepair
    {
        private static FieldInfo? _bindField;
        private static Delegate? _realBind;
        private static Delegate? _chainWritten;
        private static bool _gaveUp;

        /// <summary>The mod whose bind was found replaced, for the report; empty when none was.</summary>
        internal static string ReplacedBy { get; private set; } = "";

        /// <summary>How many calls one texture bind makes now, for the report: 2 after a repair.</summary>
        internal static int BindCallsPerBind =>
            _bindField?.GetValue(null) is Delegate current ? current.GetInvocationList().Length : 0;

        /// <summary>Once a frame. The other mod makes its swap whenever its GL code is first
        /// touched, which can be long after launch, so this cannot be a one-time check.</summary>
        internal static void Hold(IMonitor monitor)
        {
            if (_gaveUp)
                return;
            // Clear Glasses and SpriteMaster are desktop mods, and the phone's own bind is not
            // MonoGame's type, so there is nothing here to repair and a wrong guess costs every frame.
            if (Constants.TargetPlatform == GamePlatform.Android)
            {
                _gaveUp = true;
                return;
            }
            try
            {
                Assembly monoGame = typeof(Texture2D).Assembly;
                _bindField ??= monoGame.GetType("MonoGame.OpenGL.GL")?.GetField("BindTexture",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (_bindField?.GetValue(null) is not Delegate current)
                    return;
                if (ReferenceEquals(current, _chainWritten))
                    return;
                if (_realBind != null && current.GetInvocationList().Contains(_realBind))
                    return;
                Assembly? owner = current.Method.DeclaringType?.Assembly;
                if (owner == null || owner == monoGame || !IsModAssembly(owner))
                    return;
                _realBind ??= LoadRealBind(_bindField);
                if (_realBind == null)
                {
                    _gaveUp = true;
                    monitor.Log($"{owner.GetName().Name} replaced the texture bind and the real one could not be loaded.", LogLevel.Warn);
                    return;
                }
                _chainWritten = Delegate.Combine(current, _realBind);
                _bindField.SetValue(null, _chainWritten);
                ReplacedBy = owner.GetName().Name ?? "another mod";
                monitor.Log($"{ReplacedBy} replaced the game's texture bind with one that skips binds this mod's "
                    + "shaders need. The real bind now runs after it.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _gaveUp = true;
                monitor.Log($"could not check the texture bind ({ex.GetType().Name}: {ex.Message}).", LogLevel.Debug);
            }
        }

        /// <summary>Whether the assembly is a SMAPI mod's: loaded from a folder that holds a
        /// manifest.json. The game, MonoGame and the runtime never sit beside one.</summary>
        private static bool IsModAssembly(Assembly assembly)
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                return false;
            string? folder = Path.GetDirectoryName(assembly.Location);
            return folder != null && File.Exists(Path.Combine(folder, "manifest.json"));
        }

        /// <summary>The real glBindTexture, for code that keeps a raw pointer to it: a pointer taken
        /// from the replaced delegate would skip binds too. Null when it cannot be loaded.</summary>
        internal static Delegate? RealBind(FieldInfo bindField) => _realBind ??= LoadRealBind(bindField);

        private static Delegate? LoadRealBind(FieldInfo bindField)
        {
            MethodInfo? load = bindField.DeclaringType?.GetMethod("LoadFunction",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return load?.MakeGenericMethod(bindField.FieldType).Invoke(null, ["glBindTexture", false]) as Delegate;
        }
    }
}
