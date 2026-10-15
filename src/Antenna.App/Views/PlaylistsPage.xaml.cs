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

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SourceViewModel vm })
            return;
        var dialog = new AddPlaylistDialog(vm.Source) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Result is null)
            return;

        AppServices.Library.SaveSource(dialog.Result);
        Reload();
        // Only re-download when what's fetched changed; renames and colours apply immediately.
        var old = vm.Source;
        var changed = old.Location != dialog.Result.Location || old.Username != dialog.Result.Username ||
                      old.Password != dialog.Result.Password || old.EpgUrl != dialog.Result.EpgUrl || old.Kind != dialog.Result.Kind;
        if (changed)
            await RefreshAsync(dialog.Result);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int delta)
    {
        if (sender is FrameworkElement { Tag: SourceViewModel vm })
        {
            AppServices.Library.MoveSource(vm.Source.Id, delta);
            Reload();
        }
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
            var result = await AppServices.RefreshSourceAsync(source);
            if (result.GuideError is { } guideError)
                Show(InfoBarSeverity.Warning, $"{source.Name}: loaded {result.Items:N0} items, but the guide failed: {guideError}");
            else
                Show(InfoBarSeverity.Success, $"{source.Name}: loaded {result.Items:N0} items and {result.Programmes:N0} guide entries.");
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
