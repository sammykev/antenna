using Antenna.Core.Library;

namespace Antenna.Core.Epg;

/// <summary>Downloads a source's XMLTV guide, links it to the source's channels and stores it.</summary>
public sealed class EpgImporter(HttpClient http, LibraryStore store)
{
    /// <summary>How far ahead the guide is kept.</summary>
    public static readonly TimeSpan Lookahead = TimeSpan.FromDays(3);

    /// <returns>The number of programmes stored.</returns>
    public async Task<int> ImportAsync(PlaylistSource source, IReadOnlyList<MediaItem> items, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.EpgUrl))
            return 0;

        var live = items.Where(i => i.Kind == MediaKind.Live).ToList();
        if (live.Count == 0)
            return 0;

        // Keep as much history as the longest catch-up archive (at least a few hours, for "what was just on").
        var now = DateTimeOffset.UtcNow;
        var archiveDays = Math.Clamp(live.Max(i => i.CatchupDays), 0, 14);
        var from = now - (archiveDays > 0 ? TimeSpan.FromDays(archiveDays) : TimeSpan.FromHours(6));
        var to = now + Lookahead;

        await using var stream = await XmltvParser.OpenAsync(http, source.EpgUrl, ct);
        using var writer = store.BeginGuideImport(source.Id);

        await XmltvParser.ParseAsync(
            stream,
            channels =>
            {
                var map = channels.Count > 0
                    ? EpgMatcher.Match(live, channels)
                    // Guides without <channel> entries: trust the playlist's tvg-ids as-is.
                    : live.Where(i => i.EpgChannelId is not null).ToDictionary(i => i.ExternalId, i => i.EpgChannelId!);

                foreach (var (externalId, channelId) in map)
                    writer.SetEpgChannel(externalId, channelId);

                var wanted = new HashSet<string>(map.Values, StringComparer.Ordinal);
                return wanted.Contains;
            },
            writer.Add,
            from,
            to,
            ct);

        writer.Commit();
        return writer.Count;
    }
}
