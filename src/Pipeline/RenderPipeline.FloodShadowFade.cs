using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace SDVRadiance
{
    /// <summary>
    /// Which lights in the flood pass get a shadow, and how strongly - eased, so crossing the
    /// boundary between the two tiers is a fade rather than a switch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The flood shader treats lights in two tiers: the first eight get a shadow ray marched
    /// toward them, and the next sixteen get a pool with no shadow at all. Which eight they are
    /// comes from the same ranking that decides slots, and that ranking moves as the camera does.
    /// So a lamp crossing rank eight while you walked flipped between a shadowed pool and a flat
    /// one in one frame. Nothing faded it: every fade in the lighting path guards whether a light
    /// is in the array, and none of them had ever guarded which tier it landed in.
    /// </para>
    /// <para>
    /// Found by elimination, after five wrong suspects. Turning the whole mod off stopped it;
    /// turning off bloom, the lighting pass, tilt shift, cloud shadows and the colour grade one at
    /// a time did not; pinning the bounce grid with radiance_flood freeze did not, because that
    /// pins a CPU sweep while this is computed in the shader every frame. Switching off flood
    /// lighting stopped it, in town and in the saloon, which put the fault inside this pass but
    /// not in its grid.
    /// </para>
    /// <para>
    /// A light entering the shadowed tier therefore starts at weight zero, which is exactly what
    /// the tier below it looks like, and grows its shadow over about a third of a second. A light
    /// that loses its place KEEPS the slot while its weight falls, and only releases it at zero -
    /// otherwise fading out would still end in a jump, because the tier it falls into has no
    /// shadow to fade to. That costs a shadowed slot for a few frames, which is the same trade
    /// the light slots themselves already make to get a crossfade.
    /// </para>
    /// </remarks>
    internal sealed partial class RenderPipeline
    {
        /// <summary>Per-light shadow weight, keyed by the light id the ranking uses.</summary>
        private readonly Dictionary<int, float> _floodShadowWeight = new();
        private readonly List<int> _floodShadowOrder = new();
        private readonly List<int> _floodShadowDrop = new();
        private readonly HashSet<int> _floodShadowWanted = new();
        /// <summary>Light ids in rank order for this frame, reused so the per-frame
        /// tier decision does not allocate.</summary>
        private readonly List<int> _floodLiveIds = new();

        /// <summary>Matches the light array's own entry rate, so a lamp's shadow arrives with the
        /// rest of it rather than trailing behind or racing ahead.</summary>
        private const float FloodShadowFadePerFrame = 0.045f;

        /// <summary>
        /// Decide this frame's shadowed tier and advance every weight.
        /// </summary>
        /// <param name="liveIds">Light ids in rank order, as the array was written.</param>
        /// <returns>Ids that hold a shadowed slot, in the order they should be uploaded.</returns>
        private List<int> AdvanceFloodShadowTier(List<int> liveIds)
        {
            // Wanted: the top of the ranking, which is what the tier would have been with no
            // easing at all.
            _floodShadowWanted.Clear();
            for (int i = 0; i < liveIds.Count && i < FloodShadowedLights; i++)
                _floodShadowWanted.Add(liveIds[i]);

            // A light that left the array entirely takes its weight with it: it is not on screen,
            // so there is nothing left to fade.
            _floodShadowDrop.Clear();
            foreach (int id in _floodShadowWeight.Keys)
                if (!liveIds.Contains(id))
                    _floodShadowDrop.Add(id);
            foreach (int id in _floodShadowDrop)
                _floodShadowWeight.Remove(id);

            foreach (int id in liveIds)
            {
                bool wanted = _floodShadowWanted.Contains(id);
                _floodShadowWeight.TryGetValue(id, out float w);
                if (wanted)
                    w = Math.Min(1f, w + FloodShadowFadePerFrame);
                else
                    w -= FloodShadowFadePerFrame;
                if (w <= 0f)
                    _floodShadowWeight.Remove(id);
                else
                    _floodShadowWeight[id] = w;
            }

            // Fill the slots: anything still carrying weight first, brightest weight first, so a
            // light on its way out cannot be evicted by one on its way in and leave a step behind.
            _floodShadowOrder.Clear();
            foreach (int id in liveIds)
                if (_floodShadowWeight.ContainsKey(id))
                    _floodShadowOrder.Add(id);
            _floodShadowOrder.Sort((a, b) =>
            {
                float wa = _floodShadowWeight[a], wb = _floodShadowWeight[b];
                int byWeight = wb.CompareTo(wa);
                return byWeight != 0 ? byWeight : a.CompareTo(b);
            });
            if (_floodShadowOrder.Count > FloodShadowedLights)
                _floodShadowOrder.RemoveRange(FloodShadowedLights,
                    _floodShadowOrder.Count - FloodShadowedLights);
            return _floodShadowOrder;
        }

        /// <summary>This light's shadow weight, 0 when it has none.</summary>
        private float FloodShadowWeight(int id)
            => _floodShadowWeight.TryGetValue(id, out float w) ? MathHelper.Clamp(w, 0f, 1f) : 0f;
    }
}
