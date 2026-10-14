using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Antenna.Core.Library;

/// <summary>
/// Local SQLite library of every source's items, with an FTS5 index so one search
/// covers live channels, movies and series across all playlists.
/// Safe to call from any thread: calls are serialized on one connection.
/// </summary>
public sealed class LibraryStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    public LibraryStore(string databasePath)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");
        Migrate();
    }

    /// <summary>The default database location, %LOCALAPPDATA%\Antenna\library.db on Windows.</summary>
    public static string DefaultPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Antenna");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "library.db");
    }

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS sources (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                kind INTEGER NOT NULL,
                location TEXT NOT NULL,
                username TEXT,
                password TEXT,
                epg_url TEXT,
                color TEXT NOT NULL,
                icon TEXT NOT NULL,
                last_refreshed TEXT,
                position INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS items (
                rowid INTEGER PRIMARY KEY,
                source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
                external_id TEXT NOT NULL,
                kind INTEGER NOT NULL,
                title TEXT NOT NULL,
                grp TEXT NOT NULL,
                logo TEXT,
                stream_url TEXT,
                epg_id TEXT,
                catchup_days INTEGER NOT NULL,
                sort_index INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS items_browse ON items(kind, source_id, grp, sort_index);
            CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(
                title, grp, tokenize = 'unicode61 remove_diacritics 2'
            );
            """);
    }

    public void SaveSource(PlaylistSource source)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO sources (id, name, kind, location, username, password, epg_url, color, icon, last_refreshed, position)
                VALUES ($id, $name, $kind, $location, $username, $password, $epg, $color, $icon, $refreshed,
                        (SELECT COALESCE(MAX(position) + 1, 0) FROM sources))
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, kind = excluded.kind, location = excluded.location,
                    username = excluded.username, password = excluded.password, epg_url = excluded.epg_url,
                    color = excluded.color, icon = excluded.icon, last_refreshed = excluded.last_refreshed;
                """);
            cmd.Parameters.AddWithValue("$id", source.Id);
            cmd.Parameters.AddWithValue("$name", source.Name);
            cmd.Parameters.AddWithValue("$kind", (int)source.Kind);
            cmd.Parameters.AddWithValue("$location", source.Location);
            cmd.Parameters.AddWithValue("$username", (object?)source.Username ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$password", (object?)source.Password ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$epg", (object?)source.EpgUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$color", source.Color);
            cmd.Parameters.AddWithValue("$icon", source.Icon);
            cmd.Parameters.AddWithValue("$refreshed", (object?)source.LastRefreshed?.ToString("O") ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<PlaylistSource> GetSources()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT id, name, kind, location, username, password, epg_url, color, icon, last_refreshed FROM sources ORDER BY position");
            using var r = cmd.ExecuteReader();
            var list = new List<PlaylistSource>();
            while (r.Read())
            {
                list.Add(new PlaylistSource
                {
                    Id = r.GetString(0),
                    Name = r.GetString(1),
                    Kind = (SourceKind)r.GetInt32(2),
                    Location = r.GetString(3),
                    Username = r.IsDBNull(4) ? null : r.GetString(4),
                    Password = r.IsDBNull(5) ? null : r.GetString(5),
                    EpgUrl = r.IsDBNull(6) ? null : r.GetString(6),
                    Color = r.GetString(7),
                    Icon = r.GetString(8),
                    LastRefreshed = r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9), CultureInfo.InvariantCulture),
                });
            }
            return list;
        }
    }

    public void DeleteSource(string sourceId)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            DeleteItems(sourceId, tx);
            using var cmd = Command("DELETE FROM sources WHERE id = $id", tx);
            cmd.Parameters.AddWithValue("$id", sourceId);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <summary>Replaces everything stored for a source with a freshly imported catalogue.</summary>
    public void ReplaceItems(string sourceId, IReadOnlyList<MediaItem> items)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            DeleteItems(sourceId, tx);

            using var insert = Command("""
                INSERT INTO items (source_id, external_id, kind, title, grp, logo, stream_url, epg_id, catchup_days, sort_index)
                VALUES ($source, $ext, $kind, $title, $grp, $logo, $url, $epg, $catchup, $sort)
                RETURNING rowid;
                """, tx);
            var pSource = insert.Parameters.Add("$source", SqliteType.Text);
            var pExt = insert.Parameters.Add("$ext", SqliteType.Text);
            var pKind = insert.Parameters.Add("$kind", SqliteType.Integer);
            var pTitle = insert.Parameters.Add("$title", SqliteType.Text);
            var pGrp = insert.Parameters.Add("$grp", SqliteType.Text);
            var pLogo = insert.Parameters.Add("$logo", SqliteType.Text);
            var pUrl = insert.Parameters.Add("$url", SqliteType.Text);
            var pEpg = insert.Parameters.Add("$epg", SqliteType.Text);
            var pCatchup = insert.Parameters.Add("$catchup", SqliteType.Integer);
            var pSort = insert.Parameters.Add("$sort", SqliteType.Integer);

            using var fts = Command("INSERT INTO items_fts (rowid, title, grp) VALUES ($rowid, $title, $grp)", tx);
            var fRow = fts.Parameters.Add("$rowid", SqliteType.Integer);
            var fTitle = fts.Parameters.Add("$title", SqliteType.Text);
            var fGrp = fts.Parameters.Add("$grp", SqliteType.Text);

            foreach (var item in items)
            {
                pSource.Value = sourceId;
                pExt.Value = item.ExternalId;
                pKind.Value = (int)item.Kind;
                pTitle.Value = item.Title;
                pGrp.Value = item.Group;
                pLogo.Value = (object?)item.LogoUrl ?? DBNull.Value;
                pUrl.Value = (object?)item.StreamUrl ?? DBNull.Value;
                pEpg.Value = (object?)item.EpgChannelId ?? DBNull.Value;
                pCatchup.Value = item.CatchupDays;
                pSort.Value = item.SortIndex;
                var rowid = (long)insert.ExecuteScalar()!;

                fRow.Value = rowid;
                fTitle.Value = item.Title;
                fGrp.Value = item.Group;
                fts.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Groups (categories) in provider order. Pass null to span every source.</summary>
    public IReadOnlyList<string> GetGroups(MediaKind kind, string? sourceId = null)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT grp FROM items
                WHERE kind = $kind AND ($source IS NULL OR source_id = $source)
                GROUP BY grp ORDER BY MIN(sort_index)
                """);
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            cmd.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read())
                list.Add(r.GetString(0));
            return list;
        }
    }

    public IReadOnlyList<LibraryEntry> GetItems(MediaKind kind, string? sourceId = null, string? group = null, int limit = 5000)
    {
        lock (_gate)
        {
            using var cmd = Command($"""
                {SelectEntries}
                WHERE i.kind = $kind AND ($source IS NULL OR i.source_id = $source) AND ($grp IS NULL OR i.grp = $grp)
                ORDER BY s.position, i.sort_index
                LIMIT $limit
                """);
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            cmd.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$grp", (object?)group ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadEntries(cmd);
        }
    }

    /// <summary>
    /// Prefix search over titles and groups across all sources: "bbc new" matches "BBC News HD".
    /// Accents are ignored. Results rank by relevance.
    /// </summary>
    public IReadOnlyList<LibraryEntry> Search(string query, MediaKind? kind = null, int limit = 200)
    {
        lock (_gate)
        {
            var match = BuildMatchExpression(query);
            if (match is null)
                return [];

            using var cmd = Command($"""
                {SelectEntries}
                JOIN items_fts f ON f.rowid = i.rowid
                WHERE items_fts MATCH $match AND ($kind IS NULL OR i.kind = $kind)
                ORDER BY bm25(items_fts, 10.0, 1.0), i.kind, s.position, i.sort_index
                LIMIT $limit
                """);
            cmd.Parameters.AddWithValue("$match", match);
            cmd.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : (object)(int)kind.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadEntries(cmd);
        }
    }

    /// <summary>Turns free text into an FTS5 expression of quoted prefix terms, so user input can't inject syntax.</summary>
    internal static string? BuildMatchExpression(string query)
    {
        var terms = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in query)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                terms.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            terms.Add(current.ToString());

        return terms.Count == 0 ? null : string.Join(' ', terms.Select(t => $"\"{t}\"*"));
    }

    private const string SelectEntries = """
        SELECT i.source_id, i.external_id, i.kind, i.title, i.grp, i.logo, i.stream_url, i.epg_id, i.catchup_days, i.sort_index,
               s.name, s.color, s.icon
        FROM items i JOIN sources s ON s.id = i.source_id
        """;

    private static List<LibraryEntry> ReadEntries(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<LibraryEntry>();
        while (r.Read())
        {
            var item = new MediaItem
            {
                SourceId = r.GetString(0),
                ExternalId = r.GetString(1),
                Kind = (MediaKind)r.GetInt32(2),
                Title = r.GetString(3),
                Group = r.GetString(4),
                LogoUrl = r.IsDBNull(5) ? null : r.GetString(5),
                StreamUrl = r.IsDBNull(6) ? null : r.GetString(6),
                EpgChannelId = r.IsDBNull(7) ? null : r.GetString(7),
                CatchupDays = r.GetInt32(8),
                SortIndex = r.GetInt32(9),
            };
            list.Add(new LibraryEntry(item, r.GetString(10), r.GetString(11), r.GetString(12)));
        }
        return list;
    }

    private void DeleteItems(string sourceId, SqliteTransaction tx)
    {
        using var cmd = Command("""
            DELETE FROM items_fts WHERE rowid IN (SELECT rowid FROM items WHERE source_id = $id);
            DELETE FROM items WHERE source_id = $id;
            """, tx);
        cmd.Parameters.AddWithValue("$id", sourceId);
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private void Exec(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
            _db.Dispose();
    }
}
