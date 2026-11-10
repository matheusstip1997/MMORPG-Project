using System.Runtime;
using System.Text;

namespace Tests.Common;

/// <summary>
/// The one way the test projects measure that a hot path allocates nothing (CLAUDE.md: no allocations in the tick loop or
/// packet encode/decode; spec 11.2). Compiled into Shared.Tests and GameServer.Tests (linked source).
/// <para>
/// How it measures: <see cref="GC.GetAllocatedBytesForCurrentThread"/> around one run of the window, so other tests running
/// in parallel on other threads never count (a GC they trigger does not change this thread's counter either). What did count,
/// and made these tests fail about every second CI run with a few thousand bytes, is the runtime's own one-time work on this
/// thread: the JIT compiling a method on its first call or a loop's on-stack-replacement variant while the window runs. On a
/// loaded machine the tiered JIT runs late (call counting waits for a quiet moment in the whole process), so a warm-up that
/// is long enough alone can still see that work land inside the window. <see cref="JitInfo.GetCompiledMethodCount"/> for the
/// current thread says when it did: such a window is not steady state, so it is thrown away and the window runs again, now
/// compiled.
/// </para>
/// <para>
/// Why that keeps the tests' meaning: the first window in which the JIT did not run on this thread decides, exactly, byte for
/// byte; it is not a minimum over windows, so an allocation the code under test makes only now and then (a list that grows)
/// still fails the test when it lands in the deciding window. A window that keeps compiling (code that generates code every
/// time) never becomes steady state and fails.
/// </para>
/// </summary>
public static class AllocationMeter
{
    /// <summary>Windows tried before giving up on finding one without JIT work on this thread.</summary>
    public const int MaxWindows = 8;

    static AllocationMeter()
    {
        // The counters' own first calls, so they are never compiled inside a window.
        GC.GetAllocatedBytesForCurrentThread();
        JitInfo.GetCompiledMethodCount(currentThread: true);
        GC.CollectionCount(0);
    }

    /// <summary>
    /// Runs <paramref name="window"/> until one run happens without the JIT compiling on this thread (at most
    /// <see cref="MaxWindows"/> runs) and returns what that run allocated on this thread. The caller warms the code up first
    /// (queues and lists at their working size); <paramref name="window"/> may run more than once, so it must be repeatable.
    /// </summary>
    public static AllocationMeasurement Measure(Action window)
    {
        var tried = new (long Bytes, long Jitted, int Collections)[MaxWindows];
        for (int i = 0; i < MaxWindows; i++)
        {
            int collections = GC.CollectionCount(0);
            long jitted = JitInfo.GetCompiledMethodCount(currentThread: true);
            long before = GC.GetAllocatedBytesForCurrentThread();
            window();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            tried[i] = (bytes, JitInfo.GetCompiledMethodCount(currentThread: true) - jitted, GC.CollectionCount(0) - collections);
            if (tried[i].Jitted == 0)
                return new AllocationMeasurement(bytes, true, Describe(tried, i + 1));
        }
        return new AllocationMeasurement(tried[MaxWindows - 1].Bytes, false, Describe(tried, MaxWindows));
    }

    /// <summary>Asserts that <paramref name="window"/> allocates nothing once warm (see <see cref="Measure"/>).</summary>
    public static void AssertNone(Action window)
    {
        var measured = Measure(window);
        Assert.True(measured.Steady, "no window ran without the JIT compiling on this thread: " + measured.Windows);
        Assert.True(measured.Bytes == 0, $"allocated {measured.Bytes} bytes in a window without JIT work on this thread: " + measured.Windows);
    }

    private static string Describe((long Bytes, long Jitted, int Collections)[] tried, int count)
    {
        var text = new StringBuilder();
        for (int i = 0; i < count; i++)
            text.Append(i == 0 ? "" : "; ").Append($"window {i + 1}: {tried[i].Bytes} bytes, {tried[i].Jitted} methods compiled on this thread, {tried[i].Collections} GCs");
        return text.ToString();
    }
}

/// <param name="Bytes">What the deciding window allocated on this thread.</param>
/// <param name="Steady">False when every window had JIT work on this thread (then <paramref name="Bytes"/> is the last one's).</param>
/// <param name="Windows">Every window tried, for the failure message.</param>
public readonly record struct AllocationMeasurement(long Bytes, bool Steady, string Windows);
