using Antenna.Core;
using Antenna.Core.Playlists;

namespace Antenna.App.Services;

/// <summary>How many streams an Xtream account says it allows at once, cached for a while. 0 = unknown.</summary>
internal static class ConnectionLimits
{
    private static readonly Dictionary<string, (int Max, DateTimeOffset At)> Cache = [];

    public static async Task<int> MaxAsync(string? sourceId)
    {
        if (sourceId is null || AppServices.Library.GetSources().FirstOrDefault(s => s.Id == sourceId) is not { Kind: SourceKind.Xtream } source)
            return 0;
        lock (Cache)
        {
            if (Cache.TryGetValue(sourceId, out var cached) && DateTimeOffset.Now - cached.At < TimeSpan.FromMinutes(30))
                return cached.Max;
        }
        try
        {
            var account = await XtreamClient.For(AppServices.Http, source).AuthenticateAsync();
            lock (Cache)
                Cache[sourceId] = (account.MaxConnections, DateTimeOffset.Now);
            return account.MaxConnections;
        }
        catch (Exception ex)
        {
            Log.Info($"connection limit check failed: {ex.Message}");
            return 0;
        }
    }
}
