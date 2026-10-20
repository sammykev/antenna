using Antenna.Core;

namespace Antenna.Mobile.Views;

/// <summary>One line in a channel list: the channel and what's on it now.</summary>
public sealed class ChannelRow(LibraryEntry entry, Programme? now)
{
    public MediaItem Item => entry.Item;

    public string Title => entry.Item.Title;

    public string? Logo => string.IsNullOrWhiteSpace(entry.Item.LogoUrl) ? null : entry.Item.LogoUrl;

    /// <summary>Two letters shown when there's no logo.</summary>
    public string Initials => new([.. entry.Item.Title.Where(char.IsLetterOrDigit).Take(2)]);

    public string Subtitle => now is null ? entry.Item.Group : $"{now.Start.LocalDateTime:HH:mm}  {now.Title}";

    public double Progress => now is null ? 0 : Math.Clamp((DateTimeOffset.Now - now.Start) / now.Duration, 0, 1);

    public bool HasProgramme => now is not null;

    public static ChannelRow For(LibraryEntry entry, Dictionary<(string, string), Programme> nowPlaying) =>
        new(entry, entry.Item.EpgChannelId is { } id && nowPlaying.TryGetValue((entry.Item.SourceId, id), out var p) ? p : null);
}

/// <summary>A category chip above the channel list.</summary>
public sealed record GroupChip(string Name, string? Group, bool Favourites = false, bool Continue = false);

/// <summary>A programme in a guide list, in local time.</summary>
public sealed class GuideRow(Programme programme, LibraryEntry? channel = null)
{
    public Programme Programme => programme;

    public string Time => programme.Start.LocalDateTime.Date == DateTime.Today
        ? $"{programme.Start.LocalDateTime:HH:mm}–{programme.Stop.LocalDateTime:HH:mm}"
        : $"{programme.Start.LocalDateTime:ddd d MMM HH:mm}";

    public string Title => programme.Title;

    public string? Description => programme.Description;

    public LibraryEntry? Entry => channel;

    public string Subtitle => channel is null ? "" : IsOnNow ? $"On now · {channel.Item.Title}" : channel.Item.Title;

    public bool IsOnNow => programme.IsOnAt(DateTimeOffset.Now);
}

/// <summary>A poster in the movies or series grid.</summary>
public sealed class PosterRow(LibraryEntry entry, double progress = 0)
{
    public LibraryEntry Entry => entry;

    public string Title => entry.Item.Title;

    public string? Poster => string.IsNullOrWhiteSpace(entry.Item.LogoUrl) ? null : entry.Item.LogoUrl;

    public double Progress => progress;

    public bool HasProgress => progress > 0;
}

/// <summary>An episode on a series page, with how much of it has been watched.</summary>
public sealed class EpisodeRow(Episode episode, WatchRecord? watch)
{
    public Episode Episode => episode;

    public string Title => $"{episode.Number}. {episode.Title}";

    public string? Plot => episode.Plot;

    public bool HasPlot => !string.IsNullOrWhiteSpace(episode.Plot);

    public double Progress => watch?.IsResumable == true ? watch.Progress : 0;

    public bool HasProgress => Progress > 0;

    /// <summary>Finished: the player saves a finished episode with no position, or it got past 95%.</summary>
    public string Watched => watch is { IsResumable: false } w && (w.LengthMs == 0 || w.PositionMs >= w.LengthMs * 0.95) ? "✓" : "";
}
