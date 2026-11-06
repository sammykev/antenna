using System.Globalization;
using System.Text.Json;

namespace Antenna.Core.Metadata;

/// <summary>A film's scores: IMDb (out of 10, with its vote count), Rotten Tomatoes' Tomatometer and Metacritic (out of 100).</summary>
public sealed record FilmRating
{
    public double? Imdb { get; init; }
    public int? ImdbVotes { get; init; }
    public int? RottenTomatoes { get; init; }
    public int? Metascore { get; init; }
    public string? ImdbId { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>One number to rank by: the average of IMDb (×10) and Rotten Tomatoes where both are known.</summary>
    public double? Score => (Imdb, RottenTomatoes) switch
    {
        ({ } i, { } rt) => (i * 10 + rt) / 2,
        ({ } i, null) => i * 10,
        (null, { } rt) => rt,
        _ => null,
    };
}

/// <param name="Stop">True for a wrong key or the day's limit: no point asking again today.</param>
public sealed class OmdbException(string message, bool stop) : Exception(message)
{
    public bool Stop { get; } = stop;
}

/// <summary>
/// IMDb and Rotten Tomatoes ratings from OMDb (omdbapi.com), which needs a free key. Free keys allow 1,000 lookups
/// a day, so <see cref="RatingsService"/> keeps everything it learns and spreads lookups out.
/// </summary>
public sealed class OmdbClient(HttpClient http, string key)
{
    /// <summary>The film's ratings, or null when OMDb doesn't know it.</summary>
    public async Task<FilmRating?> FindAsync(string title, int? year, CancellationToken ct = default)
    {
        var url = $"https://www.omdbapi.com/?apikey={Uri.EscapeDataString(key.Trim())}&t={Uri.EscapeDataString(title)}&type=movie" +
                  (year is { } y ? $"&y={y.ToString(CultureInfo.InvariantCulture)}" : "");
        // OMDb answers a bad key with 401 and a JSON reason, so the body is read whatever the status.
        using var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            throw new OmdbException("OMDb sent something unexpected.", stop: false);
        }
    }

    public static FilmRating? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new OmdbException("OMDb sent something unexpected.", stop: false);
        if (string.Equals(Str(root, "Response"), "False", StringComparison.OrdinalIgnoreCase))
        {
            var error = Str(root, "Error") ?? "OMDb refused the request.";
            if (error.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return null;
            // "Invalid API key!", "Request limit reached!".
            throw new OmdbException(error, error.Contains("key", StringComparison.OrdinalIgnoreCase) || error.Contains("limit", StringComparison.OrdinalIgnoreCase));
        }

        string? tomatoes = null;
        if (root.TryGetProperty("Ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Array)
            foreach (var r in ratings.EnumerateArray())
                if (Str(r, "Source") == "Rotten Tomatoes")
                    tomatoes = Str(r, "Value");

        return new FilmRating
        {
            Imdb = double.TryParse(Str(root, "imdbRating"), NumberStyles.Float, CultureInfo.InvariantCulture, out var imdb) ? imdb : null,
            ImdbVotes = int.TryParse(Str(root, "imdbVotes")?.Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var votes) ? votes : null,
            RottenTomatoes = int.TryParse(tomatoes?.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rt) ? rt : null,
            Metascore = int.TryParse(Str(root, "Metascore"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meta) ? meta : null,
            ImdbId = Str(root, "imdbID"),
            Genres = (Str(root, "Genre") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(g => g != "N/A").ToList(),
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
