using Antenna.App.ViewModels;
using Antenna.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;

namespace Antenna.App.Views;

public sealed partial class AddPlaylistDialog : ContentDialog
{
    private static readonly string[] Palette = ["#4F7CFF", "#E5484D", "#30A46C", "#F5A524", "#8E4EC6", "#12A594", "#E54D9E", "#6E6E6E"];

    /// <summary>TV, film, globe, sport, music, kids, news, star, heart, home.</summary>
    private static readonly string[] Icons = ["\uE7F4", "\uE8B2", "\uE774", "\uE805", "\uE8D6", "\uE716", "\uE789", "\uE734", "\uEB51", "\uE80F"];

    private readonly PlaylistSource? _editing;

    internal PlaylistSource? Result { get; private set; }

    public AddPlaylistDialog() : this(null)
    {
    }

    /// <summary>With a source, the dialog edits it (keeping its id, favourites and history).</summary>
    internal AddPlaylistDialog(PlaylistSource? editing)
    {
        InitializeComponent();
        _editing = editing;
        foreach (var hex in Palette)
            ColorChoice.Items.Add(new Ellipse { Width = 24, Height = 24, Fill = Ui.Brush(hex), Tag = hex });
        foreach (var glyph in Icons)
            IconChoice.Items.Add(new FontIcon { Glyph = glyph, FontSize = 20, Tag = glyph });
        ColorChoice.SelectedIndex = 0;
        IconChoice.SelectedIndex = 0;

        if (editing is null)
            return;

        Title = "Edit playlist";
        PrimaryButtonText = "Save";
        NameBox.Text = editing.Name;
        EpgBox.Text = editing.EpgUrl ?? "";
        ColorChoice.SelectedIndex = Math.Max(0, Array.IndexOf(Palette, editing.Color));
        IconChoice.SelectedIndex = Math.Max(0, Array.IndexOf(Icons, editing.Icon));
        if (editing.Kind == SourceKind.Xtream)
        {
            KindChoice.SelectedIndex = 2;
            ServerBox.Text = editing.Location;
            UserBox.Text = editing.Username ?? "";
            PasswordBox.Password = editing.Password ?? "";
        }
        else if (Uri.TryCreate(editing.Location, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            KindChoice.SelectedIndex = 0;
            UrlBox.Text = editing.Location;
        }
        else
        {
            KindChoice.SelectedIndex = 1;
            FileBox.Text = editing.Location;
        }
        KindChoice_SelectionChanged(KindChoice, null!);
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
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N"),
            LastRefreshed = _editing?.LastRefreshed,
            Icon = (IconChoice.SelectedItem as FontIcon)?.Tag as string ?? Icons[0],
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
