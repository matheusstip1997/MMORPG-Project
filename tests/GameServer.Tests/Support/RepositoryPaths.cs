namespace GameServer.Tests.Support;

/// <summary>Paths inside the checked-out repository, found from the test binaries' folder.</summary>
public static class RepositoryPaths
{
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }

    public static string Tables() => Path.Combine(Root(), "data", "tables");

    /// <summary>The committed M1 sample world exported by UnityExporter (backlog C2).</summary>
    public static string M1World() => Path.Combine(Root(), "data", "world", "m1");
}
