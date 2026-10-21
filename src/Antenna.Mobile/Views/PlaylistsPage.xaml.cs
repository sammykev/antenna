using Antenna.Core;
using Antenna.Mobile.Services;

namespace Antenna.Mobile.Views;

public sealed class SourceRow(PlaylistSource source, LibraryCounts counts)
{
    public PlaylistSource Source => source;

    public string Name => source.Name;

    public Color Color => Color.TryParse(source.Color, out var c) ? c : Colors.CornflowerBlue;

    public string Details =>
        $"{(source.Kind == SourceKind.Xtream ? "Xtream Codes" : "M3U")} · {counts.Live:N0} channels · " +
        (source.LastRefreshed is { } at ? $"updated {at.LocalDateTime:d MMM HH:mm}" : "not loaded yet");
}

public partial class PlaylistsPage : ContentPage
{
    private bool _busy;

    public PlaylistsPage()
    {
        InitializeComponent();
        VersionText.Text = $"Antenna {AppInfo.VersionString}";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var rows = await Task.Run(() =>
        {
            var library = MobileServices.Library;
            return library.GetSources()
                .Select(s => new SourceRow(s, library.CountItems(s.Id)))
                .ToList();
        });
        Sources.ItemsSource = rows;
    }

    private async void Sync_Clicked(object? sender, EventArgs e) => await Navigation.PushAsync(new SyncPage());

    private async void Add_Clicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("addplaylist");

    private async void Refresh_Clicked(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is SourceRow row)
            await RefreshAsync([row.Source]);
    }

    private async void RefreshAll_Clicked(object? sender, EventArgs e) =>
        await RefreshAsync(MobileServices.Library.GetSources());

    /// <summary>Refreshes playlists one at a time, showing progress at the top of the page.</summary>
    public async Task RefreshAsync(IReadOnlyList<PlaylistSource> sources)
    {
        if (_busy || sources.Count == 0)
            return;
        _busy = true;
        Progress.IsVisible = true;
        var problems = new List<string>();
        try
        {
            foreach (var source in sources)
            {
                Progress.Text = $"Loading {source.Name}… (the guide can take a minute)";
                try
                {
                    var result = await MobileServices.Refresher.RefreshAsync(source);
                    Log.Info($"Refreshed {source.Name}: {result.Items} items, {result.Programmes} programmes");
                    if (result.GuideError is { } guideError)
                        problems.Add($"{source.Name}: channels loaded, but the guide failed ({guideError})");
                }
                catch (Exception ex)
                {
                    Log.Error($"Refreshing {source.Name} failed", ex);
                    problems.Add($"{source.Name}: {ex.Message}");
                }
                MobileServices.InvalidateNowPlaying();
            }
        }
        finally
        {
            _busy = false;
            Progress.IsVisible = false;
        }
        await LoadAsync();
        if (problems.Count > 0)
            await DisplayAlertAsync("Some playlists had problems", string.Join("\n\n", problems), "OK");
    }

    private async void Delete_Clicked(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not SourceRow row)
            return;
        if (!await DisplayAlertAsync("Delete playlist?", $"{row.Name} and its channels and guide will be removed from this phone.", "Delete", "Cancel"))
            return;
        await Task.Run(() => MobileServices.Library.DeleteSource(row.Source.Id));
        var file = AddPlaylistPage.LocalCopyPath(row.Source.Id);
        if (File.Exists(file))
            File.Delete(file);
        MobileServices.InvalidateNowPlaying();
        await LoadAsync();
    }

    private async void ShareLog_Clicked(object? sender, EventArgs e)
    {
        if (!File.Exists(Log.FilePath))
        {
            await DisplayAlertAsync("No log yet", "Nothing has been logged yet.", "OK");
            return;
        }
        await Share.Default.RequestAsync(new ShareFileRequest("Antenna log", new ShareFile(Log.FilePath, "text/plain")));
    }
}
