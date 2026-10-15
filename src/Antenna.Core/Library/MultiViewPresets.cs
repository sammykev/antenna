using System.Text.Json;
using System.Text.Json.Serialization;

namespace Antenna.Core.Library;

/// <summary>A channel in a saved multi-view tile; null tiles are empty.</summary>
public sealed record PresetTile(string SourceId, string ChannelId, string Title);

/// <summary>A saved multi-view: up to four channels, which one has sound, and the layout.</summary>
public sealed record MultiViewPreset(string Name, string Layout, int MainTile, IReadOnlyList<PresetTile?> Tiles);

/// <summary>Stores presets as JSON in one setting, keeping the list sorted by name.</summary>
public static class MultiViewPresets
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static IReadOnlyList<MultiViewPreset> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<MultiViewPreset>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return []; // A damaged setting shouldn't break multi-view.
        }
    }

    public static string Serialize(IEnumerable<MultiViewPreset> presets) =>
        JsonSerializer.Serialize(presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), Json);

    /// <summary>Adds the preset, replacing one with the same name (ignoring case).</summary>
    public static IReadOnlyList<MultiViewPreset> Save(IReadOnlyList<MultiViewPreset> presets, MultiViewPreset preset) =>
        [.. presets.Where(p => !string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase)), preset];

    public static IReadOnlyList<MultiViewPreset> Delete(IReadOnlyList<MultiViewPreset> presets, string name) =>
        [.. presets.Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))];
}
