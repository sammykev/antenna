using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Antenna.Core.Library;

/// <summary>Favourites, watch history, settings, category flags and recordings.</summary>
public sealed partial class LibraryStore
{
    private void MigrateUserData()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS favourites (
                source_id TEXT NOT NULL,
                external_id TEXT NOT NULL,
                kind INTEGER NOT NULL,
                added INTEGER NOT NULL,
                PRIMARY KEY (source_id, external_id, kind)
            );
            CREATE TABLE IF NOT EXISTS history (
                source_id TEXT NOT NULL,
                external_id TEXT NOT NULL,
                kind INTEGER NOT NULL,
                title TEXT NOT NULL,
                stream_url TEXT NOT NULL,
                logo TEXT,
                player_options TEXT,
                position_ms INTEGER NOT NULL,
                length_ms INTEGER NOT NULL,
                watched_at INTEGER NOT NULL,
                PRIMARY KEY (source_id, external_id)
            );
            CREATE INDEX IF NOT EXISTS history_recent ON history(watched_at DESC);
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS group_flags (
                kind INTEGER NOT NULL,
                grp TEXT NOT NULL,
                flag TEXT NOT NULL,
                PRIMARY KEY (kind, grp, flag)
            );
            CREATE TABLE IF NOT EXISTS reminders (
                id TEXT PRIMARY KEY,
                source_id TEXT NOT NULL,
                channel_id TEXT NOT NULL,
                channel_title TEXT NOT NULL,
                title TEXT NOT NULL,
                start INTEGER NOT NULL,
                stop INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS recordings (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                player_options TEXT,
                is_download INTEGER NOT NULL,
                start INTEGER NOT NULL,
                stop INTEGER,
                path TEXT NOT NULL,
                status INTEGER NOT NULL,
                error TEXT,
                bytes INTEGER NOT NULL DEFAULT 0,
                total_bytes INTEGER
            );
            """);
    }

    // ---- Favourites ----

    public void SetFavourite(MediaItem item, bool favourite)
    {
        lock (_gate)
        {
            using var cmd = Command(favourite
                ? "INSERT OR IGNORE INTO favourites (source_id, external_id, kind, added) VALUES ($s, $e, $k, $t)"
                : "DELETE FROM favourites WHERE source_id = $s AND external_id = $e AND kind = $k");
            cmd.Parameters.AddWithValue("$s", item.SourceId);
            cmd.Parameters.AddWithValue("$e", item.ExternalId);
            cmd.Parameters.AddWithValue("$k", (int)item.Kind);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    public bool IsFavourite(MediaItem item)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT 1 FROM favourites WHERE source_id = $s AND external_id = $e AND kind = $k");
            cmd.Parameters.AddWithValue("$s", item.SourceId);
            cmd.Parameters.AddWithValue("$e", item.ExternalId);
            cmd.Parameters.AddWithValue("$k", (int)item.Kind);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>Favourites that still exist in their playlist, newest first.</summary>
    public IReadOnlyList<LibraryEntry> GetFavourites(MediaKind? kind = null)
    {
        lock (_gate)
        {
            using var cmd = Command($"""
                {SelectEntries}
                JOIN favourites fav ON fav.source_id = i.source_id AND fav.external_id = i.external_id AND fav.kind = i.kind
                WHERE ($kind IS NULL OR i.kind = $kind) AND {VisibleFilter}
                ORDER BY fav.added DESC
                """);
            cmd.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : (object)(int)kind.Value);
            AddVisibility(cmd);
            return ReadEntries(cmd);
        }
    }

    // ---- History ----

    public void RecordWatch(WatchRecord record)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO history (source_id, external_id, kind, title, stream_url, logo, player_options, position_ms, length_ms, watched_at)
                VALUES ($s, $e, $k, $title, $url, $logo, $opts, $pos, $len, $at)
                ON CONFLICT(source_id, external_id) DO UPDATE SET
                    kind = excluded.kind, title = excluded.title, stream_url = excluded.stream_url, logo = excluded.logo,
                    player_options = excluded.player_options, position_ms = excluded.position_ms,
                    length_ms = excluded.length_ms, watched_at = excluded.watched_at
                """);
            cmd.Parameters.AddWithValue("$s", record.SourceId);
            cmd.Parameters.AddWithValue("$e", record.ExternalId);
            cmd.Parameters.AddWithValue("$k", (int)record.Kind);
            cmd.Parameters.AddWithValue("$title", record.Title);
            cmd.Parameters.AddWithValue("$url", MaskUrl(record.SourceId, record.StreamUrl));
            cmd.Parameters.AddWithValue("$logo", (object?)record.LogoUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$opts", (object?)record.PlayerOptions ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pos", record.PositionMs);
            cmd.Parameters.AddWithValue("$len", record.LengthMs);
            cmd.Parameters.AddWithValue("$at", record.WatchedAt.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }

    public WatchRecord? GetWatch(string sourceId, string externalId)
    {
        lock (_gate)
        {
            using var cmd = Command($"{SelectHistory} WHERE h.source_id = $s AND h.external_id = $e");
            cmd.Parameters.AddWithValue("$s", sourceId);
            cmd.Parameters.AddWithValue("$e", externalId);
            return ReadHistory(cmd).FirstOrDefault();
        }
    }

    /// <summary>Most recently watched first. Locked categories are left out while the parental lock is on.</summary>
    public IReadOnlyList<WatchRecord> GetHistory(int limit = 50)
    {
        lock (_gate)
        {
            using var cmd = Command($"""
                {SelectHistory}
                WHERE NOT ($locked = 1 AND EXISTS (
                    SELECT 1 FROM items i JOIN group_flags gf ON gf.kind = i.kind AND gf.grp = i.grp AND gf.flag = 'locked'
                    WHERE i.source_id = h.source_id
                      AND (i.external_id = h.external_id OR ('ep:' || i.external_id) = h.external_id)))
                ORDER BY h.watched_at DESC
                LIMIT $limit
                """);
            cmd.Parameters.AddWithValue("$limit", limit);
            AddVisibility(cmd);
            return ReadHistory(cmd);
        }
    }

    public void RemoveWatch(string sourceId, string externalId)
    {
        lock (_gate)
        {
            using var cmd = Command("DELETE FROM history WHERE source_id = $s AND external_id = $e");
            cmd.Parameters.AddWithValue("$s", sourceId);
            cmd.Parameters.AddWithValue("$e", externalId);
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearHistory()
    {
        lock (_gate)
            Exec("DELETE FROM history");
    }

    private const string SelectHistory = """
        SELECT h.source_id, h.external_id, h.kind, h.title, h.stream_url, h.logo, h.player_options, h.position_ms, h.length_ms, h.watched_at
        FROM history h
        """;

    private List<WatchRecord> ReadHistory(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<WatchRecord>();
        while (r.Read())
        {
            var sourceId = r.GetString(0);
            list.Add(new WatchRecord
            {
                SourceId = sourceId,
                ExternalId = r.GetString(1),
                Kind = (MediaKind)r.GetInt32(2),
                Title = r.GetString(3),
                StreamUrl = UnmaskUrl(sourceId, r.GetString(4))!,
                LogoUrl = r.IsDBNull(5) ? null : r.GetString(5),
                PlayerOptions = r.IsDBNull(6) ? null : r.GetString(6),
                PositionMs = r.GetInt64(7),
                LengthMs = r.GetInt64(8),
                WatchedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)).ToLocalTime(),
            });
        }
        return list;
    }

    // ---- Settings ----

    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT value FROM settings WHERE key = $k");
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>Stores a setting; null removes it.</summary>
    public void SetSetting(string key, string? value)
    {
        lock (_gate)
        {
            using var cmd = Command(value is null
                ? "DELETE FROM settings WHERE key = $k"
                : "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value");
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyDictionary<string, string> GetAllSettings()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT key, value FROM settings");
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, string>();
            while (r.Read())
                map[r.GetString(0)] = r.GetString(1);
            return map;
        }
    }

    // ---- Category flags ----

    public void SetGroupFlag(MediaKind kind, string group, GroupFlag flag, bool on)
    {
        lock (_gate)
        {
            using var cmd = Command(on
                ? "INSERT OR IGNORE INTO group_flags (kind, grp, flag) VALUES ($k, $g, $f)"
                : "DELETE FROM group_flags WHERE kind = $k AND grp = $g AND flag = $f");
            cmd.Parameters.AddWithValue("$k", (int)kind);
            cmd.Parameters.AddWithValue("$g", group);
            cmd.Parameters.AddWithValue("$f", FlagName(flag));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<(MediaKind Kind, string Group)> GetFlaggedGroups(GroupFlag flag)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT kind, grp FROM group_flags WHERE flag = $f ORDER BY kind, grp");
            cmd.Parameters.AddWithValue("$f", FlagName(flag));
            using var r = cmd.ExecuteReader();
            var list = new List<(MediaKind, string)>();
            while (r.Read())
                list.Add(((MediaKind)r.GetInt32(0), r.GetString(1)));
            return list;
        }
    }

    private static string FlagName(GroupFlag flag) => flag switch
    {
        GroupFlag.Hidden => "hidden",
        GroupFlag.Locked => "locked",
        _ => "pinned",
    };

    // ---- Reminders ----

    public void SaveReminder(Reminder reminder)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT OR REPLACE INTO reminders (id, source_id, channel_id, channel_title, title, start, stop)
                VALUES ($id, $source, $channel, $channelTitle, $title, $start, $stop)
                """);
            cmd.Parameters.AddWithValue("$id", reminder.Id);
            cmd.Parameters.AddWithValue("$source", reminder.SourceId);
            cmd.Parameters.AddWithValue("$channel", reminder.ChannelId);
            cmd.Parameters.AddWithValue("$channelTitle", reminder.ChannelTitle);
            cmd.Parameters.AddWithValue("$title", reminder.Title);
            cmd.Parameters.AddWithValue("$start", reminder.Start.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$stop", reminder.Stop.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<Reminder> GetReminders()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT source_id, channel_id, channel_title, title, start, stop FROM reminders ORDER BY start");
            using var r = cmd.ExecuteReader();
            var list = new List<Reminder>();
            while (r.Read())
            {
                list.Add(new Reminder
                {
                    SourceId = r.GetString(0),
                    ChannelId = r.GetString(1),
                    ChannelTitle = r.GetString(2),
                    Title = r.GetString(3),
                    Start = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)).ToLocalTime(),
                    Stop = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)).ToLocalTime(),
                });
            }
            return list;
        }
    }

    public bool HasReminder(string id)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT 1 FROM reminders WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public void DeleteReminder(string id)
    {
        lock (_gate)
        {
            using var cmd = Command("DELETE FROM reminders WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- Recordings ----

    public void SaveRecording(Recording rec)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO recordings (id, title, url, player_options, is_download, start, stop, path, status, error, bytes, total_bytes)
                VALUES ($id, $title, $url, $opts, $dl, $start, $stop, $path, $status, $error, $bytes, $total)
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title, url = excluded.url, player_options = excluded.player_options,
                    is_download = excluded.is_download, start = excluded.start, stop = excluded.stop, path = excluded.path,
                    status = excluded.status, error = excluded.error, bytes = excluded.bytes, total_bytes = excluded.total_bytes
                """);
            cmd.Parameters.AddWithValue("$id", rec.Id);
            cmd.Parameters.AddWithValue("$title", rec.Title);
            cmd.Parameters.AddWithValue("$url", rec.Url);
            cmd.Parameters.AddWithValue("$opts", (object?)rec.PlayerOptions ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dl", rec.IsDownload ? 1 : 0);
            cmd.Parameters.AddWithValue("$start", rec.Start.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$stop", rec.Stop is { } stop ? stop.ToUnixTimeSeconds() : DBNull.Value);
            cmd.Parameters.AddWithValue("$path", rec.Path);
            cmd.Parameters.AddWithValue("$status", (int)rec.Status);
            cmd.Parameters.AddWithValue("$error", (object?)rec.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bytes", rec.BytesWritten);
            cmd.Parameters.AddWithValue("$total", rec.TotalBytes is { } t ? t : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<Recording> GetRecordings()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT id, title, url, player_options, is_download, start, stop, path, status, error, bytes, total_bytes FROM recordings ORDER BY start DESC");
            using var r = cmd.ExecuteReader();
            var list = new List<Recording>();
            while (r.Read())
            {
                list.Add(new Recording
                {
                    Id = r.GetString(0),
                    Title = r.GetString(1),
                    Url = r.GetString(2),
                    PlayerOptions = r.IsDBNull(3) ? null : r.GetString(3),
                    IsDownload = r.GetInt32(4) == 1,
                    Start = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)).ToLocalTime(),
                    Stop = r.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6)).ToLocalTime(),
                    Path = r.GetString(7),
                    Status = (RecordingStatus)r.GetInt32(8),
                    Error = r.IsDBNull(9) ? null : r.GetString(9),
                    BytesWritten = r.GetInt64(10),
                    TotalBytes = r.IsDBNull(11) ? null : r.GetInt64(11),
                });
            }
            return list;
        }
    }

    public void DeleteRecording(string id)
    {
        lock (_gate)
        {
            using var cmd = Command("DELETE FROM recordings WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
