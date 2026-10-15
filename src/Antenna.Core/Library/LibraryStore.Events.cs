using Antenna.Core.Events;

namespace Antenna.Core.Library;

/// <summary>Live-event search across the guide and event channels.</summary>
public sealed partial class LibraryStore
{
    /// <summary>
    /// Guide programmes matching an event search within [from, to): what's on now first,
    /// then by start time. Programmes shown on several channels come back once, with all of them.
    /// </summary>
    public IReadOnlyList<EventHit> SearchEvents(string query, DateTimeOffset from, DateTimeOffset to, int limit = 200)
    {
        var match = EventSearch.BuildExpression(query);
        if (match is null)
            return [];

        var rows = ReadAside((db, credentials) =>
        {
            // Time window first (indexed), then the text match by rowid: far cheaper than
            // matching the whole guide when only today's programmes matter.
            using var cmd = Command(db, $"""
                SELECT p.channel_id, p.start, p.stop, p.title, p.description,
                       i.source_id, i.external_id, i.kind, i.title, i.grp, i.logo, i.stream_url, i.epg_id, i.catchup_days, i.sort_index,
                       s.name, s.color, s.icon, i.catchup_mode, i.catchup_source, i.player_options
                FROM programmes p
                JOIN items i ON i.source_id = p.source_id AND i.epg_id = p.channel_id AND i.kind = {(int)MediaKind.Live}
                JOIN sources s ON s.id = i.source_id
                WHERE p.stop > $from AND p.start < $to
                  AND p.rowid IN (SELECT rowid FROM programmes_fts WHERE programmes_fts MATCH $match)
                  AND {VisibleFilter}
                ORDER BY p.start, s.position, i.sort_index
                LIMIT $limit
                """);
            cmd.Parameters.AddWithValue("$match", match);
            cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$limit", limit * 4);
            AddVisibility(cmd);

            using var r = cmd.ExecuteReader();
            var found = new List<(Programme Programme, LibraryEntry Channel)>();
            while (r.Read())
            {
                var programme = ReadProgramme(r, 0);
                var sourceId = r.GetString(5);
                var item = new MediaItem
                {
                    SourceId = sourceId,
                    ExternalId = r.GetString(6),
                    Kind = (MediaKind)r.GetInt32(7),
                    Title = r.GetString(8),
                    Group = r.GetString(9),
                    LogoUrl = r.IsDBNull(10) ? null : r.GetString(10),
                    StreamUrl = r.IsDBNull(11) ? null : Unmask(credentials, sourceId, r.GetString(11)),
                    EpgChannelId = r.IsDBNull(12) ? null : r.GetString(12),
                    CatchupDays = r.GetInt32(13),
                    SortIndex = r.GetInt32(14),
                    CatchupMode = r.IsDBNull(18) ? null : r.GetString(18),
                    CatchupSource = r.IsDBNull(19) ? null : r.GetString(19),
                    PlayerOptions = r.IsDBNull(20) ? null : r.GetString(20),
                };
                found.Add((programme, new LibraryEntry(item, r.GetString(15), r.GetString(16), r.GetString(17))));
            }
            return found;
        });

        var now = DateTimeOffset.Now;
        return rows
            .GroupBy(x => (Title: x.Programme.Title.Trim().ToLowerInvariant(), x.Programme.Start, x.Programme.Stop))
            .Select(g => new EventHit(g.First().Programme, g.Select(x => x.Channel).DistinctBy(c => (c.Item.SourceId, c.Item.ExternalId)).ToList()))
            .OrderByDescending(h => h.IsLive(now))
            .ThenBy(h => h.Programme.Start)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Live channels whose name matches an event search. Many providers run per-event channels
    /// like "NBA 03: Lakers vs Celtics" that aren't in the guide.
    /// </summary>
    public IReadOnlyList<LibraryEntry> SearchEventChannels(string query, int limit = 100)
    {
        var match = EventSearch.BuildExpression(query);
        if (match is null)
            return [];
        return ReadAside((db, credentials) =>
        {
            using var cmd = Command(db, $"""
                {SelectEntries}
                JOIN items_fts f ON f.rowid = i.rowid
                WHERE items_fts MATCH $match AND i.kind = {(int)MediaKind.Live} AND {VisibleFilter}
                ORDER BY bm25(items_fts, 10.0, 1.0), s.position, i.sort_index
                LIMIT $limit
                """);
            cmd.Parameters.AddWithValue("$match", match);
            cmd.Parameters.AddWithValue("$limit", limit);
            AddVisibility(cmd);
            return ReadEntries(cmd, credentials);
        });
    }
}
