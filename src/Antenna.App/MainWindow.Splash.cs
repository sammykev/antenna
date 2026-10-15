using System.Diagnostics;
using System.Globalization;
using Antenna.App.Services;
using Antenna.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Antenna.App;

/// <summary>The start-up screen: counts what's in the library while the app gets ready.</summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan CountUp = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan MinimumShown = TimeSpan.FromMilliseconds(1800);
    private static readonly TimeSpan HoldAfterCounts = TimeSpan.FromMilliseconds(700);

    private bool _splashClosing;

    private async Task ShowSplashAsync()
    {
        Splash.Visibility = Visibility.Visible;
        SplashLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
            new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Logo-256.png")));
        var shown = Stopwatch.StartNew();

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

        SplashRing.IsActive = false;
        SplashRing.Visibility = Visibility.Collapsed;
        SplashStatus.Text = counts.Playlists switch
        {
            0 => "Add a playlist to get started",
            1 => "1 playlist ready",
            var n => $"{n} playlists ready",
        };
        await AnimateCountsAsync(counts);

        var wait = Max(MinimumShown - shown.Elapsed, HoldAfterCounts);
        await Task.Delay(wait);
        HideSplash();
    }

    /// <summary>Counts each number up from zero, easing out, so the totals land together.</summary>
    private async Task AnimateCountsAsync(LibraryCounts counts)
    {
        var clock = Stopwatch.StartNew();
        while (!_splashClosing)
        {
            var t = Math.Min(1.0, clock.Elapsed / CountUp);
            var eased = 1 - Math.Pow(1 - t, 3);
            LiveCount.Text = Format(counts.Live * eased);
            MovieCount.Text = Format(counts.Movies * eased);
            SeriesCount.Text = Format(counts.Series * eased);
            if (t >= 1)
                break;
            await Task.Delay(16);
        }
    }

    private static string Format(double value) => Math.Round(value).ToString("N0", CultureInfo.CurrentCulture);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private void Splash_Tapped(object sender, TappedRoutedEventArgs e) => HideSplash();

    private void HideSplash()
    {
        if (_splashClosing || Splash.Visibility == Visibility.Collapsed)
            return;
        _splashClosing = true;
        var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(300) };
        Storyboard.SetTarget(fade, Splash);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => Splash.Visibility = Visibility.Collapsed;
        storyboard.Begin();
    }
}
