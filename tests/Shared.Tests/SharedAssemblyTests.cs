using System.Reflection;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace Shared.Tests;

/// <summary>
/// Architecture rule 7: Shared stays portable. Unity compiles it as netstandard2.1 and C# 9; the server and these tests
/// load its net10.0 build (MemoryPack's generated code needs it, user decision 2026-10-24). Both targets build together.
/// </summary>
public class SharedAssemblyTests
{
    private static readonly Assembly SharedAssembly = typeof(SharedAssemblyMarker).Assembly;

    [Fact]
    public void Targets_netstandard_2_1_for_Unity_and_net10_for_the_server()
    {
        var project = Project();
        var frameworks = project.Descendants("TargetFrameworks").Single().Value.Split(';');

        Assert.Equal(new[] { "netstandard2.1", "net10.0" }, frameworks);
        Assert.Equal(".NETCoreApp,Version=v10.0", SharedAssembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
    }

    [Fact]
    public void Our_sources_are_pinned_to_csharp_9()
    {
        var project = Project();
        var unconditional = project.Descendants("PropertyGroup")
            .Where(g => g.Attribute("Condition") == null)
            .SelectMany(g => g.Elements("LangVersion"))
            .Single();

        Assert.Equal("9", unconditional.Value);
    }

    [Fact]
    public void References_only_MemoryPack_and_the_standard_library()
    {
        var references = SharedAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name != "netstandard" && !name.StartsWith("System", StringComparison.Ordinal)
                           && !name.StartsWith("MemoryPack", StringComparison.Ordinal));

        Assert.Empty(references);
    }

    private static XDocument Project()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "Shared", "Shared.csproj"));
    }
}
