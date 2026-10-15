namespace Antenna.Core;

public enum SourceKind
{
    M3u,
    Xtream,
}

public enum MediaKind
{
    Live,
    Movie,
    Series,
}

/// <summary>A playlist the user has added: an M3U URL/file or an Xtream Codes account.</summary>
public sealed record PlaylistSource
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required SourceKind Kind { get; init; }

    /// <summary>M3U: http(s) URL or local file path. Xtream: server base URL (e.g. http://host:8080).</summary>
    public required string Location { get; init; }

    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>Optional XMLTV guide URL. M3U playlists often announce one via url-tvg.</summary>
    public string? EpgUrl { get; init; }

    /// <summary>Hex color (#RRGGBB) used to tag items from this source in the UI.</summary>
    public string Color { get; init; } = "#4F7CFF";

    /// <summary>Segoe Fluent Icons glyph shown next to items from this source.</summary>
    public string Icon { get; init; } = "";

    public DateTimeOffset? LastRefreshed { get; init; }
}

/// <summary>A playable entry: live channel, movie, or series (series resolve to episodes on demand).</summary>
public sealed record MediaItem
{
    public required string SourceId { get; init; }

    /// <summary>Stable within a source: the Xtream stream/series id, or the M3U stream URL.</summary>
    public required string ExternalId { get; init; }

    public required MediaKind Kind { get; init; }
    public required string Title { get; init; }
    public string Group { get; init; } = "";
    public string? LogoUrl { get; init; }

    /// <summary>Direct stream URL. Null for series, which need episode lookup.</summary>
    public string? StreamUrl { get; init; }

    public string? EpgChannelId { get; init; }

    /// <summary>Days of catch-up archive the provider keeps. 0 = no catch-up.</summary>
    public int CatchupDays { get; init; }

    /// <summary>M3U catch-up style (default, append, shift, flussonic, xc). Null = infer.</summary>
    public string? CatchupMode { get; init; }

    /// <summary>M3U catch-up URL template, e.g. "?utc={utc}&amp;lutc={lutc}".</summary>
    public string? CatchupSource { get; init; }

    /// <summary>
    /// LibVLC media options (one per line, e.g. ":http-user-agent=Foo") the stream needs to play.
    /// </summary>
    public string? PlayerOptions { get; init; }

    /// <summary>Position in the source playlist, used to keep the provider's ordering.</summary>
    public int SortIndex { get; init; }

    public string[] Options => PlayerOptions?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
}

public sealed record Episode
{
    public required string Id { get; init; }
    public required int Season { get; init; }
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string StreamUrl { get; init; }
    public string? Plot { get; init; }
    public string? ImageUrl { get; init; }
}

/// <summary>One programme from the XMLTV guide.</summary>
public sealed record Programme
{
    /// <summary>XMLTV channel id, matched against <see cref="MediaItem.EpgChannelId"/>.</summary>
    public required string ChannelId { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset Stop { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }

    public TimeSpan Duration => Stop - Start;

    public bool IsOnAt(DateTimeOffset time) => Start <= time && time < Stop;
}

/// <summary>Something the user watched: drives "Continue watching", "Recently watched" and resume.</summary>
public sealed record WatchRecord
{
    public required string SourceId { get; init; }

    /// <summary>Item external id; episodes use "ep:{episode id}".</summary>
    public required string ExternalId { get; init; }

    public required MediaKind Kind { get; init; }
    public required string Title { get; init; }
    public required string StreamUrl { get; init; }
    public string? LogoUrl { get; init; }
    public string? PlayerOptions { get; init; }
    public long PositionMs { get; init; }
    public long LengthMs { get; init; }
    public DateTimeOffset WatchedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Worth offering "continue": started properly and not (nearly) finished.</summary>
    public bool IsResumable => Kind != MediaKind.Live && PositionMs > 60_000 && LengthMs > 0 && PositionMs < LengthMs * 0.95;

    public double Progress => LengthMs > 0 ? Math.Clamp((double)PositionMs / LengthMs, 0, 1) : 0;
}

public enum GroupFlag
{
    Hidden,
    Locked,

    /// <summary>Shown first in the category list.</summary>
    Pinned,
}

public enum RecordingStatus
{
    Scheduled,
    Recording,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A recording or download: live TV now or on a schedule, catch-up, or a movie/episode file.</summary>
public sealed record Recording
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? PlayerOptions { get; init; }

    /// <summary>Live and catch-up streams record for a time window; downloads copy a whole file.</summary>
    public required bool IsDownload { get; init; }

    public required DateTimeOffset Start { get; init; }
    public DateTimeOffset? Stop { get; init; }
    public required string Path { get; init; }
    public RecordingStatus Status { get; init; } = RecordingStatus.Scheduled;
    public string? Error { get; init; }
    public long BytesWritten { get; init; }
    public long? TotalBytes { get; init; }
}

/// <summary>Extra information for a movie or series, from the provider (Xtream) where available.</summary>
public sealed record MediaDetails
{
    public string? Plot { get; init; }
    public string? Cast { get; init; }
    public string? Director { get; init; }
    public string? Genre { get; init; }
    public string? Released { get; init; }
    public string? Rating { get; init; }
    public string? Duration { get; init; }
    public string? PosterUrl { get; init; }
    public string? BackdropUrl { get; init; }
}

/// <summary>A search hit or browse result, joined with the source it came from.</summary>
public sealed record LibraryEntry(MediaItem Item, string SourceName, string SourceColor, string SourceIcon);
