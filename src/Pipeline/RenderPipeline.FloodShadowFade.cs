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
        private readonly Dictionary<int, float> _floodShadowWeight = [];
        private readonly List<int> _floodShadowOrder = [];
        private readonly List<int> _floodShadowDrop = [];
        private readonly HashSet<int> _floodShadowWanted = [];
        /// <summary>The lights holding a shadowed slot after the last frame.</summary>
        private readonly List<int> _floodShadowHolders = [];
        /// <summary>Light ids in rank order for this frame, reused so the per-frame
        /// tier decision does not allocate.</summary>
        private readonly List<int> _floodLiveIds = [];
        /// <summary>The same lights by rank, best first: who the shadowed tier should want.</summary>
        private readonly List<int> _floodRankedIds = [];
        private readonly List<int> _floodRankedSlots = [];
        private Comparison<int>? _floodByRankThenId;
        private Comparison<int>? _floodShadowByWeightThenId;

        /// <summary>Matches the light array's own entry rate, so a lamp's shadow arrives with the
        /// rest of it rather than trailing behind or racing ahead.</summary>
        private const float FloodShadowFadePerFrame = 0.045f;

        /// <summary>
        /// Decide this frame's shadowed tier and advance every weight.
        /// </summary>
        /// <param name="liveIds">Light ids in the array's slot order, as it was written.</param>
        /// <param name="rankedIds">The same ids by rank, best first.</param>
        /// <returns>Ids that hold a shadowed slot, in the order they should be uploaded.</returns>
        private List<int> AdvanceFloodShadowTier(List<int> liveIds, List<int> rankedIds)
        {
            // Wanted: the top of the RANKING, which is what the tier would have been with no
            // easing at all. Not the top of the slot order: slots are stable for a light's stay,
            // so that was "whoever arrived first", and a glow ring re-equipped mid-session never
            // got a shadow ray again (see SetLightArrays).
            // A light that casts no shadow (a flying companion's) is passed over, so its place goes
            // to the next lamp; one that already held a slot fades out like any light leaving.
            _floodShadowWanted.Clear();
            for (int i = 0; i < rankedIds.Count && _floodShadowWanted.Count < FloodShadowedLights; i++)
                if (!_shadowlessLightIds.Contains(rankedIds[i]))
                    _floodShadowWanted.Add(rankedIds[i]);

            // A light that left the array entirely takes its weight with it: it is not on screen,
            // so there is nothing left to fade.
            _floodShadowDrop.Clear();
            foreach (int id in _floodShadowWeight.Keys)
                if (!liveIds.Contains(id))
                    _floodShadowDrop.Add(id);
            foreach (int id in _floodShadowDrop)
                _floodShadowWeight.Remove(id);

            // Who holds a shadowed slot this frame. A holder keeps its slot for as long as it has
            // any weight, wanted or not, and a newcomer takes only a slot that is free. The weight
            // used to grow for every wanted light whether it held a slot or not, and the slots went
            // to the heaviest: with a ninth lamp in play, the one arriving climbed while the one
            // leaving fell, and they swapped slots where the two crossed near half, so one shadow
            // vanished at half strength and the other appeared at half strength, each in a frame.
            // Walking at night among more than eight lamps, found by a code audit. The class notes
            // above always said a leaving light keeps its slot until its weight reaches zero.
            _floodShadowOrder.Clear();
            foreach (int id in _floodShadowHolders)
                if (liveIds.Contains(id) && _floodShadowWeight.ContainsKey(id))
                    _floodShadowOrder.Add(id);
            foreach (int id in rankedIds)
            {
                if (_floodShadowOrder.Count >= FloodShadowedLights)
                    break;
                if (_floodShadowWanted.Contains(id) && !_floodShadowOrder.Contains(id))
                    _floodShadowOrder.Add(id);
            }

            // Only a holder carries weight: it grows while the light is wanted and falls while it
            // is not, and a holder that reaches zero gives its slot up for the next frame.
            _floodShadowDrop.Clear();
            foreach (int id in _floodShadowWeight.Keys)
                if (!_floodShadowOrder.Contains(id))
                    _floodShadowDrop.Add(id);
            foreach (int id in _floodShadowDrop)
                _floodShadowWeight.Remove(id);
            _floodShadowHolders.Clear();
            foreach (int id in _floodShadowOrder)
            {
                _floodShadowWeight.TryGetValue(id, out float w);
                w = _floodShadowWanted.Contains(id) ? Math.Min(1f, w + FloodShadowFadePerFrame) : w - FloodShadowFadePerFrame;
                if (w <= 0f)
                {
                    _floodShadowWeight.Remove(id);
                    continue;
                }
                _floodShadowWeight[id] = w;
                _floodShadowHolders.Add(id);
            }
            _floodShadowOrder.Clear();
            _floodShadowOrder.AddRange(_floodShadowHolders);
            // The comparison is kept, not written inline: a lambda that reads a field captures
            // this, and List.Sort with a fresh delegate allocated one per frame for as long
            // as the game ran. Same idiom as _floodByRankThenId beside it. The order only fixes which
            // uniform slot each holder is uploaded to; which lights hold a slot is decided above.
            _floodShadowByWeightThenId ??= (a, b) =>
            {
                float wa = _floodShadowWeight[a], wb = _floodShadowWeight[b];
                int byWeight = wb.CompareTo(wa);
                return byWeight != 0 ? byWeight : a.CompareTo(b);
            };
            _floodShadowOrder.Sort(_floodShadowByWeightThenId);
            return _floodShadowOrder;
        }

        /// <summary>The shadow weight of a light the game names by this key, for radiance_lights. A
        /// map lamp merged into its neighbourhood goes by the neighbourhood's id and reads 0 here.</summary>
        internal float FloodShadowWeightOf(string lightKey) => FloodShadowWeight(StableLightId(lightKey));

        /// <summary>This light's shadow weight, 0 when it has none.</summary>
        private float FloodShadowWeight(int id)
            => _floodShadowWeight.TryGetValue(id, out float w) ? MathHelper.Clamp(w, 0f, 1f) : 0f;
    }
}
