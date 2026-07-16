using System;

namespace SDVRadiance.Integrations
{
    /// <summary>
    /// Minimal subset of the Generic Mod Config Menu API we consume.
    /// Full interface: https://github.com/spacechase0/StardewValleyMods/blob/develop/GenericModConfigMenu/IGenericModConfigMenuApi.cs
    /// </summary>
    public interface IGenericModConfigMenuApi
    {
        void Register(StardewModdingAPI.IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

        void AddSectionTitle(StardewModdingAPI.IManifest mod, Func<string> text, Func<string>? tooltip = null);

        void AddParagraph(StardewModdingAPI.IManifest mod, Func<string> text);

        void AddBoolOption(StardewModdingAPI.IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);

        void AddTextOption(StardewModdingAPI.IManifest mod, Func<string> getValue, Action<string> setValue, Func<string> name, Func<string>? tooltip = null, string[]? allowedValues = null, Func<string, string>? formatAllowedValue = null, string? fieldId = null);

        void AddNumberOption(StardewModdingAPI.IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);

        void AddNumberOption(StardewModdingAPI.IManifest mod, Func<float> getValue, Action<float> setValue, Func<string> name, Func<string>? tooltip = null, float? min = null, float? max = null, float? interval = null, Func<float, string>? formatValue = null, string? fieldId = null);
    }
}
