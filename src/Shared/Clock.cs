using System.Diagnostics;

namespace HeadTracking.Shared
{
    /// <summary>Seconds on a monotonic clock. Safe from any thread.</summary>
    public static class Clock
    {
        private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

        public static double Now()
        {
            return Stopwatch.GetTimestamp() * TicksToSeconds;
        }
    }
}
