using TimerResolution = GameServer.Host.WindowsTimerResolution;

namespace GameServer.Tests.Host;

/// <summary>QA wave 6 O1: dev builds on Windows raise the timer resolution for the tick loop; nothing else changes.</summary>
public class WindowsTimerResolutionTests
{
    [Fact]
    public void Only_a_dev_build_on_windows_raises_it_and_dispose_lowers_it_again()
    {
        if (!OperatingSystem.IsWindows())
            Assert.False(TimerResolution.Applies); // Linux (production, CI): never
        var timer = TimerResolution.Raise();
        if (!TimerResolution.Applies)
            Assert.False(timer.Raised); // release builds and Linux: no call at all
        timer.Dispose();
        Assert.False(timer.Raised);
        timer.Dispose(); // a second dispose ends nothing twice
    }
}
