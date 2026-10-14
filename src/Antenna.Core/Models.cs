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

    /// <summary>Position in the source playlist, used to keep the provider's ordering.</summary>
    public int SortIndex { get; init; }
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

/// <summary>A search hit or browse result, joined with the source it came from.</summary>
public sealed record LibraryEntry(MediaItem Item, string SourceName, string SourceColor, string SourceIcon);
