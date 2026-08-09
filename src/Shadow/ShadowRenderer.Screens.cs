using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// ShadowRenderer — SPLIT SCREEN. The bake caches are keyed by sprite and are shared happily:
    /// two farmers looking at the same fence want the same silhouette. The PLAYER bake is not, and
    /// it is the expensive one.
    ///
    /// <para>
    /// It is a render target holding one pose, kept from frame to frame and reused as long as the
    /// pose has not changed, because re-baking it measured as the single most expensive thing this
    /// mod does. On a split screen there were two players and one target: whoever drew last owned
    /// it, so the pose it held never matched the player about to be drawn, the reuse test failed
    /// every single time, and both screens paid the full bake every frame. The water reflection
    /// reads the same pair of targets, so it was mirroring whichever body was baked most recently.
    /// </para>
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        private sealed class ScreenBake
        {
            public RenderTarget2D? Mask;
            public RenderTarget2D? Color;
            public (int frame, int facing, Rectangle src) Signature = (-1, -1, default);
            public Vector2 FeetInRenderTarget;
            public bool Ready, MaskFresh, ColorFresh;

            public void Release()
            {
                Mask?.Dispose();
                Color?.Dispose();
            }
        }

        private readonly Dictionary<int, ScreenBake> _screenBakes = new();
        private int _activeScreenId = -1;
        private readonly List<int> _departedScreens = new();

        /// <summary>Hand the player bake over to one screen, at the top of that screen's turn.
        /// A no-op in single player, where the id never changes.</summary>
        internal void BeginScreen(int screenId)
        {
            if (screenId == _activeScreenId)
                return;
            if (_activeScreenId >= 0)
            {
                if (!_screenBakes.TryGetValue(_activeScreenId, out ScreenBake? outgoing))
                    _screenBakes[_activeScreenId] = outgoing = new ScreenBake();
                outgoing.Mask = _playerRenderTarget;
                outgoing.Color = _playerColorRenderTarget;
                outgoing.Signature = _playerBakeSignature;
                outgoing.FeetInRenderTarget = _playerFeetInRenderTarget;
                outgoing.Ready = _playerReady;
                outgoing.MaskFresh = _playerMaskFresh;
                outgoing.ColorFresh = _playerColorFresh;
            }
            _activeScreenId = screenId;
            if (!_screenBakes.TryGetValue(screenId, out ScreenBake? incoming))
                _screenBakes[screenId] = incoming = new ScreenBake();
            _playerRenderTarget = incoming.Mask;
            _playerColorRenderTarget = incoming.Color;
            _playerBakeSignature = incoming.Signature;
            _playerFeetInRenderTarget = incoming.FeetInRenderTarget;
            _playerReady = incoming.Ready;
            _playerMaskFresh = incoming.MaskFresh;
            _playerColorFresh = incoming.ColorFresh;
            // The published pair follows the screen too: their one reader is this screen's
            // reflection, which runs between now and the next screen's turn.
            PlayerMask = _playerMaskFresh ? _playerRenderTarget : null;
            PlayerColor = _playerColorFresh ? _playerColorRenderTarget : null;
            ForgetDepartedScreens();
        }

        private void ForgetDepartedScreens()
        {
            int live = GameRunner.instance?.gameInstances?.Count ?? 1;
            if (_screenBakes.Count <= live)
                return;
            _departedScreens.Clear();
            foreach (var kv in _screenBakes)
            {
                if (kv.Key >= live && kv.Key != _activeScreenId)
                    _departedScreens.Add(kv.Key);
            }
            foreach (int id in _departedScreens)
            {
                _screenBakes[id].Release();
                _screenBakes.Remove(id);
            }
        }
    }
}
