using Antenna.Core;
using Antenna.Mobile.Services;

namespace Antenna.Mobile.Views;

public partial class AddPlaylistPage : ContentPage
{
    private static readonly string[] Colours = ["#4F7CFF", "#2EB67D", "#E8A33D", "#D9534F", "#9B6BFF", "#1FB5C9"];

    private string? _pickedFile;

    public AddPlaylistPage()
    {
        InitializeComponent();
        KindPicker.SelectedIndex = 0;
    }

    /// <summary>Where a playlist picked from the phone's files is kept (the original may be moved or deleted).</summary>
    public static string LocalCopyPath(string sourceId) => Path.Combine(FileSystem.AppDataDirectory, "playlists", sourceId + ".m3u");

    private bool IsXtream => KindPicker.SelectedIndex == 2;

    private bool IsFile => KindPicker.SelectedIndex == 1;

    private void KindPicker_SelectedIndexChanged(object? sender, EventArgs e)
    {
        LocationBox.IsVisible = !IsFile;
        LocationBox.Placeholder = IsXtream ? "Server (e.g. http://example.com:8080)" : "M3U link (http://…)";
        PickFileButton.IsVisible = FileText.IsVisible = IsFile;
        UserBox.IsVisible = PasswordBox.IsVisible = IsXtream;
    }

    private async void PickFile_Clicked(object? sender, EventArgs e)
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose an M3U playlist" });
        if (file is null)
            return;
        _pickedFile = file.FullPath;
        FileText.Text = file.FileName;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = Path.GetFileNameWithoutExtension(file.FileName);
    }

    private async void Save_Clicked(object? sender, EventArgs e)
    {
        ErrorText.IsVisible = false;
        var location = LocationBox.Text?.Trim() ?? "";
        string? error =
            IsFile && _pickedFile is null ? "Choose a playlist file." :
            !IsFile && !Uri.TryCreate(location, UriKind.Absolute, out _) ? "Enter the full link, starting with http:// or https://." :
            IsXtream && (string.IsNullOrWhiteSpace(UserBox.Text) || string.IsNullOrEmpty(PasswordBox.Text)) ? "Enter your username and password." :
            null;
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        var existing = MobileServices.Library.GetSources().Count;
        var id = Guid.NewGuid().ToString("N");
        if (IsFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalCopyPath(id))!);
            File.Copy(_pickedFile!, LocalCopyPath(id), overwrite: true);
            location = LocalCopyPath(id);
        }
        var epg = EpgBox.Text?.Trim();
        var source = new PlaylistSource
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? (IsXtream ? new Uri(location).Host : "Playlist") : NameBox.Text.Trim(),
            Kind = IsXtream ? SourceKind.Xtream : SourceKind.M3u,
            Location = location.TrimEnd('/'),
            Username = IsXtream ? UserBox.Text!.Trim() : null,
            Password = IsXtream ? PasswordBox.Text : null,
            EpgUrl = string.IsNullOrEmpty(epg) ? null : epg,
            Color = Colours[existing % Colours.Length],
        };

        SetBusy(true, $"Loading {source.Name}… big playlists and guides can take a minute.");
        try
        {
            MobileServices.Library.SaveSource(source);
            var result = await MobileServices.Refresher.RefreshAsync(source);
            Log.Info($"Added {source.Name}: {result.Items} items, {result.Programmes} programmes");
            if (result.GuideError is { } guideError)
                await DisplayAlertAsync("Guide not loaded", $"The channels loaded, but the guide didn't: {guideError}", "OK");
            await Shell.Current.GoToAsync("//live");
        }
        catch (Exception ex)
        {
            Log.Error($"Adding {source.Name} failed", ex);
            // Keep nothing half-added: the user can fix the details and try again.
            MobileServices.Library.DeleteSource(source.Id);
            if (IsFile)
                File.Delete(LocalCopyPath(id));
            ShowError($"Couldn't load the playlist: {ex.Message}");
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private void SetBusy(bool busy, string text)
    {
        Busy.IsRunning = busy;
        BusyText.Text = text;
        SaveButton.IsEnabled = !busy;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }
}
