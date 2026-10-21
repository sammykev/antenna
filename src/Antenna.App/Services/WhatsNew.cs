using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Antenna.App.Services;

/// <summary>After an update, shows that release's notes once ("What's new in Antenna 0.1.5").</summary>
internal static class WhatsNew
{
    public static async Task ShowIfUpdatedAsync(XamlRoot root)
    {
        var current = Updater.CurrentVersion;
        var last = AppSettings.LastVersion;
        if (last == current)
            return;
        AppSettings.LastVersion = current;
        // First run ever, or a copy that wasn't installed (dotnet run): nothing to announce.
        if (last is null || !Updater.IsInstalled)
            return;

        try
        {
            var notes = StoredNotes(current) ?? await FetchNotesAsync(current);
            AppSettings.PendingNotes = null;
            if (string.IsNullOrWhiteSpace(notes))
                return;
            Log.Info($"updates: showing what's new in {current}");
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = $"What's new in Antenna {current}",
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new TextBlock { Text = ToPlainText(notes), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                },
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "what's new");
        }
    }

    private static string? StoredNotes(string version) =>
        AppSettings.PendingNotes is { } stored && stored.Split('\n', 2) is [var v, var notes] && v == version ? notes : null;

    /// <summary>The GitHub release's description, for updates installed by running Setup.exe.</summary>
    private static async Task<string?> FetchNotesAsync(string version)
    {
        foreach (var url in new[] { Updater.RepositoryUrl, Updater.SourceRepositoryUrl })
        {
            if (await FetchNotesAsync(new Uri(url).AbsolutePath.Trim('/'), version) is { } notes)
                return notes;
        }
        return null;
    }

    private static async Task<string?> FetchNotesAsync(string repo, string version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/tags/v{version}");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Antenna", version));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await AppServices.Http.SendAsync(request, cts.Token);
        if (!response.IsSuccessStatusCode)
            return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        return json.RootElement.TryGetProperty("body", out var body) ? body.GetString() : null;
    }

    /// <summary>Notes are Markdown bullets under a heading; the dialog already has the heading.</summary>
    internal static string ToPlainText(string markdown) =>
        string.Join("\n", markdown.Replace("\r", "").Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))
            .Select(line => line.StartsWith("- ") || line.StartsWith("* ") ? "•  " + line[2..] : line))
            .Trim();
}
