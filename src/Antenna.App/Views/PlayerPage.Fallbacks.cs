using Antenna.App.Services;
using Microsoft.UI.Dispatching;

namespace Antenna.App.Views;

/// <summary>
/// Watching a match: the player itself checks the channel is broadcasting. If it fails, or nothing plays within
/// <see cref="StartTimeout"/>, the next channel listing the match is tried, until one plays or none are left.
/// </summary>
public sealed partial class PlayerPage
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private DispatcherQueueTimer? _startTimer;

    /// <summary>Starts the clock on a match channel: it must be playing within <see cref="StartTimeout"/>.</summary>
    private void ArmStartTimer()
    {
        _startTimer?.Stop();
        if (_request is not { Fallbacks.Count: > 0 })
            return;
        _startTimer ??= DispatcherQueue.CreateTimer();
        _startTimer.Interval = StartTimeout;
        _startTimer.IsRepeating = false;
        _startTimer.Tick -= OnStartTimeout;
        _startTimer.Tick += OnStartTimeout;
        _startTimer.Start();
    }

    private void OnStartTimeout(DispatcherQueueTimer sender, object args)
    {
        if (_closing || _player is { IsPlaying: true })
            return;
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
        Load(PlayRequest.ForItem(next) with
        {
            Title = $"{next.Title} · {match}",
            Fallbacks = request.Fallbacks.Skip(1).ToList(),
            MatchTitle = request.MatchTitle,
        });
        ShowStatus($"{request.Item?.Title ?? "That channel"} isn't broadcasting it. Trying {next.Title}…");
        return true;
    }
}
