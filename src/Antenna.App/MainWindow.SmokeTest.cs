using Antenna.App.Services;

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

        Log.Info(failed.Count == 0 ? "Smoke test passed" : $"Smoke test failed: {string.Join(", ", failed)}");
        Environment.Exit(failed.Count == 0 ? 0 : 1);
    }
}
