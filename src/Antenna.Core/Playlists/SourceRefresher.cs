using Antenna.Core.Epg;
using Antenna.Core.Library;

namespace Antenna.Core.Playlists;

public sealed record RefreshResult(int Items, int Programmes, string? GuideError);

/// <summary>Re-imports a playlist's catalogue and then its guide, saving each into the library.</summary>
public sealed class SourceRefresher(PlaylistImporter importer, EpgImporter guide, LibraryStore library)
{
    /// <summary>Raised (on a background thread) when part of a catalogue has been saved, and when the guide has.</summary>
    public event Action<PlaylistSource, MediaKind?, int>? Saved;

    /// <summary>
    /// Runs off the calling thread. A guide failure doesn't undo the catalogue; it's reported in
    /// <see cref="RefreshResult.GuideError"/>.
    /// </summary>
    public Task<RefreshResult> RefreshAsync(PlaylistSource source, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            // Xtream catalogues arrive in parts: save each as it lands so Live TV is usable while
            // movies and series are still downloading.
            var savedParts = new HashSet<MediaKind>();
            var result = await importer.ImportAsync(source, (kind, items) =>
            {
                library.ReplaceItems(source.Id, items, kind);
                lock (savedParts)
                    savedParts.Add(kind);
                Saved?.Invoke(source, kind, items.Count);
            }, ct).ConfigureAwait(false);
            // Xtream guide URLs embed the password, so they're derived at import time, never stored.
            var saved = source with
            {
                EpgUrl = source.Kind != SourceKind.Xtream ? result.EpgUrl
                    : source.EpgUrl?.Contains("xmltv.php", StringComparison.OrdinalIgnoreCase) == true ? null : source.EpgUrl,
                LastRefreshed = DateTimeOffset.Now,
            };
            library.SaveSource(saved);
            if (savedParts.Count == 0)
                library.ReplaceItems(source.Id, result.Items);
            Saved?.Invoke(saved, null, result.Items.Count);

            try
            {
                var programmes = await guide.ImportAsync(saved with { EpgUrl = result.EpgUrl }, result.Items, ct).ConfigureAwait(false);
                Saved?.Invoke(saved, null, result.Items.Count);
                return new RefreshResult(result.Items.Count, programmes, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new RefreshResult(result.Items.Count, 0, ex.Message);
            }
        }, ct);
}
