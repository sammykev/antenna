using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
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
