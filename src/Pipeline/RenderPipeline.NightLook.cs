using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley;

namespace SDVRadiance
{
    /// <summary>
    /// RenderPipeline, a look of its own for the night: the player's settings by day, the chosen
    /// preset or saved look after dark, eased between them with the evening.
    /// </summary>
    internal sealed partial class RenderPipeline
    {
        private ModConfig? _blendedLook;
        private ModConfig? _nightLook;
        private string? _nightLookBuiltFor;
        private int _nightLookBuiltTick = -1000;

        /// <summary>How often the night look is laid over a fresh copy of the settings, in ticks: often
        /// enough that a slider moved in F6 reaches the night side within a moment, and rare enough
        /// that the copy costs nothing a frame.</summary>
        private const int NightLookRebuildTicks = 30;

        /// <summary>
        /// The settings this frame is drawn with: the player's own, or, with a separate night look,
        /// every number in them eased toward the night look's by <see cref="NightFactorNow"/>.
        /// </summary>
        /// <remarks>
        /// <para>The night factor is the same ease the night mist, the vignette and the sky follow,
        /// so the look changes as the light does and never in a step. Numbers are eased; a switch,
        /// a choice or a name changes when the evening is half way, and every stage it switches
        /// fades itself in and out as it always has, so nothing pops.</para>
        /// <para>The one name that is drawn from is the colour look's file: two different files
        /// cannot be mixed in one pass, so the day's fades out over the first half of the evening
        /// and the night's fades in over the second, through no look at all at the middle.</para>
        /// <para>Off, or while a cost measurement owns the settings, this is the player's own object,
        /// untouched, and costs nothing.</para>
        /// </remarks>
        internal ModConfig LookForThisFrame(ModConfig config)
        {
            if (BenchRunning || EffectCostRunning)
                return config;
            GameLocation? location = Game1.currentLocation;
            string chosen;
            float share;
            if (location != null && IsUnderground(location))
            {
                // Underground there is no day or night to follow: the cave's look is the look.
                chosen = config.CaveLook ?? "";
                share = 1f;
            }
            else if (!config.NightLookSeparate)
                return config;
            else
            {
                // Indoors keeps the day's look unless one is chosen for it: the night look's glow
                // over a shop lit by its own lamps was far too much (the author, 27 Oct 2026). A
                // separate look outdoors with none named is the night preset.
                chosen = location is { IsOutdoors: false } ? config.NightLookIndoors ?? ""
                       : string.IsNullOrEmpty(config.NightLook) ? nameof(LookPreset.Nocturne) : config.NightLook;
                share = MathHelper.Clamp(NightFactorNow(), 0f, 1f);
            }
            if (chosen.Length == 0 || share <= 0f)
                return config;
            if (_nightLook == null || _nightLookBuiltFor != chosen
                || Math.Abs(Game1Ticks() - _nightLookBuiltTick) >= NightLookRebuildTicks)
            {
                _nightLook ??= new ModConfig();
                CopySettings(config, _nightLook);
                if (!ApplyNamedLook(config, chosen, _nightLook))
                    return config;
                _nightLookBuiltFor = chosen;
                _nightLookBuiltTick = Game1Ticks();
            }
            _blendedLook ??= new ModConfig();
            CopySettings(share >= 0.5f ? _nightLook : config, _blendedLook);
            foreach ((Func<ModConfig, float> read, Action<ModConfig, float> write) in FloatSettings)
            {
                float own = read(config), other = read(_nightLook);
                if (own != other)
                    write(_blendedLook, own + (other - own) * share);
            }
            // The look file: through none at the middle of the evening when the two differ.
            if (!string.Equals(config.ColorGradeLut ?? "", _nightLook.ColorGradeLut ?? "", StringComparison.OrdinalIgnoreCase))
                _blendedLook.ColorGradeLutAmount = share < 0.5f
                    ? config.ColorGradeLutAmount * (1f - 2f * share)
                    : _nightLook.ColorGradeLutAmount * (2f * share - 1f);
            return _blendedLook;
        }

        /// <summary>The mines, the volcano and every place named a cave (the farm cave, a mod's caves):
        /// the places a player thinks of as underground, where a look of their own is offered.</summary>
        private static bool IsUnderground(GameLocation location)
            => location is StardewValley.Locations.MineShaft or StardewValley.Locations.VolcanoDungeon
               || (location.Name?.Contains("Cave", StringComparison.OrdinalIgnoreCase) ?? false);

        /// <summary>A look laid over <paramref name="target"/>: a preset by its name, or a saved look as
        /// "look:" and its name. False when it names nothing that exists.</summary>
        private static bool ApplyNamedLook(ModConfig config, string chosen, ModConfig target)
        {
            if (chosen.StartsWith(ModConfig.NightLookSavedPrefix, StringComparison.Ordinal))
            {
                string name = chosen[ModConfig.NightLookSavedPrefix.Length..];
                NamedProfile? saved = config.SavedProfiles.FirstOrDefault(profile => profile.Name == name);
                if (saved == null)
                    return false;
                target.ApplyProfile(saved);
                return true;
            }
            if (!Enum.TryParse(chosen, ignoreCase: true, out LookPreset preset) || preset == LookPreset.Custom)
                return false;
            target.ApplyPreset(preset);
            return true;
        }

        private static int Game1Ticks() => StardewValley.Game1.ticks;

        /// <summary>Every public setting, copied from one settings object to another. Built once
        /// from the type, so a copy each frame is a list of assignments and no reflection.</summary>
        private static readonly Action<ModConfig, ModConfig> CopySettings = BuildCopy();

        /// <summary>Every number among the settings, as a reader and a writer.</summary>
        private static readonly List<(Func<ModConfig, float> Read, Action<ModConfig, float> Write)> FloatSettings = BuildFloats();

        private static IEnumerable<PropertyInfo> SettingProperties()
            => typeof(ModConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true
                                   && property.GetIndexParameters().Length == 0);

        private static Action<ModConfig, ModConfig> BuildCopy()
        {
            ParameterExpression from = Expression.Parameter(typeof(ModConfig), "from");
            ParameterExpression to = Expression.Parameter(typeof(ModConfig), "to");
            Expression[] assignments =
            [
                .. SettingProperties().Select(property =>
                    (Expression)Expression.Assign(Expression.Property(to, property), Expression.Property(from, property))),
            ];
            return Expression.Lambda<Action<ModConfig, ModConfig>>(Expression.Block(assignments), from, to).Compile();
        }

        private static List<(Func<ModConfig, float>, Action<ModConfig, float>)> BuildFloats()
        {
            var floats = new List<(Func<ModConfig, float>, Action<ModConfig, float>)>();
            foreach (PropertyInfo property in SettingProperties().Where(property => property.PropertyType == typeof(float)))
            {
                ParameterExpression settings = Expression.Parameter(typeof(ModConfig), "settings");
                ParameterExpression value = Expression.Parameter(typeof(float), "value");
                var read = Expression.Lambda<Func<ModConfig, float>>(Expression.Property(settings, property), settings).Compile();
                var write = Expression.Lambda<Action<ModConfig, float>>(
                    Expression.Assign(Expression.Property(settings, property), value), settings, value).Compile();
                floats.Add((read, write));
            }
            return floats;
        }
    }
}
