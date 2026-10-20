using System.Net;
using Antenna.Core;
using Antenna.Core.Epg;
using Antenna.Core.Library;
using Antenna.Core.Playlists;

namespace Antenna.App.Services;

/// <summary>App-wide singletons. Small enough that a DI container would be overkill.</summary>
public static class AppServices
{
    public static HttpClient Http { get; } = CreateHttpClient();

    public static LibraryStore Library { get; } = new(LibraryStore.DefaultPath(), new DpapiProtector());

    public static PlaylistImporter Importer { get; } = new(Http);

    public static EpgImporter Guide { get; } = new(Http, Library);

    public static SourceRefresher Refresher { get; } = CreateRefresher();

    /// <summary>Raised (on a background thread) when part of a playlist's catalogue has been saved.</summary>
    public static event Action<PlaylistSource, MediaKind, int>? CatalogPartLoaded;

    /// <summary>
    /// Re-imports a source's catalogue and then its guide, off the UI thread.
    /// A guide failure doesn't undo the catalogue; it's reported in <see cref="RefreshResult.GuideError"/>.
    /// </summary>
    public static Task<RefreshResult> RefreshSourceAsync(PlaylistSource source, CancellationToken ct = default) =>
        Refresher.RefreshAsync(source, ct);

    private static SourceRefresher CreateRefresher()
    {
        var refresher = new SourceRefresher(Importer, Guide, Library);
        refresher.Saved += (source, kind, count) =>
        {
            GuideService.InvalidateNowPlaying();
            if (kind is { } part)
                CatalogPartLoaded?.Invoke(source, part, count);
        };
        return refresher;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        // Some panels reject requests without a player-like user agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Antenna/0.1 (Windows) VLC/3.0");
        return http;
    }
}
