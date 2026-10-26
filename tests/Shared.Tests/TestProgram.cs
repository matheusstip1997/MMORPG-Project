using Shared.Tests.Protocol;

namespace Shared.Tests;

/// <summary>
/// The test assembly's entry point (the csproj turns off the generated one). The test runner never calls it;
/// <see cref="MemoryPackCrashReproductionTests"/> starts this assembly as a child process to show what an unguarded
/// MemoryPack read does to a process, without taking the test host down with it.
/// </summary>
public static class TestProgram
{
    public static int Main(string[] args) =>
        args.Length == 2 && args[0] == MemoryPackCrashProbe.Verb ? MemoryPackCrashProbe.Run(args[1]) : 0;
}
