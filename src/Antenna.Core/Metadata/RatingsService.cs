using System.Globalization;
using Antenna.Core.Library;

namespace Antenna.Core.Metadata;

/// <summary>
/// IMDb and Rotten Tomatoes scores for the films in your playlists, and recommendations made from them: the best
/// rated, and ones like a film you watched. Only films you can play are recommended. Needs a free OMDb key; lookups
/// are kept and spread out to stay inside OMDb's 1,000 a day. The same rules (and settings) as the Android app.
/// </summary>
public sealed class RatingsService(LibraryStore store, HttpClient http, Action<string>? log = null)
{
    public const string KeySetting = "omdb.key";
    private const string DaySetting = "omdb.day";
    private const string CountSetting = "omdb.count";

    /// <summary>A little under OMDb's free limit, so opening a film's page can still look it up.</summary>
    private const int Daily = 900;

    private const int PerRun = 150;
    private static readonly TimeSpan RetryMisses = TimeSpan.FromDays(30);
    private int _running;

    /// <summary>Raised (on a background thread) when new scores arrive, so pages can show them.</summary>
    public event Action? Changed;

    /// <summary>Why lookups stopped (a wrong key, or the day's limit), for Settings.</summary>
    public string? Problem { get; private set; }

    public string? Key
    {
        get => store.GetSetting(KeySetting);
        set
        {
            store.SetSetting(KeySetting, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
            Problem = null;
            Changed?.Invoke();
        }
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(Key);

    /// <summary>
    /// Matches up to 150 new films to their scores, asking OMDb only about films it hasn't seen (most playlists
    /// carry the same film several times).
    /// </summary>
    public async Task UpdateAsync(CancellationToken ct = default)
    {
        // One run at a time; a second caller just leaves it to the first.
        if (Key is not { } key || Interlocked.Exchange(ref _running, 1) == 1)
            return;
        var links = new List<(string, string, string)>();
        var asked = 0;
        try
        {
            var client = new OmdbClient(http, key);
            var known = store.GetRatedFilms();
            known.ExceptWith(store.GetStaleMisses(DateTimeOffset.UtcNow - RetryMisses));
            foreach (var item in store.GetUnlinkedMovies(PerRun * 4))
            {
                var film = TitleCleaner.Key(item.Title);
                if (film.StartsWith('|'))
                    continue;
                if (!known.Contains(film))
                {
                    if (asked >= PerRun || !Spend())
                        break;
                    asked++;
                    if (!await LookUpAsync(client, film, item.Title, ct))
                        break;
                    known.Add(film);
                }
                links.Add((item.SourceId, item.ExternalId, film));
            }
        }
        finally
        {
            store.LinkFilms(links);
            Volatile.Write(ref _running, 0);
        }
        if (links.Count > 0)
            Changed?.Invoke();
        if (asked > 0)
            log?.Invoke($"Ratings: looked up {asked} films, matched {links.Count} entries");
    }

    /// <summary>This film's scores, looking it up now if it's new (a film's page).</summary>
    public async Task<FilmRating?> ForItemAsync(MediaItem item, CancellationToken ct = default)
    {
        if (store.GetRating(item.SourceId, item.ExternalId) is { } known)
            return known;
        if (Key is not { } key)
            return null;
        var film = TitleCleaner.Key(item.Title);
        if (film.StartsWith('|'))
            return null;
        if (!store.GetRatedFilms().Contains(film) && (!Spend() || !await LookUpAsync(new OmdbClient(http, key), film, item.Title, ct)))
            return null;
        store.LinkFilms([(item.SourceId, item.ExternalId, film)]);
        return store.GetRating(item.SourceId, item.ExternalId);
    }

    /// <summary>Saves what OMDb says (a miss too, so it isn't asked again for a while); false when lookups must stop for now.</summary>
    private async Task<bool> LookUpAsync(OmdbClient client, string film, string name, CancellationToken ct)
    {
        var (title, year) = TitleCleaner.Parse(name);
        try
        {
            store.SaveRating(film, await client.FindAsync(title, year, ct));
            return true;
        }
        catch (OmdbException ex) when (ex.Stop)
        {
            Problem = ex.Message;
            log?.Invoke($"Ratings: OMDb stopped: {ex.Message}");
            return false;
        }
        catch (OmdbException)
        {
            store.SaveRating(film, null);
            return true;
        }
        catch (HttpRequestException ex)
        {
            log?.Invoke($"Ratings: couldn't reach OMDb: {ex.Message}");
            return false;
        }
    }

    /// <summary>Counts one lookup against today's allowance; false when it's used up.</summary>
    private bool Spend()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var used = store.GetSetting(DaySetting) == today && int.TryParse(store.GetSetting(CountSetting), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (used >= Daily)
            return false;
        store.SetSetting(DaySetting, today);
        store.SetSetting(CountSetting, (used + 1).ToString(CultureInfo.InvariantCulture));
        return true;
    }

    // ---- Recommendations ----

    /// <summary>The best rated films in your playlists.</summary>
    public IReadOnlyList<(LibraryEntry Entry, FilmRating Rating)> TopRated(int limit = 24) => store.GetTopRated(limit);

    /// <summary>
    /// Films like one you watched lately: sharing its genres (two of them when it has two or more), best rated first,
    /// leaving out what you've already watched. Null until there's a rated film in your history with enough like it.
    /// </summary>
    public (string Title, IReadOnlyList<(LibraryEntry Entry, FilmRating Rating)> Picks)? BecauseYouWatched(int limit = 24)
    {
        var history = store.GetHistory(40).Where(h => h.Kind == MediaKind.Movie).ToList();
        if (history.Count == 0)
            return null;
        var watched = history.Select(h => (h.SourceId, h.ExternalId)).ToHashSet();
        var watchedFilms = history.Select(h => TitleCleaner.Key(h.Title)).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<(LibraryEntry Entry, FilmRating Rating)>? pool = null;
        foreach (var seed in history.Take(5))
        {
            if (store.GetRating(seed.SourceId, seed.ExternalId)?.Genres is not { Count: > 0 } genres)
                continue;
            pool ??= store.GetTopRated(500);
            var need = Math.Min(2, genres.Count);
            var picks = pool.Where(p => !watched.Contains((p.Entry.Item.SourceId, p.Entry.Item.ExternalId))
                                        && !watchedFilms.Contains(TitleCleaner.Key(p.Entry.Item.Title))
                                        && p.Rating.Genres.Count(genres.Contains) >= need)
                .Take(limit).ToList();
            if (picks.Count >= 4)
            {
                var title = TitleCleaner.Parse(seed.Title).Title;
                return (title.Length > 0 ? title : seed.Title, picks);
            }
        }
        return null;
    }
}
