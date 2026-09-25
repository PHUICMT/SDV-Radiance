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
            public (int frame, int facing, Rectangle sourceRect, int look) Signature = (-1, -1, default, 0);
            /// <summary>Whose silhouette this is. Another screen's remote-farmer pass reads it to
            /// borrow this bake instead of making a second one of the same person (see
            /// TryBorrowPlayerBake).</summary>
            public long FarmerId;
            public Vector2 FeetInRenderTarget;
            public bool Ready, MaskFresh, ColorFresh;
            /// <summary>Where this screen last enumerated a location for object bakes. It has to
            /// be per screen for the same reason the pose does: two screens standing in two
            /// different places wrote that location into one shared field, so each screen read
            /// the OTHER one's place, every frame looked like an arrival to both, and both paid
            /// the full walk of their whole map every frame to bake nothing at all.</summary>
            public GameLocation? ObjectBakeLocation;
            /// <summary>The player's silhouette laid down by this screen's sun, and what it was
            /// laid down with (see LayDownPlayerSun). Per screen for the same reason the pose
            /// is: two screens are two players under one sun, and one target cannot hold both.</summary>
            public RenderTarget2D? SunMask;
            public Vector2 SunFeet;
            public float SunUnbake = 1f;
            public Rectangle SunContent;
            public ShadowProjection SunProjection;
            public float SunBlur = -1f;
            /// <summary>The rest of what the laid-down silhouette was baked with. Shared, one screen's
            /// re-bake stamped the new values and the other screen's old silhouette then passed the
            /// check, keeping its old edge after the strength, contact or penumbra dials moved.</summary>
            public float SunContactHardness = -1f, SunPenumbraStretch = -1f, SunBakeDepth = -1f;
            public bool SunFresh;
            public (int frame, int facing, Rectangle sourceRect, int look) SunSignature = (-1, -1, default, 0);
            /// <summary>This screen's pose library and what it watches to know when to empty itself
            /// (see ShadowRenderer.PoseLibrary). Per screen because the place and the menu it
            /// watches are each screen's own.</summary>
            public Dictionary<PoseLibraryKey, PoseLibraryEntry> PoseLibrary = [];
            public long PoseLibraryClearedTick;
            public bool PoseLibrarySawMenu;
            public GameLocation? PoseLibraryLocation;

            public void Release()
            {
                Mask?.Dispose();
                Color?.Dispose();
                SunMask?.Dispose();
                foreach (PoseLibraryEntry entry in PoseLibrary.Values)
                {
                    entry.Mask?.Dispose();
                    entry.Colour?.Dispose();
                }
                PoseLibrary.Clear();
            }
        }

        private readonly Dictionary<int, ScreenBake> _screenBakes = [];
        private int _activeScreenId = -1;
        private readonly List<int> _departedScreens = [];

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
                outgoing.FarmerId = _playerBakeFarmerId;
                outgoing.FeetInRenderTarget = _playerFeetInRenderTarget;
                outgoing.Ready = _playerReady;
                outgoing.MaskFresh = _playerMaskFresh;
                outgoing.ColorFresh = _playerColorFresh;
                outgoing.ObjectBakeLocation = _objectBakeLocation;
                outgoing.SunMask = _playerSunRenderTarget;
                outgoing.SunFeet = _playerSunFeet;
                outgoing.SunUnbake = _playerSunUnbake;
                outgoing.SunContent = _playerSunContent;
                outgoing.SunProjection = _playerSunProjection;
                outgoing.SunBlur = _playerSunBlur;
                outgoing.SunContactHardness = _playerSunContactHardness;
                outgoing.SunPenumbraStretch = _playerSunPenumbraStretch;
                outgoing.SunBakeDepth = _playerSunBakeDepth;
                outgoing.SunFresh = _playerSunFresh;
                outgoing.SunSignature = _playerSunSignature;
                outgoing.PoseLibrary = _poseLibrary;
                outgoing.PoseLibraryClearedTick = _poseLibraryClearedTick;
                outgoing.PoseLibrarySawMenu = _poseLibrarySawMenu;
                outgoing.PoseLibraryLocation = _poseLibraryLocation;
            }
            _activeScreenId = screenId;
            if (!_screenBakes.TryGetValue(screenId, out ScreenBake? incoming))
                _screenBakes[screenId] = incoming = new ScreenBake();
            _playerRenderTarget = incoming.Mask;
            _playerColorRenderTarget = incoming.Color;
            _playerBakeSignature = incoming.Signature;
            _playerBakeFarmerId = incoming.FarmerId;
            _playerFeetInRenderTarget = incoming.FeetInRenderTarget;
            _playerReady = incoming.Ready;
            _playerMaskFresh = incoming.MaskFresh;
            _playerColorFresh = incoming.ColorFresh;
            _objectBakeLocation = incoming.ObjectBakeLocation;
            _playerSunRenderTarget = incoming.SunMask;
            _playerSunFeet = incoming.SunFeet;
            _playerSunUnbake = incoming.SunUnbake;
            _playerSunContent = incoming.SunContent;
            _playerSunProjection = incoming.SunProjection;
            _playerSunBlur = incoming.SunBlur;
            _playerSunContactHardness = incoming.SunContactHardness;
            _playerSunPenumbraStretch = incoming.SunPenumbraStretch;
            _playerSunBakeDepth = incoming.SunBakeDepth;
            _playerSunFresh = incoming.SunFresh;
            _playerSunSignature = incoming.SunSignature;
            _poseLibrary = incoming.PoseLibrary;
            _poseLibraryClearedTick = incoming.PoseLibraryClearedTick;
            _poseLibrarySawMenu = incoming.PoseLibrarySawMenu;
            _poseLibraryLocation = incoming.PoseLibraryLocation;
            // The published pair follows the screen too: their one reader is this screen's
            // reflection, which runs between now and the next screen's turn.
            // A rider's bake is for the shadow alone (see PreparePlayer).
            bool riding = Game1.player?.isRidingHorse() ?? false;
            PlayerMask = _playerMaskFresh && !riding ? _playerRenderTarget : null;
            PlayerColor = _playerColorFresh && !riding ? _playerColorRenderTarget : null;
            ForgetDepartedScreens();
        }

        /// <summary>Forget where every screen last enumerated, not only the one drawing now.
        /// The bakes are gone, so each screen has to walk its own map again on its next turn or
        /// its draw pass finds every sprite missing and paints a screen of banded stand-ins.</summary>
        private void ForgetObjectBakeLocations()
        {
            _objectBakeLocation = null;
            foreach (ScreenBake parked in _screenBakes.Values)
                parked.ObjectBakeLocation = null;
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
            // Before anything is disposed: a remote farmer entry may be pointing at one of these
            // targets (TryBorrowPlayerBake).
            DropFarmerBakeLoans();
            foreach (int id in _departedScreens)
            {
                _screenBakes[id].Release();
                _screenBakes.Remove(id);
            }
        }
    }
}
