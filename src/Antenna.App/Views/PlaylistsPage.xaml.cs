using Antenna.App.Services;
using Antenna.App.ViewModels;
using Antenna.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

public sealed partial class PlaylistsPage : Page
{
    public PlaylistsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Reload();
    }

    private void Reload() =>
        Sources.ItemsSource = AppServices.Library.GetSources().Select(s => new SourceViewModel(s)).ToList();

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddPlaylistDialog { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Result is null)
            return;

        AppServices.Library.SaveSource(dialog.Result);
        Reload();
        await RefreshAsync(dialog.Result);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SourceViewModel vm })
            await RefreshAsync(vm.Source);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SourceViewModel vm })
            return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove “{vm.Name}”?",
            Content = "Its channels, movies and series will be removed from the library.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        await Task.Run(() => AppServices.Library.DeleteSource(vm.Source.Id));
        Reload();
    }

    private async Task RefreshAsync(PlaylistSource source)
    {
        Busy.Visibility = Visibility.Visible;
        Status.IsOpen = false;
        try
        {
            var count = await AppServices.RefreshSourceAsync(source);
            Show(InfoBarSeverity.Success, $"{source.Name}: loaded {count:N0} items.");
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Error, $"{source.Name}: {ex.Message}");
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            Reload();
        }
    }

    private void Show(InfoBarSeverity severity, string message)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;
    }
}
