using System.Net;
using System.Text;
using Antenna.Core.Metadata;

namespace Antenna.Core.Tests;

public sealed class TmdbTests
{
    [Theory]
    [InlineData("EN - The Matrix (1999) [4K]", "The Matrix", 1999)]
    [InlineData("|UK| Line of Duty S01 FHD", "Line of Duty", null)]
    [InlineData("4K-NF: Glass Onion 2022", "Glass Onion", 2022)]
    [InlineData("[FR] Le Dîner de cons (1998) MULTI", "Le Dîner de cons", 1998)]
    [InlineData("Blade Runner 2049 (2017)", "Blade Runner 2049", 2017)]
    [InlineData("Dune.Part.Two.2024.1080p", "Dune Part Two", 2024)]
    public void Cleans_catalogue_names(string name, string title, int? year) =>
        Assert.Equal((title, year), TitleCleaner.Parse(name));

    [Fact]
    public async Task Finds_a_movie_with_credits_and_caches_it()
    {
        var handler = new FakeTmdb();
        var client = new TmdbClient(new HttpClient(handler), "key123");

        var details = await client.FindAsync("EN - The Matrix (1999) [4K]", MediaKind.Movie);
        var again = await client.FindAsync("EN - The Matrix (1999) [4K]", MediaKind.Movie);

        Assert.NotNull(details);
        Assert.Equal("A hacker learns the truth.", details.Plot);
        Assert.Equal("Action, Science Fiction", details.Genre);
        Assert.Equal("8.2", details.Rating);
        Assert.Equal("2h 16m", details.Duration);
        Assert.Equal("Keanu Reeves, Laurence Fishburne", details.Cast);
        Assert.Equal("Lana Wachowski", details.Director);
        Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", details.PosterUrl);
        Assert.Same(details, again);
        Assert.Equal(2, handler.Requests.Count); // search + details, then the cache
        Assert.Contains("query=The%20Matrix", handler.Requests[0]);
        Assert.Contains("year=1999", handler.Requests[0]);
        Assert.Contains("api_key=key123", handler.Requests[0]);
    }

    [Fact]
    public async Task No_match_is_null_and_a_bad_key_says_so()
    {
        Assert.Null(await new TmdbClient(new HttpClient(new FakeTmdb(empty: true)), "k").FindAsync("Nothing Here", MediaKind.Movie));
        var ex = await Assert.ThrowsAsync<TmdbException>(() =>
            new TmdbClient(new HttpClient(new FakeTmdb(status: HttpStatusCode.Unauthorized)), "bad").FindAsync("X", MediaKind.Series));
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public async Task Finds_just_a_poster_with_one_request()
    {
        var handler = new FakeTmdb();
        var client = new TmdbClient(new HttpClient(handler), "k");

        Assert.Equal("https://image.tmdb.org/t/p/w342/search.jpg", await client.FindPosterAsync("|EN| The Matrix 1999", MediaKind.Movie));
        Assert.Equal("https://image.tmdb.org/t/p/w342/search.jpg", await client.FindPosterAsync("|EN| The Matrix 1999", MediaKind.Movie));
        Assert.Single(handler.Requests); // One search, then cached; no details request.
        Assert.Null(await new TmdbClient(new HttpClient(new FakeTmdb(empty: true)), "k").FindPosterAsync("Nothing", MediaKind.Series));
    }

    [Fact]
    public void Merge_keeps_the_playlist_details_and_fills_the_gaps()
    {
        var playlist = new MediaDetails { Plot = "From the provider", Rating = "7" };
        var tmdb = new MediaDetails { Plot = "From TMDB", PosterUrl = "p", Cast = "c" };

        var merged = TmdbClient.Merge(playlist, tmdb)!;

        Assert.Equal("From the provider", merged.Plot);
        Assert.Equal("7", merged.Rating);
        Assert.Equal("p", merged.PosterUrl);
        Assert.Equal("c", merged.Cast);
    }

    private sealed class FakeTmdb(bool empty = false, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var url = request.RequestUri!.AbsoluteUri; // As sent (ToString would unescape %20).
            Requests.Add(url);
            var json = url.Contains("/search/")
                ? empty ? """{"results":[]}""" : """{"results":[{"id":603,"title":"The Matrix","poster_path":"/search.jpg"}]}"""
                : """
                  {"overview":"A hacker learns the truth.","genres":[{"name":"Action"},{"name":"Science Fiction"}],
                   "release_date":"1999-03-30","vote_average":8.2,"runtime":136,"poster_path":"/poster.jpg","backdrop_path":"/back.jpg",
                   "credits":{"cast":[{"name":"Keanu Reeves"},{"name":"Laurence Fishburne"}],
                              "crew":[{"job":"Producer","name":"Joel Silver"},{"job":"Director","name":"Lana Wachowski"}]}}
                  """;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
