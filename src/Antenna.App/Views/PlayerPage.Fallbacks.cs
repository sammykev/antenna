using Antenna.App.Services;
using Microsoft.UI.Dispatching;

namespace Antenna.App.Views;

/// <summary>
/// Watching a match: the player itself checks the channel is broadcasting. A channel that sends nothing at all within
/// <see cref="NoDataTimeout"/> is given up on; one that's loading (buffering data) gets up to <see cref="LoadingTimeout"/>,
/// as some take a long time to start. Then the next channel listing the match is tried, after a pause so a one-stream
/// account has let go of the last connection.
/// </summary>
public sealed partial class PlayerPage
{
    private static readonly TimeSpan NoDataTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LoadingTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Providers count a connection for a moment after it closes; switching sooner gets refused.</summary>
    private static readonly TimeSpan SwitchPause = TimeSpan.FromSeconds(3);

    private DispatcherQueueTimer? _startTimer;
    private DateTimeOffset _startedAt;

    /// <summary>Set when the stream has sent something (buffering), so a slow channel isn't mistaken for a dark one.</summary>
    private bool _sawData;

    /// <summary>Starts the clock on a match channel.</summary>
    private void ArmStartTimer()
    {
        _startTimer?.Stop();
        _sawData = false;
        if (_request is not { Fallbacks.Count: > 0 })
            return;
        _startedAt = DateTimeOffset.Now;
        _startTimer ??= DispatcherQueue.CreateTimer();
        _startTimer.Interval = NoDataTimeout;
        _startTimer.IsRepeating = false;
        _startTimer.Tick -= OnStartTimeout;
        _startTimer.Tick += OnStartTimeout;
        _startTimer.Start();
    }

    private void OnStartTimeout(DispatcherQueueTimer sender, object args)
    {
        if (_closing || _player is { IsPlaying: true })
            return;
        // Loading but slow: keep waiting, up to a point.
        if (_sawData && DateTimeOffset.Now - _startedAt < LoadingTimeout)
        {
            sender.Interval = TimeSpan.FromSeconds(10);
            sender.Start();
            return;
        }
        Log.Info($"player: nothing from '{_request?.Item?.Title}' after {(int)(DateTimeOffset.Now - _startedAt).TotalSeconds} s" +
                 (_sawData ? " (it was loading)" : " (no data at all)"));
        TryNextMatchChannel();
    }

    /// <summary>Moves on to the match's next channel; false when there's none left.</summary>
    private bool TryNextMatchChannel()
    {
        if (_request is not { Fallbacks.Count: > 0 } request)
            return false;
        _startTimer?.Stop();
        var next = request.Fallbacks[0];
        var match = request.MatchTitle ?? request.Title;
        Log.Info($"player: '{request.Item?.Title}' isn't broadcasting {match}, trying '{next.Title}'");
        if (_player is { } player)
            Worker.Run(() => player.Stop(), "player stop");
        ShowStatus($"{request.Item?.Title ?? "That channel"} isn't broadcasting it. Trying {next.Title}…");
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = SwitchPause;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (_closing || !ReferenceEquals(_request, request))
                return;
            Load(PlayRequest.ForItem(next) with
            {
                Title = $"{next.Title} · {match}",
                Fallbacks = request.Fallbacks.Skip(1).ToList(),
                MatchTitle = request.MatchTitle,
            });
            ShowStatus($"{request.Item?.Title ?? "That channel"} isn't broadcasting it. Trying {next.Title}…");
        };
        timer.Start();
        return true;
    }
}
