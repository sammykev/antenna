using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Antenna.Core.Metadata;

public sealed class TmdbException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Looks up posters, backdrops, plot, rating and cast on The Movie Database (themoviedb.org), for
/// playlists that don't provide them. Accepts either a v3 API key or a v4 read access token.
/// </summary>
public sealed class TmdbClient(HttpClient http, string apiKey)
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string Images = "https://image.tmdb.org/t/p/";

    private readonly Dictionary<string, MediaDetails?> _cache = [];

    /// <summary>Details for the best match of an IPTV movie or series name, or null when nothing matches.</summary>
    public async Task<MediaDetails?> FindAsync(string name, MediaKind kind, CancellationToken ct = default)
    {
        var (title, year) = TitleCleaner.Parse(name);
        if (title.Length == 0)
            return null;
        var key = $"{kind}|{title.ToLowerInvariant()}|{year}";
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var tv = kind == MediaKind.Series;
        var query = $"query={Uri.EscapeDataString(title)}&include_adult=false";
        if (year is { } y)
            query += tv ? $"&first_air_date_year={y}" : $"&year={y}";
        using var search = await GetAsync($"/search/{(tv ? "tv" : "movie")}?{query}", ct);
        var first = search.RootElement.GetProperty("results").EnumerateArray().FirstOrDefault();
        MediaDetails? details = null;
        if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var id))
        {
            using var doc = await GetAsync($"/{(tv ? "tv" : "movie")}/{id.GetInt32()}?append_to_response=credits", ct);
            details = Map(doc.RootElement, tv);
        }
        lock (_cache)
            _cache[key] = details;
        return details;
    }

    internal static MediaDetails Map(JsonElement e, bool tv)
    {
        string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

        var genres = e.TryGetProperty("genres", out var g) ? string.Join(", ", g.EnumerateArray().Select(x => x.GetProperty("name").GetString())) : null;
        var rating = e.TryGetProperty("vote_average", out var r) && r.TryGetDouble(out var score) && score > 0
            ? score.ToString("0.0", CultureInfo.InvariantCulture)
            : null;
        int? minutes = tv
            ? e.TryGetProperty("episode_run_time", out var ert) && ert.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Number } m ? m.GetInt32() : null
            : e.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : null;

        string? cast = null;
        string? director = null;
        if (e.TryGetProperty("credits", out var credits))
        {
            if (credits.TryGetProperty("cast", out var c))
                cast = string.Join(", ", c.EnumerateArray().Take(6).Select(x => x.GetProperty("name").GetString()));
            if (credits.TryGetProperty("crew", out var crew))
                director = crew.EnumerateArray().FirstOrDefault(x => x.TryGetProperty("job", out var job) && job.GetString() == "Director") is { ValueKind: JsonValueKind.Object } d
                    ? d.GetProperty("name").GetString()
                    : null;
        }
        if (tv && director is null && e.TryGetProperty("created_by", out var creators))
            director = string.Join(", ", creators.EnumerateArray().Select(x => x.GetProperty("name").GetString())) is { Length: > 0 } names ? names : null;

        return new MediaDetails
        {
            Plot = Str("overview"),
            Genre = string.IsNullOrEmpty(genres) ? null : genres,
            Released = Str(tv ? "first_air_date" : "release_date"),
            Rating = rating,
            Duration = minutes is > 0 ? (minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m") : null,
            Cast = string.IsNullOrEmpty(cast) ? null : cast,
            Director = director,
            PosterUrl = Str("poster_path") is { } poster ? Images + "w500" + poster : null,
            BackdropUrl = Str("backdrop_path") is { } backdrop ? Images + "w1280" + backdrop : null,
        };
    }

    /// <summary>Fills what <paramref name="primary"/> is missing from <paramref name="fallback"/>.</summary>
    public static MediaDetails? Merge(MediaDetails? primary, MediaDetails? fallback) =>
        (primary, fallback) switch
        {
            (null, _) => fallback,
            (_, null) => primary,
            _ => new MediaDetails
            {
                Plot = primary.Plot ?? fallback.Plot,
                Cast = primary.Cast ?? fallback.Cast,
                Director = primary.Director ?? fallback.Director,
                Genre = primary.Genre ?? fallback.Genre,
                Released = primary.Released ?? fallback.Released,
                Rating = primary.Rating ?? fallback.Rating,
                Duration = primary.Duration ?? fallback.Duration,
                PosterUrl = primary.PosterUrl ?? fallback.PosterUrl,
                BackdropUrl = primary.BackdropUrl ?? fallback.BackdropUrl,
            },
        };

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        var bearer = apiKey.StartsWith("eyJ", StringComparison.Ordinal); // v4 read access tokens are JWTs.
        var url = BaseUrl + path + (bearer ? "" : $"&api_key={Uri.EscapeDataString(apiKey)}");
        if (!path.Contains('?'))
            url = url.Replace("&api_key=", "?api_key=");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new TmdbException("TMDB didn't accept the API key. Check it in Settings.");
            response.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TmdbException($"Couldn't reach TMDB: {ex.Message}", ex);
        }
    }
}
