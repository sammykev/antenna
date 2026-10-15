using System.Globalization;
using Antenna.App.Services;
using Antenna.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Antenna.App;

/// <summary>
/// The start-up screen: the library's live channel, movie and series counts. It stays until one of
/// them is clicked (which opens that section) or Continue goes to the usual start page.
/// </summary>
public sealed partial class MainWindow
{
    private string _splashTarget = "live";

    private async Task ShowSplashAsync(string startPage)
    {
        _splashTarget = startPage;
        Splash.Visibility = Visibility.Visible;
        SplashLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
            new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Logo-256.png")));
        SplashContinue.Content = $"Continue to {PageLabel(startPage)}";

        LibraryCounts counts;
        try
        {
            counts = await Task.Run(AppServices.Library.CountItems);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "start-up screen counts");
            HideSplash();
            return;
        }

        LiveCount.Text = Format(counts.Live);
        MovieCount.Text = Format(counts.Movies);
        SeriesCount.Text = Format(counts.Series);
        SplashRing.IsActive = false;
        SplashRing.Visibility = Visibility.Collapsed;
        SplashStatus.Text = counts.Playlists switch
        {
            0 => "Add a playlist to get started",
            1 => "1 playlist",
            var n => $"{n} playlists",
        };
    }

    private static string Format(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>The pane's name for a page tag, e.g. "live" -> "Live TV".</summary>
    private string PageLabel(string tag) =>
        NavView.MenuItems.Concat(NavView.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == tag)?.Content as string ?? "Live TV";

    private void SplashTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            NavigateTo(tag);
            HideSplash();
        }
    }

    private void SplashContinue_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo(_splashTarget);
        HideSplash();
    }

    private void HideSplash()
    {
        if (Splash.Visibility == Visibility.Collapsed || Splash.Opacity < 1)
            return;
        var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(200) };
        Storyboard.SetTarget(fade, Splash);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => Splash.Visibility = Visibility.Collapsed;
        storyboard.Begin();
    }
}
