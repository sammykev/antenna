using Microsoft.Data.Sqlite;

namespace Antenna.Core.Library;

/// <summary>
/// Replaces one source's guide in a single transaction on a dedicated connection.
/// Nothing is visible to readers until <see cref="Commit"/>; disposing without
/// committing rolls back and keeps the previous guide.
/// </summary>
public sealed class ProgrammeWriter : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly SqliteTransaction _tx;
    private readonly SqliteCommand _insert;
    private readonly SqliteCommand _setEpgId;
    private readonly SqliteCommand _ftsInsert;
    private readonly SqliteParameter _ftsRow, _ftsTitle, _ftsDesc;
    private readonly SqliteParameter _channel, _start, _stop, _title, _desc, _epgId, _externalId;
    private readonly string _sourceId;

    public int Count { get; private set; }

    internal ProgrammeWriter(string connectionString, string sourceId)
    {
        _sourceId = sourceId;
        _db = new SqliteConnection(connectionString);
        _db.Open();
        using (var pragma = _db.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }
        _tx = _db.BeginTransaction();

        using (var delete = _db.CreateCommand())
        {
            delete.Transaction = _tx;
            delete.CommandText = """
                DELETE FROM programmes_fts WHERE rowid IN (SELECT rowid FROM programmes WHERE source_id = $source);
                DELETE FROM programmes WHERE source_id = $source;
                """;
            delete.Parameters.AddWithValue("$source", sourceId);
            delete.ExecuteNonQuery();
        }

        _insert = _db.CreateCommand();
        _insert.Transaction = _tx;
        _insert.CommandText = """
            INSERT INTO programmes (source_id, channel_id, start, stop, title, description)
            VALUES ($source, $channel, $start, $stop, $title, $desc)
            RETURNING rowid;
            """;
        _insert.Parameters.AddWithValue("$source", sourceId);
        _channel = _insert.Parameters.Add("$channel", SqliteType.Text);
        _start = _insert.Parameters.Add("$start", SqliteType.Integer);
        _stop = _insert.Parameters.Add("$stop", SqliteType.Integer);
        _title = _insert.Parameters.Add("$title", SqliteType.Text);
        _desc = _insert.Parameters.Add("$desc", SqliteType.Text);

        _ftsInsert = _db.CreateCommand();
        _ftsInsert.Transaction = _tx;
        _ftsInsert.CommandText = "INSERT INTO programmes_fts (rowid, title, description) VALUES ($rowid, $title, $desc)";
        _ftsRow = _ftsInsert.Parameters.Add("$rowid", SqliteType.Integer);
        _ftsTitle = _ftsInsert.Parameters.Add("$title", SqliteType.Text);
        _ftsDesc = _ftsInsert.Parameters.Add("$desc", SqliteType.Text);

        _setEpgId = _db.CreateCommand();
        _setEpgId.Transaction = _tx;
        _setEpgId.CommandText = "UPDATE items SET epg_id = $epg WHERE source_id = $source AND external_id = $ext AND kind = $kind";
        _setEpgId.Parameters.AddWithValue("$source", sourceId);
        _setEpgId.Parameters.AddWithValue("$kind", (int)MediaKind.Live);
        _epgId = _setEpgId.Parameters.Add("$epg", SqliteType.Text);
        _externalId = _setEpgId.Parameters.Add("$ext", SqliteType.Text);
    }

    public void Add(Programme p)
    {
        _channel.Value = p.ChannelId;
        _start.Value = p.Start.ToUnixTimeSeconds();
        _stop.Value = p.Stop.ToUnixTimeSeconds();
        _title.Value = p.Title;
        _desc.Value = (object?)p.Description ?? DBNull.Value;
        var rowid = (long)_insert.ExecuteScalar()!;

        // Indexed so live events ("man utd v city", "NBA") can be searched across the guide.
        _ftsRow.Value = rowid;
        _ftsTitle.Value = p.Title;
        _ftsDesc.Value = p.Description ?? "";
        _ftsInsert.ExecuteNonQuery();
        Count++;
    }

    /// <summary>Records which guide channel a live item matched, so lookups use it from now on.</summary>
    public void SetEpgChannel(string externalId, string channelId)
    {
        _epgId.Value = channelId;
        _externalId.Value = externalId;
        _setEpgId.ExecuteNonQuery();
    }

    public void Commit() => _tx.Commit();

    public void Dispose()
    {
        _insert.Dispose();
        _ftsInsert.Dispose();
        _setEpgId.Dispose();
        _tx.Dispose();
        _db.Dispose();
    }

    public override string ToString() => $"Guide import for {_sourceId}: {Count} programmes";
}
