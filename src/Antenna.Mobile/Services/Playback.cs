using Antenna.Core;

namespace Antenna.Mobile.Services;

/// <summary>Something the player can play: a channel, a movie or an episode.</summary>
public sealed record PlayItem(string Title, string Url, MediaKind Kind, string SourceId, string HistoryId)
{
    public string? Subtitle { get; init; }
    public string? LogoUrl { get; init; }
    public string? PlayerOptions { get; init; }

    /// <summary>The library item (channels and movies), for favourites and the guide. Null for episodes.</summary>
    public MediaItem? Item { get; init; }

    public bool IsLive => Kind == MediaKind.Live;

    public string[] Options => PlayerOptions?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];

    public static PlayItem For(MediaItem item) =>
        new(item.Title, item.StreamUrl ?? "", item.Kind, item.SourceId, item.ExternalId)
        {
            Subtitle = item.Group,
            LogoUrl = item.LogoUrl,
            PlayerOptions = item.PlayerOptions,
            Item = item,
        };

    /// <summary>Episodes are remembered as "ep:{id}", as in the Windows app.</summary>
    public static PlayItem For(MediaItem series, Episode episode) =>
        new($"S{episode.Season:00}E{episode.Number:00} {episode.Title}", episode.StreamUrl, MediaKind.Series, series.SourceId, "ep:" + episode.Id)
        {
            Subtitle = series.Title,
            LogoUrl = episode.ImageUrl ?? series.LogoUrl,
            PlayerOptions = series.PlayerOptions,
        };
}

/// <summary>What the player should play: the list it was picked from, so next/previous step through it.</summary>
public static class Playback
{
    public static IReadOnlyList<PlayItem> Queue { get; private set; } = [];

    public static int Index { get; set; }

    public static PlayItem? Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;

    public static Task PlayAsync(IReadOnlyList<PlayItem> queue, int index)
    {
        Queue = queue;
        Index = index;
        return Shell.Current.GoToAsync("player");
    }

    public static Task PlayAsync(IReadOnlyList<MediaItem> items, int index) =>
        PlayAsync([.. items.Select(PlayItem.For)], index);
}
