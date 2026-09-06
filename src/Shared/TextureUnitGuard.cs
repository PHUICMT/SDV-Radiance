using System;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// Keeps MonoGame's idea of which texture sits on each texture unit true at the moment it
    /// writes a sampler to that unit.
    /// </summary>
    /// <remarks>
    /// <para>MonoGame GL applies a sampler state to whatever texture is bound on the unit right
    /// then (SamplerStateCollection.PlatformSetSamplers: ActiveTexture, then Activate, with no
    /// bind of its own), trusting TextureCollection to have bound Textures[i] there. But the
    /// collection only binds a unit whose entry CHANGED, and Texture2D.GetData binds the texture
    /// it reads on the current active unit and never puts the previous one back.</para>
    /// <para>So: the flood pass leaves units up to 11 holding this mod's own targets, and the
    /// active unit high. Anything that then reads a sheet with GetData (this mod's surface and
    /// shadow readers at a warp, SMAPI and content packs loading art, a costume mod composing a
    /// character) parks that sheet on the high unit. The next flood pass re-applies its LINEAR
    /// sampler on that slot; MonoGame writes it into the parked sheet and records the change
    /// against the target it believed was there. From then on every Point batch in the game reads
    /// that sheet linearly: a mailbox, a crop, a walking villager turns into a soft blot while the
    /// map tiles around it stay crisp, and a restart is the only cure. Measured 2026-09-06 on the
    /// farm mailbox, 4x4 screen blocks that are not one colour: without this mod 0%, with it 82%,
    /// with flood lighting off 0.4%, and 21% the frame after the Point filter was forced back.
    /// The game and its other mods never reach this because none of them use a second texture
    /// unit.</para>
    /// <para>The prefix on TextureCollection.SetTextures marks every slot whose sampler is about
    /// to be re-applied as dirty, so the collection binds the believed texture on that unit first
    /// and the sampler lands where MonoGame thinks it does. Once per draw call, at most sixteen
    /// slots, and GL is only ever touched through MonoGame's own path.</para>
    /// </remarks>
    internal static class TextureUnitGuard
    {
        private static AccessTools.FieldRef<SamplerStateCollection, SamplerState[]>? _actualSamplersOf;
        private static AccessTools.FieldRef<TextureCollection, Texture[]>? _texturesOf;
        private static AccessTools.FieldRef<TextureCollection, int>? _dirtyOf;
        private static AccessTools.FieldRef<Texture, SamplerState>? _lastSamplerOf;
        /// <summary>Off only for an A/B (radiance_unitguard off): the prefix stays patched and does nothing.</summary>
        internal static bool Enabled = true;
        internal static bool Installed { get; private set; }
        /// <summary>Slots rebound because a sampler was about to be written to them, for the report.</summary>
        internal static long Rebinds;
        /// <summary>Texture units above the first, the ones only multi-texture effects use.</summary>
        private const int HighUnits = 16;

        internal static void Install(Harmony harmony, IMonitor monitor)
        {
            try
            {
                var setTextures = AccessTools.Method(typeof(TextureCollection), "SetTextures", new[] { typeof(GraphicsDevice) });
                if (setTextures == null)
                {
                    monitor.Log("TextureCollection.SetTextures not found; the texture unit guard is off, and a sheet a GetData parks on a high unit can be read linearly for the session.", LogLevel.Warn);
                    return;
                }
                _actualSamplersOf = AccessTools.FieldRefAccess<SamplerStateCollection, SamplerState[]>("_actualSamplers");
                _texturesOf = AccessTools.FieldRefAccess<TextureCollection, Texture[]>("_textures");
                _dirtyOf = AccessTools.FieldRefAccess<TextureCollection, int>("_dirty");
                _lastSamplerOf = AccessTools.FieldRefAccess<Texture, SamplerState>("glLastSamplerState");
                harmony.Patch(setTextures, prefix: new HarmonyMethod(typeof(TextureUnitGuard), nameof(SetTextures_Prefix)));
                Installed = true;
            }
            catch (Exception ex)
            {
                Installed = false;
                monitor.Log($"texture unit guard not installed ({ex.GetType().Name}: {ex.Message}); a sheet a GetData parks on a high texture unit can be read linearly for the session.", LogLevel.Warn);
            }
        }

        /// <summary>Before the collection binds its dirty slots: any slot whose sampler MonoGame is
        /// about to re-apply is made dirty too, so the unit holds the believed texture when the
        /// sampler is written. Only the pixel stage; the vertex collection is unused here.</summary>
        private static void SetTextures_Prefix(TextureCollection __instance, GraphicsDevice device)
        {
            if (!Enabled || !ReferenceEquals(__instance, device.Textures))
                return;
            SamplerState[] samplers = _actualSamplersOf!(device.SamplerStates);
            Texture[] textures = _texturesOf!(__instance);
            ref int dirty = ref _dirtyOf!(__instance);
            int slots = Math.Min(samplers.Length, textures.Length);
            for (int i = 0; i < slots; i++)
            {
                Texture? texture = textures[i];
                SamplerState? sampler = samplers[i];
                if (texture == null || sampler == null || ReferenceEquals(sampler, _lastSamplerOf!(texture)))
                    continue;
                int bit = 1 << i;
                if ((dirty & bit) != 0)
                    continue;
                dirty |= bit;
                Rebinds++;
            }
        }

        /// <summary>Hand every texture unit above the first back, so nothing this mod parked there
        /// is still believed to be there when the game and the other mods run: a GetData that
        /// lands on one of these units then meets a slot MonoGame knows is empty and skips.
        /// Setting a slot that is already empty changes nothing, so this is free to call often.</summary>
        internal static void ReleaseHighUnits(GraphicsDevice device)
        {
            if (!Enabled)
                return;
            TextureCollection textures = device.Textures;
            for (int i = 1; i < HighUnits; i++)
                textures[i] = null;
        }
    }
}
