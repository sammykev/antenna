using Antenna.App.ViewModels;
using Antenna.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;

namespace Antenna.App.Views;

public sealed partial class AddPlaylistDialog : ContentDialog
{
    private static readonly string[] Palette = ["#4F7CFF", "#E5484D", "#30A46C", "#F5A524", "#8E4EC6", "#12A594", "#E54D9E", "#6E6E6E"];

    internal PlaylistSource? Result { get; private set; }

    public AddPlaylistDialog()
    {
        InitializeComponent();
        foreach (var hex in Palette)
            ColorChoice.Items.Add(new Ellipse { Width = 24, Height = 24, Fill = Ui.Brush(hex), Tag = hex });
        ColorChoice.SelectedIndex = 0;
    }

    private int Kind => KindChoice.SelectedIndex; // 0 = M3U URL, 1 = M3U file, 2 = Xtream

    private void KindChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UrlBox is null)
            return; // Fires during InitializeComponent, before the panels exist.
        UrlBox.Visibility = Kind == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        FilePanel.Visibility = Kind == 1 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        XtreamPanel.Visibility = Kind == 2 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private async void Browse_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".m3u");
        picker.FileTypeFilter.Add(".m3u8");
        // Unpackaged apps must tell the picker which window owns it.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;
        FileBox.Text = file.Path;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(file.Path);
    }

    private static bool IsValidServer(string server)
    {
        try
        {
            Antenna.Core.Playlists.XtreamClient.NormalizeServer(server);
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? error = null;
        string location = Kind switch { 0 => UrlBox.Text.Trim(), 1 => FileBox.Text.Trim(), _ => ServerBox.Text.Trim() };

        if (location.Length == 0)
            error = Kind switch { 0 => "Enter the playlist URL.", 1 => "Choose a playlist file.", _ => "Enter the server address." };
        else if (Kind == 0 && !Uri.TryCreate(location, UriKind.Absolute, out _))
            error = "The playlist URL is not valid.";
        else if (Kind == 2 && !IsValidServer(location))
            error = "The server address is not valid.";
        else if (Kind == 2 && (UserBox.Text.Trim().Length == 0 || PasswordBox.Password.Length == 0))
            error = "Enter the username and password from your provider.";

        if (error is not null)
        {
            Error.Message = error;
            Error.IsOpen = true;
            args.Cancel = true;
            return;
        }

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            var url = Kind == 2 ? Antenna.Core.Playlists.XtreamClient.NormalizeServer(location) : location;
            name = Kind == 1 ? System.IO.Path.GetFileNameWithoutExtension(location)
                : Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host
                : location;
        }

        Result = new PlaylistSource
        {
            Name = name,
            Kind = Kind == 2 ? SourceKind.Xtream : SourceKind.M3u,
            Location = location,
            Username = Kind == 2 ? UserBox.Text.Trim() : null,
            Password = Kind == 2 ? PasswordBox.Password : null,
            EpgUrl = string.IsNullOrWhiteSpace(EpgBox.Text) ? null : EpgBox.Text.Trim(),
            Color = (ColorChoice.SelectedItem as Ellipse)?.Tag as string ?? Palette[0],
        };
    }
}
