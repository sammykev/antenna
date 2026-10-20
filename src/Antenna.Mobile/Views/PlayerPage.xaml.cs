using Android.Content.PM;
using AndroidX.Core.View;
using Antenna.Core;
using Antenna.Mobile.Services;
using LibVLCSharp.Shared;

namespace Antenna.Mobile.Views;

/// <summary>Full-screen player: landscape, tap for controls, swipe or ⏮/⏭ to change channel.</summary>
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
    private MediaItem? _item;
    private int _attempts;
    private int _aspect;
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
        _player.Playing += (_, _) => OnUi(() => { _attempts = 0; ShowStatus(null); PauseButton.Text = "⏸"; });
        _player.Paused += (_, _) => OnUi(() => PauseButton.Text = "▶");
        _player.Buffering += (_, e) => OnUi(() => ShowStatus(e.Cache < 100 ? $"Buffering {e.Cache:0}%" : null));
        _player.EncounteredError += (_, _) => OnUi(() => Failed("The stream failed"));
        _player.EndReached += (_, _) => OnUi(() => Failed("The stream ended"));
        Video.MediaPlayer = _player;

        _clock = Dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(30);
        _clock.Tick += (_, _) => ShowNowNext();
        _clock.Start();

        Play(Playback.Current);
        ShowOverlay();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
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

    private void Play(MediaItem? item)
    {
        if (item?.StreamUrl is not { } url || _player is not { } player)
            return;
        _item = item;
        ChannelTitle.Text = item.Title;
        ChannelGroup.Text = item.Group;
        FavouriteButton.Text = MobileServices.Library.IsFavourite(item) ? "★" : "☆";
        GuidePanel.IsVisible = false;
        ShowNowNext();
        ShowStatus("Connecting…");
        Log.Info($"Playing {item.Title}");

        string[] options = [":network-caching=1500", ":http-reconnect", .. item.Options];
        Task.Run(() =>
        {
            using var media = new Media(Vlc.Value, url, FromType.FromLocation, options);
            player.Play(media);
        });

        Task.Run(() =>
        {
            try
            {
                MobileServices.Library.RecordWatch(new WatchRecord
                {
                    SourceId = item.SourceId,
                    ExternalId = item.ExternalId,
                    Kind = item.Kind,
                    Title = item.Title,
                    StreamUrl = url,
                    LogoUrl = item.LogoUrl,
                    PlayerOptions = item.PlayerOptions,
                });
            }
            catch (Exception ex)
            {
                Log.Error("Saving history failed", ex);
            }
        });
    }

    private async void Failed(string what)
    {
        if (_item is not { } item || _player is null)
            return;
        Log.Info($"{what} ({item.Title}, attempt {_attempts + 1})");
        if (++_attempts > MaxAttempts)
        {
            ShowStatus($"{what}. Tap ⏸ to try again.");
            PauseButton.Text = "↻";
            ShowOverlay(stay: true);
            return;
        }
        // Live streams drop now and then: reconnect after a short pause.
        ShowStatus($"{what}, reconnecting…");
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * _attempts, 8)));
        if (_item == item && _player is not null)
            Play(item);
    }

    private void Zap(int delta)
    {
        if (Playback.Queue.Count < 2)
            return;
        Playback.Index = (Playback.Index + delta + Playback.Queue.Count) % Playback.Queue.Count;
        _attempts = 0;
        Play(Playback.Current);
        ShowOverlay();
    }

    private void Window_Stopped(object? sender, EventArgs e)
    {
        // Leaving the app: stop live TV rather than keep streaming in the background.
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

    private void Previous_Clicked(object? sender, EventArgs e) => Zap(-1);

    private void Next_Clicked(object? sender, EventArgs e) => Zap(1);

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
        if (_item is not { } item)
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
        if (GuidePanel.IsVisible && _item is { EpgChannelId: { } channel } item)
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
            if (!GuidePanel.IsVisible && _attempts <= MaxAttempts)
                Overlay.IsVisible = false;
        };
        return timer;
    }

    private void ShowNowNext()
    {
        NowText.Text = NextText.Text = "";
        NowProgress.Progress = 0;
        NowProgress.IsVisible = false;
        if (_item is not { EpgChannelId: { } channel } item)
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
