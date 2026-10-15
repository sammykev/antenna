using System.Runtime.InteropServices;
using Antenna.Core.Playback;
using LibVLCSharp.Shared;

namespace Antenna.App.Services;

/// <summary>Feeds LibVLC from a <see cref="TimeshiftBuffer"/>, starting at a chosen byte offset.</summary>
internal sealed class TimeshiftInput : MediaInput
{
    private readonly TimeshiftBuffer _buffer;
    private readonly CancellationTokenSource _closed = new();
    private readonly byte[] _chunk = new byte[256 * 1024];

    public TimeshiftInput(TimeshiftBuffer buffer, long start)
    {
        _buffer = buffer;
        Position = start;
        CanSeek = false; // Rewinding starts a new input at the new offset instead.
    }

    /// <summary>The next byte LibVLC will get. Playback is a moment behind this (its own cache).</summary>
    public long Position { get; private set; }

    public override bool Open(out ulong size)
    {
        size = ulong.MaxValue; // Live: no known length.
        return true;
    }

    public override int Read(IntPtr buf, uint len)
    {
        var want = (int)Math.Min(len, (uint)_chunk.Length);
        var n = _buffer.Read(Position, _chunk.AsSpan(0, want), _closed.Token);
        if (n < 0 && !_closed.IsCancellationRequested && Position < _buffer.Start)
        {
            // Paused for longer than the buffer holds: carry on from the oldest part still kept.
            Position = _buffer.OldestOffset();
            n = _buffer.Read(Position, _chunk.AsSpan(0, want), _closed.Token);
        }
        if (n <= 0)
            return n;
        Marshal.Copy(_chunk, 0, buf, n);
        Position += n;
        return n;
    }

    public override bool Seek(ulong offset) => false;

    public override void Close() => _closed.Cancel();
}
