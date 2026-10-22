using Antenna.App.Services;
using Antenna.App.Views;
using Antenna.Core;

namespace Antenna.App;

/// <summary>
/// --smoke-test: opens every main page in turn so a build that can't show a window or page fails CI
/// instead of reaching users. Errors raised while a page loads reach App's handler and are logged as ERROR,
/// which the workflow also checks for.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan PageSettle = TimeSpan.FromSeconds(1.5);

    internal async Task RunSmokeTestAsync()
    {
        var failed = new List<string>();
        // Pages draw their cards only when there's something to show: give them a small library first.
        try
        {
            SeedLibrary();
        }
        catch (Exception ex)
        {
            failed.Add("seeding the library");
            Log.Error(ex, "smoke test: seeding");
        }
        await Task.Delay(PageSettle);
        foreach (var (tag, target) in Pages)
        {
            try
            {
                if (!ContentFrame.Navigate(target.Page, target.Parameter))
                    throw new InvalidOperationException("navigation was refused");
                await Task.Delay(PageSettle);
                Log.Info($"smoke test: {tag} opened");
            }
            catch (Exception ex)
            {
                failed.Add(tag);
                Log.Error(ex, $"smoke test: {tag}");
            }
        }

        // My list's other tabs use other templates.
        try
        {
            ContentFrame.Navigate(Pages["favourites"].Page);
            await Task.Delay(PageSettle);
            foreach (var tab in new[] { "continue", "recent", "pinned", "fav" })
            {
                (ContentFrame.Content as FavouritesPage)?.SelectTab(tab);
                await Task.Delay(PageSettle);
                Log.Info($"smoke test: My list → {tab} opened");
            }
        }
        catch (Exception ex)
        {
            failed.Add("my list tabs");
            Log.Error(ex, "smoke test: My list tabs");
        }

        // TV mode moves the navigation view into a scaler and back: make sure that holds together.
        try
        {
            ApplyTvMode(true);
            ContentFrame.Navigate(Pages["home"].Page);
            await Task.Delay(PageSettle);
            ApplyTvMode(false);
            await Task.Delay(PageSettle);
            Log.Info("smoke test: TV mode on and off");
        }
        catch (Exception ex)
        {
            failed.Add("tv mode");
            Log.Error(ex, "smoke test: TV mode");
        }

        if (App.SmokeTestTv)
        {
            Log.Info("smoke test: started with TV mode on");
            AppSettings.TvMode = false;
        }
        Log.Info(failed.Count == 0 ? "Smoke test passed" : $"Smoke test failed: {string.Join(", ", failed)}");
        Environment.Exit(failed.Count == 0 ? 0 : 1);
    }

    /// <summary>A playlist with a channel, a film and a series, all favourited, with history and a guide.</summary>
    private static void SeedLibrary()
    {
        var library = AppServices.Library;
        library.SaveSource(new PlaylistSource
        {
            Id = "smoke", Name = "Smoke test", Kind = SourceKind.M3u, Location = "http://127.0.0.1:9/smoke.m3u", LastRefreshed = DateTimeOffset.Now,
        });
        MediaItem[] items =
        [
            new() { SourceId = "smoke", ExternalId = "c1", Kind = MediaKind.Live, Title = "Smoke Sports 1", Group = "Sports", StreamUrl = "http://127.0.0.1:9/c1.ts", EpgChannelId = "c1", SortIndex = 0 },
            new() { SourceId = "smoke", ExternalId = "m1", Kind = MediaKind.Movie, Title = "Smoke Film", Group = "Drama", StreamUrl = "http://127.0.0.1:9/m1.mkv", SortIndex = 1 },
            new() { SourceId = "smoke", ExternalId = "s1", Kind = MediaKind.Series, Title = "Smoke Series", Group = "Series", SortIndex = 2 },
        ];
        library.ReplaceItems("smoke", items);
        var now = DateTimeOffset.UtcNow;
        using (var guide = library.BeginGuideImport("smoke"))
        {
            guide.Add(new Programme { ChannelId = "c1", Title = "Live: Arsenal v Chelsea", Start = now.AddMinutes(-30), Stop = now.AddMinutes(90) });
            guide.Commit();
        }
        foreach (var item in items)
            library.SetFavourite(item, true);
        library.RecordWatch(new WatchRecord { SourceId = "smoke", ExternalId = "m1", Kind = MediaKind.Movie, Title = "Smoke Film", StreamUrl = "http://127.0.0.1:9/m1.mkv", PositionMs = 600_000, LengthMs = 6_000_000 });
        library.RecordWatch(new WatchRecord { SourceId = "smoke", ExternalId = "c1", Kind = MediaKind.Live, Title = "Smoke Sports 1", StreamUrl = "http://127.0.0.1:9/c1.ts" });
        library.SetGroupFlag(MediaKind.Live, "Sports", GroupFlag.Pinned, true);
        GuideService.InvalidateNowPlaying();
    }
}
