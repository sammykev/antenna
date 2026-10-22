using Antenna.Core;

namespace Antenna.App.Services;

/// <summary>Opens a browse page on a particular category (used by pinned categories).</summary>
public sealed record BrowseTarget(MediaKind Kind, string Group);

/// <summary>Everything the player needs: what to play, how, and where it came from (for history and zapping).</summary>
public sealed record PlayRequest(string Title, string Url, bool IsLive)
{
    /// <summary>LibVLC media options the stream needs, e.g. ":http-user-agent=…".</summary>
    public string[] Options { get; init; } = [];

    /// <summary>Source and history key; null for things that shouldn't be remembered (catch-up, local files).</summary>
    public string? SourceId { get; init; }
    public string? HistoryId { get; init; }
    public MediaKind Kind { get; init; } = MediaKind.Movie;
    public string? LogoUrl { get; init; }
    public string? EpgChannelId { get; init; }

    /// <summary>Where to start, for "continue watching".</summary>
    public long StartAtMs { get; init; }

    /// <summary>The channel list this was opened from, for Ch+/Ch- and number entry.</summary>
    public IReadOnlyList<MediaItem> Channels { get; init; } = [];
    public int ChannelIndex { get; init; } = -1;

    internal MediaItem? Item { get; init; }

    /// <summary>
    /// For a match: its other channels, in order. If this one isn't broadcasting (fails or shows nothing), the player
    /// moves on to the next by itself.
    /// </summary>
    public IReadOnlyList<MediaItem> Fallbacks { get; init; } = [];

    /// <summary>The match being watched ("Machac vs Khachanov"), for messages about its channels.</summary>
    public string? MatchTitle { get; init; }

    public static PlayRequest ForItem(MediaItem item, IReadOnlyList<MediaItem>? channels = null)
    {
        var list = item.Kind == MediaKind.Live ? channels ?? [item] : [];
        return new PlayRequest(item.Title, item.StreamUrl ?? "", item.Kind == MediaKind.Live)
        {
            Options = item.Options,
            SourceId = item.SourceId,
            HistoryId = item.ExternalId,
            Kind = item.Kind,
            LogoUrl = item.LogoUrl,
            EpgChannelId = item.EpgChannelId,
            Channels = list,
            ChannelIndex = IndexOf(list, item),
            Item = item,
        };
    }

    public static PlayRequest ForEpisode(MediaItem series, Episode episode) =>
        new($"{series.Title} · S{episode.Season:00}E{episode.Number:00} {episode.Title}", episode.StreamUrl, false)
        {
            SourceId = series.SourceId,
            HistoryId = "ep:" + episode.Id,
            Kind = MediaKind.Series,
            LogoUrl = episode.ImageUrl ?? series.LogoUrl,
            Options = series.Options,
        };

    public static PlayRequest ForWatch(WatchRecord w) =>
        new(w.Title, w.StreamUrl, w.Kind == MediaKind.Live)
        {
            SourceId = w.SourceId,
            HistoryId = w.ExternalId,
            Kind = w.Kind,
            LogoUrl = w.LogoUrl,
            Options = w.PlayerOptions?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [],
            StartAtMs = w.IsResumable ? w.PositionMs : 0,
        };

    public static PlayRequest ForCatchup(MediaItem channel, Programme programme, string url) =>
        new($"{channel.Title} · {programme.Title} (catch-up)", url, false) { Options = channel.Options, LogoUrl = channel.LogoUrl };

    public static PlayRequest ForFile(string title, string path) =>
        new(title, new Uri(path).AbsoluteUri, false);

    public bool CanZap => IsLive && Channels.Count > 1 && ChannelIndex >= 0;

    public PlayRequest WithChannel(int index) =>
        ForItem(Channels[(index % Channels.Count + Channels.Count) % Channels.Count], Channels);

    public WatchRecord? ToWatch(long positionMs, long lengthMs) =>
        SourceId is null || HistoryId is null ? null : new WatchRecord
        {
            SourceId = SourceId,
            ExternalId = HistoryId,
            Kind = Kind,
            Title = Title,
            StreamUrl = Url,
            LogoUrl = LogoUrl,
            PlayerOptions = Options.Length > 0 ? string.Join('\n', Options) : null,
            PositionMs = IsLive ? 0 : positionMs,
            LengthMs = IsLive ? 0 : lengthMs,
        };

    private static int IndexOf(IReadOnlyList<MediaItem> list, MediaItem item)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].SourceId == item.SourceId && list[i].ExternalId == item.ExternalId)
                return i;
        }
        return -1;
    }
}
