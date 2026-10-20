using Antenna.Core;

namespace Antenna.Mobile.Services;

/// <summary>What the player should play: the list a channel was picked from, so next/previous zap through it.</summary>
public static class Playback
{
    public static IReadOnlyList<MediaItem> Queue { get; private set; } = [];

    public static int Index { get; set; }

    public static MediaItem? Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;

    public static Task PlayAsync(IReadOnlyList<MediaItem> queue, int index)
    {
        Queue = queue;
        Index = index;
        return Shell.Current.GoToAsync("player");
    }
}
