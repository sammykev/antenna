using System.Globalization;
using System.Text.Json;

namespace Antenna.Core.Playlists;

/// <param name="ServerUtcOffset">Offset of the panel's clock from UTC; timeshift URLs use server-local time.</param>
public sealed record XtreamAccount(string Status, DateTimeOffset? ExpiresAt, int MaxConnections, int ActiveConnections, TimeSpan ServerUtcOffset);

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
        // Pasted logins often carry a stray space or line break, which panels count as part of the name.
        _user = username.Trim();
        _pass = password.Trim();
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

    /// <summary>Catch-up URL for a programme, converting its start into the server's local time.</summary>
    public string CatchupUrl(string streamId, DateTimeOffset start, TimeSpan duration, TimeSpan serverUtcOffset) =>
        CatchupUrl(streamId, start.ToOffset(serverUtcOffset).DateTime, duration);

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
        var server = doc.RootElement.TryGetProperty("server_info", out var si) ? si : default;
        return new XtreamAccount(
            Str(info, "status") ?? "Unknown",
            exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp) : null,
            Int(info, "max_connections"),
            Int(info, "active_cons"),
            ServerOffset(server));
    }

    /// <summary>
    /// Works out the panel's UTC offset. Comparing its local "time_now" with the
    /// "timestamp_now" epoch is the most reliable; the timezone name is the fallback.
    /// </summary>
    internal static TimeSpan ServerOffset(JsonElement serverInfo)
    {
        if (serverInfo.ValueKind != JsonValueKind.Object)
            return TimeSpan.Zero;

        var epoch = Long(serverInfo, "timestamp_now");
        if (epoch > 0 && DateTime.TryParseExact(Str(serverInfo, "time_now"), "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            var diff = local - DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            // Round to the nearest 15 minutes to absorb the gap between the two fields.
            var quarters = Math.Round(diff.TotalMinutes / 15);
            if (Math.Abs(quarters) <= 14 * 4)
                return TimeSpan.FromMinutes(quarters * 15);
        }

        if (Str(serverInfo, "timezone") is { Length: > 0 } tz)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(tz).GetUtcOffset(DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeSpan.Zero;
    }

    /// <summary>Downloads the full live, VOD and series catalogue as library items.</summary>
    public async Task<IReadOnlyList<MediaItem>> GetCatalogAsync(string sourceId, CancellationToken ct = default)
    {
        var live = GetLiveAsync(sourceId, ct);
        var movies = GetMoviesAsync(sourceId, ct);
        var series = GetSeriesAsync(sourceId, ct);
        await Task.WhenAll(live, movies, series);
        return [.. live.Result, .. movies.Result, .. series.Result];
    }

    public async Task<IReadOnlyList<MediaItem>> GetLiveAsync(string sourceId, CancellationToken ct = default)
    {
        var cats = GetCategoriesAsync("get_live_categories", ct);
        var streams = GetArrayAsync("get_live_streams", ct);
        await Task.WhenAll(cats, streams);
        var items = new List<MediaItem>();
        foreach (var s in streams.Result)
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
                Group = cats.Result.GetValueOrDefault(Str(s, "category_id") ?? "", ""),
                LogoUrl = NullIfEmpty(Str(s, "stream_icon")),
                StreamUrl = LiveUrl(id),
                EpgChannelId = NullIfEmpty(Str(s, "epg_channel_id")),
                CatchupDays = archive,
                SortIndex = items.Count,
            });
        }
        return items;
    }

    public async Task<IReadOnlyList<MediaItem>> GetMoviesAsync(string sourceId, CancellationToken ct = default)
    {
        var cats = GetCategoriesAsync("get_vod_categories", ct);
        var streams = GetArrayAsync("get_vod_streams", ct);
        await Task.WhenAll(cats, streams);
        var items = new List<MediaItem>();
        foreach (var s in streams.Result)
        {
            var id = Str(s, "stream_id");
            if (id is null) continue;
            items.Add(new MediaItem
            {
                SourceId = sourceId,
                ExternalId = id,
                Kind = MediaKind.Movie,
                Title = Str(s, "name") ?? id,
                Group = cats.Result.GetValueOrDefault(Str(s, "category_id") ?? "", ""),
                LogoUrl = NullIfEmpty(Str(s, "stream_icon")),
                StreamUrl = MovieUrl(id, Str(s, "container_extension") ?? "mp4"),
                SortIndex = items.Count,
            });
        }
        return items;
    }

    public async Task<IReadOnlyList<MediaItem>> GetSeriesAsync(string sourceId, CancellationToken ct = default)
    {
        var cats = GetCategoriesAsync("get_series_categories", ct);
        var list = GetArrayAsync("get_series", ct);
        await Task.WhenAll(cats, list);
        var items = new List<MediaItem>();
        foreach (var s in list.Result)
        {
            var id = Str(s, "series_id");
            if (id is null) continue;
            items.Add(new MediaItem
            {
                SourceId = sourceId,
                ExternalId = id,
                Kind = MediaKind.Series,
                Title = Str(s, "name") ?? id,
                Group = cats.Result.GetValueOrDefault(Str(s, "category_id") ?? "", ""),
                LogoUrl = NullIfEmpty(Str(s, "cover")),
                SortIndex = items.Count,
            });
        }
        return items;
    }

    public async Task<IReadOnlyList<Episode>> GetEpisodesAsync(string seriesId, CancellationToken ct = default) =>
        (await GetSeriesInfoAsync(seriesId, ct)).Episodes;

    /// <summary>Series details (plot, cast, artwork) and its episodes, from one request.</summary>
    public async Task<(MediaDetails Details, IReadOnlyList<Episode> Episodes)> GetSeriesInfoAsync(string seriesId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"get_series_info&series_id={Esc(seriesId)}", ct);
        var details = doc.RootElement.TryGetProperty("info", out var info) ? ReadDetails(info, "cover") : new MediaDetails();
        return (details, ReadEpisodes(doc.RootElement));
    }

    /// <summary>Movie details: plot, cast, director, rating, poster and backdrop.</summary>
    public async Task<MediaDetails> GetVodInfoAsync(string streamId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"get_vod_info&vod_id={Esc(streamId)}", ct);
        return doc.RootElement.TryGetProperty("info", out var info) ? ReadDetails(info, "movie_image") : new MediaDetails();
    }

    internal static MediaDetails ReadDetails(JsonElement info, string posterField)
    {
        if (info.ValueKind != JsonValueKind.Object)
            return new MediaDetails();

        // backdrop_path is usually an array of URLs, sometimes a single string.
        string? backdrop = null;
        if (info.TryGetProperty("backdrop_path", out var b))
        {
            backdrop = b.ValueKind switch
            {
                JsonValueKind.Array => b.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                JsonValueKind.String => b.GetString(),
                _ => null,
            };
        }

        return new MediaDetails
        {
            Plot = NullIfEmpty(Str(info, "plot") ?? Str(info, "description")),
            Cast = NullIfEmpty(Str(info, "cast") ?? Str(info, "actors")),
            Director = NullIfEmpty(Str(info, "director")),
            Genre = NullIfEmpty(Str(info, "genre")),
            Released = NullIfEmpty(Str(info, "releasedate") ?? Str(info, "releaseDate") ?? Str(info, "release_date")),
            Rating = NullIfEmpty(Str(info, "rating")) is { } r && r != "0" ? r : null,
            Duration = NullIfEmpty(Str(info, "duration") ?? Str(info, "episode_run_time")),
            PosterUrl = NullIfEmpty(Str(info, posterField) ?? Str(info, "cover_big")),
            BackdropUrl = NullIfEmpty(backdrop),
        };
    }

    private IReadOnlyList<Episode> ReadEpisodes(JsonElement root)
    {
        if (!root.TryGetProperty("episodes", out var episodes))
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
            if (!response.IsSuccessStatusCode)
                throw Failure((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
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

    /// <summary>
    /// Panels say why they refused in the body ({"error":{"message":"`x` login username doesn't exist"}}),
    /// so that is shown rather than a bare status code.
    /// </summary>
    internal static XtreamException Failure(int code, string? body)
    {
        string? reason = null;
        var authZero = false;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "null" : body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    reason = m.GetString();
                else if (root.TryGetProperty("message", out var m2) && m2.ValueKind == JsonValueKind.String)
                    reason = m2.GetString();
                authZero = root.TryGetProperty("user_info", out var info) && info.ValueKind == JsonValueKind.Object
                           && info.TryGetProperty("auth", out var auth) && auth.ToString() == "0";
            }
        }
        catch (JsonException)
        {
        }
        var refused = code is 401 or 403 || authZero;
        return new XtreamException((refused, string.IsNullOrWhiteSpace(reason)) switch
        {
            (true, false) => $"The server refused the login: {reason!.Replace("`", "", StringComparison.Ordinal)}. Check the username and password.",
            (true, true) => "The server refused the login. Check the username and password, and that the subscription is still active.",
            (false, false) => $"The Xtream server answered {code}: {reason}",
            _ => $"The Xtream server answered {code}.",
        });
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
