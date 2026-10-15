using System.Text;

namespace Antenna.App.Services;

/// <summary>
/// Writes problems to %LOCALAPPDATA%\Antenna\logs\antenna.log so crashes can be diagnosed.
/// Never throws: logging must not be the thing that breaks.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Antenna", "logs");

    public static string FilePath => Path.Combine(Folder, "antenna.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(Exception ex, string where) => Write("ERROR", $"{where}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, Path.Combine(Folder, "antenna.old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// Runs work on the thread pool. Unlike ThreadPool.QueueUserWorkItem, an exception is logged
/// instead of terminating the app.
/// </summary>
internal static class Worker
{
    public static void Run(Action action, string where = "background")
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error(ex, where);
            }
        });
    }
}
