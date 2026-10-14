using System.Globalization;
using Antenna.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Antenna.App.ViewModels;

internal static class Ui
{
    public static SolidColorBrush Brush(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6 && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return new SolidColorBrush(Colors.Gray);
    }

    public static ImageSource? Image(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")
            ? new BitmapImage(uri) { DecodePixelHeight = 160 }
            : null;
}

/// <summary>One tile in browse and search lists.</summary>
public sealed class ItemViewModel(LibraryEntry entry)
{
    private ImageSource? _logo;
    private bool _logoLoaded;

    internal LibraryEntry Entry { get; } = entry;
    internal MediaItem Item => Entry.Item;
    public string Title => Item.Title;
    public string Group => Item.Group;
    public string SourceName => Entry.SourceName;
    public string SourceIcon => Entry.SourceIcon;
    public SolidColorBrush SourceBrush { get; } = Ui.Brush(entry.SourceColor);

    public string Badge => Item.Kind switch
    {
        MediaKind.Live when Item.CatchupDays > 0 => $"Live · Catch-up {Item.CatchupDays}d",
        MediaKind.Live => "Live",
        MediaKind.Movie => "Movie",
        MediaKind.Series => "Series",
        _ => "",
    };

    public string Initials => string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    /// <summary>Created on first use so long virtualized lists only fetch logos for visible tiles.</summary>
    public ImageSource? Logo
    {
        get
        {
            if (!_logoLoaded)
            {
                _logo = Ui.Image(Item.LogoUrl);
                _logoLoaded = true;
            }
            return _logo;
        }
    }
}

public sealed class SourceViewModel(PlaylistSource source)
{
    internal PlaylistSource Source { get; } = source;
    public string Name => Source.Name;
    public string Icon => Source.Icon;
    public SolidColorBrush Brush { get; } = Ui.Brush(source.Color);

    public string Details =>
        (Source.Kind == SourceKind.Xtream ? $"Xtream · {Source.Username}@{Source.Location}" : $"M3U · {Source.Location}") +
        (Source.LastRefreshed is { } t ? $" · Updated {t.LocalDateTime:g}" : " · Never updated");
}

public sealed class EpisodeViewModel(Episode episode)
{
    internal Episode Episode { get; } = episode;
    public string Title => $"S{Episode.Season:00}E{Episode.Number:00} · {Episode.Title}";
    public string Plot => Episode.Plot ?? "";
}

/// <summary>Navigation parameter for the player.</summary>
public sealed record PlayRequest(string Title, string Url, bool IsLive);
