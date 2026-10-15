using Antenna.App.Services;
using Antenna.Core.Playback;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Antenna.App.Views;

/// <summary>
/// Pause and rewind for live TV. The stream is downloaded into a <see cref="TimeshiftBuffer"/> on disk
/// and LibVLC plays from it, so pausing keeps recording and rewinding is a jump within the buffer.
/// Streams that can't be buffered (HLS) or that fail to start this way play directly as before.
/// </summary>
public sealed partial class PlayerPage
{
    private static readonly TimeSpan LiveTolerance = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan LiveStartBehind = TimeSpan.FromSeconds(1);

    private sealed class Timeshift
    {
        public required string Url;
        public required TimeshiftBuffer Buffer;
        public required LiveCapture Capture;
        public required CancellationTokenSource Cancel;
        public readonly List<TimeshiftInput> Inputs = [];
        public TimeshiftInput? Input;
    }

    private Timeshift? _timeshift;
    private string? _directOnlyUrl;
    private DispatcherQueueTimer? _behindTimer;

    /// <summary>Folder for this session's buffers; leftovers from a crash are removed at start-up.</summary>
    internal static string TimeshiftRoot => Path.Combine(Path.GetTempPath(), "Antenna", "timeshift");

    internal static void CleanUpTimeshiftFolders()
    {
        try
        {
            if (Directory.Exists(TimeshiftRoot))
                Directory.Delete(TimeshiftRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Info($"timeshift: couldn't clear old buffers: {ex.Message}");
        }
    }

    private bool UseTimeshift(PlayRequest request) =>
        request.IsLive && AppSettings.Timeshift && request.Url != _directOnlyUrl && LiveCapture.CanBuffer(request.Url);

    /// <summary>Starts (or reuses) the buffer for this channel, then plays from it; falls back to direct play.</summary>
    private async void StartTimeshifted(PlayRequest request, MediaPlayer player, LibVLC vlc, IReadOnlyList<string> options)
    {
        var ts = _timeshift;
        // A buffer whose download has ended (provider gave up) can't be resumed: start a fresh one.
        if (ts is null || ts.Url != request.Url || ts.Buffer.IsCompleted)
        {
            StopTimeshift();
            var buffer = new TimeshiftBuffer(Path.Combine(TimeshiftRoot, Guid.NewGuid().ToString("N")), TimeSpan.FromMinutes(AppSettings.TimeshiftMinutes));
            ts = new Timeshift
            {
                Url = request.Url,
                Buffer = buffer,
                Capture = new LiveCapture(AppServices.Http, request.Url, request.Options, buffer),
                Cancel = new CancellationTokenSource(),
            };
            _timeshift = ts;
            var capture = ts.Capture;
            var cancel = ts.Cancel.Token;
            _ = Task.Run(() => capture.RunAsync(cancel));
            Log.Info($"timeshift: buffering '{request.Title}'");
        }

        bool started;
        try
        {
            started = await ts.Capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            started = false;
        }
        if (!ReferenceEquals(ts, _timeshift) || !ReferenceEquals(request, _request) || _closing)
            return;
        if (!started)
        {
            Log.Info($"timeshift: can't buffer this stream ({ts.Capture.Error ?? "no data"}); playing it directly");
            _directOnlyUrl = request.Url;
            StopTimeshift();
            StartDirect(request, player, vlc, options);
            return;
        }

        PlayFrom(ts, ts.Buffer.OffsetBehindLive(LiveStartBehind), player, vlc, options);
        StartBehindTimer();
    }

    private void PlayFrom(Timeshift ts, long offset, MediaPlayer player, LibVLC vlc, IReadOnlyList<string> options)
    {
        var input = new TimeshiftInput(ts.Buffer, offset);
        ts.Inputs.Add(input);
        ts.Input = input;
        // Options that only matter for network access are left out: the data is local now.
        var local = options.Where(o => !o.StartsWith(":network-caching") && !o.StartsWith(":http-")).Append(":file-caching=500").ToArray();
        Worker.Run(() =>
        {
            using var media = new Media(vlc, input, local);
            player.Play(media);
        }, "timeshift play");
    }

    /// <summary>Jumps by <paramref name="offset"/> within the buffer (or back to live when jumping past it).</summary>
    private bool TimeshiftSeek(TimeSpan offset)
    {
        if (_timeshift is not { Input: { } input } ts || _player is not { } player || _vlc is not { } vlc || _request is not { } request)
            return false;
        var behind = ts.Buffer.BehindLive(input.Position) - offset;
        if (behind <= LiveTolerance)
            behind = LiveStartBehind;
        var target = ts.Buffer.OffsetBehindLive(behind);
        Log.Info($"timeshift: jump to {Behind(ts.Buffer.BehindLive(target))} behind live");
        PlayFrom(ts, target, player, vlc, BaseOptions(request));
        return true;
    }

    private void GoLive()
    {
        if (_timeshift is { Input: not null } && _player is not null)
            TimeshiftSeek(TimeSpan.FromHours(24));
    }

    private void StopTimeshift()
    {
        _behindTimer?.Stop();
        if (_timeshift is not { } ts)
            return;
        _timeshift = null;
        ts.Cancel.Cancel();
        // LibVLC may still be finishing a read from the old input: release it a little later.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            ts.Buffer.Dispose();
            foreach (var input in ts.Inputs)
                input.Dispose();
        });
        SetLiveBadge(TimeSpan.Zero);
    }

    private void StartBehindTimer()
    {
        if (_behindTimer is null)
        {
            _behindTimer = DispatcherQueue.CreateTimer();
            _behindTimer.Interval = TimeSpan.FromSeconds(1);
            _behindTimer.IsRepeating = true;
            _behindTimer.Tick += (_, _) =>
            {
                if (_timeshift is { Input: { } input } ts)
                    SetLiveBadge(ts.Buffer.BehindLive(input.Position));
            };
        }
        _behindTimer.Start();
    }

    private void SetLiveBadge(TimeSpan behind)
    {
        var live = behind <= LiveTolerance;
        LiveBadge.Text = live ? "● LIVE" : $"● −{Behind(behind)} · Go live";
        LiveBadge.Foreground = new SolidColorBrush(live ? Microsoft.UI.ColorHelper.FromArgb(255, 0xFF, 0x5A, 0x5F) : Microsoft.UI.ColorHelper.FromArgb(255, 0xC8, 0xC8, 0xC8));
        ToolTipService.SetToolTip(LiveBadge, live ? null : "Back to live");
    }

    private static string Behind(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void LiveBadge_Tapped(object sender, TappedRoutedEventArgs e)
    {
        GoLive();
        e.Handled = true;
    }
}
