using System.Globalization;
using Android.Content.PM;
using AndroidX.Core.View;
using Antenna.Core;
using Antenna.Mobile.Services;
using LibVLCSharp.Shared;

namespace Antenna.Mobile.Views;

/// <summary>
/// Full-screen player: landscape, tap for controls. Live TV: swipe or ⏮/⏭ to change channel, now/next and the
/// channel's guide. Films and episodes: seek bar, skip back/forward, resume where you stopped, next episode.
/// </summary>
public partial class PlayerPage : ContentPage
{
    private const int MaxAttempts = 5;

    private static readonly Lazy<LibVLC> Vlc = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC(enableDebugLogs: false);
    });

    private static readonly string[] AspectModes = ["Fit", "Fill", "16:9", "4:3"];

    private MediaPlayer? _player;
    private PlayItem? _item;
    private int _attempts;
    private int _aspect;
    private long _resumeAt;
    private bool _seeking;
    private int _ticks;
    private IDispatcherTimer? _hideTimer;
    private IDispatcherTimer? _clock;

    public PlayerPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        EnterFullScreen(true);
        DeviceDisplay.Current.KeepScreenOn = true;
        if (Window is { } window)
        {
            window.Stopped += Window_Stopped;
            window.Resumed += Window_Resumed;
        }

        _player = new MediaPlayer(Vlc.Value);
        _player.Playing += (_, _) => OnUi(Started);
        _player.Paused += (_, _) => OnUi(() => PauseButton.Text = "▶");
        _player.Buffering += (_, e) => OnUi(() => ShowStatus(e.Cache < 100 ? $"Buffering {e.Cache:0}%" : null));
        _player.EncounteredError += (_, _) => OnUi(() => Failed("The stream failed"));
        _player.EndReached += (_, _) => OnUi(Ended);
        Video.MediaPlayer = _player;

        _clock = Dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => Tick();
        _clock.Start();

        Play(Playback.Current);
        ShowOverlay();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SaveProgress();
        _clock?.Stop();
        _hideTimer?.Stop();
        if (Window is { } window)
        {
            window.Stopped -= Window_Stopped;
            window.Resumed -= Window_Resumed;
        }
        DeviceDisplay.Current.KeepScreenOn = false;
        EnterFullScreen(false);

        var player = _player;
        _player = null;
        Video.MediaPlayer = null;
        if (player is not null)
        {
            // Never stop or dispose from libvlc's own threads; the thread pool is safe.
            Task.Run(() =>
            {
                player.Stop();
                player.Dispose();
            });
        }
    }

    // ---- Playing ----

    private void Play(PlayItem? item, bool resume = true)
    {
        if (item is null || item.Url.Length == 0 || _player is not { } player)
            return;
        if (_item is not null && _item != item)
            SaveProgress();
        _item = item;
        ChannelTitle.Text = item.Title;
        ChannelGroup.Text = item.Subtitle;
        FavouriteButton.IsVisible = item.Item is not null;
        FavouriteButton.Text = item.Item is { } fav && MobileServices.Library.IsFavourite(fav) ? "★" : "☆";
        GuideButton.IsVisible = item.IsLive && item.Item?.EpgChannelId is not null;
        GuidePanel.IsVisible = false;
        LiveInfo.IsVisible = item.IsLive;
        SeekBar.IsVisible = SkipBackButton.IsVisible = SkipForwardButton.IsVisible = !item.IsLive;
        PreviousButton.IsVisible = NextButton.IsVisible = Playback.Queue.Count > 1;
        Seek.Value = 0;
        PositionText.Text = LengthText.Text = "0:00";
        ShowNowNext();
        ShowStatus("Connecting…");
        Log.Info($"Playing {item.Title}");

        _resumeAt = resume && !item.IsLive && MobileServices.Library.GetWatch(item.SourceId, item.HistoryId) is { IsResumable: true } w
            ? w.PositionMs : 0;
        string[] options = [":network-caching=1500", ":http-reconnect", .. item.Options];
        Task.Run(() =>
        {
            using var media = new Media(Vlc.Value, item.Url, FromType.FromLocation, options);
            player.Play(media);
        });
        if (item.IsLive)
            Task.Run(() => Remember(item, 0, 0));
    }

    private void Started()
    {
        _attempts = 0;
        ShowStatus(null);
        PauseButton.Text = "⏸";
        if (_resumeAt > 0 && _player is { } player)
        {
            var at = _resumeAt;
            _resumeAt = 0;
            Task.Run(() => player.Time = at);
            ShowStatus($"Continuing from {Clock(at)}");
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(3), () => ShowStatus(null));
        }
    }

    private void Ended()
    {
        if (_item is not { } item)
            return;
        if (item.IsLive)
        {
            Failed("The stream ended");
            return;
        }
        // Finished: forget the position, then go on to the next episode or close.
        Task.Run(() => Remember(item, 0, 0));
        if (item.Kind == MediaKind.Series && Playback.Index + 1 < Playback.Queue.Count)
        {
            Playback.Index++;
            _item = null;
            Play(Playback.Current, resume: false);
            ShowOverlay();
        }
        else
        {
            _item = null;
            Shell.Current.GoToAsync("..");
        }
    }

    private async void Failed(string what)
    {
        if (_item is not { } item || _player is null)
            return;
        Log.Info($"{what} ({item.Title}, attempt {_attempts + 1})");
        if (++_attempts > MaxAttempts || !item.IsLive && _attempts > 2)
        {
            ShowStatus($"{what}. Tap ↻ to try again.");
            PauseButton.Text = "↻";
            _attempts = MaxAttempts + 1;
            ShowOverlay(stay: true);
            return;
        }
        // Live streams drop now and then: reconnect after a short pause.
        ShowStatus($"{what}, reconnecting…");
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * _attempts, 8)));
        if (_item == item && _player is not null)
            Play(item);
    }

    private void Step(int delta)
    {
        if (Playback.Queue.Count < 2)
            return;
        Playback.Index = (Playback.Index + delta + Playback.Queue.Count) % Playback.Queue.Count;
        _attempts = 0;
        Play(Playback.Current);
        ShowOverlay();
    }

    /// <summary>Every second: the seek bar; every 15 s: save the position; every 30 s: now/next.</summary>
    private void Tick()
    {
        _ticks++;
        if (_player is not { } player || _item is not { } item)
            return;
        if (!item.IsLive && !_seeking && Overlay.IsVisible)
        {
            var length = player.Length;
            var time = player.Time;
            if (length > 0)
            {
                Seek.Value = Math.Clamp((double)time / length, 0, 1);
                PositionText.Text = Clock(time);
                LengthText.Text = Clock(length);
            }
        }
        if (_ticks % 15 == 0)
            SaveProgress();
        if (item.IsLive && _ticks % 30 == 0)
            ShowNowNext();
    }

    private void SaveProgress()
    {
        if (_item is not { IsLive: false } item || _player is not { } player)
            return;
        var (time, length) = (player.Time, player.Length);
        if (length > 0 && time > 0)
            Task.Run(() => Remember(item, time, length));
    }

    private static void Remember(PlayItem item, long position, long length)
    {
        try
        {
            MobileServices.Library.RecordWatch(new WatchRecord
            {
                SourceId = item.SourceId,
                ExternalId = item.HistoryId,
                Kind = item.Kind,
                Title = item.Kind == MediaKind.Series && item.Subtitle is { } series ? $"{series} · {item.Title}" : item.Title,
                StreamUrl = item.Url,
                LogoUrl = item.LogoUrl,
                PlayerOptions = item.PlayerOptions,
                PositionMs = position,
                LengthMs = length,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Saving history failed", ex);
        }
    }

    private void Window_Stopped(object? sender, EventArgs e)
    {
        SaveProgress();
        // Leaving the app: stop rather than keep streaming in the background. Films resume from the saved position.
        if (_player is { } player)
            Task.Run(player.Stop);
    }

    private void Window_Resumed(object? sender, EventArgs e)
    {
        EnterFullScreen(true);
        _attempts = 0;
        Play(_item);
    }

    // ---- Controls ----

    private void Back_Clicked(object? sender, EventArgs e) => Shell.Current.GoToAsync("..");

    private void Previous_Clicked(object? sender, EventArgs e) => Step(-1);

    private void Next_Clicked(object? sender, EventArgs e) => Step(1);

    private void SkipBack_Clicked(object? sender, EventArgs e) => SkipBy(-10_000);

    private void SkipForward_Clicked(object? sender, EventArgs e) => SkipBy(30_000);

    private void SkipBy(long ms)
    {
        if (_player is not { } player || _item is not { IsLive: false })
            return;
        Task.Run(() => player.Time = Math.Clamp(player.Time + ms, 0, Math.Max(0, player.Length - 1000)));
        ShowOverlay();
    }

    private void Seek_DragStarted(object? sender, EventArgs e)
    {
        _seeking = true;
        ShowOverlay(stay: true);
    }

    private void Seek_DragCompleted(object? sender, EventArgs e)
    {
        _seeking = false;
        if (_player is { } player && player.Length > 0)
        {
            var target = (long)(Seek.Value * player.Length);
            PositionText.Text = Clock(target);
            Task.Run(() => player.Time = target);
        }
        ShowOverlay();
    }

    private void Pause_Clicked(object? sender, EventArgs e)
    {
        if (_player is not { } player)
            return;
        if (_attempts > MaxAttempts || !player.IsPlaying && player.State is VLCState.Stopped or VLCState.Ended or VLCState.Error)
        {
            _attempts = 0;
            Play(_item);
        }
        else
        {
            Task.Run(player.Pause);
        }
        ShowOverlay();
    }

    private void Favourite_Clicked(object? sender, EventArgs e)
    {
        if (_item?.Item is not { } item)
            return;
        var favourite = !MobileServices.Library.IsFavourite(item);
        MobileServices.Library.SetFavourite(item, favourite);
        FavouriteButton.Text = favourite ? "★" : "☆";
        ShowOverlay();
    }

    private void Aspect_Clicked(object? sender, EventArgs e)
    {
        if (_player is not { } player)
            return;
        _aspect = (_aspect + 1) % AspectModes.Length;
        var mode = AspectModes[_aspect];
        AspectButton.Text = mode;
        // Fill crops the picture to the screen's shape; the others letterbox.
        var screen = Width > 0 && Height > 0 ? $"{(int)Width}:{(int)Height}" : "16:9";
        var (aspect, crop) = mode switch
        {
            "Fill" => (screen, screen),
            "Fit" => ((string?)null, (string?)null),
            _ => (mode, (string?)null),
        };
        Task.Run(() =>
        {
            player.AspectRatio = aspect;
            player.CropGeometry = crop;
        });
        ShowOverlay();
    }

    private void Guide_Clicked(object? sender, EventArgs e)
    {
        GuidePanel.IsVisible = !GuidePanel.IsVisible;
        if (GuidePanel.IsVisible && _item?.Item is { EpgChannelId: { } channel } item)
        {
            var now = DateTimeOffset.Now;
            GuideList.ItemsSource = MobileServices.Library.GetProgrammes(item.SourceId, channel, now, now.AddHours(24))
                .Select(p => new GuideRow(p)).ToList();
        }
        else if (GuidePanel.IsVisible)
        {
            GuideList.ItemsSource = null;
        }
        ShowOverlay(stay: GuidePanel.IsVisible);
    }

    private void Surface_Tapped(object? sender, TappedEventArgs e)
    {
        if (Overlay.IsVisible && !GuidePanel.IsVisible)
            Overlay.IsVisible = false;
        else
            ShowOverlay();
    }

    private void ShowOverlay(bool stay = false)
    {
        Overlay.IsVisible = true;
        _hideTimer ??= CreateHideTimer();
        _hideTimer.Stop();
        if (!stay)
            _hideTimer.Start();
    }

    private IDispatcherTimer CreateHideTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!GuidePanel.IsVisible && !_seeking && _attempts <= MaxAttempts)
                Overlay.IsVisible = false;
        };
        return timer;
    }

    private void ShowNowNext()
    {
        NowText.Text = NextText.Text = "";
        NowProgress.Progress = 0;
        NowProgress.IsVisible = false;
        if (_item?.Item is not { Kind: MediaKind.Live, EpgChannelId: { } channel } item)
            return;
        var now = DateTimeOffset.Now;
        var programmes = MobileServices.Library.GetProgrammes(item.SourceId, channel, now.AddHours(-6), now.AddHours(12));
        var current = programmes.FirstOrDefault(p => p.IsOnAt(now));
        var next = programmes.FirstOrDefault(p => p.Start >= (current?.Stop ?? now));
        if (current is not null)
        {
            NowText.Text = $"Now  {current.Start.LocalDateTime:HH:mm}  {current.Title}";
            NowProgress.Progress = Math.Clamp((now - current.Start) / current.Duration, 0, 1);
            NowProgress.IsVisible = true;
        }
        if (next is not null)
            NextText.Text = $"Next  {next.Start.LocalDateTime:HH:mm}  {next.Title}";
    }

    private void ShowStatus(string? text)
    {
        Status.Text = text;
        Status.IsVisible = text is not null;
    }

    private static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void OnUi(Action action) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (_player is not null)
            action();
    });

    /// <summary>Landscape with the status and navigation bars hidden (swipe from an edge to see them).</summary>
    private static void EnterFullScreen(bool on)
    {
        if (Platform.CurrentActivity is not { Window: { } window } activity)
            return;
        activity.RequestedOrientation = on ? ScreenOrientation.SensorLandscape : ScreenOrientation.Unspecified;
        if (WindowCompat.GetInsetsController(window, window.DecorView) is not { } controller)
            return;
        if (on)
        {
            controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
            controller.Hide(WindowInsetsCompat.Type.SystemBars());
        }
        else
        {
            controller.Show(WindowInsetsCompat.Type.SystemBars());
        }
    }
}
