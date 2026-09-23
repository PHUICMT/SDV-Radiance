using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewModdingAPI;

namespace SDVRadiance
{
    /// <summary>
    /// Which content pack's Content Patcher edit is painting one tile of a map tilesheet right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A label painted for a pack's art is tied to the fingerprint of the exact picture it was
    /// painted on, and one pack can hand the game many pictures of the same tile. Way Back Pelican
    /// Town paints its hot spring by the railroad from <c>1_{{season}}_outdoorsTileSheet.png</c> in
    /// one of seven recolour folders, with a sunlight overlay on top when it is not raining: four
    /// seasons, two weathers and seven palettes of one pond, and a fingerprint names one of them.
    /// Elacro's autumn picture was one it did not name, so the tile fell back to the base game's
    /// label, painted for the base game's art, and the water effect landed on the pack's rocks.
    /// </para>
    /// <para>
    /// A map does not change with the season, the weather or the palette. What is water is still
    /// water; only the paint moves. So the question worth asking is not "which picture is this" but
    /// "whose picture is this", and Content Patcher knows: it holds every patch, whether its
    /// conditions hold right now, which pack it belongs to and which rectangle it paints.
    /// </para>
    /// <para>
    /// Read through reflection, because Content Patcher publishes no API for it. Every step fails
    /// soft: the first thing that is not where it was switches this off until the game restarts, with one
    /// line in the log, and every caller then behaves exactly as it did before this existed.
    /// Checked against Content Patcher 2.9.1.
    /// </para>
    /// </remarks>
    internal static class ContentPatcherArtOwners
    {
        private readonly record struct Claim(string Pack, int Priority, int Order, Rectangle? Area);

        private static IModHelper? _helper;
        private static IMonitor? _monitor;
        private static bool _unavailable;
        private static object? _screenManagers;
        private static PropertyInfo? _screenValue;
        private static PropertyInfo? _patchManagerProperty;
        private static MethodInfo? _getPatchesForAsset;
        private static readonly Dictionary<(Type, string), FieldInfo?> _areaFieldByPatchType = [];
        private static readonly Dictionary<string, Claim[]> _claimsByAsset = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How often a tile's painter was asked for and how often a pack was named, for
        /// radiance_report.</summary>
        internal static int Lookups { get; private set; }
        internal static int PacksNamed { get; private set; }

        /// <summary>Every pack named, with how often, and the first ask nobody claimed, so a report
        /// can tell "Content Patcher named nobody" from "it named a pack no label lists".</summary>
        internal static readonly Dictionary<string, int> NamedPacks = new(StringComparer.OrdinalIgnoreCase);
        internal static string? FirstUnclaimed { get; private set; }
        internal static bool Available => !_unavailable && _helper != null;

        /// <summary>radiance_labelfollow: off makes every painted label follow only its exact
        /// pictures again, as before this existed. For A/B, and for a report that suspects it.</summary>
        internal static bool Enabled = true;

        /// <summary>radiance_labelfollow only: a tile a pack owns takes its label from the painter
        /// alone, even where a fingerprint would have matched, so the follow can be seen at work on
        /// art the dump already holds. A test switch; it is never on unless typed.</summary>
        internal static bool FollowOnly;

        internal static void Initialise(IModHelper helper, IMonitor monitor)
        {
            _helper = helper;
            _monitor = monitor;
        }

        /// <summary>
        /// The pack whose applied edit paints this tile last, so it is the art on screen. False
        /// when no applied edit covers the tile, or when Content Patcher cannot be read.
        /// </summary>
        /// <param name="assetName">The asset the sheet is loaded from, e.g. <c>Maps/fall_town</c>.</param>
        /// <param name="sheetWidthTiles">The sheet's width in tiles, to turn the index into pixels.</param>
        internal static bool TryGetPainter(string assetName, int tileIndex, int sheetWidthTiles, out string pack)
        {
            pack = "";
            if (!Enabled || _unavailable || _helper == null || sheetWidthTiles <= 0)
                return false;
            Lookups++;
            if (!_claimsByAsset.TryGetValue(assetName, out Claim[]? claims))
            {
                if (!TryReadClaims(assetName, out claims))
                    return false;
                _claimsByAsset[assetName] = claims;
            }
            var tile = new Point(tileIndex % sheetWidthTiles * 16 + 8, tileIndex / sheetWidthTiles * 16 + 8);
            Claim? last = null;
            foreach (Claim claim in claims)
            {
                if (claim.Area is Rectangle area && !area.Contains(tile))
                    continue;
                if (last == null || claim.Priority > last.Value.Priority
                    || (claim.Priority == last.Value.Priority && claim.Order > last.Value.Order))
                    last = claim;
            }
            if (last == null)
            {
                FirstUnclaimed ??= $"{assetName} tile {tileIndex} ({claims.Length} applied edit(s) on it)";
                return false;
            }
            pack = last.Value.Pack;
            PacksNamed++;
            NamedPacks[pack] = NamedPacks.TryGetValue(pack, out int seen) ? seen + 1 : 1;
            return true;
        }

        /// <summary>Drop what is known about these assets. Content Patcher invalidates an asset when
        /// a patch on it starts or stops applying, so the reload event is the moment the answer can
        /// have changed.</summary>
        internal static void Forget(IEnumerable<string> assetNames)
        {
            if (_claimsByAsset.Count == 0)
                return;
            foreach (string name in assetNames)
                _claimsByAsset.Remove(AssetNames.Normalise(name));
        }

        internal static void ForgetAll()
        {
            _claimsByAsset.Clear();
            NamedPacks.Clear();
            FirstUnclaimed = null;
        }

        private static bool TryReadClaims(string assetName, out Claim[] claims)
        {
            claims = [];
            try
            {
                object? patchManager = PatchManager();
                if (patchManager == null)
                    return false;
                object parsed = _helper!.GameContent.ParseAssetName(assetName);
                if (_getPatchesForAsset!.Invoke(patchManager, [parsed]) is not System.Collections.IEnumerable patches)
                    return false;
                var found = new List<Claim>();
                int order = 0;
                foreach (object patch in patches)
                {
                    order++;
                    Type type = patch.GetType();
                    if (type.GetProperty("IsApplied")?.GetValue(patch) is not true)
                        continue;
                    string kind = type.GetProperty("Type")?.GetValue(patch)?.ToString() ?? "";
                    if (kind is not "EditImage" and not "Load")
                        continue;
                    object? contentPack = type.GetProperty("ContentPack")?.GetValue(patch);
                    object? manifest = contentPack?.GetType().GetProperty("Manifest")?.GetValue(contentPack);
                    string? packId = manifest?.GetType().GetProperty("UniqueID")?.GetValue(manifest) as string;
                    if (string.IsNullOrEmpty(packId))
                        continue;
                    int priority = type.GetProperty("Priority")?.GetValue(patch) is int value ? value : 0;
                    Rectangle? area = null;
                    if (kind == "EditImage" && !TryReadToArea(patch, type, out area))
                        continue;
                    found.Add(new Claim(packId, priority, order, area));
                }
                claims = [.. found];
                return true;
            }
            catch (Exception ex)
            {
                SwitchOff($"reading its patches failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>The rectangle an image edit paints, null for the whole image. False when it has
        /// one that cannot be read right now, which is not a claim on anything.</summary>
        /// <remarks>Content Patcher places an edit with no ToArea at the top left corner, the size
        /// of its FromArea; only an edit with neither is taken as the whole image, which is what it
        /// is whenever the source picture is the size of the sheet.</remarks>
        private static bool TryReadToArea(object patch, Type type, out Rectangle? area)
        {
            area = null;
            if (!TryReadArea(patch, type, "ToArea", out bool hasTarget, out Rectangle target))
                return false;
            if (hasTarget)
            {
                area = target;
                return true;
            }
            if (!TryReadArea(patch, type, "FromArea", out bool hasSource, out Rectangle source))
                return false;
            if (hasSource)
                area = new Rectangle(0, 0, source.Width, source.Height);
            return true;
        }

        private static bool TryReadArea(object patch, Type type, string fieldName, out bool present, out Rectangle area)
        {
            present = false;
            area = Rectangle.Empty;
            var key = (type, fieldName);
            if (!_areaFieldByPatchType.TryGetValue(key, out FieldInfo? field))
            {
                field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _areaFieldByPatchType[key] = field;
            }
            object? tokenRectangle = field?.GetValue(patch);
            if (tokenRectangle == null)
                return true;
            MethodInfo? read = tokenRectangle.GetType().GetMethod("TryGetRectangle");
            if (read == null)
                return false;
            object?[] arguments = [null, null];
            if (read.Invoke(tokenRectangle, arguments) is not true || arguments[0] is not Rectangle rectangle)
                return false;
            present = true;
            area = rectangle;
            return true;
        }

        private static object? PatchManager()
        {
            if (_screenManagers == null)
            {
                IModInfo? info = _helper!.ModRegistry.Get("Pathoschild.ContentPatcher");
                if (info == null)
                {
                    _unavailable = true;       // not installed: nothing to ask, nothing to log
                    return null;
                }
                object? modEntry = info.GetType().GetProperty("Mod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(info);
                FieldInfo? screenField = modEntry?.GetType().GetField("ScreenManager", BindingFlags.Instance | BindingFlags.NonPublic);
                _screenManagers = screenField?.GetValue(modEntry);
                _screenValue = _screenManagers?.GetType().GetProperty("Value");
                if (_screenManagers == null || _screenValue == null)
                {
                    SwitchOff("its screen manager is not where it was");
                    return null;
                }
            }
            object? screen = _screenValue!.GetValue(_screenManagers);
            if (screen == null)
                return null;
            _patchManagerProperty ??= screen.GetType().GetProperty("PatchManager");
            object? patchManager = _patchManagerProperty?.GetValue(screen);
            if (patchManager == null)
            {
                SwitchOff("its patch manager is not where it was");
                return null;
            }
            if (_getPatchesForAsset == null)
            {
                foreach (MethodInfo method in patchManager.GetType().GetMethods())
                {
                    if (method.Name != "GetPatches")
                        continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType.Name == "IAssetName")
                        _getPatchesForAsset = method;
                }
                if (_getPatchesForAsset == null)
                {
                    SwitchOff("GetPatches(IAssetName) is gone");
                    return null;
                }
            }
            return patchManager;
        }

        private static void SwitchOff(string why)
        {
            _unavailable = true;
            _claimsByAsset.Clear();
            _monitor?.Log($"Could not ask Content Patcher which pack paints a tile ({why}). Labels painted "
                        + "for a pack's art will only follow the exact pictures they were painted on.", LogLevel.Trace);
        }
    }
}
