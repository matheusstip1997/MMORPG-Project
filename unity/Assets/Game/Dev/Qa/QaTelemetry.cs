#nullable enable
using System.Collections.Generic;

namespace Game.Dev.Qa
{
    /// <summary>
    /// The server's movement telemetry of one character, read back from the run's JSON lines (spec 7 telemetry, spec 2.2):
    /// the lock-demon step's end-to-end check that an honest client is never clamped (M3 decision 15).
    /// </summary>
    public static class QaTelemetry
    {
        /// <summary>
        /// The <c>LockFacingClamp</c> records (kind 103) of <paramref name="entityId"/>: checked clamps (a locked facing the server
        /// pulled toward the target) and unchecked frames (a locked frame applied unlocked: no target known for it).
        /// </summary>
        public static void CountLockFacingClamps(IEnumerable<string> lines, uint entityId, out int clamped, out int uncheckedFrames)
        {
            clamped = 0;
            uncheckedFrames = 0;
            foreach (var line in lines)
            {
                if (!line.Contains("\"k\":\"LockFacingClamp\"") || QaDemand.Field(line, "entity") != entityId)
                    continue;
                if (QaDemand.Field(line, "unchecked") == 1)
                    uncheckedFrames++;
                else
                    clamped++;
            }
        }
    }
}
