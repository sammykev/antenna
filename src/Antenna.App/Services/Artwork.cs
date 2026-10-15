using Antenna.Core;
using Antenna.Core.Metadata;

namespace Antenna.App.Services;

/// <summary>Fills in movie and series details from TMDB when a key is set in Settings.</summary>
internal static class Artwork
{
    private static TmdbClient? _client;
    private static string? _clientKey;

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(AppSettings.TmdbApiKey);

    /// <summary>
    /// The playlist's details with gaps (poster, plot, cast…) filled from TMDB. Returns the playlist's
    /// details unchanged when TMDB is off, finds nothing, or can't be reached.
    /// </summary>
    public static async Task<MediaDetails?> CompleteAsync(MediaItem item, MediaDetails? fromPlaylist)
    {
        if (!IsEnabled || !NeedsMore(fromPlaylist))
            return fromPlaylist;
        try
        {
            var found = await Client().FindAsync(item.Title, item.Kind);
            return TmdbClient.Merge(fromPlaylist, found);
        }
        catch (TmdbException ex)
        {
            Log.Info($"tmdb: {ex.Message}");
            return fromPlaylist;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "tmdb");
            return fromPlaylist;
        }
    }

    private static bool NeedsMore(MediaDetails? d) =>
        d is null || d.PosterUrl is null || d.BackdropUrl is null || d.Plot is null || d.Cast is null;

    private static TmdbClient Client()
    {
        var key = AppSettings.TmdbApiKey!.Trim();
        if (_client is null || _clientKey != key)
        {
            _client = new TmdbClient(AppServices.Http, key);
            _clientKey = key;
        }
        return _client;
    }
}
