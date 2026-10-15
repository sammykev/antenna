using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Antenna.App.Services;
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

    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");

    public static ImageSource FromFile(string path, int decodeHeight) =>
        new BitmapImage(new Uri(path)) { DecodePixelHeight = decodeHeight, DecodePixelType = DecodePixelType.Logical };

    public static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public static string Ago(DateTimeOffset when)
    {
        var d = DateTimeOffset.Now - when;
        return d.TotalMinutes < 1 ? "just now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
            : d.TotalDays < 2 ? "yesterday"
            : when.LocalDateTime.ToString("d MMM", CultureInfo.CurrentCulture);
    }
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// An image loaded lazily through the disk cache: the first binding read starts the download,
/// and the image appears when it's ready.
/// </summary>
public sealed class CachedImage(string? url, int decodeHeight) : Observable
{
    private ImageSource? _source;
    private bool _started;

    public ImageSource? Source
    {
        get
        {
            if (!_started)
            {
                _started = true;
                Load();
            }
            return _source;
        }
    }

    private async void Load()
    {
        if (!Ui.IsWebUrl(url))
            return;
        if (ImageCache.TryGetCached(url!) is { } cached)
        {
            _source = Ui.FromFile(cached, decodeHeight);
            return;
        }
        if (await ImageCache.GetAsync(url!) is { } path)
        {
            _source = Ui.FromFile(path, decodeHeight);
            Raise(nameof(Source));
        }
    }
}

/// <summary>One tile in browse, search and favourites.</summary>
public sealed class ItemViewModel : Observable
{
    private bool? _isFavourite;

    public ItemViewModel(LibraryEntry entry)
    {
        Entry = entry;
        SourceBrush = Ui.Brush(entry.SourceColor);
        Logo = new CachedImage(entry.Item.LogoUrl, entry.Item.Kind == MediaKind.Live ? 104 : 260);
    }

    internal LibraryEntry Entry { get; }
    internal MediaItem Item => Entry.Item;
    public string Title => Item.Title;
    public string Group => Item.Group;
    public string SourceName => Entry.SourceName;
    public string SourceIcon => Entry.SourceIcon;
    public SolidColorBrush SourceBrush { get; }
    public CachedImage Logo { get; }

    /// <summary>1-based position in the list it was shown in, used as the channel number.</summary>
    public int Number { get; set; }

    public string NumberText => Item.Kind == MediaKind.Live && Number > 0 ? Number.ToString(CultureInfo.InvariantCulture) : "";

    public string Badge => Item.Kind switch
    {
        MediaKind.Live when Item.CatchupDays > 0 => $"Live · Catch-up {Item.CatchupDays}d",
        MediaKind.Live => "Live",
        MediaKind.Movie => "Movie",
        MediaKind.Series => "Series",
        _ => "",
    };

    /// <summary>What's on now, for live channels with a guide.</summary>
    internal Programme? Now { get; set; }

    public string NowText => Now is { } p ? $"Now: {p.Title}" : "";

    public double NowProgress => Now is { } p && p.Duration.TotalSeconds > 0
        ? Math.Clamp((DateTimeOffset.Now - p.Start) / p.Duration, 0, 1) * 100
        : 0;

    public Microsoft.UI.Xaml.Visibility NowVisibility => Now is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public string Initials => string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public bool IsFavourite
    {
        get => _isFavourite ??= AppServices.Library.IsFavourite(Item);
        set
        {
            _isFavourite = value;
            Raise();
            Raise(nameof(FavouriteGlyph));
        }
    }

    /// <summary>Filled star when favourited, nothing otherwise.</summary>
    public string FavouriteGlyph => IsFavourite ? "" : "";
}

public sealed class SourceViewModel(PlaylistSource source)
{
    internal PlaylistSource Source { get; } = source;
    public string Name => Source.Name;
    public string Icon => Source.Icon;
    public SolidColorBrush Brush { get; } = Ui.Brush(source.Color);

    public string Details =>
        (Source.Kind == SourceKind.Xtream ? $"Xtream · {Source.Username}@{Source.Location}" : $"M3U · {Source.Location}") +
        (Source.LastRefreshed is { } t ? $" · Updated {t.LocalDateTime:g}" : " · Never updated") +
        (Source.Kind == SourceKind.Xtream && Source.Password is null ? " · ⚠ Re-enter password" : "");
}

public sealed class EpisodeViewModel
{
    internal EpisodeViewModel(Episode episode, WatchRecord? watched)
    {
        Episode = episode;
        Watched = watched;
        Image = new CachedImage(episode.ImageUrl, 90);
    }

    internal Episode Episode { get; }
    internal WatchRecord? Watched { get; }
    public string Title => $"E{Episode.Number:00} · {Episode.Title}";
    public string Plot => Episode.Plot ?? "";
    public CachedImage Image { get; }
    public double Progress => (Watched?.Progress ?? 0) * 100;
    public Microsoft.UI.Xaml.Visibility ProgressVisibility =>
        Watched is { PositionMs: > 0 } ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}

/// <summary>One row in the guide's programme list.</summary>
public sealed class ProgrammeViewModel
{
    internal ProgrammeViewModel(Programme programme, bool isLive, bool canReplay)
    {
        Programme = programme;
        IsLive = isLive;
        CanReplay = canReplay;
    }

    internal Programme Programme { get; }
    internal bool IsLive { get; }
    internal bool CanReplay { get; }

    public string Time => $"{Programme.Start.LocalDateTime:HH:mm} – {Programme.Stop.LocalDateTime:HH:mm}";
    public string Title => Programme.Title;
    public string Description => Programme.Description ?? "";
    public string State => IsLive ? "● LIVE" : CanReplay ? "▶ Catch-up" : Programme.Start > DateTimeOffset.Now ? "" : "";
    public double Opacity => IsLive || CanReplay || Programme.Start > DateTimeOffset.Now ? 1.0 : 0.5;
}

/// <summary>A "continue watching" / "recently watched" row.</summary>
public sealed class WatchViewModel
{
    internal WatchViewModel(WatchRecord record)
    {
        Record = record;
        Logo = new CachedImage(record.LogoUrl, 90);
    }

    internal WatchRecord Record { get; }
    public string Title => Record.Title;
    public CachedImage Logo { get; }
    public string Initials => string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public string Details => Record.Kind == MediaKind.Live
        ? $"Live · {Ui.Ago(Record.WatchedAt)}"
        : Record.LengthMs > 0
            ? $"{Ui.Clock(Record.PositionMs)} of {Ui.Clock(Record.LengthMs)} · {Ui.Ago(Record.WatchedAt)}"
            : Ui.Ago(Record.WatchedAt);

    public double Progress => Record.Progress * 100;

    public Microsoft.UI.Xaml.Visibility ProgressVisibility =>
        Record.Kind != MediaKind.Live && Record.LengthMs > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}

/// <summary>A category in the browse list. ToString is what the list shows.</summary>
public sealed record GroupItem(string Name, bool Locked, bool IsAll = false, bool Pinned = false)
{
    public const string Uncategorised = "Uncategorised";

    /// <summary>The group value to filter by: null for "All", "" for uncategorised.</summary>
    public string? Filter => IsAll ? null : Name == Uncategorised ? "" : Name;

    public override string ToString() => (Pinned ? "📌 " : "") + (Locked ? "🔒 " : "") + Name;
}

/// <summary>A search-box suggestion. ToString is what the list shows.</summary>
public sealed class SuggestionItem(LibraryEntry entry)
{
    internal LibraryEntry Entry { get; } = entry;

    public override string ToString()
    {
        var kind = Entry.Item.Kind switch { MediaKind.Live => "Live", MediaKind.Movie => "Movie", _ => "Series" };
        return $"{Entry.Item.Title}  ·  {kind} · {Entry.SourceName}";
    }
}

/// <summary>A live/upcoming event in event search.</summary>
public sealed class EventViewModel
{
    internal EventViewModel(Antenna.Core.Events.EventHit hit, DateTimeOffset now)
    {
        Hit = hit;
        var p = hit.Programme;
        IsLive = p.IsOnAt(now);
        CanReplay = !IsLive && p.Stop <= now && hit.Channels.Any(c => Antenna.Core.Epg.Catchup.IsAvailable(c.Item, p, now));
        var first = hit.Channels[0];
        Logo = new CachedImage(first.Item.LogoUrl, 40);
        SourceBrush = Ui.Brush(first.SourceColor);
    }

    internal Antenna.Core.Events.EventHit Hit { get; }
    internal Programme Programme => Hit.Programme;
    internal bool IsLive { get; }
    internal bool CanReplay { get; }

    public string Title => Programme.Title;
    public string Description => Programme.Description ?? "";
    public CachedImage Logo { get; }
    public SolidColorBrush SourceBrush { get; }

    public string When
    {
        get
        {
            var start = Programme.Start.LocalDateTime;
            var time = $"{start:HH:mm}–{Programme.Stop.LocalDateTime:HH:mm}";
            var day = (start.Date - DateTime.Today).Days switch
            {
                0 => "Today",
                1 => "Tomorrow",
                -1 => "Yesterday",
                _ => start.ToString("ddd d MMM", CultureInfo.CurrentCulture),
            };
            return $"{day} {time}";
        }
    }

    public string State => IsLive ? "● LIVE" : CanReplay ? "▶ Catch-up" : Programme.Start > DateTimeOffset.Now ? StartsIn() : "";

    public string Channels => Hit.Channels.Count == 1
        ? $"{Hit.Channels[0].Item.Title} · {Hit.Channels[0].SourceName}"
        : $"{Hit.Channels[0].Item.Title} + {Hit.Channels.Count - 1} more";

    public double Progress => IsLive ? Math.Clamp((DateTimeOffset.Now - Programme.Start) / Programme.Duration, 0, 1) * 100 : 0;

    public Microsoft.UI.Xaml.Visibility ProgressVisibility => IsLive ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private string StartsIn()
    {
        var d = Programme.Start - DateTimeOffset.Now;
        return d.TotalMinutes < 60 ? $"in {(int)Math.Ceiling(d.TotalMinutes)} min"
            : d.TotalHours < 24 ? $"in {(int)d.TotalHours} h {d.Minutes:00} min"
            : "";
    }
}
