using Antenna.App.Services;
using Antenna.App.ViewModels;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Antenna.App.Views;

/// <summary>
/// Picture-in-picture: a small always-on-top window that keeps playing while you browse,
/// use other apps, or open another channel. Built in code; it's a single video surface
/// with a few hover controls.
/// </summary>
internal sealed class PipWindow : Window
{
    private static PipWindow? _current;

    private readonly VideoView _video = new();
    private readonly Grid _controls;
    private readonly FontIcon _playIcon = new() { Glyph = "" };
    private readonly FontIcon _muteIcon = new() { Glyph = "" };
    private readonly TextBlock _title;
    private readonly DispatcherQueueTimer _hideTimer;
    private PlayRequest _request;
    private LibVLC? _vlc;
    private MediaPlayer? _player;
    private long _lastTime;
    private bool _closed;

    public static bool IsOpen => _current is not null;

    /// <summary>Opens (or retargets) the PiP window with this stream.</summary>
    public static void Open(PlayRequest request)
    {
        if (_current is { } existing)
        {
            existing.Load(request);
            existing.Activate();
            return;
        }
        _current = new PipWindow(request);
        _current.Activate();
    }

    private PipWindow(PlayRequest request)
    {
        _request = request;
        Title = "Antenna · Picture in picture";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Antenna.ico"));
        AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(480, 270));

        _title = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.White),
            Margin = new Thickness(10, 8, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 10),
            RequestedTheme = ElementTheme.Dark,
        };
        buttons.Children.Add(MakeButton("", "Previous channel", () => Zap(-1), visible: request.CanZap));
        buttons.Children.Add(MakeButton(_playIcon, "Play/Pause", TogglePause));
        buttons.Children.Add(MakeButton("", "Next channel", () => Zap(+1), visible: request.CanZap));
        buttons.Children.Add(MakeButton(_muteIcon, "Mute", ToggleMute));
        buttons.Children.Add(MakeButton("", "Back to the app", Expand));
        buttons.Children.Add(MakeButton("", "Close", Close));

        _controls = new Grid { Background = new SolidColorBrush(ColorHelper.FromArgb(110, 0, 0, 0)), Visibility = Visibility.Collapsed };
        _controls.Children.Add(_title);
        _controls.Children.Add(buttons);

        var root = new Grid { Background = new SolidColorBrush(Colors.Black) };
        root.Children.Add(_video);
        var hit = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        hit.PointerMoved += (_, _) => ShowControls();
        hit.Tapped += (_, _) => ShowControls();
        hit.DoubleTapped += (_, _) => Expand();
        root.Children.Add(hit);
        root.Children.Add(_controls);
        _controls.PointerMoved += (_, _) => ShowControls();
        Content = root;

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(2.5);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => _controls.Visibility = Visibility.Collapsed;

        _video.Initialized += (_, e) =>
        {
            _vlc = new LibVLC(enableDebugLogs: false, e.SwapChainOptions);
            _player = new MediaPlayer(_vlc);
            _player.TimeChanged += (_, a) => _lastTime = a.Time;
            _player.Playing += (_, _) => DispatcherQueue.TryEnqueue(() => _playIcon.Glyph = "");
            _player.Paused += (_, _) => DispatcherQueue.TryEnqueue(() => _playIcon.Glyph = "");
            _video.MediaPlayer = _player;
            Play();
        };

        Closed += (_, _) => Shutdown();
        _title.Text = request.Title;
    }

    private void Load(PlayRequest request)
    {
        _request = request;
        _title.Text = request.Title;
        _lastTime = 0;
        Play();
    }

    private void Play()
    {
        if (_player is not { } player || _vlc is not { } vlc || _closed)
            return;
        var request = _request;
        var options = new List<string>
        {
            $":network-caching={AppSettings.NetworkCachingMs}",
            ":http-reconnect",
            AppSettings.HardwareDecoding ? ":avcodec-hw=any" : ":avcodec-hw=none",
        };
        if (!request.IsLive && request.StartAtMs > 0)
            options.Add($":start-time={request.StartAtMs / 1000}");
        options.AddRange(request.Options);
        Background.Run(() =>
        {
            using var media = new Media(vlc, request.Url, FromType.FromLocation, [.. options]);
            player.Play(media);
        });
    }

    private void Zap(int delta)
    {
        if (_request.CanZap)
            Load(_request.WithChannel(_request.ChannelIndex + delta));
    }

    private void TogglePause()
    {
        if (_player is { } p)
            p.SetPause(p.IsPlaying);
    }

    private void ToggleMute()
    {
        if (_player is not { } p)
            return;
        p.Mute = !p.Mute;
        _muteIcon.Glyph = p.Mute ? "" : "";
    }

    /// <summary>Back to the full player in the main window, at the same point.</summary>
    private void Expand()
    {
        var request = _request with { StartAtMs = _request.IsLive ? 0 : _lastTime };
        Close();
        App.Window.Activate();
        Playback.Play(request);
    }

    private void ShowControls()
    {
        _controls.Visibility = Visibility.Visible;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void Shutdown()
    {
        _closed = true;
        _current = null;
        _hideTimer.Stop();
        if (_request.ToWatch(_lastTime, _player?.Length ?? 0) is { } record && (_request.IsLive || _lastTime > 0))
            AppServices.Library.RecordWatch(record);

        var player = _player;
        var vlc = _vlc;
        _video.MediaPlayer = null;
        _player = null;
        _vlc = null;
        _ = Task.Run(() =>
        {
            player?.Stop();
            player?.Dispose();
            vlc?.Dispose();
        });
    }

    private static Button MakeButton(object icon, string tip, Action onClick, bool visible = true)
    {
        var button = new Button
        {
            Content = icon as UIElement ?? new FontIcon { Glyph = (string)icon, FontSize = 14 },
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed,
            Padding = new Thickness(10, 6, 10, 6),
        };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }
}
