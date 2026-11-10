using System.Runtime;
using System.Runtime.CompilerServices;

namespace Tests.Common;

/// <summary>
/// The one way the test projects measure that a hot path allocates nothing (CLAUDE.md: no allocations in the tick loop or
/// packet encode/decode; spec 11.2). Compiled into Shared.Tests and GameServer.Tests (linked source).
/// <para>
/// What it measures: <see cref="GC.GetAllocatedBytesForCurrentThread"/> around exactly one run of the window, a per-thread
/// counter, so what other tests allocate on their threads is never counted directly. It is not exact on its own, though: the
/// counter is "bytes handed to this thread's allocation context, minus the context's unused rest", and while other threads
/// allocate, the runtime can take this thread's context away and the unused rest (up to one 8 KB allocation quantum) then
/// counts as allocated, sometimes with no GC counted during the window. That made these tests fail about every second CI
/// run with "0 expected, 3,592 to 7,464 bytes actual" while other test classes ran in parallel. Reproduced outside the
/// tests (2026-11-10, .NET 10): code that allocates nothing, measured 3,000 times while six threads allocated, read 7.5 to
/// 8 KB in 22 to 24 windows, and 0 in all 3,000 with a gen-0 collection right before each window.
/// </para>
/// <para>
/// So a forced gen-0 collection right before the window empties this thread's allocation context (a GC resets every
/// thread's context), and nothing is left to count but what the window allocates.
/// </para>
/// <para>
/// The decision is by bytes only, from that one run: any byte fails, whatever else happened in the window. JIT work on the
/// thread during the window (a loop's on-stack replacement, a first call the warm-up missed) is not forgiven; it puts no
/// managed bytes on the thread by itself (measured: a 5,000-iteration window whose loop is compiled inside it reads 0), so
/// it can neither fail nor excuse a window. The JIT and GC counts are only reported in the failure message. The window
/// never runs twice, so a window may carry state from the test (a tick counter, a filling queue) and an allocation the code
/// makes only once still fails.
/// </para>
/// </summary>
public static class AllocationMeter
{
    static AllocationMeter()
    {
        // The counters' own first calls, so they are never compiled inside a window.
        GC.GetAllocatedBytesForCurrentThread();
        JitInfo.GetCompiledMethodCount(currentThread: true);
        GC.CollectionCount(0);
    }

    /// <summary>
    /// Runs <paramref name="window"/> once and returns what it allocated on this thread. The caller warms the code up first
    /// (queues and lists at their working size, first calls made).
    /// </summary>
    public static AllocationMeasurement Measure(Action window)
    {
        // The window's own first compile, outside the measurement (the code it calls is the caller's warm-up).
        RuntimeHelpers.PrepareMethod(window.Method.MethodHandle);
        // Empties this thread's allocation context: no unused rest is left for the runtime to count as allocated.
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        int collections = GC.CollectionCount(0);
        long jitted = JitInfo.GetCompiledMethodCount(currentThread: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        window();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return new AllocationMeasurement(bytes,
            JitInfo.GetCompiledMethodCount(currentThread: true) - jitted, GC.CollectionCount(0) - collections);
    }

    /// <summary>Asserts that one run of <paramref name="window"/> allocates nothing on this thread (see <see cref="Measure"/>).</summary>
    public static void AssertNone(Action window)
    {
        var measured = Measure(window);
        Assert.True(measured.Bytes == 0, "allocated " + measured);
    }
}

/// <param name="Bytes">What the window allocated on this thread.</param>
/// <param name="Jitted">Methods the JIT compiled on this thread during the window (for the failure message).</param>
/// <param name="Collections">Gen-0 collections counted during the window (for the failure message).</param>
public readonly record struct AllocationMeasurement(long Bytes, long Jitted, int Collections)
{
    public override string ToString() =>
        $"{Bytes} bytes in the window ({Jitted} methods compiled on this thread, {Collections} GCs during it)";
}
