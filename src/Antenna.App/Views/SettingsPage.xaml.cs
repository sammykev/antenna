using Antenna.App.Services;
using Antenna.Core;
using Antenna.Core.Backup;
using Antenna.Core.Library;
using Antenna.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Antenna.App.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly (string Tag, string Label)[] StartPages =
        [("live", "Live TV"), ("guide", "Guide"), ("favourites", "My stuff"), ("movies", "Movies"), ("series", "Series")];

    private static readonly (int Hours, string Label)[] RefreshOptions =
        [(0, "Off"), (6, "Every 6 hours"), (12, "Every 12 hours"), (24, "Every day"), (168, "Every week")];

    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;

        ThemeChoice.SelectedIndex = AppSettings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        StartPageChoice.ItemsSource = StartPages.Select(p => p.Label).ToList();
        StartPageChoice.SelectedIndex = Math.Max(0, Array.FindIndex(StartPages, p => p.Tag == AppSettings.StartPage));
        SplashToggle.IsOn = AppSettings.ShowSplash;
        CachingBox.Value = AppSettings.NetworkCachingMs;
        HardwareToggle.IsOn = AppSettings.HardwareDecoding;
        TimeshiftToggle.IsOn = AppSettings.Timeshift;
        PreloadToggle.IsOn = AppSettings.PreloadNextChannel;
        RefreshChoice.ItemsSource = RefreshOptions.Select(o => o.Label).ToList();
        var refreshIndex = Array.FindIndex(RefreshOptions, o => o.Hours == AppSettings.AutoRefreshHours);
        RefreshChoice.SelectedIndex = refreshIndex >= 0 ? refreshIndex : 3;
        ApiKeyBox.Text = AppSettings.OpenSubtitlesApiKey ?? "";
        TmdbKeyBox.Text = AppSettings.TmdbApiKey ?? "";
        SubsLangBox.Text = AppSettings.SubtitleLanguages;
        SubsUserBox.Text = AppSettings.OpenSubtitlesUser ?? "";
        SubsPassBox.Password = AppSettings.OpenSubtitlesPassword ?? "";
        FolderBox.Text = AppSettings.RecordingsFolder;
        AutoUpdateSwitch.IsOn = AppSettings.AutoUpdate;
        ShowUpdateState();

        _loading = false;
        RefreshLists();
        RefreshPinState();
        _ = ShowAboutAsync();
    }

    private async Task ShowAboutAsync()
    {
        var cacheMb = await Task.Run(() => ImageCache.SizeBytes() / 1024.0 / 1024.0);
        ClearImagesButton.Content = $"Clear image cache ({cacheMb:0} MB)";
        AboutLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Logo-256.png")));
        AboutText.Text = $"Antenna {Updater.CurrentVersion}\nData: {Path.GetDirectoryName(LibraryStore.DefaultPath())}\n" +
                         "Video playback by LibVLC (VideoLAN, LGPL-2.1-or-later).";
    }

    // ---- Updates ----

    private void ShowUpdateState()
    {
        var installed = Updater.IsInstalled;
        AutoUpdateSwitch.IsEnabled = installed;
        CheckUpdatesButton.IsEnabled = installed;
        RestartUpdateButton.Visibility = Updater.Ready is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateText.Text = !installed
            ? $"Version {Updater.CurrentVersion}. Updates work when Antenna is installed with Setup.exe from {Updater.RepositoryUrl}/releases."
            : Updater.Ready is { } ready
                ? $"Version {Updater.CurrentVersion}. Version {ready.TargetFullRelease.Version} is downloaded and installs when you restart."
                : $"Version {Updater.CurrentVersion}.";
    }

    private void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            AppSettings.AutoUpdate = AutoUpdateSwitch.IsOn;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            UpdateText.Text = "Checking…";
            if (await Updater.CheckAsync() is not { } update)
            {
                ShowUpdateState();
                UpdateText.Text += " You have the latest version.";
                return;
            }
            UpdateText.Text = $"Downloading version {update.TargetFullRelease.Version}…";
            UpdateProgress.Value = 0;
            UpdateProgress.Visibility = Visibility.Visible;
            await Updater.DownloadAsync(update, percent => DispatcherQueue.TryEnqueue(() => UpdateProgress.Value = percent));
            ShowUpdateState();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "updates: manual check");
            ShowUpdateState();
            Show(InfoBarSeverity.Warning, $"Couldn't check for updates: {ex.Message}");
        }
        finally
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.IsEnabled = Updater.IsInstalled;
        }
    }

    private void RestartUpdate_Click(object sender, RoutedEventArgs e) => Updater.RestartToUpdate();

    private void Show(InfoBarSeverity severity, string message)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;
    }

    // ---- Appearance / playback / library ----

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        AppSettings.Theme = ThemeChoice.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        App.Window.ApplyTheme();
    }

    private void Splash_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            AppSettings.ShowSplash = SplashToggle.IsOn;
    }

    private void StartPage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && StartPageChoice.SelectedIndex >= 0)
            AppSettings.StartPage = StartPages[StartPageChoice.SelectedIndex].Tag;
    }

    private void Caching_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loading && !double.IsNaN(args.NewValue))
            AppSettings.NetworkCachingMs = (int)args.NewValue;
    }

    private void Preload_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            AppSettings.PreloadNextChannel = PreloadToggle.IsOn;
    }

    private void Timeshift_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            AppSettings.Timeshift = TimeshiftToggle.IsOn;
    }

    private void Hardware_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            AppSettings.HardwareDecoding = HardwareToggle.IsOn;
    }

    private void Refresh_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && RefreshChoice.SelectedIndex >= 0)
            AppSettings.AutoRefreshHours = RefreshOptions[RefreshChoice.SelectedIndex].Hours;
    }

    private void RefreshLists()
    {
        FillFlagList(HiddenList, HiddenEmpty, GroupFlag.Hidden, "Unhide", needsPin: false);
        FillFlagList(LockedList, LockedEmpty, GroupFlag.Locked, "Unlock", needsPin: true);
    }

    private void FillFlagList(ListView list, TextBlock empty, GroupFlag flag, string action, bool needsPin)
    {
        var groups = AppServices.Library.GetFlaggedGroups(flag);
        list.Items.Clear();
        foreach (var (kind, group) in groups)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = kind switch { MediaKind.Live => "Live TV", MediaKind.Movie => "Movies", _ => "Series" };
            row.Children.Add(new TextBlock { Text = $"{(group.Length == 0 ? "Uncategorised" : group)}  ·  {label}", VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = action };
            Grid.SetColumn(button, 1);
            button.Click += async (_, _) =>
            {
                if (needsPin && !await Parental.EnsureUnlockedAsync(XamlRoot))
                    return;
                AppServices.Library.SetGroupFlag(kind, group, flag, false);
                Parental.Apply();
                RefreshLists();
            };
            row.Children.Add(button);
            list.Items.Add(row);
        }
        empty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(AppServices.Library.ClearHistory);
        Show(InfoBarSeverity.Success, "Watch history cleared.");
    }

    private async void ClearImages_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(ImageCache.Clear);
        ClearImagesButton.Content = "Clear image cache (0 MB)";
        Show(InfoBarSeverity.Success, "Image cache cleared. Logos re-download as you browse.");
    }

    // ---- Parental ----

    private void RefreshPinState()
    {
        PinState.Text = !Parental.IsPinSet ? "No PIN set. Set one to lock categories (for example adult or late-night channels)."
            : Parental.IsLocked ? "PIN set. Locked categories are hidden."
            : "PIN set. Locked categories are visible until you lock again or restart Antenna.";
        SetPinButton.Content = Parental.IsPinSet ? "Change PIN" : "Set PIN";
        RemovePinButton.Visibility = Parental.IsPinSet ? Visibility.Visible : Visibility.Collapsed;
        LockNowButton.Visibility = Parental.IsPinSet && !Parental.IsLocked ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SetPin_Click(object sender, RoutedEventArgs e)
    {
        if (Parental.IsPinSet && !await Parental.EnsureUnlockedAsync(XamlRoot))
            return;
        var first = await Parental.AskPinAsync(XamlRoot, "New PIN", "Choose 4–8 digits.");
        if (first is null)
            return;
        var second = await Parental.AskPinAsync(XamlRoot, "Confirm PIN");
        if (second != first)
        {
            Show(InfoBarSeverity.Error, "The PINs didn't match. Nothing was changed.");
            return;
        }
        Parental.SetPin(first);
        RefreshPinState();
        Show(InfoBarSeverity.Success, "PIN saved. Right-click a category to lock it.");
    }

    private async void RemovePin_Click(object sender, RoutedEventArgs e)
    {
        if (!await Parental.EnsureUnlockedAsync(XamlRoot))
            return;
        Parental.SetPin(null);
        RefreshPinState();
        Show(InfoBarSeverity.Informational, "PIN removed. Locked categories are visible to everyone until you set a new PIN.");
    }

    private void LockNow_Click(object sender, RoutedEventArgs e)
    {
        Parental.Lock();
        RefreshPinState();
    }

    // ---- Subtitles / recordings ----

    private void Tmdb_LostFocus(object sender, RoutedEventArgs e) => AppSettings.TmdbApiKey = TmdbKeyBox.Text;

    private void Subtitles_LostFocus(object sender, RoutedEventArgs e)
    {
        AppSettings.OpenSubtitlesApiKey = ApiKeyBox.Text;
        AppSettings.SubtitleLanguages = string.IsNullOrWhiteSpace(SubsLangBox.Text) ? "en" : SubsLangBox.Text.Trim();
        AppSettings.OpenSubtitlesUser = SubsUserBox.Text;
        AppSettings.OpenSubtitlesPassword = SubsPassBox.Password;
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            AppSettings.RecordingsFolder = folder.Path;
            FolderBox.Text = folder.Path;
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.Folder);
        System.Diagnostics.Process.Start("explorer.exe", $"\"{Log.Folder}\"");
    }

    // ---- Backup ----

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = $"antenna-backup-{DateTime.Now:yyyy-MM-dd}" };
        picker.FileTypeChoices.Add("Antenna backup", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        if (await picker.PickSaveFileAsync() is not { } file)
            return;

        var include = IncludePasswords.IsChecked == true;
        var json = await Task.Run(() => BackupService.Export(AppServices.Library, include));
        await File.WriteAllTextAsync(file.Path, json);
        Show(InfoBarSeverity.Success, $"Backup saved to {file.Path}.");
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        if (await picker.PickSingleFileAsync() is not { } file)
            return;

        try
        {
            var json = await File.ReadAllTextAsync(file.Path);
            var restored = await Task.Run(() => BackupService.Import(AppServices.Library, json));
            Parental.Apply();
            RefreshLists();
            Show(InfoBarSeverity.Informational, $"Restored {restored.Count} playlist(s). Refreshing them in the background…");

            var failures = 0;
            foreach (var source in restored)
            {
                try { await AppServices.RefreshSourceAsync(source); }
                catch (Exception) { failures++; }
            }
            Show(failures == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                failures == 0 ? "Backup restored and playlists refreshed." : $"Backup restored; {failures} playlist(s) couldn't refresh (check passwords in Playlists).");
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            Show(InfoBarSeverity.Error, $"Couldn't restore: {ex.Message}");
        }
    }
}
