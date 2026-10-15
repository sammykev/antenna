using System.Globalization;
using Antenna.App.Services;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Antenna.App.Views;

/// <summary>Picture and sound fixes for awkward streams: aspect ratio/zoom, audio delay, deinterlacing, stats.</summary>
public sealed partial class PlayerPage
{
    /// <summary>Label, then the libvlc aspect ratio (null = the stream's own). "fill" crops to the window.</summary>
    private static readonly (string Label, string? Value)[] Aspects =
    [
        ("Auto", null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("Zoom to fill", "fill"),
    ];

    private static readonly TimeSpan AudioDelayStep = TimeSpan.FromMilliseconds(100);

    private int _aspect;
    private bool _deinterlace;
    private DispatcherQueueTimer? _statsTimer;

    private void PictureMenu_Opening(object sender, object e)
    {
        PictureMenu.Items.Clear();
        if (_player is not { } p)
            return;

        var aspect = new MenuFlyoutSubItem { Text = $"Aspect ratio: {Aspects[_aspect].Label} (Z)" };
        for (var i = 0; i < Aspects.Length; i++)
        {
            var index = i;
            var item = new RadioMenuFlyoutItem { Text = Aspects[i].Label, GroupName = "aspect", IsChecked = i == _aspect };
            item.Click += (_, _) => SetAspect(index);
            aspect.Items.Add(item);
        }
        PictureMenu.Items.Add(aspect);

        var delayMs = p.AudioDelay / 1000;
        var delay = new MenuFlyoutSubItem { Text = $"Audio delay: {FormatDelay(delayMs)}" };
        delay.Items.Add(DelayItem($"Sound earlier (−{AudioDelayStep.TotalMilliseconds:0} ms)", -AudioDelayStep));
        delay.Items.Add(DelayItem($"Sound later (+{AudioDelayStep.TotalMilliseconds:0} ms)", AudioDelayStep));
        var reset = new MenuFlyoutItem { Text = "Reset", IsEnabled = delayMs != 0 };
        reset.Click += (_, _) => SetAudioDelay(TimeSpan.Zero);
        delay.Items.Add(reset);
        PictureMenu.Items.Add(delay);

        var deinterlace = new ToggleMenuFlyoutItem { Text = "Deinterlace (fixes combing on sports)", IsChecked = _deinterlace };
        deinterlace.Click += (_, _) => SetDeinterlace(!_deinterlace);
        PictureMenu.Items.Add(deinterlace);

        PictureMenu.Items.Add(new MenuFlyoutSeparator());
        var stats = new ToggleMenuFlyoutItem { Text = "Stream stats (S)", IsChecked = StatsPanel.Visibility == Visibility.Visible };
        stats.Click += (_, _) => ToggleStats();
        PictureMenu.Items.Add(stats);
    }

    private MenuFlyoutItem DelayItem(string text, TimeSpan step)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) =>
        {
            if (_player is { } p)
                SetAudioDelay(TimeSpan.FromMilliseconds(p.AudioDelay / 1000.0) + step);
        };
        return item;
    }

    private static string FormatDelay(long ms) => ms == 0 ? "none" : $"{(ms > 0 ? "+" : "−")}{Math.Abs(ms)} ms";

    private void SetAspect(int index)
    {
        _aspect = index;
        ApplyPicture();
        ShowStatusBriefly($"Aspect ratio: {Aspects[index].Label}");
    }

    private void SetAudioDelay(TimeSpan delay)
    {
        if (_player is not { } p)
            return;
        var us = (long)(delay.TotalMilliseconds * 1000);
        Worker.Run(() => p.SetAudioDelay(us), "audio delay");
        ShowStatusBriefly($"Audio delay: {FormatDelay(us / 1000)}");
    }

    private void SetDeinterlace(bool on)
    {
        _deinterlace = on;
        ApplyPicture();
        ShowStatusBriefly(on ? "Deinterlacing on" : "Deinterlacing off");
    }

    /// <summary>Re-applies the picture choices; also called when a new stream starts, since libvlc resets some per stream.</summary>
    private void ApplyPicture()
    {
        if (_player is not { } p)
            return;
        var value = Aspects[_aspect].Value;
        string? crop = null;
        string? ratio = value;
        if (value == "fill")
        {
            // Crop the picture to the window's shape so it fills it without black bars.
            var w = Math.Max(1, (int)Video.ActualWidth);
            var h = Math.Max(1, (int)Video.ActualHeight);
            crop = $"{w}:{h}";
            ratio = null;
        }
        var deinterlace = _deinterlace ? "yadif" : null;
        Worker.Run(() =>
        {
            p.AspectRatio = ratio;
            p.CropGeometry = crop;
            p.SetDeinterlace(deinterlace);
        }, "picture settings");
    }

    private void ToggleStats()
    {
        var show = StatsPanel.Visibility != Visibility.Visible;
        StatsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_statsTimer is null)
        {
            _statsTimer = DispatcherQueue.CreateTimer();
            _statsTimer.Interval = TimeSpan.FromSeconds(1);
            _statsTimer.IsRepeating = true;
            _statsTimer.Tick += (_, _) => UpdateStats();
        }
        if (show)
        {
            UpdateStats();
            _statsTimer.Start();
        }
        else
        {
            _statsTimer.Stop();
        }
    }

    private void UpdateStats()
    {
        if (_player is not { Media: { } media } p)
        {
            StatsText.Text = "No stream";
            return;
        }
        var s = media.Statistics;
        uint width = 0, height = 0;
        p.Size(0, ref width, ref height);
        var lines = new List<string>
        {
            width > 0 ? $"Video     {width}×{height}" : "Video     –",
            // libvlc reports bitrates in kilobytes per millisecond.
            $"Bitrate   {s.DemuxBitrate * 8000:0} kb/s",
            $"Frames    {s.DisplayedPictures:N0} shown, {s.LostPictures:N0} dropped",
            $"Audio     {s.LostAudioBuffers:N0} lost buffers",
        };
        if (_timeshift is { Input: { } input } ts)
            lines.Add($"Buffer    {Behind(ts.Buffer.BehindLive(input.Position))} behind live, {Behind(ts.Buffer.BehindLive(ts.Buffer.Start))} kept");
        StatsText.Text = string.Join("\n", lines);
    }

    private void ShowStatusBriefly(string message)
    {
        ShowStatus(message);
        var hide = DispatcherQueue.CreateTimer();
        hide.Interval = TimeSpan.FromSeconds(1.5);
        hide.IsRepeating = false;
        hide.Tick += (_, _) =>
        {
            if (StatusText.Text == message)
                StatusPanel.Visibility = Visibility.Collapsed;
        };
        hide.Start();
    }

    private void CycleAspect_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        Handle(args, () => SetAspect((_aspect + 1) % Aspects.Length));

    private void ToggleStats_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, ToggleStats);
}
