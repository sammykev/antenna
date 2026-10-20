using System.Globalization;

namespace Antenna.Mobile.Services;

/// <summary>A small rolling text log in the app's data folder (Playlists → Share log sends it).</summary>
public static class Log
{
    public static string FilePath { get; } = Path.Combine(FileSystem.AppDataDirectory, "logs", "antenna.log");

    private static readonly Lock Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }
}
