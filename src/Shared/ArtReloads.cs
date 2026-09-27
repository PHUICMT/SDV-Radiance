using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// Which sheets had their pixels replaced IN PLACE since the last frame, so every cache that
    /// remembers something about a sheet's pixels can let that answer go.
    /// </summary>
    /// <remarks>
    /// <para>When a mod edits a texture that is already loaded (a Content Patcher patch starting
    /// or stopping to apply, a seasonal swap), SMAPI does not hand the game a new texture. It
    /// loads the new picture and copies it into the instance the game already holds, through
    /// <c>Texture2D.CopyFromTexture</c>, which can change the sheet's size as well. Every cache in
    /// this mod is keyed by that instance, so to them nothing had happened: Smooth art kept
    /// drawing its smoothed copy of the OLD picture over the new one. Reported as one interior
    /// mod's art showing over another's until the mod was switched off and on again, which is the
    /// only thing that emptied those caches.</para>
    /// <para>The copy is seen two ways. The method itself is patched, which names the exact
    /// instance. And the asset names SMAPI invalidates are kept as well, matched against a
    /// sheet's name, in case a build ever copies some other way or the call is inlined away.
    /// Either one is enough for a sheet to be forgotten; forgetting one too many costs one
    /// re-bake of it.</para>
    /// <para>Collected from wherever the copy happens and handed out once, at the start of the
    /// next frame's drawing, which is on the thread every cache belongs to.</para>
    /// </remarks>
    internal static class ArtReloads
    {
        private static readonly object Gate = new();
        private static readonly HashSet<Texture2D> _copiedInto = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<string> _invalidatedNames = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<Texture2D> _reloadedThisFrame = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<string> _reloadedNamesThisFrame = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many sheets have been forgotten for a reload since the game started, for
        /// the report.</summary>
        internal static int SheetsForgotten;
        internal static int FramesWithReloads;
        internal static bool Watching;

        internal static void Install(Harmony harmony, IMonitor monitor)
        {
            MethodInfo? copy = AccessTools.Method(typeof(Texture2D), "CopyFromTexture", [typeof(Texture2D)]);
            if (copy == null)
            {
                monitor.Log("Texture2D.CopyFromTexture not found: sheets reloaded in place are only "
                          + "recognised by their asset name.", LogLevel.Trace);
                return;
            }
            try
            {
                harmony.Patch(copy, postfix: new HarmonyMethod(typeof(ArtReloads), nameof(CopyFromTexture_Postfix)));
                Watching = true;
            }
            catch (Exception ex)
            {
                monitor.Log($"Could not watch Texture2D.CopyFromTexture: {ex.Message}", LogLevel.Trace);
            }
        }

        private static void CopyFromTexture_Postfix(Texture2D __instance)
        {
            lock (Gate)
                _copiedInto.Add(__instance);
        }

        /// <summary>Asset names SMAPI invalidated, which for a texture already in use means its
        /// pixels are about to be, or have been, copied over.</summary>
        internal static void NoteInvalidated(IEnumerable<IAssetName> names)
        {
            lock (Gate)
            {
                foreach (IAssetName name in names)
                {
                    _invalidatedNames.Add(Normalize(name.Name));
                    _invalidatedNames.Add(Normalize(name.BaseName));
                }
            }
        }

        /// <summary>Move what was collected since the last frame into this frame's answer. True
        /// when anything was reloaded, which is the only time the caches need asking.</summary>
        internal static bool TakeReloaded()
        {
            lock (Gate)
            {
                _reloadedThisFrame.Clear();
                _reloadedNamesThisFrame.Clear();
                if (_copiedInto.Count == 0 && _invalidatedNames.Count == 0)
                    return false;
                _reloadedThisFrame.UnionWith(_copiedInto);
                _reloadedNamesThisFrame.UnionWith(_invalidatedNames);
                _copiedInto.Clear();
                _invalidatedNames.Clear();
            }
            FramesWithReloads++;
            return true;
        }

        /// <summary>Whether this sheet's pixels changed since the caches last looked, by instance
        /// or by name.</summary>
        internal static bool WasReloaded(Texture2D sheet)
        {
            if (_reloadedThisFrame.Contains(sheet))
                return true;
            if (_reloadedNamesThisFrame.Count == 0)
                return false;
            string? name = sheet.Name;
            return !string.IsNullOrEmpty(name) && _reloadedNamesThisFrame.Contains(Normalize(name));
        }

        /// <summary>Remove every entry of <paramref name="cache"/> whose sheet was reloaded, and
        /// say how many went.</summary>
        internal static int Forget<TKey, TValue>(Dictionary<TKey, TValue> cache, Func<TKey, Texture2D> sheetOf)
            where TKey : notnull
        {
            if (cache.Count == 0)
                return 0;
            List<TKey>? gone = null;
            foreach (TKey key in cache.Keys)
                if (WasReloaded(sheetOf(key)))
                    (gone ??= []).Add(key);
            if (gone == null)
                return 0;
            foreach (TKey key in gone)
                cache.Remove(key);
            return gone.Count;
        }

        private static string Normalize(string assetName) => assetName.Replace('\\', '/');
    }
}
