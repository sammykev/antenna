using System.Collections.Concurrent;
using Antenna.Core;
using Antenna.Core.Recordings;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;

namespace Antenna.App.Services;

/// <summary>
/// Records live TV and catch-up with LibVLC's stream output (no decoding, just copying the
/// stream to a .ts file), and downloads movies/episodes over HTTP. Scheduled recordings start
/// and stop on a timer while the app is running.
/// </summary>
internal static class Recorder
{
    private sealed class Job
    {
        public required Recording Recording;
        public MediaPlayer? Player;
        public CancellationTokenSource? Cancel;
    }

    private static readonly ConcurrentDictionary<string, Job> Active = new();
    private static readonly Lazy<LibVLC> Vlc = new(() => new LibVLC(enableDebugLogs: false, "--no-video-title-show"));
    private static DispatcherQueue? _queue;
    private static DispatcherQueueTimer? _timer;

    /// <summary>Raised on the UI thread whenever a recording starts, progresses, finishes or is removed.</summary>
    public static event Action? Changed;

    public static void Start(DispatcherQueue queue)
    {
        _queue = queue;
        var now = DateTimeOffset.Now;
        var all = AppServices.Library.GetRecordings();

        // Anything "recording" from a previous run was cut off when the app closed.
        foreach (var r in all.Where(r => r.Status == RecordingStatus.Recording))
            Finish(r, File.Exists(r.Path) && new FileInfo(r.Path).Length > 0 ? RecordingStatus.Completed : RecordingStatus.Failed, "Antenna was closed while recording.");
        foreach (var r in RecordingPlanner.Missed(all, now))
            Finish(r, RecordingStatus.Failed, "Missed: Antenna wasn't running at the scheduled time.");

        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(15);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private static void Tick()
    {
        var now = DateTimeOffset.Now;
        foreach (var r in RecordingPlanner.DueToStart(AppServices.Library.GetRecordings(), now))
            Begin(r);

        foreach (var job in Active.Values.ToList())
        {
            var r = job.Recording;
            if (RecordingPlanner.ShouldStop(r, now))
            {
                Stop(r.Id);
                continue;
            }
            if (File.Exists(r.Path))
            {
                job.Recording = r with { BytesWritten = new FileInfo(r.Path).Length };
                AppServices.Library.SaveRecording(job.Recording);
            }
        }
        Changed?.Invoke();
    }

    public static string? ActiveRecordingFor(string url) =>
        Active.Values.FirstOrDefault(j => !j.Recording.IsDownload && j.Recording.Url == url)?.Recording.Id;

    /// <summary>Starts recording a live stream now, for at most <paramref name="maxDuration"/>.</summary>
    public static Recording RecordNow(string title, string url, IEnumerable<string> options, TimeSpan maxDuration)
    {
        var now = DateTimeOffset.Now;
        var rec = new Recording
        {
            Title = title,
            Url = url,
            PlayerOptions = Join(options),
            IsDownload = false,
            Start = now,
            Stop = now + maxDuration,
            Path = PathFor(title, now, "ts"),
        };
        AppServices.Library.SaveRecording(rec);
        Begin(rec);
        return rec;
    }

    /// <summary>Schedules a guide programme (with padding). Catch-up programmes in the past record immediately.</summary>
    public static Recording Schedule(string title, string url, IEnumerable<string> options, DateTimeOffset start, DateTimeOffset stop)
    {
        var rec = new Recording
        {
            Title = title,
            Url = url,
            PlayerOptions = Join(options),
            IsDownload = false,
            Start = start,
            Stop = stop,
            Path = PathFor(title, start, "ts"),
        };
        AppServices.Library.SaveRecording(rec);
        Tick();
        return rec;
    }

    /// <summary>Downloads a movie or episode file in the background.</summary>
    public static Recording Download(string title, string url, IEnumerable<string> options)
    {
        var ext = Path.GetExtension(new Uri(url).AbsolutePath).TrimStart('.');
        var rec = new Recording
        {
            Title = title,
            Url = url,
            PlayerOptions = Join(options),
            IsDownload = true,
            Start = DateTimeOffset.Now,
            Path = PathFor(title, DateTimeOffset.Now, ext.Length is > 0 and <= 5 ? ext : "mp4"),
        };
        AppServices.Library.SaveRecording(rec);
        Begin(rec);
        return rec;
    }

    public static void Stop(string id)
    {
        if (!Active.TryRemove(id, out var job))
        {
            // Scheduled but not started yet: cancel it.
            var pending = AppServices.Library.GetRecordings().FirstOrDefault(r => r.Id == id && r.Status == RecordingStatus.Scheduled);
            if (pending is not null)
                Finish(pending, RecordingStatus.Cancelled, null);
            return;
        }

        job.Cancel?.Cancel();
        if (job.Player is { } player)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                player.Stop();
                player.Dispose();
                var size = File.Exists(job.Recording.Path) ? new FileInfo(job.Recording.Path).Length : 0;
                Ui(() => Finish(job.Recording with { BytesWritten = size }, size > 0 ? RecordingStatus.Completed : RecordingStatus.Failed,
                    size > 0 ? null : "Nothing was received from the stream."));
            });
        }
    }

    public static void Delete(Recording rec, bool deleteFile)
    {
        Stop(rec.Id);
        AppServices.Library.DeleteRecording(rec.Id);
        if (deleteFile)
        {
            try { File.Delete(rec.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    private static void Begin(Recording rec)
    {
        if (Active.ContainsKey(rec.Id))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(rec.Path)!);
        var started = rec with { Status = RecordingStatus.Recording };
        var job = new Job { Recording = started };
        Active[rec.Id] = job;
        AppServices.Library.SaveRecording(started);

        if (rec.IsDownload)
            _ = DownloadAsync(job);
        else
            StartStreamCopy(job);
        Changed?.Invoke();
    }

    private static void StartStreamCopy(Job job)
    {
        var rec = job.Recording;
        // VLC's sout parser treats backslashes as escapes; forward slashes work on Windows.
        var dst = rec.Path.Replace('\\', '/').Replace("\"", "");
        var options = new List<string>
        {
            $":sout=#std{{access=file,mux=ts,dst=\"{dst}\"}}",
            ":sout-keep",
            ":sout-all",
            ":network-caching=3000",
            ":http-reconnect",
        };
        options.AddRange(rec.PlayerOptions?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? []);

        var player = new MediaPlayer(Vlc.Value);
        job.Player = player;
        player.EndReached += (_, _) => Ui(() => Stop(rec.Id));
        player.EncounteredError += (_, _) => Ui(() =>
        {
            if (Active.TryRemove(rec.Id, out var failed))
            {
                ThreadPool.QueueUserWorkItem(_ => failed.Player?.Dispose());
                Finish(failed.Recording, RecordingStatus.Failed, "The stream couldn't be opened or dropped.");
            }
        });
        ThreadPool.QueueUserWorkItem(_ =>
        {
            using var media = new Media(Vlc.Value, rec.Url, FromType.FromLocation, [.. options]);
            player.Play(media);
        });
    }

    private static async Task DownloadAsync(Job job)
    {
        var rec = job.Recording;
        var cts = new CancellationTokenSource();
        job.Cancel = cts;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, rec.Url);
            foreach (var opt in rec.PlayerOptions?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [])
            {
                if (opt.StartsWith(":http-user-agent=", StringComparison.Ordinal))
                    request.Headers.TryAddWithoutValidation("User-Agent", opt[":http-user-agent=".Length..]);
                else if (opt.StartsWith(":http-referrer=", StringComparison.Ordinal))
                    request.Headers.TryAddWithoutValidation("Referer", opt[":http-referrer=".Length..]);
            }

            using var response = await AppServices.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            job.Recording = rec = rec with { TotalBytes = total };

            await using var input = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var output = new FileStream(rec.Path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cts.Token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                written += read;
                job.Recording = rec with { BytesWritten = written };
            }

            Active.TryRemove(rec.Id, out _);
            Ui(() => Finish(rec with { BytesWritten = written }, RecordingStatus.Completed, null));
        }
        catch (OperationCanceledException)
        {
            Ui(() => Finish(rec, RecordingStatus.Cancelled, null));
        }
        catch (Exception ex)
        {
            Active.TryRemove(rec.Id, out _);
            Ui(() => Finish(rec, RecordingStatus.Failed, ex.Message));
        }
    }

    /// <summary>Current state, including live byte counts for running jobs.</summary>
    public static IReadOnlyList<Recording> All()
    {
        var stored = AppServices.Library.GetRecordings();
        return stored.Select(r => Active.TryGetValue(r.Id, out var job) ? job.Recording : r).ToList();
    }

    private static void Finish(Recording rec, RecordingStatus status, string? error)
    {
        AppServices.Library.SaveRecording(rec with { Status = status, Error = error });
        Changed?.Invoke();
    }

    private static void Ui(Action action)
    {
        if (_queue is null || !_queue.TryEnqueue(() => action()))
            action();
    }

    private static string PathFor(string title, DateTimeOffset start, string ext)
    {
        var folder = AppSettings.RecordingsFolder;
        var path = Path.Combine(folder, RecordingPlanner.FileName(title, start, ext));
        var n = 2;
        while (File.Exists(path))
            path = Path.Combine(folder, RecordingPlanner.FileName($"{title} ({n++})", start, ext));
        return path;
    }

    private static string? Join(IEnumerable<string> options)
    {
        var list = options.ToList();
        return list.Count == 0 ? null : string.Join('\n', list);
    }
}
