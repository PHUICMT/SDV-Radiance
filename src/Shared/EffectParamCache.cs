using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;

namespace SDVRadiance
{
    /// <summary>
    /// Cached <see cref="EffectParameter"/> lookups. MonoGame's EffectParameterCollection
    /// indexer is a LINEAR scan with string compares, and the stages look parameters up
    /// ~100 times per frame — cache the references once per (effect, name) so a warm frame
    /// pays a dictionary hash instead. Clear on dispose so stale Effect keys don't pin
    /// disposed shaders in memory.
    /// </summary>
    internal sealed class EffectParamCache
    {
        private readonly Dictionary<(Effect effect, string name), EffectParameter?> _byEffectAndName = new();

        public EffectParameter? Get(Effect effect, string name)
        {
            var key = (effect, name);
            if (!_byEffectAndName.TryGetValue(key, out EffectParameter? parameter))
                _byEffectAndName[key] = parameter = effect.Parameters[name];
            return parameter;
        }

        public void Clear() => _byEffectAndName.Clear();
    }
}
