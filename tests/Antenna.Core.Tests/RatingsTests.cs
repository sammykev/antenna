using System.Net;
using System.Text.Json;
using Antenna.Core.Library;
using Antenna.Core.Metadata;

namespace Antenna.Core.Tests;

public sealed class RatingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"antenna-test-{Guid.NewGuid():N}.db");
    private readonly LibraryStore _store;

    public RatingsTests() => _store = new LibraryStore(_path);

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(f);
    }

    private sealed class FakeOmdb(Func<Uri, string> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request.RequestUri!)) });
        }
    }

    private void Library()
    {
        _store.SaveSource(new PlaylistSource { Id = "a", Name = "A", Kind = SourceKind.M3u, Location = "http://a", Color = "#FF0000" });
        _store.SaveSource(new PlaylistSource { Id = "b", Name = "B", Kind = SourceKind.M3u, Location = "http://b", Color = "#00FF00" });
        _store.ReplaceItems("a", [Movie("a", "1", "EN - The Matrix (1999)"), Movie("a", "2", "Average Film (2010)"), Movie("a", "3", "Tiny Film (2020)")]);
        _store.ReplaceItems("b", [Movie("b", "9", "|NL| THE MATRIX (1999) 4K")]);
    }

    private static MediaItem Movie(string source, string id, string title) =>
        new() { SourceId = source, ExternalId = id, Kind = MediaKind.Movie, Title = title, Group = "Films", StreamUrl = $"http://x/{id}.mp4" };

    [Fact]
    public void Every_playlists_copy_is_one_film_with_the_same_key_as_android()
    {
        Assert.Equal("the matrix|1999", TitleCleaner.Key("EN - The Matrix (1999) [4K]"));
        Assert.Equal(TitleCleaner.Key("EN - The Matrix (1999) [4K]"), TitleCleaner.Key("|NL| THE MATRIX (1999)"));
    }

    [Fact]
    public void Omdb_scores_are_read()
    {
        using var doc = JsonDocument.Parse("""
            {"Title":"The Matrix","Genre":"Action, Sci-Fi","imdbRating":"8.7","imdbVotes":"2,123,456","imdbID":"tt0133093","Metascore":"73",
             "Ratings":[{"Source":"Internet Movie Database","Value":"8.7/10"},{"Source":"Rotten Tomatoes","Value":"83%"}],"Response":"True"}
            """);
        var r = OmdbClient.Parse(doc.RootElement)!;
        Assert.Equal(8.7, r.Imdb);
        Assert.Equal(2_123_456, r.ImdbVotes);
        Assert.Equal(83, r.RottenTomatoes);
        Assert.Equal(73, r.Metascore);
        Assert.Equal(["Action", "Sci-Fi"], r.Genres);
        Assert.Equal((87 + 83) / 2.0, r.Score!.Value, 3);

        using var missing = JsonDocument.Parse("""{"Response":"False","Error":"Movie not found!"}""");
        Assert.Null(OmdbClient.Parse(missing.RootElement));
        using var limit = JsonDocument.Parse("""{"Response":"False","Error":"Request limit reached!"}""");
        Assert.True(Assert.Throws<OmdbException>(() => OmdbClient.Parse(limit.RootElement)).Stop);
    }

    [Fact]
    public void Top_rated_lists_each_film_once_best_first_and_skips_obscure_ones()
    {
        Library();
        _store.SaveRating(TitleCleaner.Key("The Matrix (1999)"), new FilmRating { Imdb = 8.7, ImdbVotes = 2_000_000, RottenTomatoes = 83, Genres = ["Action"] });
        _store.SaveRating(TitleCleaner.Key("Average Film (2010)"), new FilmRating { Imdb = 6.1, ImdbVotes = 40_000, RottenTomatoes = 55 });
        _store.SaveRating(TitleCleaner.Key("Tiny Film (2020)"), new FilmRating { Imdb = 9.6, ImdbVotes = 40 });
        _store.LinkFilms(_store.GetUnlinkedMovies(100).Select(m => (m.SourceId, m.ExternalId, TitleCleaner.Key(m.Title))).ToList());

        Assert.Empty(_store.GetUnlinkedMovies(100));
        var top = _store.GetTopRated(10);
        Assert.Equal(["EN - The Matrix (1999)", "Average Film (2010)"], top.Select(t => t.Entry.Item.Title));
        Assert.Equal(83, top[0].Rating.RottenTomatoes);
        Assert.Equal(["Action"], _store.GetRating("b", "9")!.Genres);
    }

    [Fact]
    public async Task Update_asks_once_per_film_and_remembers_misses()
    {
        Library();
        var omdb = new FakeOmdb(uri => uri.Query.Contains("Matrix", StringComparison.OrdinalIgnoreCase)
            ? """{"imdbRating":"8.7","imdbVotes":"2,000,000","Ratings":[{"Source":"Rotten Tomatoes","Value":"83%"}],"Genre":"Action","Response":"True"}"""
            : """{"Response":"False","Error":"Movie not found!"}""");
        var ratings = new RatingsService(_store, new HttpClient(omdb)) { Key = "k" };

        await ratings.UpdateAsync();

        // Three films (the Matrix is in both playlists), so three lookups.
        Assert.Equal(3, omdb.Requests.Count);
        Assert.Contains("y=1999", omdb.Requests[0].Query);
        Assert.Equal(8.7, _store.GetRating("b", "9")!.Imdb);
        await ratings.UpdateAsync();
        Assert.Equal(3, omdb.Requests.Count);
    }

    [Fact]
    public async Task A_wrong_key_stops_lookups_and_says_why()
    {
        Library();
        var omdb = new FakeOmdb(_ => """{"Response":"False","Error":"Invalid API key!"}""");
        var ratings = new RatingsService(_store, new HttpClient(omdb)) { Key = "bad" };

        await ratings.UpdateAsync();

        Assert.Single(omdb.Requests);
        Assert.Equal("Invalid API key!", ratings.Problem);
        Assert.Equal(4, _store.GetUnlinkedMovies(100).Count);
    }
}
