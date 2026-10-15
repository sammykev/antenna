using Microsoft.Win32.SafeHandles;

namespace Antenna.Core.Playback;

/// <summary>
/// The last N minutes of a live stream on disk, so live TV can be paused and rewound. One writer
/// appends what arrives from the network; readers ask for any byte offset still held and wait for
/// data that hasn't arrived yet. Stored as fixed-size chunk files so the oldest can be dropped cheaply.
/// </summary>
public sealed class TimeshiftBuffer : IDisposable
{
    /// <summary>MPEG-TS packets are 188 bytes; jumping to a packet boundary lets the demuxer sync at once.</summary>
    public const int PacketSize = 188;

    private readonly string _folder;
    private readonly int _chunkBytes;
    private readonly TimeSpan _keep;
    private readonly long _fallbackMaxBytes;
    private readonly Dictionary<long, SafeFileHandle> _chunks = [];
    private readonly object _gate = new(); // Monitor.Wait/PulseAll need a plain object, not System.Threading.Lock.
    private readonly Queue<(DateTimeOffset At, long End)> _rate = new();
    private readonly Func<DateTimeOffset> _clock;
    private bool _completed;
    private bool _disposed;

    /// <param name="folder">An empty folder of its own; deleted on dispose.</param>
    /// <param name="keep">How much to keep, converted to bytes from the measured bitrate.</param>
    /// <param name="fallbackMaxBytes">Size limit until the bitrate is known.</param>
    public TimeshiftBuffer(string folder, TimeSpan keep, long fallbackMaxBytes = 2L << 30, int chunkBytes = 8 << 20, Func<DateTimeOffset>? clock = null)
    {
        _folder = folder;
        _keep = keep;
        _fallbackMaxBytes = fallbackMaxBytes;
        _chunkBytes = chunkBytes;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(folder);
    }

    /// <summary>Oldest byte still held.</summary>
    public long Start { get; private set; }

    /// <summary>One past the newest byte received.</summary>
    public long End { get; private set; }

    /// <summary>True once the writer has finished (stream over or given up): readers get 0 at the end.</summary>
    public bool IsCompleted
    {
        get
        {
            lock (_gate)
                return _completed;
        }
    }

    /// <summary>Bytes per second over the last ~30 s, or 0 until there's enough to tell.</summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_gate)
                return Rate();
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (data.Length > 0)
            {
                var index = End / _chunkBytes;
                var within = End % _chunkBytes;
                var n = (int)Math.Min(data.Length, _chunkBytes - within);
                RandomAccess.Write(Chunk(index), data[..n], within);
                End += n;
                data = data[n..];
            }
            var now = _clock();
            _rate.Enqueue((now, End));
            while (_rate.Count > 2 && now - _rate.Peek().At > TimeSpan.FromSeconds(30))
                _rate.Dequeue();
            Trim();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Reads from <paramref name="offset"/>, waiting until data is there. Returns the bytes read, 0 at the
    /// end of a completed stream, or -1 if the offset is no longer held (fell behind) or the wait was cancelled.
    /// </summary>
    public int Read(long offset, Span<byte> destination, CancellationToken cancel)
    {
        using var wake = cancel.Register(() =>
        {
            lock (_gate)
                Monitor.PulseAll(_gate);
        });
        lock (_gate)
        {
            while (true)
            {
                if (_disposed || cancel.IsCancellationRequested || offset < Start)
                    return -1;
                if (offset < End)
                    break;
                if (_completed)
                    return 0;
                Monitor.Wait(_gate, TimeSpan.FromSeconds(1));
            }
            var index = offset / _chunkBytes;
            var within = offset % _chunkBytes;
            var n = (int)Math.Min(Math.Min(destination.Length, End - offset), _chunkBytes - within);
            return RandomAccess.Read(_chunks[index], destination[..n], within);
        }
    }

    /// <summary>The writer is done; waiting readers are released.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// The byte offset roughly <paramref name="behind"/> before the newest data, kept inside what's held
    /// and on a packet boundary.
    /// </summary>
    public long OffsetBehindLive(TimeSpan behind)
    {
        lock (_gate)
        {
            var rate = Rate();
            var bytes = rate > 0 ? (long)(behind.TotalSeconds * rate) : 0;
            return Align(Math.Clamp(End - bytes, Start, End));
        }
    }

    /// <summary>How far <paramref name="offset"/> is behind the newest data, from the measured bitrate.</summary>
    public TimeSpan BehindLive(long offset)
    {
        lock (_gate)
        {
            var rate = Rate();
            return rate > 0 ? TimeSpan.FromSeconds(Math.Max(0, End - offset) / rate) : TimeSpan.Zero;
        }
    }

    /// <summary>Earliest offset still held, on a packet boundary.</summary>
    public long OldestOffset()
    {
        lock (_gate)
            return Align(Start);
    }

    private long Align(long offset)
    {
        var aligned = offset - offset % PacketSize;
        return aligned < Start ? aligned + PacketSize : aligned;
    }

    private double Rate()
    {
        if (_rate.Count < 2)
            return 0;
        var (firstAt, firstEnd) = _rate.Peek();
        var seconds = (_clock() - firstAt).TotalSeconds;
        return seconds >= 2 ? (End - firstEnd) / seconds : 0;
    }

    private SafeFileHandle Chunk(long index)
    {
        if (!_chunks.TryGetValue(index, out var handle))
        {
            handle = File.OpenHandle(Path.Combine(_folder, $"{index:D6}.ts"), FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            _chunks[index] = handle;
        }
        return handle;
    }

    /// <summary>Drops whole chunks older than what's kept.</summary>
    private void Trim()
    {
        var rate = Rate();
        var max = rate > 0 ? Math.Max((long)(_keep.TotalSeconds * rate), _chunkBytes * 2L) : _fallbackMaxBytes;
        while (End - Start - _chunkBytes > max) // Written this way round so a huge max can't overflow.
        {
            var index = Start / _chunkBytes;
            if (_chunks.Remove(index, out var handle))
            {
                handle.Dispose();
                TryDelete(Path.Combine(_folder, $"{index:D6}.ts"));
            }
            Start = (index + 1) * _chunkBytes;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var handle in _chunks.Values)
                handle.Dispose();
            _chunks.Clear();
            Monitor.PulseAll(_gate);
        }
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
