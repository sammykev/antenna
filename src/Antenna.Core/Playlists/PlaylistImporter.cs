namespace Antenna.Core.Playlists;

public sealed record ImportResult(IReadOnlyList<MediaItem> Items, string? EpgUrl);

/// <summary>Fetches a source's full catalogue, whatever kind of playlist it is.</summary>
public sealed class PlaylistImporter(HttpClient http)
{
    /// <param name="partLoaded">
    /// For Xtream accounts, called as each part of the catalogue arrives (live first, then movies and
    /// series), so it can be saved and shown before the rest has downloaded.
    /// </param>
    public async Task<ImportResult> ImportAsync(PlaylistSource source,
        Action<MediaKind, IReadOnlyList<MediaItem>>? partLoaded = null, CancellationToken ct = default)
    {
        switch (source.Kind)
        {
            case SourceKind.M3u:
            {
                var text = await ReadM3uAsync(source.Location, ct);
                var playlist = M3uParser.Parse(text, source.Id);
                return new ImportResult(playlist.Items, source.EpgUrl ?? playlist.EpgUrl);
            }
            case SourceKind.Xtream:
            {
                var client = XtreamClient.For(http, source);
                await client.AuthenticateAsync(ct);
                // Live TV first: it's what people open first, and usually the smallest list.
                var live = await client.GetLiveAsync(source.Id, ct);
                partLoaded?.Invoke(MediaKind.Live, live);
                var movies = Part(client.GetMoviesAsync(source.Id, ct), MediaKind.Movie, partLoaded);
                var series = Part(client.GetSeriesAsync(source.Id, ct), MediaKind.Series, partLoaded);
                await Task.WhenAll(movies, series);
                return new ImportResult([.. live, .. movies.Result, .. series.Result], source.EpgUrl ?? client.XmltvUrl);
            }
            default:
                throw new NotSupportedException($"Unknown source kind {source.Kind}.");
        }
    }

    private static async Task<IReadOnlyList<MediaItem>> Part(Task<IReadOnlyList<MediaItem>> download, MediaKind kind,
        Action<MediaKind, IReadOnlyList<MediaItem>>? partLoaded)
    {
        var items = await download;
        partLoaded?.Invoke(kind, items);
        return items;
    }

    private async Task<string> ReadM3uAsync(string location, CancellationToken ct)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return await http.GetStringAsync(uri, ct);

        var path = uri is { IsFile: true } ? uri.LocalPath : location;
        return await File.ReadAllTextAsync(path, ct);
    }
}
