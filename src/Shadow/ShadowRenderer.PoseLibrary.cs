using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// ShadowRenderer, the POSE LIBRARY: every pose of the player baked once is kept, and a pose
    /// seen again is copied back into the live targets instead of being drawn again.
    ///
    /// <para>
    /// The pose bake draws the farmer through FarmerRenderer, and that draw is only as cheap as the
    /// appearance mods patched into it. Measured 23/9 with an outfit mod installed: 2 ms for the
    /// first draw of a pose, which a walk pays every few frames, 0.5 ms a frame on average and a
    /// 20 ms frame now and then. A walk cycles through the same handful of frames per direction,
    /// so after one lap of it every pose is in here and the copy costs what one quad costs.
    /// </para>
    ///
    /// <para>
    /// A copy is only right while the farmer still looks as they did when it was baked. The key
    /// carries what the game says they wear and hold; a menu closing empties the library, because
    /// that is where an outfit mod changes clothes without touching any of those fields; and the
    /// whole library is emptied every <see cref="PoseLibraryLifetimeTicks"/> frames regardless,
    /// because an outfit mod can also switch clothes by itself (one of them does it by
    /// place and weather) and animate a piece on its own clock. Anything that got past the key and
    /// the menu is at most that stale, and one lap of the walk re-bakes each pose once per
    /// lifetime: sixteen-odd bakes in eight seconds where a walk used to make one every few frames.
    /// With an appearance mod known to animate every frame (<see cref="PlayerAccessoriesAnimate"/>)
    /// the library stays out of the way entirely.
    /// </para>
    ///
    /// <para>
    /// An outfit mod that offers a look number (Integrations.OutfitAppearance) says outright when its farmer changed: its look number is part of the key, so a pose kept
    /// under an older look is simply never asked for again and ages out, and the timer is not needed
    /// while the outfit mod answers. While the outfit mod says a piece is animating the library stays out of the way
    /// too, since every frame of that piece would be a new key kept once and never copied.
    /// </para>
    /// </summary>
    internal sealed partial class ShadowRenderer
    {
        private readonly record struct PoseLibraryKey(long FarmerId, int Frame, int Facing, Rectangle SourceRect, int Appearance);

        private sealed class PoseLibraryEntry
        {
            public RenderTarget2D? Mask;
            public RenderTarget2D? Colour;
            public long LastUsedTick;
        }

        /// <summary>Off by radiance_poselibrary off, for the A/B. Not saved.</summary>
        internal static bool PoseLibraryEnabled = true;
        /// <summary>A walk has four frames a direction and a tool swing a few more; this holds every
        /// pose of a day's play at 67 KB a target.</summary>
        private const int MostLibraryPoses = 48;
        /// <summary>How long a kept pose may be copied before it is drawn again: eight seconds.</summary>
        private const long PoseLibraryLifetimeTicks = 480;
        /// <summary>This screen's library. Swapped per screen with the player bake (see
        /// ShadowRenderer.Screens): a split screen's farmhand has its own copies of every location,
        /// so one shared library saw the place change on every turn and was emptied, and its
        /// targets made again, every frame.</summary>
        private Dictionary<PoseLibraryKey, PoseLibraryEntry> _poseLibrary = [];
        private long _poseLibraryClearedTick;
        private bool _poseLibrarySawMenu;
        private GameLocation? _poseLibraryLocation;
        /// <summary>Poses copied from the library and poses drawn, since the game started: the
        /// receipt radiance_poselibrary prints.</summary>
        internal static int PoseLibraryCopies { get; private set; }
        /// <inheritdoc cref="PoseLibraryCopies"/>
        internal static int PoseLibraryBakes { get; private set; }

        /// <summary>What the farmer wears and holds, as far as the game's own fields say.</summary>
        private static int AppearanceOf(Farmer who)
        {
            var hash = new HashCode();
            hash.Add(who.hat.Value?.QualifiedItemId);
            hash.Add(who.shirtItem.Value?.QualifiedItemId);
            hash.Add(who.shirtItem.Value?.clothesColor.Value);
            hash.Add(who.pantsItem.Value?.QualifiedItemId);
            hash.Add(who.pantsItem.Value?.clothesColor.Value);
            hash.Add(who.boots.Value?.QualifiedItemId);
            hash.Add(who.hair.Value);
            hash.Add(who.skin.Value);
            hash.Add(who.accessory.Value);
            hash.Add(who.hairstyleColor.Value);
            hash.Add(who.pantsColor.Value);
            hash.Add(who.newEyeColor.Value);
            hash.Add(who.IsMale);
            hash.Add(who.bathingClothes.Value);
            hash.Add(who.FarmerRenderer?.textureName.Value);
            hash.Add(who.IsCarrying() ? who.ActiveObject?.QualifiedItemId : null);
            hash.Add(who.UsingTool ? who.CurrentTool?.QualifiedItemId : null);
            hash.Add(Integrations.OutfitAppearance.VersionOf(who));
            return hash.ToHashCode();
        }

        /// <summary>Empty the library when the farmer may have changed clothes behind the key's back:
        /// a menu has just closed, or they are somewhere new.</summary>
        private void KeepPoseLibraryHonest()
        {
            bool menuOpen = Game1.activeClickableMenu != null;
            if (menuOpen)
                _poseLibrarySawMenu = true;
            else if (_poseLibrarySawMenu)
            {
                _poseLibrarySawMenu = false;
                ClearPoseLibrary();
            }
            if (!ReferenceEquals(_poseLibraryLocation, Game1.currentLocation))
            {
                _poseLibraryLocation = Game1.currentLocation;
                ClearPoseLibrary();
            }
            if (!Integrations.OutfitAppearance.Connected && SharedTicks.Now - _poseLibraryClearedTick > PoseLibraryLifetimeTicks)
                ClearPoseLibrary();
        }

        private void ClearPoseLibrary()
        {
            foreach (PoseLibraryEntry entry in _poseLibrary.Values)
            {
                entry.Mask?.Dispose();
                entry.Colour?.Dispose();
            }
            _poseLibrary.Clear();
            _poseLibraryClearedTick = SharedTicks.Now;
        }

        /// <summary>Copy a kept pose into the live targets. False when there is none to copy.</summary>
        private bool TryCopyKeptPose(GraphicsDevice graphicsDevice, PoseLibraryKey key, bool needsColour, Farmer who)
        {
            if (!PoseLibraryEnabled || PlayerAccessoriesAnimate || Integrations.OutfitAppearance.IsAnimating(who)
                || !_poseLibrary.TryGetValue(key, out PoseLibraryEntry? entry)
                || !GpuContent.Usable(entry.Mask) || (needsColour && !GpuContent.Usable(entry.Colour)))
                return false;
            // A pose kept at another size than the targets are made at now is no use; it is dropped
            // when the size grows, and this keeps a copy from ever landing in a target of another size.
            if (entry.Mask!.Width != PlayerRtW || entry.Mask.Height != PlayerRtH)
                return false;
            MakePlayerSized(graphicsDevice, ref _playerRenderTarget, "player silhouette");
            CopyTarget(graphicsDevice, entry.Mask!, _playerRenderTarget);
            if (needsColour)
            {
                MakePlayerSized(graphicsDevice, ref _playerColorRenderTarget, "player colour");
                CopyTarget(graphicsDevice, entry.Colour!, _playerColorRenderTarget);
            }
            entry.LastUsedTick = SharedTicks.Now;
            PoseLibraryCopies++;
            return true;
        }

        /// <summary>Keep what was just baked into the live targets under its pose.</summary>
        private void KeepBakedPose(GraphicsDevice graphicsDevice, PoseLibraryKey key, bool withColour, Farmer who)
        {
            PoseLibraryBakes++;
            if (!PoseLibraryEnabled || PlayerAccessoriesAnimate || Integrations.OutfitAppearance.IsAnimating(who) || _playerRenderTarget == null)
                return;
            if (!_poseLibrary.TryGetValue(key, out PoseLibraryEntry? entry))
            {
                if (_poseLibrary.Count >= MostLibraryPoses)
                    DropColdestPose();
                _poseLibrary[key] = entry = new PoseLibraryEntry();
            }
            entry.Mask = KeepCopy(graphicsDevice, _playerRenderTarget, entry.Mask);
            entry.Colour = withColour && _playerColorRenderTarget != null
                ? KeepCopy(graphicsDevice, _playerColorRenderTarget, entry.Colour)
                : null;
            entry.LastUsedTick = SharedTicks.Now;
        }

        private void DropColdestPose()
        {
            PoseLibraryKey coldest = default;
            long oldest = long.MaxValue;
            foreach (var pair in _poseLibrary)
            {
                if (pair.Value.LastUsedTick < oldest)
                {
                    oldest = pair.Value.LastUsedTick;
                    coldest = pair.Key;
                }
            }
            if (_poseLibrary.Remove(coldest, out PoseLibraryEntry? dropped))
            {
                dropped.Mask?.Dispose();
                dropped.Colour?.Dispose();
            }
        }

        private RenderTarget2D KeepCopy(GraphicsDevice graphicsDevice, RenderTarget2D from, RenderTarget2D? into)
        {
            MakePlayerSized(graphicsDevice, ref into, "player pose library");
            CopyTarget(graphicsDevice, from, into!);
            return into!;
        }

        /// <summary>Texel for texel: opaque blend and point sampling at the same size change nothing.</summary>
        private void CopyTarget(GraphicsDevice graphicsDevice, RenderTarget2D from, RenderTarget2D into)
        {
            RenderTargetBinding[] previousTargets = graphicsDevice.GetRenderTargets();
            try
            {
                graphicsDevice.SetRenderTarget(into);
                graphicsDevice.Clear(Color.Transparent);
                _renderTargetSpriteBatch!.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                _renderTargetSpriteBatch.Draw(from, Vector2.Zero, Color.White);
                _renderTargetSpriteBatch.End();
            }
            finally
            {
                graphicsDevice.SetRenderTargets(previousTargets);
            }
        }
    }
}
