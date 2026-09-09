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
        /// <summary>How many texture units MonoGame keeps a sampler slot for on this machine, or
        /// zero before the guard has looked. It asks the driver for
        /// GL_MAX_COMBINED_TEXTURE_IMAGE_UNITS and does not clamp it, and it walks every one of
        /// them on every draw call inside SamplerStateCollection.PlatformSetSamplers. On a card
        /// that answers in the hundreds that is a loop nobody wrote and nobody can see; a shader
        /// model 3 pixel shader can address sixteen. Reported so the number is known before anyone
        /// spends a day on the idea of capping it.</summary>
        internal static int SamplerSlots { get; private set; }

        /// <summary>The arrays MonoGame made at startup, kept so the cap can be lifted again.</summary>
        private static SamplerState[]? _fullSamplers, _fullActualSamplers;
        /// <summary>How many slots the sampler collection currently holds, or zero if untouched.</summary>
        internal static int SamplerSlotCap { get; private set; }
        /// <summary>What a shader model 3 pixel shader can address, and four times what the
        /// heaviest shader in this mod declares.</summary>
        internal const int AddressableSamplerSlots = 16;
        /// <summary>The slot count kept by default. Every slot past this one is already dead in
        /// MonoGame: <c>TextureCollection</c>'s dirty mask is a 32-bit int, and its indexer marks
        /// a slot with <c>1 &lt;&lt; index</c>, which for index 32 and beyond wraps and dirties
        /// some other slot instead. So a texture can never be bound above 31, a sampler above 31
        /// is applied to nothing, and dropping those slots cannot change a pixel. Sixteen measured
        /// no faster than thirty-two (10.18 and 10.11 against 9.86 and 10.14 ms on the same spot),
        /// so the safer number is the one that ships.</summary>
        internal const int DefaultSamplerSlots = 32;
        /// <summary>radiance_samplerslots: 0 leaves the driver's count alone.</summary>
        internal static int WantedSamplerSlots = DefaultSamplerSlots;

        /// <summary>
        /// Shorten the sampler collection so the per-draw loop walks the slots a shader can
        /// actually use instead of every unit the driver reports.
        /// </summary>
        /// <remarks>
        /// <para><c>SamplerStateCollection.PlatformSetSamplers</c> runs on every draw call, from
        /// <c>GraphicsDevice.ApplyState</c>, and loops the whole array with no early out: this
        /// machine answers GL_MAX_COMBINED_TEXTURE_IMAGE_UNITS with 192, and a busy frame here
        /// issues about 2,750 draw calls, so that is half a million iterations a frame spent
        /// asking whether slots nothing can address have changed. The texture collection beside it
        /// has an early out and a dirty mask, and that mask is a 32-bit int, so MonoGame itself
        /// cannot use a slot past 31.</para>
        /// <para>Shortening the arrays is the only way to shorten the loop without replacing the
        /// method. The cost of being wrong is an IndexOutOfRangeException the first time anything
        /// sets a sampler above the cap, so the cap is sixteen (what the OpenGL profile's pixel
        /// shaders can address; the heaviest shader in this mod declares five) and it is a switch,
        /// off until measured.</para>
        /// </remarks>
        internal static bool CapSamplerSlots(GraphicsDevice device, int cap, IMonitor monitor)
        {
            try
            {
                var samplersOf = AccessTools.FieldRefAccess<SamplerStateCollection, SamplerState[]>("_samplers");
                var actualOf = _actualSamplersOf
                    ?? AccessTools.FieldRefAccess<SamplerStateCollection, SamplerState[]>("_actualSamplers");
                SamplerStateCollection collection = device.SamplerStates;
                ref SamplerState[] samplers = ref samplersOf(collection);
                ref SamplerState[] actual = ref actualOf(collection);
                _fullSamplers ??= samplers;
                _fullActualSamplers ??= actual;
                int wanted = cap <= 0 ? _fullSamplers.Length : Math.Min(cap, _fullSamplers.Length);
                var newSamplers = new SamplerState[wanted];
                var newActual = new SamplerState[wanted];
                for (int i = 0; i < wanted; i++)
                {
                    newSamplers[i] = i < samplers.Length ? samplers[i] : _fullSamplers[i];
                    newActual[i] = i < actual.Length ? actual[i] : _fullActualSamplers[i];
                }
                samplers = newSamplers;
                actual = newActual;
                SamplerSlotCap = wanted;
                monitor.Log($"sampler slots walked per draw call: {wanted} of {_fullSamplers.Length}.", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                monitor.Log($"could not change the sampler slot count ({ex.GetType().Name}: {ex.Message}).", LogLevel.Warn);
                return false;
            }
        }

        /// <summary>Read the sampler slot count once, without patching anything. Safe to call
        /// before Install: it uses its own field accessor if the guard has not made one.</summary>
        internal static int CountSamplerSlots(GraphicsDevice device)
        {
            if (SamplerSlots > 0)
                return SamplerSlots;
            try
            {
                var samplersOf = _actualSamplersOf
                    ?? AccessTools.FieldRefAccess<SamplerStateCollection, SamplerState[]>("_actualSamplers");
                SamplerSlots = samplersOf(device.SamplerStates).Length;
            }
            catch
            {
                SamplerSlots = 0;
            }
            return SamplerSlots;
        }

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

        /// <summary>Keep the sampler collection at the wanted length. The device rebuilds these
        /// arrays whenever it is reset (a resolution change, a full-screen toggle), so this is
        /// checked rather than done once: it is one length comparison on the frames where nothing
        /// changed.</summary>
        internal static void HoldSamplerSlots(GraphicsDevice device, IMonitor monitor)
        {
            if (WantedSamplerSlots <= 0)
            {
                // Switched off after being on: give MonoGame its own arrays back, so the setting
                // takes effect without a restart.
                if (SamplerSlotCap > 0 && _fullSamplers != null)
                {
                    CapSamplerSlots(device, 0, monitor);
                    SamplerSlotCap = 0;
                }
                return;
            }
            try
            {
                var actualOf = _actualSamplersOf
                    ?? AccessTools.FieldRefAccess<SamplerStateCollection, SamplerState[]>("_actualSamplers");
                // Against what the cap will ACTUALLY leave behind, which is never more slots than
                // the driver has. This used to compare against the number ASKED FOR, and a card
                // that offers fewer than that could never match it: every render step found the
                // length wrong, rebuilt both arrays and wrote a log line, for the whole session.
                // Nothing was drawn differently, so the frame rate looked untouched while the
                // minimum collapsed, which is exactly how it was reported: an Apple M4 Max whose
                // Metal driver has 16 slots against the 32 asked for here, stuttering into the low
                // forties. Standing in for that card by asking for 400 on a 192 slot one produced
                // 373,244 of those lines in one short session; with this check it produces one.
                int haveNow = actualOf(device.SamplerStates).Length;
                int driverHas = _fullSamplers?.Length ?? SamplerSlots;
                int willApply = driverHas > 0 ? Math.Min(WantedSamplerSlots, driverHas) : WantedSamplerSlots;
                if (haveNow == willApply)
                    return;
            }
            catch
            {
                WantedSamplerSlots = 0;
                return;
            }
            _fullSamplers = null;
            _fullActualSamplers = null;
            if (!CapSamplerSlots(device, WantedSamplerSlots, monitor))
                WantedSamplerSlots = 0;
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
