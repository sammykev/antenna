using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Antenna.Core.Library;

/// <summary>Encrypts secrets at rest. The Windows app uses DPAPI; tests use a fake.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

/// <summary>
/// Local SQLite library of every source's items, with an FTS5 index so one search
/// covers live channels, movies and series across all playlists.
/// Safe to call from any thread: calls are serialized on one connection.
/// </summary>
public sealed partial class LibraryStore : IDisposable
{
    private const string EncryptedPrefix = "enc:";

    /// <summary>Stands in for "/user/pass/" inside stored Xtream URLs so credentials live only in <c>sources</c>.</summary>
    internal const string CredentialToken = "/{xc}/";

    private readonly SqliteConnection _db;
    private readonly string _connectionString;
    private readonly ISecretProtector? _protector;
    private readonly Lock _gate = new();
    private Dictionary<string, (string User, string Pass)>? _credentials;

    public LibraryStore(string databasePath, ISecretProtector? protector = null)
    {
        _protector = protector;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        _db = new SqliteConnection(_connectionString);
        _db.Open();
        Exec("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");
        Migrate();
        EncryptLegacyPasswords();
    }

    /// <summary>
    /// When true, items in categories the user locked are left out of every query
    /// (browse, search, favourites, history) until the PIN is entered.
    /// </summary>
    public bool ParentalLockActive { get; set; }

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
            CREATE INDEX IF NOT EXISTS items_key ON items(source_id, external_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(
                title, grp, tokenize = 'unicode61 remove_diacritics 2'
            );
            CREATE TABLE IF NOT EXISTS programmes (
                source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
                channel_id TEXT NOT NULL,
                start INTEGER NOT NULL,
                stop INTEGER NOT NULL,
                title TEXT NOT NULL,
                description TEXT
            );
            CREATE INDEX IF NOT EXISTS programmes_channel ON programmes(source_id, channel_id, start);
            CREATE INDEX IF NOT EXISTS programmes_time ON programmes(start, stop);
            CREATE VIRTUAL TABLE IF NOT EXISTS programmes_fts USING fts5(
                title, description, tokenize = 'unicode61 remove_diacritics 2'
            );
            """);
        // Guides imported before programme search existed: index them once.
        Exec("""
            INSERT INTO programmes_fts (rowid, title, description)
            SELECT rowid, title, COALESCE(description, '') FROM programmes
            WHERE NOT EXISTS (SELECT 1 FROM programmes_fts LIMIT 1);
            """);
        AddColumnIfMissing("items", "catchup_mode", "TEXT");
        AddColumnIfMissing("items", "catchup_source", "TEXT");
        AddColumnIfMissing("items", "player_options", "TEXT");
        MigrateUserData();
    }

    private void AddColumnIfMissing(string table, string column, string type)
    {
        using var cmd = Command($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $col");
        cmd.Parameters.AddWithValue("$col", column);
        if ((long)cmd.ExecuteScalar()! == 0)
            Exec($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    // ---- Sources ----

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
            cmd.Parameters.AddWithValue("$password", (object?)ProtectSecret(source.Password) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$epg", (object?)source.EpgUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$color", source.Color);
            cmd.Parameters.AddWithValue("$icon", source.Icon);
            cmd.Parameters.AddWithValue("$refreshed", (object?)source.LastRefreshed?.ToString("O") ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            _credentials = null;
        }
    }

    public IReadOnlyList<PlaylistSource> GetSources()
    {
        lock (_gate)
        {
            return ReadSources();
        }
    }

    private List<PlaylistSource> ReadSources()
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
                Password = r.IsDBNull(5) ? null : UnprotectSecret(r.GetString(5)),
                EpgUrl = r.IsDBNull(6) ? null : r.GetString(6),
                Color = r.GetString(7),
                Icon = r.GetString(8),
                LastRefreshed = r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9), CultureInfo.InvariantCulture),
            });
        }
        return list;
    }

    public void DeleteSource(string sourceId)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            DeleteItems(sourceId, tx);
            using var cmd = Command("""
                DELETE FROM programmes_fts WHERE rowid IN (SELECT rowid FROM programmes WHERE source_id = $id);
                DELETE FROM programmes WHERE source_id = $id;
                DELETE FROM favourites WHERE source_id = $id;
                DELETE FROM history WHERE source_id = $id;
                DELETE FROM sources WHERE id = $id;
                """, tx);
            cmd.Parameters.AddWithValue("$id", sourceId);
            cmd.ExecuteNonQuery();
            tx.Commit();
            _credentials = null;
        }
    }

    /// <summary>Moves a playlist up (-1) or down (+1) in the list; its items follow in browse order.</summary>
    public void MoveSource(string sourceId, int delta)
    {
        lock (_gate)
        {
            var ids = ReadSources().Select(s => s.Id).ToList();
            var from = ids.IndexOf(sourceId);
            var to = Math.Clamp(from + delta, 0, ids.Count - 1);
            if (from < 0 || from == to)
                return;
            ids.RemoveAt(from);
            ids.Insert(to, sourceId);

            using var tx = _db.BeginTransaction();
            using var cmd = Command("UPDATE sources SET position = $pos WHERE id = $id", tx);
            var pPos = cmd.Parameters.Add("$pos", SqliteType.Integer);
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            for (var i = 0; i < ids.Count; i++)
            {
                pPos.Value = i;
                pId.Value = ids[i];
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private string? ProtectSecret(string? secret) =>
        secret is null || _protector is null ? secret : EncryptedPrefix + _protector.Protect(secret);

    private string? UnprotectSecret(string stored)
    {
        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return stored;
        if (_protector is null)
            return null;
        try
        {
            return _protector.Unprotect(stored[EncryptedPrefix.Length..]);
        }
        catch (Exception)
        {
            // Encrypted for another Windows user or machine: the user has to re-enter it.
            return null;
        }
    }

    /// <summary>Databases from before encryption kept passwords in plain text; encrypt them in place.</summary>
    private void EncryptLegacyPasswords()
    {
        if (_protector is null)
            return;
        lock (_gate)
        {
            foreach (var source in ReadSources())
            {
                using var check = Command("SELECT password FROM sources WHERE id = $id");
                check.Parameters.AddWithValue("$id", source.Id);
                if (check.ExecuteScalar() is string stored && !stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
                {
                    using var update = Command("UPDATE sources SET password = $p WHERE id = $id");
                    update.Parameters.AddWithValue("$p", ProtectSecret(stored));
                    update.Parameters.AddWithValue("$id", source.Id);
                    update.ExecuteNonQuery();
                }
            }
        }
    }

    /// <summary>Xtream credentials per source, for masking/unmasking stream URLs. Caller holds the lock.</summary>
    private Dictionary<string, (string User, string Pass)> Credentials =>
        _credentials ??= ReadSources()
            .Where(s => s.Kind == SourceKind.Xtream && !string.IsNullOrEmpty(s.Username) && s.Password is not null)
            .ToDictionary(s => s.Id, s => (s.Username!, s.Password!));

    internal static string CredentialSegment(string user, string pass) =>
        $"/{Uri.EscapeDataString(user)}/{Uri.EscapeDataString(pass)}/";

    private string? MaskUrl(string sourceId, string? url) =>
        url is not null && Credentials.TryGetValue(sourceId, out var c)
            ? url.Replace(CredentialSegment(c.User, c.Pass), CredentialToken, StringComparison.Ordinal)
            : url;

    private string? UnmaskUrl(string sourceId, string? url) =>
        url is not null && url.Contains(CredentialToken, StringComparison.Ordinal) && Credentials.TryGetValue(sourceId, out var c)
            ? url.Replace(CredentialToken, CredentialSegment(c.User, c.Pass), StringComparison.Ordinal)
            : url;

    // ---- Items ----

    /// <summary>Replaces everything stored for a source with a freshly imported catalogue.</summary>
    public void ReplaceItems(string sourceId, IReadOnlyList<MediaItem> items)
    {
        lock (_gate)
        {
            _ = Credentials; // Load before the transaction: reads outside it aren't allowed while it's open.
            using var tx = _db.BeginTransaction();
            DeleteItems(sourceId, tx);

            using var insert = Command("""
                INSERT INTO items (source_id, external_id, kind, title, grp, logo, stream_url, epg_id, catchup_days, catchup_mode, catchup_source, player_options, sort_index)
                VALUES ($source, $ext, $kind, $title, $grp, $logo, $url, $epg, $catchup, $cmode, $csource, $opts, $sort)
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
            var pCatchupMode = insert.Parameters.Add("$cmode", SqliteType.Text);
            var pCatchupSource = insert.Parameters.Add("$csource", SqliteType.Text);
            var pOptions = insert.Parameters.Add("$opts", SqliteType.Text);
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
                pUrl.Value = (object?)MaskUrl(sourceId, item.StreamUrl) ?? DBNull.Value;
                pEpg.Value = (object?)item.EpgChannelId ?? DBNull.Value;
                pCatchup.Value = item.CatchupDays;
                pCatchupMode.Value = (object?)item.CatchupMode ?? DBNull.Value;
                pCatchupSource.Value = (object?)item.CatchupSource ?? DBNull.Value;
                pOptions.Value = (object?)item.PlayerOptions ?? DBNull.Value;
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

    /// <summary>Hidden categories never appear; locked ones only while the parental lock is active.</summary>
    private const string VisibleFilter = """
        NOT EXISTS (SELECT 1 FROM group_flags gf
                    WHERE gf.kind = i.kind AND gf.grp = i.grp
                      AND (gf.flag = 'hidden' OR (gf.flag = 'locked' AND $locked = 1)))
        """;

    private void AddVisibility(SqliteCommand cmd) => cmd.Parameters.AddWithValue("$locked", ParentalLockActive ? 1 : 0);

    /// <summary>Groups (categories): pinned first, then provider order, minus hidden ones. Pass null to span every source.</summary>
    public IReadOnlyList<string> GetGroups(MediaKind kind, string? sourceId = null)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT grp FROM items i
                WHERE kind = $kind AND ($source IS NULL OR source_id = $source)
                  AND NOT EXISTS (SELECT 1 FROM group_flags gf WHERE gf.kind = i.kind AND gf.grp = i.grp AND gf.flag = 'hidden')
                GROUP BY grp
                ORDER BY EXISTS (SELECT 1 FROM group_flags gp WHERE gp.kind = $kind AND gp.grp = i.grp AND gp.flag = 'pinned') DESC,
                         MIN(sort_index)
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

    public IReadOnlyList<LibraryEntry> GetItems(MediaKind kind, string? sourceId = null, string? group = null, int limit = 5000, int offset = 0)
    {
        lock (_gate)
        {
            using var cmd = Command($"""
                {SelectEntries}
                WHERE i.kind = $kind AND ($source IS NULL OR i.source_id = $source) AND ($grp IS NULL OR i.grp = $grp)
                  AND {VisibleFilter}
                ORDER BY s.position, i.sort_index
                LIMIT $limit OFFSET $offset
                """);
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            cmd.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$grp", (object?)group ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);
            AddVisibility(cmd);
            return ReadEntries(cmd);
        }
    }

    /// <summary>One item by its key, or null if it no longer exists (or is hidden).</summary>
    public LibraryEntry? GetItem(string sourceId, string externalId, MediaKind kind)
    {
        lock (_gate)
        {
            using var cmd = Command($"""
                {SelectEntries}
                WHERE i.source_id = $source AND i.external_id = $ext AND i.kind = $kind AND {VisibleFilter}
                LIMIT 1
                """);
            cmd.Parameters.AddWithValue("$source", sourceId);
            cmd.Parameters.AddWithValue("$ext", externalId);
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            AddVisibility(cmd);
            return ReadEntries(cmd).FirstOrDefault();
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
                WHERE items_fts MATCH $match AND ($kind IS NULL OR i.kind = $kind) AND {VisibleFilter}
                ORDER BY bm25(items_fts, 10.0, 1.0), i.kind, s.position, i.sort_index
                LIMIT $limit
                """);
            cmd.Parameters.AddWithValue("$match", match);
            cmd.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : (object)(int)kind.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            AddVisibility(cmd);
            return ReadEntries(cmd);
        }
    }

    // ---- Guide ----

    /// <summary>
    /// Starts replacing a source's guide. The writer has its own connection, so a large
    /// import doesn't block reads on this store (WAL lets readers see the old guide until commit).
    /// </summary>
    public ProgrammeWriter BeginGuideImport(string sourceId) => new(_connectionString, sourceId);

    /// <summary>Programmes for one channel overlapping [from, to), in time order.</summary>
    public IReadOnlyList<Programme> GetProgrammes(string sourceId, string channelId, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT channel_id, start, stop, title, description FROM programmes
                WHERE source_id = $source AND channel_id = $channel AND stop > $from AND start < $to
                ORDER BY start
                """);
            cmd.Parameters.AddWithValue("$source", sourceId);
            cmd.Parameters.AddWithValue("$channel", channelId);
            cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            var list = new List<Programme>();
            while (r.Read())
                list.Add(ReadProgramme(r, 0));
            return list;
        }
    }

    /// <summary>What's on at <paramref name="at"/> on every channel, keyed by (source id, channel id).</summary>
    public Dictionary<(string SourceId, string ChannelId), Programme> GetNowPlaying(DateTimeOffset at)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT source_id, channel_id, start, stop, title, description FROM programmes
                WHERE start <= $at AND stop > $at
                """);
            cmd.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<(string, string), Programme>();
            while (r.Read())
                map[(r.GetString(0), r.GetString(1))] = ReadProgramme(r, 1);
            return map;
        }
    }

    private static Programme ReadProgramme(SqliteDataReader r, int first) => new()
    {
        ChannelId = r.GetString(first),
        Start = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(first + 1)),
        Stop = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(first + 2)),
        Title = r.GetString(first + 3),
        Description = r.IsDBNull(first + 4) ? null : r.GetString(first + 4),
    };

    // ---- Helpers ----

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
               s.name, s.color, s.icon, i.catchup_mode, i.catchup_source, i.player_options
        FROM items i JOIN sources s ON s.id = i.source_id
        """;

    private List<LibraryEntry> ReadEntries(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<LibraryEntry>();
        while (r.Read())
        {
            var sourceId = r.GetString(0);
            var item = new MediaItem
            {
                SourceId = sourceId,
                ExternalId = r.GetString(1),
                Kind = (MediaKind)r.GetInt32(2),
                Title = r.GetString(3),
                Group = r.GetString(4),
                LogoUrl = r.IsDBNull(5) ? null : r.GetString(5),
                StreamUrl = r.IsDBNull(6) ? null : UnmaskUrl(sourceId, r.GetString(6)),
                EpgChannelId = r.IsDBNull(7) ? null : r.GetString(7),
                CatchupDays = r.GetInt32(8),
                SortIndex = r.GetInt32(9),
                CatchupMode = r.IsDBNull(13) ? null : r.GetString(13),
                CatchupSource = r.IsDBNull(14) ? null : r.GetString(14),
                PlayerOptions = r.IsDBNull(15) ? null : r.GetString(15),
            };
            list.Add(new LibraryEntry(item, r.GetString(10), r.GetString(11), r.GetString(12)));
        }
        return list;
    }

    private void DeleteItems(string sourceId, SqliteTransaction tx)
    {
        // The guide is kept: it's replaced separately, and a failed guide download shouldn't erase it.
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
