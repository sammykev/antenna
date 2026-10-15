using System.Diagnostics;
using Antenna.App.Services;
using Antenna.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>Scheduled, running and finished recordings and downloads.</summary>
public sealed partial class RecordingsPage : Page
{
    public RecordingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Recorder.Changed += Refresh;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Recorder.Changed -= Refresh;
    }

    private void Refresh()
    {
        var all = Recorder.All();
        RecordingList.Items.Clear();
        foreach (var rec in all)
            RecordingList.Items.Add(Row(rec));
        Empty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement Row(Recording rec)
    {
        var grid = new Grid { Padding = new Thickness(0, 10, 0, 10), ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = rec.IsDownload ? "" : "";
        grid.Children.Add(new FontIcon { Glyph = glyph, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });

        var info = new StackPanel { Spacing = 3 };
        info.Children.Add(new TextBlock { Text = rec.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = Describe(rec), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (rec.Status == RecordingStatus.Recording)
        {
            var bar = new ProgressBar { MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, Width = 360 };
            if (rec.IsDownload && rec.TotalBytes is long total && total > 0)
                bar.Value = 100.0 * rec.BytesWritten / total;
            else if (!rec.IsDownload && rec.Stop is { } stop && stop > rec.Start)
                bar.Value = 100.0 * Math.Clamp((DateTimeOffset.Now - rec.Start) / (stop - rec.Start), 0, 1);
            else
                bar.IsIndeterminate = true;
            info.Children.Add(bar);
        }
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (rec.Status == RecordingStatus.Completed && File.Exists(rec.Path))
        {
            actions.Children.Add(MakeButton("Play", () => Playback.Play(PlayRequest.ForFile(rec.Title, rec.Path))));
            actions.Children.Add(MakeButton("Show file", () => Process.Start("explorer.exe", $"/select,\"{rec.Path}\"")));
        }
        if (rec.Status is RecordingStatus.Recording or RecordingStatus.Scheduled)
            actions.Children.Add(MakeButton(rec.Status == RecordingStatus.Scheduled ? "Cancel" : "Stop", () => Recorder.Stop(rec.Id)));
        else
            actions.Children.Add(MakeButton("Delete", async () => await DeleteAsync(rec)));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return grid;
    }

    private static string Describe(Recording rec)
    {
        var size = rec.BytesWritten > 0 ? $" · {rec.BytesWritten / 1024.0 / 1024.0:0.0} MB" : "";
        var when = rec.Stop is { } stop && !rec.IsDownload
            ? $"{rec.Start.LocalDateTime:ddd d MMM HH:mm}–{stop.LocalDateTime:HH:mm}"
            : rec.Start.LocalDateTime.ToString("ddd d MMM HH:mm");
        var state = rec.Status switch
        {
            RecordingStatus.Scheduled => "Scheduled",
            RecordingStatus.Recording => rec.IsDownload ? "Downloading" : "Recording",
            RecordingStatus.Completed => "Done",
            RecordingStatus.Cancelled => "Cancelled",
            _ => "Failed" + (rec.Error is { } err ? $": {err}" : ""),
        };
        return $"{state} · {when}{size}";
    }

    private async Task DeleteAsync(Recording rec)
    {
        var hasFile = File.Exists(rec.Path);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete “{rec.Title}”?",
            Content = hasFile ? "Remove it from the list and delete the file?" : "Remove it from the list?",
            PrimaryButtonText = hasFile ? "Delete file" : "Remove",
            SecondaryButtonText = hasFile ? "Keep file" : "",
            CloseButtonText = "Cancel",
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
            return;
        Recorder.Delete(rec, deleteFile: result == ContentDialogResult.Primary);
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.RecordingsFolder);
        Process.Start("explorer.exe", $"\"{AppSettings.RecordingsFolder}\"");
    }
}
