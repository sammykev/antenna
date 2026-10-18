using System.Collections.Concurrent;
using Antenna.App.ViewModels;
using Antenna.Core;
using Antenna.Core.Epg;
using Antenna.Core.Playlists;

namespace Antenna.App.Services;

/// <summary>Guide lookups and catch-up URL resolution for the UI.</summary>
internal static class GuideService
{
    private static readonly ConcurrentDictionary<string, TimeSpan> ServerOffsets = new();
    private static Dictionary<(string SourceId, string ChannelId), Programme>? _nowPlaying;
    private static DateTimeOffset _nowPlayingAt;

    public static void InvalidateNowPlaying() => _nowPlaying = null;

    /// <summary>What's on now on every channel. Cached for a minute: tiles ask for it a lot.</summary>
    public static async Task<Dictionary<(string SourceId, string ChannelId), Programme>> NowPlayingAsync()
    {
        var now = DateTimeOffset.Now;
        if (_nowPlaying is { } cached && now - _nowPlayingAt < TimeSpan.FromMinutes(1))
            return cached;
        var map = await Task.Run(() => AppServices.Library.GetNowPlaying(now));
        _nowPlaying = map;
        _nowPlayingAt = now;
        return map;
    }

    /// <summary>What's on now from the cache, without waiting (empty if it hasn't been loaded yet).</summary>
    public static Dictionary<(string SourceId, string ChannelId), Programme> CachedNowPlaying() => _nowPlaying ?? [];

    public static Programme? NowOn(MediaItem item, Dictionary<(string, string), Programme> now) =>
        item is { Kind: MediaKind.Live, EpgChannelId: { } channel } && now.TryGetValue((item.SourceId, channel), out var p) ? p : null;

    /// <summary>Wraps entries in view models, numbering them and tagging live channels with what's on now.</summary>
    public static async Task<List<ItemViewModel>> ToViewModelsAsync(IEnumerable<LibraryEntry> entries, int firstNumber = 1)
    {
        var list = entries.Select(e => new ItemViewModel(e)).ToList();
        for (var i = 0; i < list.Count; i++)
            list[i].Number = firstNumber + i;

        if (list.Any(vm => vm.Item.Kind == MediaKind.Live && vm.Item.EpgChannelId is not null))
        {
            var now = await NowPlayingAsync();
            foreach (var vm in list)
                vm.Now = NowOn(vm.Item, now);
        }
        return list;
    }

    /// <summary>Current and next programme on a channel, for the player overlay.</summary>
    public static Task<(Programme? Now, Programme? Next)> NowAndNextAsync(string sourceId, string channelId) =>
        Task.Run(() =>
        {
            var now = DateTimeOffset.Now;
            var list = AppServices.Library.GetProgrammes(sourceId, channelId, now, now.AddHours(12));
            var current = list.FirstOrDefault(p => p.IsOnAt(now));
            var next = list.FirstOrDefault(p => p.Start >= (current?.Stop ?? now));
            return (current, next);
        });

    /// <summary>
    /// The archive URL for a past programme, or null if the provider gives no way to build one.
    /// Xtream panels need their clock offset, fetched once per source per session.
    /// </summary>
    public static async Task<string?> ResolveCatchupUrlAsync(MediaItem item, Programme programme)
    {
        var source = AppServices.Library.GetSources().FirstOrDefault(s => s.Id == item.SourceId);
        if (source is null)
            return null;

        if (source.Kind != SourceKind.Xtream)
            return Catchup.BuildM3uUrl(item, programme, DateTimeOffset.Now);

        var client = XtreamClient.For(AppServices.Http, source);
        if (!ServerOffsets.TryGetValue(source.Id, out var offset))
        {
            offset = (await client.AuthenticateAsync()).ServerUtcOffset;
            ServerOffsets[source.Id] = offset;
        }
        return client.CatchupUrl(item.ExternalId, programme.Start, programme.Duration, offset);
    }
}
