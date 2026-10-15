using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Antenna.App.Services;

/// <summary>
/// Disk cache for channel logos and posters, so tiles appear instantly on later visits
/// instead of re-downloading every image. Downloads are de-duplicated and throttled.
/// </summary>
internal static class ImageCache
{
    private const long MaxBytes = 400L * 1024 * 1024;

    private static readonly string Folder = CreateFolder();
    private static readonly ConcurrentDictionary<string, Task<string?>> InFlight = new();
    private static readonly SemaphoreSlim Throttle = new(6);

    private static string CreateFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Antenna", "images");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string PathFor(string url) =>
        Path.Combine(Folder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img");

    public static string? TryGetCached(string url)
    {
        var path = PathFor(url);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Local path of the image, downloading it first if needed. Null if it can't be fetched.</summary>
    public static Task<string?> GetAsync(string url) =>
        TryGetCached(url) is { } cached ? Task.FromResult<string?>(cached) : InFlight.GetOrAdd(url, DownloadAsync);

    private static async Task<string?> DownloadAsync(string url)
    {
        await Throttle.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = PathFor(url);
            using var response = await AppServices.Http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !media.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024)
                return null;
            var temp = path + ".part";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception)
        {
            return null; // Broken logo URLs are common; the tile shows initials instead.
        }
        finally
        {
            Throttle.Release();
            InFlight.TryRemove(url, out _);
        }
    }

    /// <summary>Keeps the cache under its size cap, dropping the least recently used files.</summary>
    public static void Trim()
    {
        try
        {
            var files = new DirectoryInfo(Folder).GetFiles("*.img").OrderByDescending(f => f.LastAccessTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > MaxBytes)
                    f.Delete();
            }
        }
        catch (IOException)
        {
        }
    }

    public static long SizeBytes() =>
        new DirectoryInfo(Folder).EnumerateFiles("*.img").Sum(f => f.Length);

    public static void Clear()
    {
        foreach (var f in Directory.EnumerateFiles(Folder))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }
}
