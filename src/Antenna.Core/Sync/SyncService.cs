using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Antenna.Core.Library;

namespace Antenna.Core.Sync;

/// <summary>Where the shared sync file lives: a folder on Windows, a document the user picked on Android.</summary>
public interface ISyncTarget
{
    /// <summary>The file's bytes, or null when it doesn't exist yet.</summary>
    Task<byte[]?> ReadAsync(CancellationToken ct = default);

    Task WriteAsync(byte[] data, CancellationToken ct = default);
}

/// <summary>A sync file at a path, e.g. inside the Google Drive or OneDrive folder on a PC.</summary>
public sealed class FileSyncTarget(string path) : ISyncTarget
{
    public const string FileName = "Antenna sync.antenna";

    public string Path => path;

    public async Task<byte[]?> ReadAsync(CancellationToken ct = default) =>
        File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;

    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Write then swap, so a cloud client never uploads half a file.
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class SyncPassphraseException() : Exception("The sync passphrase doesn't match the one the sync file was made with.");

/// <summary>What a sync changed on this device.</summary>
public sealed record SyncOutcome(IReadOnlyList<PlaylistSource> NewSources, int Changes, bool Wrote);

/// <summary>
/// Keeps playlists, favourites and watch history (resume points) the same on every device through one encrypted
/// file the user keeps in their own cloud storage. Each sync is a three-way merge of this device, the file, and
/// what both looked like after the last sync (kept on the device as hashes, never secrets), so additions,
/// edits and deletions on either side all carry over. Where both sides changed the same playlist, this device wins.
/// </summary>
public static class SyncService
{
    /// <summary>The store setting holding the last sync's state.</summary>
    public const string BaseSetting = "sync.base";

    private const int HistoryKept = 500;
    private static readonly byte[] Magic = "ANTSYNC1"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed record SyncSource(string Id, string Name, SourceKind Kind, string Location, string? Username, string? Password,
        string? EpgUrl, string Color, string Icon);

    public sealed record SyncFavourite(string SourceId, string ExternalId, MediaKind Kind);

    public sealed record SyncWatch(string SourceId, string ExternalId, MediaKind Kind, string Title, string StreamUrl, string? LogoUrl,
        string? PlayerOptions, long PositionMs, long LengthMs, DateTimeOffset WatchedAt);

    public sealed record SyncData
    {
        public int Version { get; init; } = 1;
        public DateTimeOffset Updated { get; init; }
        public string Device { get; init; } = "";
        public List<SyncSource> Sources { get; init; } = [];
        public List<SyncFavourite> Favourites { get; init; } = [];
        public List<SyncWatch> History { get; init; } = [];
    }

    /// <summary>The state after the last sync: playlist hashes and favourite keys.</summary>
    internal sealed record SyncBase
    {
        public Dictionary<string, string> Sources { get; init; } = [];
        public List<string> Favourites { get; init; } = [];
    }

    public static async Task<SyncOutcome> SyncAsync(LibraryStore store, ISyncTarget target, string passphrase, string device,
        CancellationToken ct = default)
    {
        var bytes = await target.ReadAsync(ct).ConfigureAwait(false);
        var remote = bytes is null ? null : JsonSerializer.Deserialize<SyncData>(Open(bytes, passphrase), Json);
        if (remote?.Version > 1)
            throw new InvalidDataException("The sync file was made by a newer version of Antenna. Update this one first.");

        var (merged, outcome) = Merge(store, remote, device);
        var changed = remote is null || !SameContent(merged, remote);
        if (changed)
            await target.WriteAsync(Seal(JsonSerializer.Serialize(merged, Json), passphrase), ct).ConfigureAwait(false);
        SaveBase(store, merged);
        return outcome with { Wrote = changed };
    }

    // ---- Merging ----

    internal static (SyncData Merged, SyncOutcome Outcome) Merge(LibraryStore store, SyncData? remote, string device)
    {
        var last = LoadBase(store);
        var changes = 0;
        var added = new List<PlaylistSource>();

        // Playlists: three-way, by id.
        var local = store.GetSources().ToDictionary(s => s.Id);
        var theirs = (remote?.Sources ?? []).ToDictionary(s => s.Id);
        var sources = new List<SyncSource>();
        foreach (var id in local.Keys.Union(theirs.Keys).Union(last.Sources.Keys).ToList())
        {
            var mine = local.TryGetValue(id, out var l) ? ToSync(l) : null;
            var other = theirs.GetValueOrDefault(id);
            var known = last.Sources.TryGetValue(id, out var hash);
            if (known && (mine is null || other is null))
            {
                // Deleted on one side since the last sync: delete it everywhere.
                if (mine is not null)
                {
                    store.DeleteSource(id);
                    changes++;
                }
                continue;
            }
            if (mine is not null && other is not null)
            {
                var keepMine = !known || Hash(mine) != hash;
                var pick = keepMine ? mine : other;
                if (pick != mine)
                {
                    store.SaveSource(FromSync(other, l!.LastRefreshed));
                    changes++;
                }
                sources.Add(pick);
            }
            else if (mine is not null)
            {
                sources.Add(mine);
            }
            else if (other is not null)
            {
                var source = FromSync(other, null);
                store.SaveSource(source);
                added.Add(source);
                changes++;
                sources.Add(other);
            }
        }
        var kept = sources.Select(s => s.Id).ToHashSet();

        // Favourites: three-way on the set of keys.
        var myFavs = store.GetFavouriteKeys().Select(f => new SyncFavourite(f.SourceId, f.ExternalId, f.Kind)).ToHashSet();
        var theirFavs = (remote?.Favourites ?? []).ToHashSet();
        var lastFavs = last.Favourites.ToHashSet();
        var favourites = new List<SyncFavourite>();
        foreach (var f in myFavs.Union(theirFavs).Union(lastFavs.Select(ParseKey).OfType<SyncFavourite>()).ToList())
        {
            if (!kept.Contains(f.SourceId))
                continue;
            bool mine = myFavs.Contains(f), other = theirFavs.Contains(f), known = lastFavs.Contains(Key(f));
            var keep = known ? mine && other : mine || other;
            if (keep != mine)
            {
                store.SetFavourite(new MediaItem { SourceId = f.SourceId, ExternalId = f.ExternalId, Kind = f.Kind, Title = "" }, keep);
                changes++;
            }
            if (keep)
                favourites.Add(f);
        }

        // History: the latest of each, so resume points follow you.
        var history = new Dictionary<(string, string), SyncWatch>();
        foreach (var w in store.GetHistory(HistoryKept).Select(ToSync))
            history[(w.SourceId, w.ExternalId)] = w;
        foreach (var w in remote?.History ?? [])
        {
            if (!kept.Contains(w.SourceId))
                continue;
            var k = (w.SourceId, w.ExternalId);
            if (history.TryGetValue(k, out var mine) && mine.WatchedAt >= w.WatchedAt)
                continue;
            history[k] = w;
            store.RecordWatch(FromSync(w));
            changes++;
        }

        var merged = new SyncData
        {
            Updated = DateTimeOffset.UtcNow,
            Device = device,
            Sources = sources,
            Favourites = favourites,
            History = [.. history.Values.Where(w => kept.Contains(w.SourceId)).OrderByDescending(w => w.WatchedAt).Take(HistoryKept)],
        };
        return (merged, new SyncOutcome(added, changes, Wrote: false));
    }

    private static bool SameContent(SyncData a, SyncData b) =>
        JsonSerializer.Serialize(a with { Updated = default, Device = "" }, Json) == JsonSerializer.Serialize(b with { Updated = default, Device = "" }, Json);

    private static SyncBase LoadBase(LibraryStore store)
    {
        var json = store.GetSetting(BaseSetting);
        if (string.IsNullOrEmpty(json))
            return new SyncBase();
        try
        {
            return JsonSerializer.Deserialize<SyncBase>(json, Json) ?? new SyncBase();
        }
        catch (JsonException)
        {
            return new SyncBase();
        }
    }

    private static void SaveBase(LibraryStore store, SyncData merged) =>
        store.SetSetting(BaseSetting, JsonSerializer.Serialize(new SyncBase
        {
            Sources = merged.Sources.ToDictionary(s => s.Id, Hash),
            Favourites = [.. merged.Favourites.Select(Key)],
        }, Json));

    /// <summary>Forgets the last sync, e.g. when switching to another sync file: the next sync only adds.</summary>
    public static void ResetBase(LibraryStore store) => store.SetSetting(BaseSetting, null);

    private static string Hash(SyncSource s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s, Json))));

    private static string Key(SyncFavourite f) => $"{(int)f.Kind}|{f.SourceId}|{f.ExternalId}";

    private static SyncFavourite? ParseKey(string key)
    {
        var parts = key.Split('|', 3);
        return parts.Length == 3 && int.TryParse(parts[0], out var kind) ? new SyncFavourite(parts[1], parts[2], (MediaKind)kind) : null;
    }

    private static SyncSource ToSync(PlaylistSource s) =>
        new(s.Id, s.Name, s.Kind, s.Location, s.Username, s.Password, s.EpgUrl, s.Color, s.Icon);

    private static PlaylistSource FromSync(SyncSource s, DateTimeOffset? lastRefreshed) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Kind = s.Kind,
        Location = s.Location,
        Username = s.Username,
        Password = s.Password,
        EpgUrl = s.EpgUrl,
        Color = s.Color,
        Icon = s.Icon,
        LastRefreshed = lastRefreshed,
    };

    private static SyncWatch ToSync(WatchRecord w) =>
        new(w.SourceId, w.ExternalId, w.Kind, w.Title, w.StreamUrl, w.LogoUrl, w.PlayerOptions, w.PositionMs, w.LengthMs, w.WatchedAt);

    private static WatchRecord FromSync(SyncWatch w) => new()
    {
        SourceId = w.SourceId,
        ExternalId = w.ExternalId,
        Kind = w.Kind,
        Title = w.Title,
        StreamUrl = w.StreamUrl,
        LogoUrl = w.LogoUrl,
        PlayerOptions = w.PlayerOptions,
        PositionMs = w.PositionMs,
        LengthMs = w.LengthMs,
        WatchedAt = w.WatchedAt,
    };

    // ---- Encryption ----

    /// <summary>AES-256-GCM with a key derived from the passphrase (PBKDF2-SHA256): magic, salt, nonce, tag, ciphertext.</summary>
    public static byte[] Seal(string json, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(json);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(DeriveKey(passphrase, salt), tag.Length))
            aes.Encrypt(nonce, plain, cipher, tag);
        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }

    public static string Open(byte[] data, string passphrase)
    {
        if (data.Length < Magic.Length + 44 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("That file isn't an Antenna sync file.");
        var at = Magic.Length;
        var salt = data[at..(at + 16)];
        var nonce = data[(at + 16)..(at + 28)];
        var tag = data[(at + 28)..(at + 44)];
        var cipher = data[(at + 44)..];
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(DeriveKey(passphrase, salt), tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new SyncPassphraseException();
        }
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 200_000, HashAlgorithmName.SHA256, 32);
}
