using System.Globalization;
using System.Text.Json;

namespace Antenna.Core.Playlists;

public sealed record XtreamAccount(string Status, DateTimeOffset? ExpiresAt, int MaxConnections, int ActiveConnections);

public sealed class XtreamException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Client for the Xtream Codes "player_api.php" API that most IPTV panels expose.
/// Provider responses are loosely typed (numbers often arrive as strings, empty
/// objects as arrays), so all field access goes through tolerant helpers.
/// </summary>
public sealed class XtreamClient
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _user;
    private readonly string _pass;

    public XtreamClient(HttpClient http, string serverUrl, string username, string password)
    {
        _http = http;
        _base = NormalizeServer(serverUrl);
        _user = username;
        _pass = password;
    }

    public static XtreamClient For(HttpClient http, PlaylistSource source)
    {
        if (source.Kind != SourceKind.Xtream)
            throw new ArgumentException("Source is not an Xtream account.", nameof(source));
        return new XtreamClient(http, source.Location, source.Username ?? "", source.Password ?? "");
    }

    /// <summary>Accepts "host:port", "http://host:port/", or a pasted player_api.php / get.php URL.</summary>
    public static string NormalizeServer(string serverUrl)
    {
        var s = serverUrl.Trim();
        if (!s.Contains("://", StringComparison.Ordinal))
            s = "http://" + s;
        var uri = new Uri(s);
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public string XmltvUrl => $"{_base}/xmltv.php?username={Esc(_user)}&password={Esc(_pass)}";

    public string LiveUrl(string streamId, string extension = "ts") => $"{_base}/live/{Esc(_user)}/{Esc(_pass)}/{streamId}.{extension}";

    public string MovieUrl(string streamId, string extension) => $"{_base}/movie/{Esc(_user)}/{Esc(_pass)}/{streamId}.{extension}";

    public string EpisodeUrl(string episodeId, string extension) => $"{_base}/series/{Esc(_user)}/{Esc(_pass)}/{episodeId}.{extension}";

    /// <summary>Catch-up URL for a programme that started at <paramref name="start"/> (provider local time).</summary>
    public string CatchupUrl(string streamId, DateTime start, TimeSpan duration) =>
        $"{_base}/timeshift/{Esc(_user)}/{Esc(_pass)}/{(int)Math.Ceiling(duration.TotalMinutes)}/" +
        $"{start.ToString("yyyy-MM-dd:HH-mm", CultureInfo.InvariantCulture)}/{streamId}.ts";

    public async Task<XtreamAccount> AuthenticateAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync(null, ct);
        if (!doc.RootElement.TryGetProperty("user_info", out var info) || info.ValueKind != JsonValueKind.Object)
            throw new XtreamException("The server did not return account information. Check the server URL.");

        if (Int(info, "auth") == 0)
            throw new XtreamException("The server rejected the username or password.");

        var exp = Long(info, "exp_date");
        return new XtreamAccount(
            Str(info, "status") ?? "Unknown",
            exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp) : null,
            Int(info, "max_connections"),
            Int(info, "active_cons"));
    }

    /// <summary>Downloads the full live, VOD and series catalogue as library items.</summary>
    public async Task<IReadOnlyList<MediaItem>> GetCatalogAsync(string sourceId, CancellationToken ct = default)
    {
        var liveCats = GetCategoriesAsync("get_live_categories", ct);
        var vodCats = GetCategoriesAsync("get_vod_categories", ct);
        var seriesCats = GetCategoriesAsync("get_series_categories", ct);
        var live = GetArrayAsync("get_live_streams", ct);
        var vod = GetArrayAsync("get_vod_streams", ct);
        var series = GetArrayAsync("get_series", ct);
        await Task.WhenAll(liveCats, vodCats, seriesCats, live, vod, series);

        var items = new List<MediaItem>();

        foreach (var s in live.Result)
        {
            var id = Str(s, "stream_id");
            if (id is null) continue;
            var archive = Int(s, "tv_archive") == 1 ? Int(s, "tv_archive_duration") : 0;
            items.Add(new MediaItem
            {
                SourceId = sourceId,
                ExternalId = id,
                Kind = MediaKind.Live,
                Title = Str(s, "name") ?? id,
                Group = liveCats.Result.GetValueOrDefault(Str(s, "category_id") ?? "", ""),
                LogoUrl = NullIfEmpty(Str(s, "stream_icon")),
                StreamUrl = LiveUrl(id),
                EpgChannelId = NullIfEmpty(Str(s, "epg_channel_id")),
                CatchupDays = archive,
                SortIndex = items.Count,
            });
        }

        foreach (var s in vod.Result)
        {
            var id = Str(s, "stream_id");
            if (id is null) continue;
            items.Add(new MediaItem
            {
                SourceId = sourceId,
                ExternalId = id,
                Kind = MediaKind.Movie,
                Title = Str(s, "name") ?? id,
                Group = vodCats.Result.GetValueOrDefault(Str(s, "category_id") ?? "", ""),
                LogoUrl = NullIfEmpty(Str(s, "stream_icon")),
                StreamUrl = MovieUrl(id, Str(s, "container_extension") ?? "mp4"),
                SortIndex = items.Count,
            });
        }

        foreach (var s in series.Result)
        {
            var id = Str(s, "series_id");
            if (id is null) continue;
            items.Add(new MediaItem
            {
                SourceId = sourceId,
                ExternalId = id,
                Kind = MediaKind.Series,
                Title = Str(s, "name") ?? id,
                Group = seriesCats.Result.GetValueOrDefault(Str(s, "category_id") ?? "", ""),
                LogoUrl = NullIfEmpty(Str(s, "cover")),
                SortIndex = items.Count,
            });
        }

        return items;
    }

    public async Task<IReadOnlyList<Episode>> GetEpisodesAsync(string seriesId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"get_series_info&series_id={Esc(seriesId)}", ct);
        if (!doc.RootElement.TryGetProperty("episodes", out var episodes))
            return [];

        // "episodes" is normally {"1": [...], "2": [...]}, but some panels send a flat array.
        IEnumerable<JsonElement> all = episodes.ValueKind switch
        {
            JsonValueKind.Object => episodes.EnumerateObject().SelectMany(p => Flatten(p.Value)),
            JsonValueKind.Array => episodes.EnumerateArray().SelectMany(Flatten),
            _ => [],
        };

        var result = new List<Episode>();
        foreach (var e in all)
        {
            var id = Str(e, "id");
            if (id is null) continue;
            var info = e.TryGetProperty("info", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
            result.Add(new Episode
            {
                Id = id,
                Season = Int(e, "season"),
                Number = Int(e, "episode_num"),
                Title = Str(e, "title") ?? $"Episode {Int(e, "episode_num")}",
                StreamUrl = EpisodeUrl(id, Str(e, "container_extension") ?? "mp4"),
                Plot = info.ValueKind == JsonValueKind.Object ? NullIfEmpty(Str(info, "plot")) : null,
                ImageUrl = info.ValueKind == JsonValueKind.Object ? NullIfEmpty(Str(info, "movie_image")) : null,
            });
        }
        return result.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList();
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [e];

    private async Task<Dictionary<string, string>> GetCategoriesAsync(string action, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        foreach (var c in await GetArrayAsync(action, ct))
        {
            var id = Str(c, "category_id");
            if (id is not null)
                map[id] = Str(c, "category_name") ?? "";
        }
        return map;
    }

    private async Task<List<JsonElement>> GetArrayAsync(string action, CancellationToken ct)
    {
        using var doc = await GetAsync(action, ct);
        // Clone so elements outlive the JsonDocument.
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
            : [];
    }

    private async Task<JsonDocument> GetAsync(string? action, CancellationToken ct)
    {
        var url = $"{_base}/player_api.php?username={Esc(_user)}&password={Esc(_pass)}";
        if (action is not null)
            url += "&action=" + action;

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            throw new XtreamException($"Could not reach the Xtream server: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new XtreamException("The server returned something that is not Xtream JSON. Check the server URL.", ex);
        }
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    internal static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null,
        };
    }

    internal static long Long(JsonElement e, string name) =>
        long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
        : double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d
        : 0;

    internal static int Int(JsonElement e, string name) => (int)Math.Clamp(Long(e, name), int.MinValue, int.MaxValue);
}
