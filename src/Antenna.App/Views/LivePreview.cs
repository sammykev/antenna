using Antenna.App.Services;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Antenna.App.Views;

/// <summary>
/// The small player in Live TV that shows the selected channel while you browse. One per visit to
/// the page: <see cref="ShutdownAsync"/> releases the stream (and with it the provider connection)
/// before the full player starts, so accounts allowing one connection still work.
/// </summary>
internal sealed class LivePreview : Grid
{
    /// <summary>Muting carries across channels and visits until the app closes.</summary>
    public static bool Muted { get; set; }

    private readonly VideoView _video = new();
    private LibVLC? _vlc;
    private MediaPlayer? _player;
    private PlayRequest? _pending;
    private bool _closed;

    /// <summary>Raised on the UI thread: true while the stream is starting, false once it plays or fails.</summary>
    public event Action<bool>? Buffering;

    /// <summary>Raised on the UI thread when a stream can't be played.</summary>
    public event Action? Failed;

    public LivePreview()
    {
        Children.Add(_video);
        _video.Initialized += (_, e) =>
        {
            if (_closed)
                return;
            _vlc = new LibVLC(enableDebugLogs: false, e.SwapChainOptions);
            _player = new MediaPlayer(_vlc);
            _player.Playing += (sender, _) =>
            {
                if (sender is MediaPlayer mp)
                    Worker.Run(() => { if (mp.Mute != Muted) mp.Mute = Muted; }, "preview sound");
                DispatcherQueue.TryEnqueue(() => Buffering?.Invoke(false));
            };
            _player.EncounteredError += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                Buffering?.Invoke(false);
                Failed?.Invoke();
            });
            _video.MediaPlayer = _player;
            if (_pending is { } request)
                Play(request);
        };
    }

    public PlayRequest? Current { get; private set; }

    public void Play(PlayRequest request)
    {
        _pending = request;
        if (_player is not { } player || _vlc is not { } vlc || _closed)
            return;
        Current = request;
        Buffering?.Invoke(true);
        var options = new List<string>
        {
            $":network-caching={AppSettings.NetworkCachingMs}",
            ":http-reconnect",
            AppSettings.HardwareDecoding ? ":avcodec-hw=any" : ":avcodec-hw=none",
        };
        options.AddRange(request.Options);
        Worker.Run(() =>
        {
            using var media = new Media(vlc, request.Url, FromType.FromLocation, [.. options]);
            player.Play(media);
        }, "preview play");
    }

    public void SetMuted(bool muted)
    {
        Muted = muted;
        if (_player is { } p)
            Worker.Run(() => p.Mute = muted, "preview mute");
    }

    /// <summary>Stops playback and frees the player. Safe to call more than once.</summary>
    public Task ShutdownAsync()
    {
        if (_closed)
            return Task.CompletedTask;
        _closed = true;
        _pending = null;
        Current = null;
        var player = _player;
        var vlc = _vlc;
        _video.MediaPlayer = null;
        _player = null;
        _vlc = null;
        return Task.Run(() =>
        {
            try
            {
                player?.Stop();
                player?.Dispose();
                vlc?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "preview shutdown");
            }
        });
    }
}
