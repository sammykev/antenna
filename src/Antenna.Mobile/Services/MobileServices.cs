using System.Net;
using Antenna.Core;
using Antenna.Core.Epg;
using Antenna.Core.Library;
using Antenna.Core.Playlists;

namespace Antenna.Mobile.Services;

/// <summary>App-wide singletons, as in the Windows app's AppServices.</summary>
public static class MobileServices
{
    public static HttpClient Http { get; } = CreateHttpClient();

    public static LibraryStore Library { get; } = new(Path.Combine(FileSystem.AppDataDirectory, "library.db"), new KeystoreProtector());

    public static PlaylistImporter Importer { get; } = new(Http);

    public static EpgImporter Guide { get; } = new(Http, Library);

    public static SourceRefresher Refresher { get; } = new(Importer, Guide, Library);

    /// <summary>Raised (on a background thread) whenever part of a playlist or its guide has been saved.</summary>
    public static event Action? LibraryChanged;

    static MobileServices() => Refresher.Saved += (_, _, _) => LibraryChanged?.Invoke();

    /// <summary>What's on now, cached for a minute: the channel list asks for it on every page.</summary>
    public static Dictionary<(string SourceId, string ChannelId), Programme> NowPlaying()
    {
        var now = DateTimeOffset.Now;
        if (_nowPlaying is null || now - _nowPlayingAt > TimeSpan.FromMinutes(1))
        {
            _nowPlaying = Library.GetNowPlaying(now);
            _nowPlayingAt = now;
        }
        return _nowPlaying;
    }

    public static void InvalidateNowPlaying() => _nowPlaying = null;

    private static Dictionary<(string, string), Programme>? _nowPlaying;
    private static DateTimeOffset _nowPlayingAt;

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        // Some panels reject requests without a player-like user agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Antenna/0.1 (Android) VLC/3.0");
        return http;
    }
}
