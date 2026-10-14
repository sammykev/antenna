using System.Net;
using Antenna.Core;
using Antenna.Core.Library;
using Antenna.Core.Playlists;

namespace Antenna.App.Services;

/// <summary>App-wide singletons. Small enough that a DI container would be overkill.</summary>
public static class AppServices
{
    public static HttpClient Http { get; } = CreateHttpClient();

    public static LibraryStore Library { get; } = new(LibraryStore.DefaultPath());

    public static PlaylistImporter Importer { get; } = new(Http);

    /// <summary>Imports a source's catalogue and stores it, off the UI thread. Returns the item count.</summary>
    public static Task<int> RefreshSourceAsync(PlaylistSource source, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var result = await Importer.ImportAsync(source, ct);
            Library.SaveSource(source with { EpgUrl = result.EpgUrl, LastRefreshed = DateTimeOffset.Now });
            Library.ReplaceItems(source.Id, result.Items);
            return result.Items.Count;
        }, ct);

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
