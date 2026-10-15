using System.Text.Json;
using System.Text.Json.Serialization;
using Antenna.Core.Library;

namespace Antenna.Core.Backup;

/// <summary>
/// Exports playlists, favourites, category settings and app settings to JSON, and restores them.
/// Channels and guides aren't included: they're re-downloaded by refreshing the playlists.
/// </summary>
public static class BackupService
{
    public const int FormatVersion = 1;

    /// <summary>Settings that only make sense on this PC (or are secrets) aren't exported.</summary>
    private static readonly HashSet<string> LocalOnlySettings = ["parental.pin"];

    public sealed record BackupFile
    {
        public int Version { get; init; } = FormatVersion;
        public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
        public List<BackupSource> Sources { get; init; } = [];
        public List<BackupFavourite> Favourites { get; init; } = [];
        public List<BackupGroupFlag> GroupFlags { get; init; } = [];
        public Dictionary<string, string> Settings { get; init; } = [];
    }

    public sealed record BackupSource(string Id, string Name, SourceKind Kind, string Location, string? Username, string? Password, string? EpgUrl, string Color, string Icon);

    public sealed record BackupFavourite(string SourceId, string ExternalId, MediaKind Kind);

    public sealed record BackupGroupFlag(MediaKind Kind, string Group, GroupFlag Flag);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="includePasswords">Passwords are written in plain text, so this is opt-in.</param>
    public static string Export(LibraryStore store, bool includePasswords)
    {
        var file = new BackupFile
        {
            Sources = store.GetSources()
                .Select(s => new BackupSource(s.Id, s.Name, s.Kind, s.Location, s.Username, includePasswords ? s.Password : null, s.EpgUrl, s.Color, s.Icon))
                .ToList(),
            Favourites = store.GetFavourites()
                .Select(f => new BackupFavourite(f.Item.SourceId, f.Item.ExternalId, f.Item.Kind))
                .ToList(),
            GroupFlags = store.GetFlaggedGroups(GroupFlag.Hidden).Select(g => new BackupGroupFlag(g.Kind, g.Group, GroupFlag.Hidden))
                .Concat(store.GetFlaggedGroups(GroupFlag.Locked).Select(g => new BackupGroupFlag(g.Kind, g.Group, GroupFlag.Locked)))
                .ToList(),
            Settings = store.GetAllSettings().Where(kv => !LocalOnlySettings.Contains(kv.Key)).ToDictionary(),
        };
        return JsonSerializer.Serialize(file, Json);
    }

    /// <summary>
    /// Merges a backup into the store. Existing playlists with the same id are updated (keeping
    /// their password if the backup has none). Returns the restored playlists so they can be refreshed.
    /// Favourites are restored as keys; they reappear once their playlist has been refreshed.
    /// </summary>
    public static IReadOnlyList<PlaylistSource> Import(LibraryStore store, string json)
    {
        var file = JsonSerializer.Deserialize<BackupFile>(json, Json) ?? throw new InvalidDataException("The file is empty.");
        if (file.Version > FormatVersion)
            throw new InvalidDataException("This backup was made by a newer version of Antenna.");

        var existing = store.GetSources().ToDictionary(s => s.Id);
        var restored = new List<PlaylistSource>();
        foreach (var b in file.Sources)
        {
            var source = new PlaylistSource
            {
                Id = b.Id,
                Name = b.Name,
                Kind = b.Kind,
                Location = b.Location,
                Username = b.Username,
                Password = b.Password ?? existing.GetValueOrDefault(b.Id)?.Password,
                EpgUrl = b.EpgUrl,
                Color = b.Color,
                Icon = b.Icon,
                LastRefreshed = existing.GetValueOrDefault(b.Id)?.LastRefreshed,
            };
            store.SaveSource(source);
            restored.Add(source);
        }

        foreach (var f in file.Favourites)
            store.SetFavourite(new MediaItem { SourceId = f.SourceId, ExternalId = f.ExternalId, Kind = f.Kind, Title = "" }, true);
        foreach (var g in file.GroupFlags)
            store.SetGroupFlag(g.Kind, g.Group, g.Flag, true);
        foreach (var (key, value) in file.Settings.Where(kv => !LocalOnlySettings.Contains(kv.Key)))
            store.SetSetting(key, value);

        return restored;
    }
}
