using Antenna.Mobile.Services;

namespace Antenna.Mobile.Views;

/// <summary>Finds programmes by title across every channel's guide.</summary>
public partial class GuideSearchPage : ContentPage
{
    private CancellationTokenSource? _searchDelay;

    public GuideSearchPage()
    {
        InitializeComponent();
    }

    private async void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchDelay?.Cancel();
        _searchDelay = new CancellationTokenSource();
        var token = _searchDelay.Token;
        try
        {
            await Task.Delay(300, token);
        }
        catch (TaskCanceledException)
        {
            return;  // still typing
        }

        var query = SearchBox.Text?.Trim() ?? "";
        if (query.Length < 2)
        {
            Results.ItemsSource = null;
            return;
        }
        Busy.IsRunning = true;
        try
        {
            var now = DateTimeOffset.Now;
            var rows = await Task.Run(() => MobileServices.Library.SearchEvents(query, now.AddHours(-4), now.AddDays(7), 300, keepAllWords: true)
                .Where(hit => hit.Programme.Stop > now && hit.Channels.Count > 0)
                .OrderByDescending(hit => hit.IsLive(now))
                .ThenBy(hit => hit.Programme.Start)
                .Select(hit => new GuideRow(hit.Programme, hit.Channels[0]))
                .ToList(), token);
            if (token.IsCancellationRequested)
                return;
            Results.ItemsSource = rows;
            EmptyText.Text = $"Nothing coming up matches “{query}”.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Guide search failed", ex);
        }
        finally
        {
            Busy.IsRunning = false;
        }
    }

    private async void Result_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not GuideRow { Entry: { } entry } row)
            return;
        if (row.IsOnNow)
        {
            await Playback.PlayAsync([entry.Item], 0);
            return;
        }
        if (await DisplayAlertAsync(row.Title, $"{row.Time} on {entry.Item.Title}.\n\n{row.Description}", "Watch channel now", "Close"))
            await Playback.PlayAsync([entry.Item], 0);
    }
}
