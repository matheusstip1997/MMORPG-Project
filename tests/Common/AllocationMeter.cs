using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tests.Common;

/// <summary>
/// The one way the test projects measure that a hot path allocates nothing (CLAUDE.md: no allocations in the tick loop or
/// packet encode/decode; spec 11.2). Compiled into Shared.Tests and GameServer.Tests (linked source).
/// <para>
/// What it measures: <see cref="GC.GetAllocatedBytesForCurrentThread"/> around one run of the window, a per-thread counter,
/// so what other tests allocate on their threads is never counted directly. It is not exact on its own, though: the counter
/// is "bytes handed to this thread's allocation context, minus the context's unused rest", and while other threads allocate,
/// the runtime can take this thread's context away and the unused rest (up to one 8 KB allocation quantum) then counts as
/// allocated, sometimes with no GC counted during the window. That made these tests fail about every second CI run with
/// "0 expected, 3,592 to 7,464 bytes actual" while other test classes ran in parallel. Reproduced outside the tests
/// (2026-11-10, .NET 10): code that allocates nothing, measured 3,000 times while six threads allocated, read 7.5 to 8 KB in
/// 22 to 24 windows, and 0 in all 3,000 with a gen-0 collection right before each window.
/// </para>
/// <para>
/// So before each window a forced gen-0 collection empties this thread's allocation context (a GC resets every thread's
/// context), and nothing is left to count but what the window allocates. As a second guard, a window in which the JIT
/// compiled on this thread (<see cref="JitInfo.GetCompiledMethodCount"/>; a first call, a loop's on-stack replacement) is
/// not steady state: it is thrown away and the window runs again.
/// </para>
/// <para>
/// Why that keeps the tests' meaning: the first window without JIT work decides, exactly, byte for byte; it is not a minimum
/// over windows, so an allocation the code under test makes only now and then (a list that grows) still fails the test when
/// it lands in the deciding window, and a single allocation of a few bytes fails it too. Without parallel neighbours the
/// first window decides (the window's own delegate is compiled before it).
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
        // The window's own first compile, outside the measurement (the code it calls is the caller's warm-up).
        RuntimeHelpers.PrepareMethod(window.Method.MethodHandle);
        for (int i = 0; i < MaxWindows; i++)
        {
            // Empties this thread's allocation context: no unused rest is left for the runtime to count as allocated.
            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
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
