using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Antenna.Core.Subtitles;

public sealed record SubtitleResult(long FileId, string Release, string Language, int Downloads, bool HearingImpaired);

public sealed class OpenSubtitlesException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Minimal client for the OpenSubtitles.com REST API (v1). Needs a free API key from
/// opensubtitles.com/consumers; an account login raises the daily download limit.
/// </summary>
public sealed class OpenSubtitlesClient(HttpClient http, string apiKey)
{
    private const string BaseUrl = "https://api.opensubtitles.com/api/v1";
    private string? _token;

    public async Task LoginAsync(string username, string password, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, "/login");
        request.Content = JsonContent.Create(new { username, password });
        using var doc = await SendAsync(request, ct);
        _token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (_token is null)
            throw new OpenSubtitlesException("OpenSubtitles didn't accept the login.");
    }

    /// <param name="languages">Comma-separated ISO 639-1 codes, e.g. "en" or "en,fr".</param>
    public async Task<IReadOnlyList<SubtitleResult>> SearchAsync(
        string query, string languages, int? season = null, int? episode = null, CancellationToken ct = default)
    {
        // The API wants parameters sorted alphabetically and lower-case values to avoid redirects.
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["languages"] = languages.ToLowerInvariant(),
            ["query"] = query.Trim().ToLowerInvariant(),
        };
        if (season is > 0) parameters["season_number"] = season.Value.ToString();
        if (episode is > 0) parameters["episode_number"] = episode.Value.ToString();
        var qs = string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        using var request = Request(HttpMethod.Get, "/subtitles?" + qs);
        using var doc = await SendAsync(request, ct);

        var results = new List<SubtitleResult>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("attributes", out var a))
                continue;
            if (!a.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                continue;
            var file = files.EnumerateArray().FirstOrDefault();
            if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("file_id", out var id) || !id.TryGetInt64(out var fileId))
                continue;

            results.Add(new SubtitleResult(
                fileId,
                (a.TryGetProperty("release", out var rel) ? rel.GetString() : null)
                    ?? (file.TryGetProperty("file_name", out var fn) ? fn.GetString() : null) ?? "Subtitle",
                a.TryGetProperty("language", out var lang) ? lang.GetString() ?? "" : "",
                a.TryGetProperty("download_count", out var dc) && dc.TryGetInt32(out var n) ? n : 0,
                a.TryGetProperty("hearing_impaired", out var hi) && hi.ValueKind == JsonValueKind.True));
        }
        return results.OrderByDescending(r => r.Downloads).ToList();
    }

    /// <summary>Downloads a subtitle file into <paramref name="directory"/> and returns its path.</summary>
    public async Task<string> DownloadAsync(long fileId, string directory, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, "/download");
        request.Content = JsonContent.Create(new DownloadRequest(fileId));
        using var doc = await SendAsync(request, ct);

        var link = doc.RootElement.TryGetProperty("link", out var l) ? l.GetString() : null;
        if (link is null)
        {
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new OpenSubtitlesException(message ?? "OpenSubtitles didn't return a download link.");
        }
        var name = doc.RootElement.TryGetProperty("file_name", out var f) ? f.GetString() : null;
        name = string.Concat((name ?? $"{fileId}.srt").Split(Path.GetInvalidFileNameChars()));

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await using var stream = await http.GetStreamAsync(link, ct);
        await using var file = File.Create(path);
        await stream.CopyToAsync(file, ct);
        return path;
    }

    private sealed record DownloadRequest([property: JsonPropertyName("file_id")] long FileId);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Add("Api-Key", apiKey);
        request.Headers.UserAgent.ParseAdd("Antenna v0.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = TryMessage(body);
                throw new OpenSubtitlesException(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "OpenSubtitles rejected the API key or login." + (detail is null ? "" : $" ({detail})"),
                    (System.Net.HttpStatusCode)406 or System.Net.HttpStatusCode.TooManyRequests => detail ?? "OpenSubtitles download limit reached. Try again tomorrow or log in.",
                    _ => detail ?? $"OpenSubtitles returned {(int)response.StatusCode}.",
                });
            }
            return JsonDocument.Parse(body);
        }
        catch (HttpRequestException ex)
        {
            throw new OpenSubtitlesException($"Couldn't reach OpenSubtitles: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new OpenSubtitlesException("OpenSubtitles returned an unexpected response.", ex);
        }
    }

    private static string? TryMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
