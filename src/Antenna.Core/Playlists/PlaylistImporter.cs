namespace Antenna.Core.Playlists;

public sealed record ImportResult(IReadOnlyList<MediaItem> Items, string? EpgUrl);

/// <summary>Fetches a source's full catalogue, whatever kind of playlist it is.</summary>
public sealed class PlaylistImporter(HttpClient http)
{
    public async Task<ImportResult> ImportAsync(PlaylistSource source, CancellationToken ct = default)
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
                var items = await client.GetCatalogAsync(source.Id, ct);
                return new ImportResult(items, source.EpgUrl ?? client.XmltvUrl);
            }
            default:
                throw new NotSupportedException($"Unknown source kind {source.Kind}.");
        }
    }

    private async Task<string> ReadM3uAsync(string location, CancellationToken ct)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return await http.GetStringAsync(uri, ct);

        var path = uri is { IsFile: true } ? uri.LocalPath : location;
        return await File.ReadAllTextAsync(path, ct);
    }
}
