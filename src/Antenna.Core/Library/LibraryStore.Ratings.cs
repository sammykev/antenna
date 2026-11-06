using Antenna.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace Antenna.Core.Library;

/// <summary>
/// IMDb / Rotten Tomatoes scores per film, keyed by cleaned title and year (<see cref="TitleCleaner.Key"/>) so every
/// playlist's copy shares one, and which film each catalogue entry is. found = 0 records "OMDb doesn't know it", so
/// it isn't asked again soon. The same tables as the Android app's.
/// </summary>
public sealed partial class LibraryStore
{
    private void MigrateRatings() => Exec("""
        CREATE TABLE IF NOT EXISTS ratings (
            film TEXT PRIMARY KEY, imdb REAL, votes INTEGER, rt INTEGER, meta INTEGER, imdb_id TEXT, genres TEXT,
            found INTEGER NOT NULL, fetched INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS item_films (
            source_id TEXT NOT NULL, external_id TEXT NOT NULL, film TEXT NOT NULL, PRIMARY KEY (source_id, external_id));
        CREATE INDEX IF NOT EXISTS item_films_film ON item_films(film);
        """);

    /// <summary>Films whose rating is known, found or not.</summary>
    public HashSet<string> GetRatedFilms()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT film FROM ratings");
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.Ordinal);
            while (r.Read())
                set.Add(r.GetString(0));
            return set;
        }
    }

    /// <summary>Films OMDb didn't know, looked up before <paramref name="before"/> (worth asking again: spellings change).</summary>
    public HashSet<string> GetStaleMisses(DateTimeOffset before)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT film FROM ratings WHERE found = 0 AND fetched < $t");
            cmd.Parameters.AddWithValue("$t", before.ToUnixTimeMilliseconds());
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.Ordinal);
            while (r.Read())
                set.Add(r.GetString(0));
            return set;
        }
    }

    /// <summary>Saves a film's scores; null records that OMDb doesn't know it.</summary>
    public void SaveRating(string film, FilmRating? rating)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO ratings (film, imdb, votes, rt, meta, imdb_id, genres, found, fetched) VALUES ($f, $i, $v, $rt, $m, $id, $g, $found, $t)
                ON CONFLICT(film) DO UPDATE SET imdb = excluded.imdb, votes = excluded.votes, rt = excluded.rt, meta = excluded.meta,
                    imdb_id = excluded.imdb_id, genres = excluded.genres, found = excluded.found, fetched = excluded.fetched
                """);
            cmd.Parameters.AddWithValue("$f", film);
            cmd.Parameters.AddWithValue("$i", (object?)rating?.Imdb ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$v", (object?)rating?.ImdbVotes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rt", (object?)rating?.RottenTomatoes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", (object?)rating?.Metascore ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", (object?)rating?.ImdbId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$g", rating is null ? DBNull.Value : string.Join(',', rating.Genres));
            cmd.Parameters.AddWithValue("$found", rating is null ? 0 : 1);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }

    public void LinkFilms(IReadOnlyCollection<(string SourceId, string ExternalId, string Film)> links)
    {
        if (links.Count == 0)
            return;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("INSERT OR REPLACE INTO item_films (source_id, external_id, film) VALUES ($s, $e, $f)", tx);
            var s = cmd.Parameters.Add("$s", SqliteType.Text);
            var e = cmd.Parameters.Add("$e", SqliteType.Text);
            var f = cmd.Parameters.Add("$f", SqliteType.Text);
            foreach (var link in links)
            {
                s.Value = link.SourceId;
                e.Value = link.ExternalId;
                f.Value = link.Film;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Films not matched to a rating yet, the ones people care about first: watched, favourites, then catalogue order.</summary>
    public IReadOnlyList<MediaItem> GetUnlinkedMovies(int limit) =>
        ReadAside((db, credentials) =>
        {
            using var cmd = Command(db, $"""
                {SelectEntries}
                LEFT JOIN history h ON h.source_id = i.source_id AND h.external_id = i.external_id
                LEFT JOIN favourites fav ON fav.source_id = i.source_id AND fav.external_id = i.external_id AND fav.kind = i.kind
                WHERE i.kind = $kind AND {VisibleFilter}
                  AND NOT EXISTS (SELECT 1 FROM item_films f WHERE f.source_id = i.source_id AND f.external_id = i.external_id)
                ORDER BY h.watched_at IS NULL, h.watched_at DESC, fav.added IS NULL, s.position, i.sort_index
                LIMIT $limit
                """);
            cmd.Parameters.AddWithValue("$kind", (int)MediaKind.Movie);
            cmd.Parameters.AddWithValue("$limit", limit);
            AddVisibility(cmd);
            return ReadEntries(cmd, credentials).Select(e => e.Item).ToList();
        });

    /// <summary>The scores of the film this entry is, if known.</summary>
    public FilmRating? GetRating(string sourceId, string externalId)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT r.imdb, r.votes, r.rt, r.meta, r.imdb_id, r.genres FROM item_films f JOIN ratings r ON r.film = f.film
                WHERE f.source_id = $s AND f.external_id = $e AND r.found = 1
                """);
            cmd.Parameters.AddWithValue("$s", sourceId);
            cmd.Parameters.AddWithValue("$e", externalId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRating(r, 0) : null;
        }
    }

    /// <summary>
    /// The library's films, best rated first, one entry per film however many playlists have it. Obscure films with
    /// few IMDb votes and no Tomatometer are left out, so a 9.4 from 40 votes doesn't top the list.
    /// </summary>
    public IReadOnlyList<(LibraryEntry Entry, FilmRating Rating)> GetTopRated(int limit) =>
        ReadAside((db, credentials) =>
        {
            using var cmd = Command(db, $"""
                SELECT * FROM (
                    SELECT i.source_id, i.external_id, i.kind, i.title, i.grp, i.logo, i.stream_url, i.epg_id, i.catchup_days, i.sort_index,
                           s.name, s.color, s.icon, i.catchup_mode, i.catchup_source, i.player_options,
                           r.imdb, r.votes, r.rt, r.meta, r.imdb_id, r.genres,
                           ROW_NUMBER() OVER (PARTITION BY f.film ORDER BY s.position, i.sort_index) AS copy,
                           CASE WHEN r.imdb IS NOT NULL AND r.rt IS NOT NULL THEN (r.imdb * 10 + r.rt) / 2.0
                                WHEN r.imdb IS NOT NULL THEN r.imdb * 10 ELSE r.rt END AS score
                    FROM items i JOIN sources s ON s.id = i.source_id
                    JOIN item_films f ON f.source_id = i.source_id AND f.external_id = i.external_id
                    JOIN ratings r ON r.film = f.film AND r.found = 1
                    WHERE i.kind = $kind AND {VisibleFilter} AND (r.rt IS NOT NULL OR r.votes >= 5000))
                WHERE copy = 1 AND score IS NOT NULL ORDER BY score DESC LIMIT $limit
                """);
            cmd.Parameters.AddWithValue("$kind", (int)MediaKind.Movie);
            cmd.Parameters.AddWithValue("$limit", limit);
            AddVisibility(cmd);
            // The 16 entry columns come first, then the rating's.
            using var r = cmd.ExecuteReader();
            var list = new List<(LibraryEntry, FilmRating)>();
            while (r.Read())
                list.Add((ReadEntry(r, credentials), ReadRating(r, 16)));
            return list;
        });

    private static FilmRating ReadRating(SqliteDataReader r, int first) => new()
    {
        Imdb = r.IsDBNull(first) ? null : r.GetDouble(first),
        ImdbVotes = r.IsDBNull(first + 1) ? null : r.GetInt32(first + 1),
        RottenTomatoes = r.IsDBNull(first + 2) ? null : r.GetInt32(first + 2),
        Metascore = r.IsDBNull(first + 3) ? null : r.GetInt32(first + 3),
        ImdbId = r.IsDBNull(first + 4) ? null : r.GetString(first + 4),
        Genres = r.IsDBNull(first + 5) ? [] : r.GetString(first + 5).Split(',', StringSplitOptions.RemoveEmptyEntries),
    };
}
