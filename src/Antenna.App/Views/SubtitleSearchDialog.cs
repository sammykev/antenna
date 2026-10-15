using System.Text.RegularExpressions;
using Antenna.App.Services;
using Antenna.Core.Subtitles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Antenna.App.Views;

/// <summary>Searches OpenSubtitles.com and downloads the chosen file. Built in code: it's a small form.</summary>
internal static partial class SubtitleSearchDialog
{
    [GeneratedRegex(@"\s*(\(catch-up\)|\[.*?\]|\(\d{4}\)|\b(4k|uhd|fhd|hd|sd)\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"S(\d{1,2})E(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();

    /// <returns>Path of the downloaded subtitle file, or null if cancelled.</returns>
    public static async Task<string?> PickAsync(XamlRoot root, string title)
    {
        if (AppSettings.OpenSubtitlesApiKey is not { } apiKey)
        {
            await new ContentDialog
            {
                XamlRoot = root,
                Title = "OpenSubtitles needs an API key",
                Content = "Create a free API key at opensubtitles.com (Profile → API consumers), then add it in Settings → Subtitles.",
                CloseButtonText = "OK",
            }.ShowAsync();
            return null;
        }

        var episode = EpisodeRegex().Match(title);
        var cleaned = NoiseRegex().Replace(EpisodeRegex().Replace(title, " "), " ").Replace("·", " ").Trim();

        var query = new TextBox { Header = "Title", Text = cleaned };
        var languages = new TextBox { Header = "Languages (e.g. en or en,fr)", Text = AppSettings.SubtitleLanguages, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        var search = new Button { Content = "Search" };
        var status = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var results = new ListView { Height = 280, SelectionMode = ListViewSelectionMode.Single };
        var panel = new StackPanel { Spacing = 10, Width = 520 };
        panel.Children.Add(query);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(languages);
        search.VerticalAlignment = VerticalAlignment.Bottom;
        row.Children.Add(search);
        panel.Children.Add(row);
        panel.Children.Add(status);
        panel.Children.Add(results);

        var client = new OpenSubtitlesClient(AppServices.Http, apiKey);
        var found = new List<SubtitleResult>();

        async Task RunSearchAsync()
        {
            status.Text = "Searching…";
            try
            {
                if (AppSettings.OpenSubtitlesUser is { } user && AppSettings.OpenSubtitlesPassword is { } pass)
                    await client.LoginAsync(user, pass);
                int? season = episode.Success ? int.Parse(episode.Groups[1].Value) : null;
                int? ep = episode.Success ? int.Parse(episode.Groups[2].Value) : null;
                found = (await client.SearchAsync(query.Text, languages.Text, season, ep)).ToList();
                results.ItemsSource = found.Select(r => $"{r.Release}  ·  {r.Language}{(r.HearingImpaired ? " · SDH" : "")}  ·  {r.Downloads:N0} downloads").ToList();
                status.Text = found.Count == 0 ? "No subtitles found. Try a shorter title." : "Pick one and press Download.";
                AppSettings.SubtitleLanguages = languages.Text;
            }
            catch (OpenSubtitlesException ex)
            {
                status.Text = ex.Message;
            }
        }

        search.Click += async (_, _) => await RunSearchAsync();

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Find subtitles",
            Content = panel,
            PrimaryButtonText = "Download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        string? path = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (results.SelectedIndex < 0 || results.SelectedIndex >= found.Count)
                {
                    status.Text = "Select a subtitle first.";
                    args.Cancel = true;
                    return;
                }
                status.Text = "Downloading…";
                var dir = Path.Combine(Path.GetTempPath(), "Antenna", "subtitles");
                path = await client.DownloadAsync(found[results.SelectedIndex].FileId, dir);
            }
            catch (OpenSubtitlesException ex)
            {
                status.Text = ex.Message;
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        _ = RunSearchAsync();
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? path : null;
    }
}
