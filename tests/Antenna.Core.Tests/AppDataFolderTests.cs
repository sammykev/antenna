namespace Antenna.Core.Tests;

public sealed class AppDataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"antenna-data-{Guid.NewGuid():N}");
    private string Legacy => Path.Combine(_root, "Antenna");
    private string Target => Path.Combine(_root, "AntennaData");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Moves_library_logs_and_images_out_of_the_install_folder()
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "logs"));
        Directory.CreateDirectory(Path.Combine(Legacy, "images"));
        Directory.CreateDirectory(Path.Combine(Legacy, "current"));
        File.WriteAllText(Path.Combine(Legacy, "library.db"), "db");
        File.WriteAllText(Path.Combine(Legacy, "library.db-wal"), "wal");
        File.WriteAllText(Path.Combine(Legacy, "logs", "antenna.log"), "log");

        var moved = AppDataFolder.MigrateFromLegacy(Legacy, Target);

        Assert.Equal(["library.db", "library.db-wal", "logs", "images"], moved);
        Assert.Equal("db", File.ReadAllText(Path.Combine(Target, "library.db")));
        Assert.True(File.Exists(Path.Combine(Target, "logs", "antenna.log")));
        Assert.False(File.Exists(Path.Combine(Legacy, "library.db")));
        Assert.True(Directory.Exists(Path.Combine(Legacy, "current"))); // The installer's files stay put.
    }

    [Fact]
    public void Leaves_an_existing_library_alone()
    {
        Directory.CreateDirectory(Legacy);
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Legacy, "library.db"), "old");
        File.WriteAllText(Path.Combine(Target, "library.db"), "new");

        Assert.Empty(AppDataFolder.MigrateFromLegacy(Legacy, Target));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Target, "library.db")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Legacy, "library.db")));
    }

    [Fact]
    public void Does_nothing_without_an_old_library()
    {
        Assert.Empty(AppDataFolder.MigrateFromLegacy(Legacy, Target));
        Assert.False(Directory.Exists(Target));
    }
}
