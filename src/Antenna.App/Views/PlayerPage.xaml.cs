using Antenna.App.ViewModels;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Antenna.App.Views;

/// <summary>
/// LibVLC-backed player. Live streams reconnect automatically with backoff when the
/// connection drops; VOD reconnects and resumes from the last position.
/// </summary>
public sealed partial class PlayerPage : Page
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];

    private static readonly string[] MediaOptions = [":network-caching=1500", ":http-reconnect"];

    private readonly DispatcherQueueTimer _hideTimer;
    private LibVLC? _vlc;
    private MediaPlayer? _player;
    private PlayRequest? _request;
    private int _retry;
    private long _resumeAt;
    private long _lastTime;
    private bool _closing;
    private bool _updatingSeek;

    public PlayerPage()
    {
        InitializeComponent();
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(3);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            if (_player?.IsPlaying == true)
                ControlsPanel.Visibility = Visibility.Collapsed;
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _request = (PlayRequest)e.Parameter;
        TitleText.Text = _request.Title;
        LiveBadge.Visibility = _request.IsLive ? Visibility.Visible : Visibility.Collapsed;
        Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _closing = true;
        _hideTimer.Stop();
        if (App.Window.IsFullScreen)
            App.Window.SetFullScreen(false);

        var player = _player;
        var vlc = _vlc;
        _player = null;
        _vlc = null;
        // Stop can block while libvlc tears down the stream, so keep it off the UI thread.
        _ = Task.Run(() =>
        {
            player?.Stop();
            player?.Dispose();
            vlc?.Dispose();
        });
    }

    private void Video_Initialized(object? sender, InitializedEventArgs e)
    {
        _vlc = new LibVLC(enableDebugLogs: false, e.SwapChainOptions);
        _player = new MediaPlayer(_vlc);
        _player.Playing += (_, _) => OnUi(OnPlaying);
        _player.Paused += (_, _) => OnUi(() => SetPlayIcon(playing: false));
        _player.Stopped += (_, _) => OnUi(() => SetPlayIcon(playing: false));
        _player.Buffering += (_, a) => OnUi(() => BufferingRing.IsActive = a.Cache < 100f);
        _player.LengthChanged += (_, a) => OnUi(() => OnLengthChanged(a.Length));
        _player.TimeChanged += (_, a) => OnUi(() => OnTimeChanged(a.Time));
        _player.EncounteredError += (_, _) => OnUi(OnStreamFailed);
        _player.EndReached += (_, _) => OnUi(OnEndReached);
        Video.MediaPlayer = _player;
        Start();
    }

    /// <summary>Starts playback once both the request and the video surface are ready.</summary>
    private void Start()
    {
        var player = _player;
        var vlc = _vlc;
        var request = _request;
        if (player is null || vlc is null || request is null || _closing)
            return;

        BufferingRing.IsActive = true;
        // Never call into libvlc from its own event threads; the thread pool is always safe.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            using var media = new Media(vlc, request.Url, FromType.FromLocation, MediaOptions);
            player.Play(media);
        });
    }

    /// <summary>libvlc raises events on its own threads; marshal to the UI and drop them once closing.</summary>
    private void OnUi(Action action) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closing)
            action();
    });

    private void OnPlaying()
    {
        _retry = 0;
        BufferingRing.IsActive = false;
        StatusPanel.Visibility = Visibility.Collapsed;
        SetPlayIcon(playing: true);
        if (_resumeAt > 0 && _player is { IsSeekable: true } p)
        {
            var at = _resumeAt;
            _resumeAt = 0;
            ThreadPool.QueueUserWorkItem(_ => p.Time = at);
        }
        _hideTimer.Start();
    }

    private void OnLengthChanged(long length)
    {
        SeekRow.Visibility = length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DurationText.Text = Format(length);
    }

    private void OnTimeChanged(long time)
    {
        _lastTime = time;
        ElapsedText.Text = Format(time);
        if (_player is { } p)
        {
            _updatingSeek = true;
            SeekSlider.Value = p.Position * 1000;
            _updatingSeek = false;
        }
    }

    private void OnEndReached()
    {
        // A live stream "ending" means the provider dropped us.
        if (_request?.IsLive == true)
        {
            OnStreamFailed();
            return;
        }
        SetPlayIcon(playing: false);
        ControlsPanel.Visibility = Visibility.Visible;
    }

    private async void OnStreamFailed()
    {
        BufferingRing.IsActive = false;
        if (_retry >= RetryDelays.Length)
        {
            ShowStatus("This stream is unavailable. Try again later or pick another channel.");
            ControlsPanel.Visibility = Visibility.Visible;
            return;
        }

        var delay = RetryDelays[_retry++];
        ShowStatus($"Connection lost. Reconnecting ({_retry}/{RetryDelays.Length})…");
        if (_request?.IsLive == false)
            _resumeAt = _lastTime;

        await Task.Delay(delay);
        Start();
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void SetPlayIcon(bool playing) => PlayPauseIcon.Glyph = playing ? "" : "";

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ---- Controls ----

    private void TogglePause()
    {
        if (_player is not { } p)
            return;
        if (p.IsPlaying)
            p.SetPause(true);
        else if (p.Media is null || p.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
            Start();
        else
            p.SetPause(false);
    }

    private void ToggleFullScreen()
    {
        var full = !App.Window.IsFullScreen;
        App.Window.SetFullScreen(full);
        FullScreenIcon.Glyph = full ? "" : "";
    }

    private void SeekBy(TimeSpan offset)
    {
        if (_player is { IsSeekable: true } p)
        {
            var target = Math.Clamp(p.Time + (long)offset.TotalMilliseconds, 0, Math.Max(0, p.Length));
            ThreadPool.QueueUserWorkItem(_ => p.Time = target);
        }
        ShowControls();
    }

    private void ToggleMute()
    {
        if (_player is not { } p)
            return;
        p.Mute = !p.Mute;
        MuteIcon.Glyph = p.Mute ? "" : "";
    }

    private void ShowControls()
    {
        ControlsPanel.Visibility = Visibility.Visible;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void SeekSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || _player is not { IsSeekable: true } p)
            return;
        var position = (float)(e.NewValue / 1000);
        ThreadPool.QueueUserWorkItem(_ => p.Position = position);
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_player is { } p)
            p.Volume = (int)e.NewValue;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowControls();

    private void Surface_Tapped(object sender, TappedRoutedEventArgs e) => ShowControls();

    private void Surface_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleFullScreen();

    private void TogglePause_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        TogglePause();
        args.Handled = true;
    }

    private void ToggleFullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleFullScreen();
        args.Handled = true;
    }

    private void ExitFullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (App.Window.IsFullScreen)
            ToggleFullScreen();
        args.Handled = true;
    }

    private void SeekBack_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SeekBy(TimeSpan.FromSeconds(-10));
        args.Handled = true;
    }

    private void SeekForward_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SeekBy(TimeSpan.FromSeconds(10));
        args.Handled = true;
    }

    private void ToggleMute_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleMute();
        args.Handled = true;
    }

    // ---- Tracks ----

    private void AudioMenu_Opening(object sender, object e)
    {
        AudioMenu.Items.Clear();
        if (_player is not { } p)
            return;

        var current = p.AudioTrack;
        foreach (var track in p.AudioTrackDescription)
        {
            var id = track.Id;
            var item = new RadioMenuFlyoutItem { Text = track.Name, GroupName = "audio", IsChecked = id == current };
            item.Click += (_, _) => ThreadPool.QueueUserWorkItem(_ => p.SetAudioTrack(id));
            AudioMenu.Items.Add(item);
        }
        if (AudioMenu.Items.Count == 0)
            AudioMenu.Items.Add(new MenuFlyoutItem { Text = "No audio tracks", IsEnabled = false });
    }

    private void SubtitleMenu_Opening(object sender, object e)
    {
        SubtitleMenu.Items.Clear();
        if (_player is not { } p)
            return;

        // libvlc lists "Disable" (id -1) first when any subtitle track exists.
        var current = p.Spu;
        foreach (var track in p.SpuDescription)
        {
            var id = track.Id;
            var item = new RadioMenuFlyoutItem { Text = track.Name, GroupName = "spu", IsChecked = id == current };
            item.Click += (_, _) => ThreadPool.QueueUserWorkItem(_ => p.SetSpu(id));
            SubtitleMenu.Items.Add(item);
        }
        if (SubtitleMenu.Items.Count > 0)
            SubtitleMenu.Items.Add(new MenuFlyoutSeparator());

        var load = new MenuFlyoutItem { Text = "Load subtitle file…" };
        load.Click += LoadSubtitleFile_Click;
        SubtitleMenu.Items.Add(load);
    }

    private async void LoadSubtitleFile_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not { } p)
            return;

        var picker = new FileOpenPicker();
        foreach (var ext in new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" })
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            p.AddSlave(MediaSlaveType.Subtitle, new Uri(file.Path).AbsoluteUri, select: true);
    }
}
