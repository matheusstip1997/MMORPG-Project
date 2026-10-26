using System.Runtime.InteropServices;

namespace GameServer.Host;

/// <summary>
/// Dev builds on Windows only: raises the Windows timer resolution to 1 ms for this process while alive (timeBeginPeriod),
/// the mechanism of the BotClient relay (PR #47, <c>src/Tools/BotClient/Program.cs</c>). Without it a 1 ms sleep in
/// <see cref="StopwatchTickClock"/> lasts up to a full 15.6 ms system timer period, so ticks started 10 to 14 ms late on a
/// Windows dev machine (QA wave 6 O1; rule 12, 50 ms ticks). Windows 11 may ignore that request for a process it
/// power-throttles (no visible window), so the process first opts out of power throttling. A dev build is a DevTools build
/// (<c>DevTools.props</c>: Debug by default, the <c>DEV_TOOLS</c> constant); other builds and other operating systems change
/// nothing.
/// </summary>
public sealed class WindowsTimerResolution : IDisposable
{
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;
    private const uint PowerThrottlingIgnoreTimerResolution = 0x4;

    private WindowsTimerResolution(bool raised)
    {
        Raised = raised;
    }

    /// <summary>The 1 ms resolution is in effect (a dev build on Windows that the system granted it).</summary>
    public bool Raised { get; private set; }

    /// <summary>Whether this build raises it at all: a DevTools build on Windows.</summary>
    public static bool Applies =>
#if DEV_TOOLS
        OperatingSystem.IsWindows();
#else
        false;
#endif

    public static WindowsTimerResolution Raise()
    {
        if (!Applies)
            return new WindowsTimerResolution(false);
        // Control both flags with their state off: no EcoQoS, and timer resolution requests are always honoured.
        var state = new PowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = PowerThrottlingExecutionSpeed | PowerThrottlingIgnoreTimerResolution,
            StateMask = 0,
        };
        _ = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
        return new WindowsTimerResolution(TimeBeginPeriod(1) == 0);
    }

    public void Dispose()
    {
        if (!Raised)
            return;
        Raised = false;
        TimeEndPeriod(1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, int size);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
