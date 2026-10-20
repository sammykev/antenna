using System.Globalization;
using Antenna.App.Services;
using Antenna.App.ViewModels;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using VirtualKey = Windows.System.VirtualKey;

namespace Antenna.App.Views;

/// <summary>
/// LibVLC-backed player. Live streams reconnect automatically with backoff when the connection
/// drops, and can be zapped with Ch+/Ch−, a typed channel number or the channel list without
/// leaving the page (so the video engine stays warm). VOD resumes where it was left.
/// </summary>
public sealed partial class PlayerPage : Page
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];

    private static readonly float[] Speeds = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f];

    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _historyTimer;
    private readonly DispatcherQueueTimer _numberTimer;
    private readonly DispatcherQueueTimer _infoTimer;
    private LibVLC? _vlc;
    private MediaPlayer? _player;
    private RendererDiscoverer? _discoverer;
    private readonly List<RendererItem> _renderers = [];
    private RendererItem? _activeRenderer;
    private PlayRequest? _request;
    private int _retry;
    private long _resumeAt;
    private long _lastTime;
    private long _lastLength;
    private bool _closing;
    private bool _updatingSeek;
    private bool _recordedStart;
    private string _typedNumber = "";
    private string? _recordingId;

    public PlayerPage()
    {
        InitializeComponent();

        _hideTimer = Timer(TimeSpan.FromSeconds(3), () =>
        {
            if (_player?.IsPlaying == true)
            {
                ControlsPanel.Visibility = Visibility.Collapsed;
                ResumePanel.Visibility = Visibility.Collapsed;
            }
        });
        _infoTimer = Timer(TimeSpan.FromSeconds(5), () => InfoPanel.Visibility = Visibility.Collapsed);
        _historyTimer = Timer(TimeSpan.FromSeconds(10), SaveProgress, repeat: true);
        _numberTimer = Timer(TimeSpan.FromSeconds(1.5), CommitTypedNumber);

        // Gamepad/remote buttons. Added in code: XAML can't parse the Gamepad* key names.
        AddKey(VirtualKey.GamepadRightShoulder, () => Zap(+1));
        AddKey(VirtualKey.GamepadLeftShoulder, () => Zap(-1));
        AddKey(VirtualKey.GamepadB, GoBack);
        AddKey(VirtualKey.GamepadMenu, () =>
        {
            if (_request is { CanZap: true })
            {
                ShowControls();
                ChannelFlyout.ShowAt(ChannelListButton);
            }
        });

        // Digit keys (top row and keypad) type a channel number.
        for (var i = 0; i <= 9; i++)
        {
            var digit = i;
            foreach (var key in new[] { VirtualKey.Number0 + i, VirtualKey.NumberPad0 + i })
            {
                var accelerator = new KeyboardAccelerator { Key = key };
                accelerator.Invoked += (_, a) =>
                {
                    TypeDigit(digit);
                    a.Handled = true;
                };
                KeyboardAccelerators.Add(accelerator);
            }
        }
    }

    private void AddKey(VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, a) =>
        {
            action();
            a.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private DispatcherQueueTimer Timer(TimeSpan interval, Action tick, bool repeat = false)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeat;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    // ---- Lifecycle ----

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Load((PlayRequest)e.Parameter);
        PlayPauseButton.Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        SaveProgress();
        _closing = true;
        foreach (var timer in new[] { _hideTimer, _historyTimer, _numberTimer, _infoTimer })
            timer.Stop();
        _statsTimer?.Stop();
        if (App.Window.IsFullScreen)
            App.Window.SetFullScreen(false);
        if (App.Window.IsMiniPlayer)
            App.Window.SetMiniPlayer(false);

        StopTimeshift();
        DropWarm();
        var player = _player;
        var vlc = _vlc;
        var discoverer = _discoverer;
        // Detach the video surface first: disposing a player that's still attached can crash natively.
        Video.MediaPlayer = null;
        _player = null;
        _vlc = null;
        _discoverer = null;
        // Stop can block while libvlc tears down the stream, so keep it off the UI thread.
        _ = Task.Run(() =>
        {
            discoverer?.Stop();
            discoverer?.Dispose();
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
        StartCastDiscovery();
        Start();
    }

    /// <summary>Shows a new request. Called on navigation and when zapping between channels.</summary>
    private void Load(PlayRequest request, bool switchingSource = false)
    {
        SaveProgress();
        if (!switchingSource)
            _triedSources.Clear();
        _request = request;
        _retry = 0;
        _resumeAt = request.StartAtMs;
        _lastTime = 0;
        _lastLength = 0;
        _recordedStart = false;

        TitleText.Text = request.Title;
        LiveBadge.Visibility = request.IsLive ? Visibility.Visible : Visibility.Collapsed;
        ZapButtons.Visibility = request.CanZap ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.Visibility = request.IsLive ? Visibility.Visible : Visibility.Collapsed;
        SpeedButton.Visibility = request.IsLive ? Visibility.Collapsed : Visibility.Visible;
        SeekRow.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        ResumePanel.Visibility = Visibility.Collapsed;
        UpdateRecordingState();
        RestoreVolume();
        _ = ShowInfoAsync();
        OnChannelLoadedForGuide();
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
        var options = BaseOptions(request);
        if (UseTimeshift(request))
        {
            StartTimeshifted(request, player, vlc, options);
            return;
        }
        StopTimeshift();
        StartDirect(request, player, vlc, options);
    }

    private static List<string> BaseOptions(PlayRequest request)
    {
        var options = new List<string>
        {
            $":network-caching={AppSettings.NetworkCachingMs}",
            ":http-reconnect",
            AppSettings.HardwareDecoding ? ":avcodec-hw=any" : ":avcodec-hw=none",
        };
        if (AppSettings.NormaliseVolume)
            options.Add(":audio-filter=normvol");
        options.AddRange(request.Options);
        return options;
    }

    private static void StartDirect(PlayRequest request, MediaPlayer player, LibVLC vlc, IReadOnlyList<string> options)
    {
        // Never call into libvlc from its own event threads; the thread pool is always safe.
        Worker.Run(() =>
        {
            using var media = new Media(vlc, request.Url, FromType.FromLocation, [.. options]);
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
        ApplySound();
        _ = PreloadNextAsync();
        if (_aspect != 0 || _deinterlace)
            ApplyPicture();

        if (_resumeAt > 0 && _player is { IsSeekable: true } p)
        {
            var at = _resumeAt;
            _resumeAt = 0;
            Worker.Run(() => p.Time = at);
            if (_request?.StartAtMs == at)
            {
                ResumeText.Text = $"Resumed from {Ui.Clock(at)}";
                ResumePanel.Visibility = Visibility.Visible;
            }
        }

        if (!_recordedStart)
        {
            _recordedStart = true;
            SaveProgress();
        }
        if (_request?.IsLive == false)
            _historyTimer.Start();
        _hideTimer.Start();
    }

    private void OnLengthChanged(long length)
    {
        _lastLength = length;
        SeekRow.Visibility = length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DurationText.Text = Ui.Clock(length);
    }

    private void OnTimeChanged(long time)
    {
        _lastTime = time;
        ElapsedText.Text = Ui.Clock(time);
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
        _historyTimer.Stop();
        if (_request?.ToWatch(_lastLength, _lastLength) is { } done)
            AppServices.Library.RecordWatch(done);
        SetPlayIcon(playing: false);
        ControlsPanel.Visibility = Visibility.Visible;
    }

    private async void OnStreamFailed()
    {
        BufferingRing.IsActive = false;
        // A live channel that keeps failing: try the same channel from another playlist.
        if (_request is { IsLive: true } && _retry >= 2 && TrySwitchSource(automatic: true))
            return;
        if (_retry >= RetryDelays.Length)
        {
            ShowStatus("This stream is unavailable. Try again later or pick another channel.");
            ControlsPanel.Visibility = Visibility.Visible;
            return;
        }

        var request = _request;
        var delay = RetryDelays[_retry++];
        ShowStatus($"Connection lost. Reconnecting ({_retry}/{RetryDelays.Length})…");
        if (request?.IsLive == false)
            _resumeAt = _lastTime;

        await Task.Delay(delay);
        if (ReferenceEquals(request, _request))
            Start();
    }

    /// <summary>Remembers what's playing (and where, for VOD) for "continue watching".</summary>
    private void SaveProgress()
    {
        if (_request is not { } request || !_recordedStart)
            return;
        if (!request.IsLive && _lastTime <= 0)
            return;
        if (request.ToWatch(_lastTime, _lastLength) is { } record)
            AppServices.Library.RecordWatch(record);
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void SetPlayIcon(bool playing) => PlayPauseIcon.Glyph = playing ? "" : "";

    // ---- Channel overlay ----

    private async Task ShowInfoAsync()
    {
        if (_request is not { } request)
            return;

        InfoTitle.Text = request.Title;
        InfoNumber.Text = request.CanZap ? (request.ChannelIndex + 1).ToString(CultureInfo.InvariantCulture) : "";
        InfoLogo.Source = null;
        InfoNowPanel.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Visible;
        _infoTimer.Stop();
        _infoTimer.Start();

        if (request.LogoUrl is { } logo && Ui.IsWebUrl(logo) && await ImageCache.GetAsync(logo) is { } path && ReferenceEquals(request, _request))
        {
            InfoLogo.Source = Ui.FromFile(path, 48);
            InfoNumber.Text = "";
        }

        if (request is { IsLive: true, SourceId: { } source, EpgChannelId: { } channel })
        {
            var (now, next) = await GuideService.NowAndNextAsync(source, channel);
            if (!ReferenceEquals(request, _request) || now is null)
                return;
            InfoNow.Text = $"{now.Start.LocalDateTime:HH:mm}–{now.Stop.LocalDateTime:HH:mm}  {now.Title}";
            InfoProgress.Value = Math.Clamp((DateTimeOffset.Now - now.Start) / now.Duration, 0, 1) * 100;
            InfoNext.Text = next is null ? "" : $"Next: {next.Start.LocalDateTime:HH:mm}  {next.Title}";
            InfoNowPanel.Visibility = Visibility.Visible;
        }
    }

    // ---- Zapping ----

    private void Zap(int delta)
    {
        if (_request is { CanZap: true } r)
        {
            _zapDirection = Math.Sign(delta);
            Load(r.WithChannel(r.ChannelIndex + delta));
        }
    }

    private void TypeDigit(int digit)
    {
        if (_request is not { CanZap: true })
            return;
        _typedNumber = (_typedNumber + digit.ToString(CultureInfo.InvariantCulture)).TrimStart('0');
        if (_typedNumber.Length > 4)
            _typedNumber = _typedNumber[^4..];
        NumberText.Text = _typedNumber.Length == 0 ? "0" : _typedNumber;
        NumberPanel.Visibility = Visibility.Visible;
        _numberTimer.Stop();
        _numberTimer.Start();
    }

    private void CommitTypedNumber()
    {
        NumberPanel.Visibility = Visibility.Collapsed;
        if (_request is { CanZap: true } r && int.TryParse(_typedNumber, out var n) && n >= 1 && n <= r.Channels.Count)
            Load(r.WithChannel(n - 1));
        _typedNumber = "";
    }

    private void ChannelFlyout_Opening(object sender, object e)
    {
        if (_request is not { CanZap: true } r)
            return;
        var now = GuideService.CachedNowPlaying();
        ChannelList.ItemsSource = r.Channels.Select((c, i) =>
            GuideService.NowOn(c, now) is { } on ? $"{i + 1}.  {c.Title}  ·  {on.Title}" : $"{i + 1}.  {c.Title}").ToList();
        ChannelList.SelectedIndex = r.ChannelIndex;
        if (r.ChannelIndex >= 0)
            ChannelList.ScrollIntoView(ChannelList.SelectedItem, ScrollIntoViewAlignment.Leading);
    }

    private void ChannelList_ItemClick(object sender, ItemClickEventArgs e)
    {
        var index = ChannelList.Items.IndexOf(e.ClickedItem);
        ChannelFlyout.Hide();
        if (_request is { CanZap: true } r && index >= 0)
            Load(r.WithChannel(index));
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
        if (App.Window.IsMiniPlayer)
            ToggleMiniPlayer();
        var full = !App.Window.IsFullScreen;
        App.Window.SetFullScreen(full);
        FullScreenIcon.Glyph = full ? "" : "";
    }

    private void ToggleMiniPlayer()
    {
        var mini = !App.Window.IsMiniPlayer;
        if (mini && App.Window.IsFullScreen)
        {
            App.Window.SetFullScreen(false);
            FullScreenIcon.Glyph = "";
        }
        App.Window.SetMiniPlayer(mini);
    }

    private void SeekBy(TimeSpan offset)
    {
        // Live TV jumps in bigger steps through the pause/rewind buffer.
        if (_request?.IsLive == true && TimeshiftSeek(offset * 3))
        {
            ShowControls();
            return;
        }
        if (_player is { IsSeekable: true } p)
        {
            var target = Math.Clamp(p.Time + (long)offset.TotalMilliseconds, 0, Math.Max(0, p.Length));
            Worker.Run(() => p.Time = target);
        }
        ShowControls();
    }

    private bool _muted;

    /// <summary>
    /// libvlc's mute and volume act on the whole app's audio session on Windows and Windows remembers
    /// them, so a new stream re-applies what the controls show instead of inheriting a stale state.
    /// </summary>
    private void ApplySound()
    {
        if (_player is not { } p)
            return;
        var muted = _muted;
        var volume = (int)VolumeSlider.Value;
        Worker.Run(() =>
        {
            if (p.Mute != muted)
                p.Mute = muted;
            if (p.Volume != volume)
                p.Volume = volume;
        }, "player sound");
    }

    private void ToggleMute()
    {
        if (_player is not { } p)
            return;
        _muted = !_muted;
        var muted = _muted;
        Worker.Run(() => p.Mute = muted);
        MuteIcon.Glyph = muted ? "" : "";
    }

    private void ShowControls()
    {
        ControlsPanel.Visibility = Visibility.Visible;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void GoBack()
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();
    private void Pip_Click(object sender, RoutedEventArgs e) => PopOut();

    private void AddToMultiView_Click(object sender, RoutedEventArgs e)
    {
        if (_request is { } r)
            MultiViewPage.Add(r);
    }

    /// <summary>Moves playback into the picture-in-picture window and returns to browsing.</summary>
    private void PopOut()
    {
        if (_request is not { } r)
            return;
        PipWindow.Open(r with { StartAtMs = r.IsLive ? 0 : _lastTime });
        GoBack();
    }
    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
    private void ChannelUp_Click(object sender, RoutedEventArgs e) => Zap(+1);
    private void ChannelDown_Click(object sender, RoutedEventArgs e) => Zap(-1);

    private void StartOver_Click(object sender, RoutedEventArgs e)
    {
        ResumePanel.Visibility = Visibility.Collapsed;
        if (_player is { IsSeekable: true } p)
            Worker.Run(() => p.Time = 0);
    }

    private void SeekSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || _player is not { IsSeekable: true } p)
            return;
        var position = (float)(e.NewValue / 1000);
        Worker.Run(() => p.Position = position);
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var volume = (int)e.NewValue;
        ShowVolume(volume);
        if (_volumeReady)
            AppSettings.PlayerVolume = volume;
        if (_player is { } p)
            Worker.Run(() => p.Volume = volume, "volume");
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowControls();
    private void Surface_Tapped(object sender, TappedRoutedEventArgs e) => ShowControls();
    private void Surface_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleFullScreen();

    private void Handle(KeyboardAcceleratorInvokedEventArgs args, Action action)
    {
        action();
        args.Handled = true;
    }

    private void TogglePause_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, TogglePause);
    private void ToggleFullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, ToggleFullScreen);
    private void SeekBack_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, () => SeekBy(TimeSpan.FromSeconds(-10)));
    private void SeekForward_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, () => SeekBy(TimeSpan.FromSeconds(10)));
    private void ToggleMute_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, ToggleMute);
    private void ChannelUp_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, () => Zap(+1));
    private void ChannelDown_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, () => Zap(-1));
    private void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, GoBack);
    private void Info_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, () => _ = ShowInfoAsync());
    private void Pip_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, PopOut);

    private void ChannelList_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, () =>
    {
        if (_request is { CanZap: true })
        {
            ShowControls();
            ChannelFlyout.ShowAt(ChannelListButton);
        }
    });

    private void ExitFullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Handle(args, () =>
    {
        if (App.Window.IsFullScreen)
            ToggleFullScreen();
        else if (App.Window.IsMiniPlayer)
            ToggleMiniPlayer();
    });

    // ---- Recording ----

    private void UpdateRecordingState()
    {
        _recordingId = _request is { } r ? Recorder.ActiveRecordingFor(r.Url) : null;
        RecordingBadge.Visibility = _recordingId is null ? Visibility.Collapsed : Visibility.Visible;
        RecordIcon.Glyph = _recordingId is null ? "" : "";
        ToolTipService.SetToolTip(RecordButton, _recordingId is null ? "Record (until stopped, max 4 h)" : "Stop recording");
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_request is not { } r)
            return;
        if (_recordingId is { } id)
            Recorder.Stop(id);
        else
            Recorder.RecordNow(r.Title, r.Url, r.Options, TimeSpan.FromHours(4));
        UpdateRecordingState();
    }

    // ---- Tracks, speed, subtitles ----

    private void SpeedMenu_Opening(object sender, object e)
    {
        SpeedMenu.Items.Clear();
        if (_player is not { } p)
            return;
        foreach (var speed in Speeds)
        {
            var item = new RadioMenuFlyoutItem { Text = $"{speed:0.##}×", GroupName = "speed", IsChecked = Math.Abs(p.Rate - speed) < 0.01 };
            item.Click += (_, _) =>
            {
                p.SetRate(speed);
                SpeedText.Text = $"{speed:0.##}×";
            };
            SpeedMenu.Items.Add(item);
        }
    }

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
            item.Click += (_, _) => Worker.Run(() => p.SetAudioTrack(id));
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
            item.Click += (_, _) => Worker.Run(() => p.SetSpu(id));
            SubtitleMenu.Items.Add(item);
        }
        if (SubtitleMenu.Items.Count > 0)
            SubtitleMenu.Items.Add(new MenuFlyoutSeparator());

        SubtitleMenu.Items.Add(ItemMenu.Item("Search online…", "", async () =>
        {
            if (_player is { } player && _request is { } request &&
                await SubtitleSearchDialog.PickAsync(XamlRoot, request.Title) is { } path)
            {
                player.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, select: true);
            }
        }));
        SubtitleMenu.Items.Add(ItemMenu.Item("Load subtitle file…", "", LoadSubtitleFile));
    }

    private async void LoadSubtitleFile()
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

    // ---- Casting ----

    private void StartCastDiscovery()
    {
        if (_vlc is null)
            return;
        try
        {
            var name = _vlc.RendererList.FirstOrDefault().Name;
            if (string.IsNullOrEmpty(name))
                return;
            _discoverer = new RendererDiscoverer(_vlc, name);
            _discoverer.ItemAdded += (_, a) => OnUi(() => _renderers.Add(a.RendererItem));
            _discoverer.ItemDeleted += (_, a) => OnUi(() => _renderers.RemoveAll(r => r.Name == a.RendererItem.Name));
            _discoverer.Start();
        }
        catch (Exception)
        {
            _discoverer = null; // Casting is optional; playback works without it.
        }
    }

    private void CastMenu_Opening(object sender, object e)
    {
        CastMenu.Items.Clear();
        var local = new RadioMenuFlyoutItem { Text = "This PC", GroupName = "cast", IsChecked = _activeRenderer is null };
        local.Click += (_, _) => CastTo(null);
        CastMenu.Items.Add(local);

        foreach (var renderer in _renderers.Where(r => r.CanRenderVideo))
        {
            var target = renderer;
            var item = new RadioMenuFlyoutItem { Text = renderer.Name, GroupName = "cast", IsChecked = ReferenceEquals(renderer, _activeRenderer) };
            item.Click += (_, _) => CastTo(target);
            CastMenu.Items.Add(item);
        }
        if (_renderers.Count == 0)
            CastMenu.Items.Add(new MenuFlyoutItem { Text = _discoverer is null ? "Casting isn't available" : "Looking for Chromecast devices…", IsEnabled = false });
    }

    /// <summary>Switching renderer takes effect on the next Play, so playback restarts (resuming VOD).</summary>
    private void CastTo(RendererItem? renderer)
    {
        if (_player is not { } p)
            return;
        _activeRenderer = renderer;
        CastBadge.Text = renderer is null ? "" : $"Casting to {renderer.Name}";
        CastBadge.Visibility = renderer is null ? Visibility.Collapsed : Visibility.Visible;
        if (_request?.IsLive == false)
            _resumeAt = _lastTime;
        Worker.Run(() =>
        {
            try
            {
                p.Stop();
                p.SetRenderer(renderer!);
            }
            catch (Exception)
            {
                // A device that vanished mid-switch: carry on locally.
            }
            OnUi(Start);
        });
    }
}
