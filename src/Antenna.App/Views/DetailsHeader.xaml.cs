using System.Globalization;
using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Antenna.Core.Metadata;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Antenna.App.Views;

/// <summary>The top of the movie and series pages.</summary>
public sealed partial class DetailsHeader : UserControl
{
    public DetailsHeader()
    {
        InitializeComponent();
    }

    public bool IsLoading
    {
        set => LoadingBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    internal async void Show(MediaItem item, MediaDetails? details)
    {
        TitleText.Text = item.Title;
        var facts = new[] { details?.Released?.Split('-')[0], details?.Genre, details?.Duration, details?.Rating is { } r ? $"★ {r}" : null, item.Group }
            .Where(f => !string.IsNullOrWhiteSpace(f));
        FactsText.Text = string.Join("  ·  ", facts);
        PlotText.Text = details?.Plot ?? "";
        CreditsText.Text = string.Join("\n", new[]
        {
            details?.Director is { } d ? $"Director: {d}" : null,
            details?.Cast is { } c ? $"Cast: {c}" : null,
        }.Where(x => x is not null));

        if ((details?.PosterUrl ?? item.LogoUrl) is { } poster && await ImageCache.GetAsync(poster) is { } posterPath)
            Poster.Source = Ui.FromFile(posterPath, 270);
        if (details?.BackdropUrl is { } backdrop && await ImageCache.GetAsync(backdrop) is { } backdropPath)
            Backdrop.Source = Ui.FromFile(backdropPath, 400);
    }

    /// <summary>IMDb's yellow, Rotten Tomatoes' red (fresh, 60% and up) or green (rotten) and Metacritic, as people know them.</summary>
    internal void ShowRating(FilmRating? rating)
    {
        RatingsPanel.Children.Clear();
        if (rating?.Imdb is { } imdb)
            RatingsPanel.Children.Add(Badge($"IMDb {imdb.ToString("0.0", CultureInfo.CurrentCulture)}", "#F5C518", dark: true));
        if (rating?.RottenTomatoes is { } rt)
            RatingsPanel.Children.Add(Badge($"{(rt >= 60 ? "🍅" : "🟢")} {rt}%", rt >= 60 ? "#FA320A" : "#0AC855"));
        if (rating?.Metascore is { } meta)
            RatingsPanel.Children.Add(Badge($"Metacritic {meta}", meta >= 61 ? "#00A64F" : meta >= 40 ? "#E8A200" : "#E5243B"));
        RatingsPanel.Visibility = RatingsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Border Badge(string text, string colour, bool dark = false) => new()
    {
        Background = Ui.Brush(colour),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 3, 8, 3),
        Child = new TextBlock
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White),
        },
    };

    public void ClearActions() => Actions.Children.Clear();

    public Button AddAction(string text, string glyph, Action onClick, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text });
        var button = new Button { Content = content };
        if (accent)
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => onClick();
        Actions.Children.Add(button);
        return button;
    }
}
