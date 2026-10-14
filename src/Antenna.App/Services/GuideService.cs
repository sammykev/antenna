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

    /// <summary>Wraps entries in view models, tagging live channels with what's on now.</summary>
    public static List<ItemViewModel> WithNowPlaying(IEnumerable<LibraryEntry> entries)
    {
        var list = entries.Select(e => new ItemViewModel(e)).ToList();
        if (!list.Any(vm => vm.Item.Kind == MediaKind.Live && vm.Item.EpgChannelId is not null))
            return list;

        var now = AppServices.Library.GetNowPlaying(DateTimeOffset.Now);
        foreach (var vm in list)
        {
            if (vm.Item is { Kind: MediaKind.Live, EpgChannelId: { } channel } item &&
                now.TryGetValue((item.SourceId, channel), out var programme))
                vm.Now = programme;
        }
        return list;
    }

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
