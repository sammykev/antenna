using System.Net;
using Antenna.Core.Playlists;

namespace Antenna.Core.Tests;

public class XtreamClientTests
{
    private sealed class FakeHandler(Func<Uri, string> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request.RequestUri!)) });
        }
    }

    private static string Action(Uri uri) =>
        System.Web.HttpUtility.ParseQueryString(uri.Query)["action"] ?? "";

    private static XtreamClient Client(Func<Uri, string> respond, out FakeHandler handler)
    {
        handler = new FakeHandler(respond);
        return new XtreamClient(new HttpClient(handler), "host.example:8080/player_api.php", "user", "p@ss");
    }

    [Theory]
    [InlineData("host:8080", "http://host:8080")]
    [InlineData("https://host/", "https://host")]
    [InlineData("http://host:80/get.php?username=a&password=b", "http://host")]
    public void Normalizes_server_url(string input, string expected) =>
        Assert.Equal(expected, XtreamClient.NormalizeServer(input));

    [Fact]
    public void Builds_stream_and_catchup_urls_with_escaped_credentials()
    {
        var client = Client(_ => "{}", out _);

        Assert.Equal("http://host.example:8080/live/user/p%40ss/42.ts", client.LiveUrl("42"));
        Assert.Equal("http://host.example:8080/movie/user/p%40ss/7.mkv", client.MovieUrl("7", "mkv"));
        Assert.Equal(
            "http://host.example:8080/timeshift/user/p%40ss/61/2026-10-14:20-05/42.ts",
            client.CatchupUrl("42", new DateTime(2026, 10, 14, 20, 5, 0), TimeSpan.FromMinutes(60.5)));
    }

    [Fact]
    public async Task Authenticate_reads_loosely_typed_user_info()
    {
        var client = Client(_ => """{"user_info":{"auth":1,"status":"Active","exp_date":"1800000000","max_connections":"2","active_cons":0}}""", out _);

        var account = await client.AuthenticateAsync();

        Assert.Equal("Active", account.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), account.ExpiresAt);
        Assert.Equal(2, account.MaxConnections);
    }

    [Fact]
    public async Task Authenticate_throws_on_rejected_credentials()
    {
        var client = Client(_ => """{"user_info":{"auth":0}}""", out _);

        var ex = await Assert.ThrowsAsync<XtreamException>(() => client.AuthenticateAsync());
        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public async Task Catalog_maps_live_vod_and_series_with_categories()
    {
        var client = Client(uri => Action(uri) switch
        {
            "get_live_categories" => """[{"category_id":"1","category_name":"News"}]""",
            "get_vod_categories" => """[{"category_id":2,"category_name":"Action"}]""",
            "get_series_categories" => """[{"category_id":"3","category_name":"Drama"}]""",
            "get_live_streams" => """[{"stream_id":101,"name":"BBC News","stream_icon":"","epg_channel_id":"bbcnews.uk","category_id":"1","tv_archive":1,"tv_archive_duration":"5"}]""",
            "get_vod_streams" => """[{"stream_id":"7","name":"Heat","category_id":"2","container_extension":"mkv"}]""",
            "get_series" => """[{"series_id":9,"name":"The Wire","cover":"http://c/9.jpg","category_id":"3"}]""",
            _ => "[]",
        }, out _);

        var items = await client.GetCatalogAsync("src");

        var live = items.Single(i => i.Kind == MediaKind.Live);
        Assert.Equal(("101", "BBC News", "News", "bbcnews.uk", 5), (live.ExternalId, live.Title, live.Group, live.EpgChannelId, live.CatchupDays));
        Assert.Null(live.LogoUrl);
        Assert.Equal("http://host.example:8080/live/user/p%40ss/101.ts", live.StreamUrl);

        var movie = items.Single(i => i.Kind == MediaKind.Movie);
        Assert.Equal("Action", movie.Group);
        Assert.EndsWith("/movie/user/p%40ss/7.mkv", movie.StreamUrl);

        var series = items.Single(i => i.Kind == MediaKind.Series);
        Assert.Equal(("9", "Drama", "http://c/9.jpg"), (series.ExternalId, series.Group, series.LogoUrl));
        Assert.Null(series.StreamUrl);
    }

    [Fact]
    public async Task Episodes_are_flattened_and_sorted()
    {
        var client = Client(_ => """
            {"episodes":{
              "2":[{"id":"21","episode_num":1,"season":2,"title":"S2E1","container_extension":"mp4"}],
              "1":[{"id":"12","episode_num":"2","season":"1","title":"S1E2","container_extension":"mkv","info":{"plot":"Plot","movie_image":"http://i"}},
                   {"id":"11","episode_num":1,"season":1,"title":"S1E1","container_extension":"mkv","info":[]}]
            }}
            """, out var handler);

        var episodes = await client.GetEpisodesAsync("9");

        Assert.Equal(["11", "12", "21"], episodes.Select(e => e.Id));
        Assert.Equal("Plot", episodes[1].Plot);
        Assert.EndsWith("/series/user/p%40ss/12.mkv", episodes[1].StreamUrl);
        Assert.Contains("action=get_series_info&series_id=9", handler.Requests.Single().Query);
    }

    [Fact]
    public async Task Non_json_response_becomes_friendly_error()
    {
        var client = Client(_ => "<html>Not here</html>", out _);

        await Assert.ThrowsAsync<XtreamException>(() => client.AuthenticateAsync());
    }

    [Fact]
    public void A_refused_login_shows_the_panels_reason()
    {
        var e = XtreamClient.Failure(401, """{"error":{"type":2,"code":1,"message":"`x` login username doesn't exist"}}""");
        Assert.Equal("The server refused the login: x login username doesn't exist. Check the username and password.", e.Message);
        Assert.StartsWith("The server refused the login. Check", XtreamClient.Failure(403, "<html/>").Message);
        Assert.Equal("The Xtream server answered 502.", XtreamClient.Failure(502, "").Message);
    }

    [Fact]
    public async Task Spaces_around_the_login_are_ignored()
    {
        var handler = new FakeHandler(_ => """{"user_info":{"auth":1,"status":"Active"}}""");
        var client = new XtreamClient(new HttpClient(handler), "host:8080", " user ", "pass \n");
        await client.AuthenticateAsync();
        Assert.Contains("username=user&password=pass", handler.Requests.Single().Query);
    }
}
