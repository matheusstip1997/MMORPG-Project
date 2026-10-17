using System.Reflection;
using System.Runtime.Versioning;

namespace Shared.Tests;

public class SharedAssemblyTests
{
    private static readonly Assembly SharedAssembly = typeof(SharedAssemblyMarker).Assembly;

    [Fact]
    public void Targets_netstandard_2_1()
    {
        var framework = SharedAssembly.GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.Equal(".NETStandard,Version=v2.1", framework?.FrameworkName);
    }

    [Fact]
    public void References_only_MemoryPack_and_the_standard_library()
    {
        var references = SharedAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name != "netstandard" && !name.StartsWith("MemoryPack", StringComparison.Ordinal));

        Assert.Empty(references);
    }
}
