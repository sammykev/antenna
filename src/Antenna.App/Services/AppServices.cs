using System.Net;
using Antenna.Core;
using Antenna.Core.Epg;
using Antenna.Core.Library;
using Antenna.Core.Playlists;

namespace Antenna.App.Services;

public sealed record RefreshResult(int Items, int Programmes, string? GuideError);

/// <summary>App-wide singletons. Small enough that a DI container would be overkill.</summary>
public static class AppServices
{
    public static HttpClient Http { get; } = CreateHttpClient();

    public static LibraryStore Library { get; } = new(LibraryStore.DefaultPath(), new DpapiProtector());

    public static PlaylistImporter Importer { get; } = new(Http);

    public static EpgImporter Guide { get; } = new(Http, Library);

    /// <summary>
    /// Re-imports a source's catalogue and then its guide, off the UI thread.
    /// A guide failure doesn't undo the catalogue; it's reported in <see cref="RefreshResult.GuideError"/>.
    /// </summary>
    public static Task<RefreshResult> RefreshSourceAsync(PlaylistSource source, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var result = await Importer.ImportAsync(source, ct);
            // Xtream guide URLs embed the password, so they're derived at import time, never stored.
            var saved = source with
            {
                EpgUrl = source.Kind != SourceKind.Xtream ? result.EpgUrl
                    : source.EpgUrl?.Contains("xmltv.php", StringComparison.OrdinalIgnoreCase) == true ? null : source.EpgUrl,
                LastRefreshed = DateTimeOffset.Now,
            };
            Library.SaveSource(saved);
            Library.ReplaceItems(source.Id, result.Items);
            GuideService.InvalidateNowPlaying();

            try
            {
                var programmes = await Guide.ImportAsync(saved with { EpgUrl = result.EpgUrl }, result.Items, ct);
                GuideService.InvalidateNowPlaying();
                return new RefreshResult(result.Items.Count, programmes, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new RefreshResult(result.Items.Count, 0, ex.Message);
            }
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
