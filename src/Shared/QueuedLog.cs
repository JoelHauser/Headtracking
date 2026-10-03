using System;
using System.Collections.Concurrent;
using System.Threading;

namespace HeadTracking.Shared
{
    public enum LogLevel
    {
        Info,
        Warning,
        Error,
    }

    public interface ILogSink
    {
        void Log(LogLevel level, string message);
    }

    /// <summary>
    /// A log any thread can write to. The UDP thread must not call BepInEx's logger itself, so it
    /// writes here and the plugin's Update hands the lines to BepInEx on the main thread. Bounded,
    /// so a flood of bad packets cannot grow it without limit; what was dropped is reported.
    /// </summary>
    public sealed class QueuedLog : ILogSink
    {
        private const int Capacity = 500;

        private readonly ConcurrentQueue<Entry> _queue = new ConcurrentQueue<Entry>();
        private int _count;
        private int _dropped;

        private struct Entry
        {
            public LogLevel Level;
            public string Message;
        }

        public void Log(LogLevel level, string message)
        {
            if (Interlocked.Increment(ref _count) > Capacity)
            {
                Interlocked.Decrement(ref _count);
                Interlocked.Increment(ref _dropped);
                return;
            }

            _queue.Enqueue(new Entry { Level = level, Message = message });
        }

        /// <summary>Main thread only. Passes every queued line to <paramref name="write"/>.</summary>
        public void Drain(Action<LogLevel, string> write)
        {
            while (_queue.TryDequeue(out Entry entry))
            {
                Interlocked.Decrement(ref _count);
                write(entry.Level, entry.Message);
            }

            int dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
            {
                write(LogLevel.Warning, dropped + " log lines from the tracking thread were dropped (queue full).");
            }
        }
    }
}
