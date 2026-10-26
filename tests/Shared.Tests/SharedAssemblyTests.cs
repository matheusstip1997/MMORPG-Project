using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Shared.Tests;

/// <summary>
/// Shared builds twice (architecture rule 7): netstandard2.1 with C# 9, the build Unity's compile matches, and net10.0 for
/// the server, where MemoryPack's interfaces have static abstract members its generated code must implement. These tests
/// read the netstandard2.1 build, which only the standard library and MemoryPack may be referenced from.
/// </summary>
public class SharedAssemblyTests
{
    private static string NetStandardAssemblyPath =>
        typeof(SharedAssemblyTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "SharedNetStandardAssembly").Value!;

    [Fact]
    public void The_unity_build_targets_netstandard_2_1()
    {
        using var file = File.OpenRead(NetStandardAssemblyPath);
        using var pe = new PEReader(file);
        var reader = pe.GetMetadataReader();

        Assert.Contains(".NETStandard,Version=v2.1", AttributeBlobs(reader, "TargetFrameworkAttribute"));
    }

    [Fact]
    public void The_unity_build_references_only_MemoryPack_and_the_standard_library()
    {
        using var file = File.OpenRead(NetStandardAssemblyPath);
        using var pe = new PEReader(file);
        var reader = pe.GetMetadataReader();
        var references = reader.AssemblyReferences
            .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
            .Where(name => name != "netstandard" && !name.StartsWith("MemoryPack", StringComparison.Ordinal));

        Assert.Empty(references);
    }

    [Fact]
    public void The_server_build_references_only_MemoryPack_and_the_runtime()
    {
        var references = typeof(SharedAssemblyMarker).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                && !name.StartsWith("MemoryPack", StringComparison.Ordinal));

        Assert.Empty(references);
    }

    /// <summary>The decoded fixed string argument of each assembly attribute of this type name.</summary>
    private static IEnumerable<string> AttributeBlobs(MetadataReader reader, string typeName)
    {
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;
            var ctor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference
                || reader.GetString(reader.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name) != typeName)
                continue;
            var blob = reader.GetBlobReader(attribute.Value);
            blob.ReadUInt16(); // prolog
            yield return blob.ReadSerializedString() ?? string.Empty;
        }
    }
}
