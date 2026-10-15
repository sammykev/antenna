namespace Antenna.Core;

/// <summary>
/// Where Antenna keeps its library, logs and image cache: %LOCALAPPDATA%\AntennaData on Windows.
/// </summary>
/// <remarks>
/// It used to be %LOCALAPPDATA%\Antenna, but the installer owns that folder (it installs the app there
/// and deletes it on uninstall), so data now lives beside it and is moved over once.
/// </remarks>
public static class AppDataFolder
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Path { get; } = System.IO.Path.Combine(LocalAppData, "AntennaData");

    public static string LegacyPath { get; } = System.IO.Path.Combine(LocalAppData, "Antenna");

    private static readonly string[] Files = ["library.db", "library.db-wal", "library.db-shm"];
    private static readonly string[] Folders = ["logs", "images"];

    /// <summary>Moves data from the old folder when the new one has no library yet. Returns what was moved.</summary>
    public static IReadOnlyList<string> MigrateFromLegacy() => MigrateFromLegacy(LegacyPath, Path);

    public static IReadOnlyList<string> MigrateFromLegacy(string legacy, string target)
    {
        var moved = new List<string>();
        if (File.Exists(System.IO.Path.Combine(target, "library.db")) || !File.Exists(System.IO.Path.Combine(legacy, "library.db")))
            return moved;

        Directory.CreateDirectory(target);
        foreach (var name in Files)
        {
            var from = System.IO.Path.Combine(legacy, name);
            if (!File.Exists(from))
                continue;
            var to = System.IO.Path.Combine(target, name);
            try
            {
                File.Move(from, to, overwrite: true);
            }
            catch (IOException)
            {
                // In use (an older copy still open): copy now, the old file stays behind.
                File.Copy(from, to, overwrite: true);
            }
            moved.Add(name);
        }
        foreach (var name in Folders)
        {
            var from = System.IO.Path.Combine(legacy, name);
            var to = System.IO.Path.Combine(target, name);
            if (!Directory.Exists(from) || Directory.Exists(to))
                continue;
            try
            {
                Directory.Move(from, to);
                moved.Add(name);
            }
            catch (IOException)
            {
                // Logs and cached images can be rebuilt; not worth failing start-up over.
            }
        }
        return moved;
    }
}
