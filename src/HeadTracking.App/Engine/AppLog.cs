using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HeadTracking.Shared;

namespace HeadTracking.App
{
    public sealed class LogLine
    {
        public DateTime Time;
        public LogLevel Level;
        public string Message;

        public string TimeText => Time.ToString("HH:mm:ss.fff");

        public override string ToString()
        {
            return TimeText + " [" + Level + "] " + Message;
        }
    }

    /// <summary>
    /// The app's log: written to HeadTrackingApp\logs\HeadTracking.log (the previous run's kept as
    /// HeadTracking-prev.log), and the newest lines kept in memory for the Diagnostics page. Any
    /// thread may write.
    /// </summary>
    public sealed class AppLog : ILogSink, IDisposable
    {
        private const int Keep = 1000;

        private readonly object _lock = new object();
        private readonly LinkedList<LogLine> _recent = new LinkedList<LogLine>();
        private StreamWriter _writer;
        private long _version;

        public string FilePath { get; }

        /// <summary>Changes whenever a line is added; the UI polls it.</summary>
        public long Version => System.Threading.Interlocked.Read(ref _version);

        public AppLog(string directory)
        {
            FilePath = Path.Combine(directory, "HeadTracking.log");
            try
            {
                Directory.CreateDirectory(directory);
                string previous = Path.Combine(directory, "HeadTracking-prev.log");
                if (File.Exists(FilePath))
                {
                    File.Copy(FilePath, previous, true);
                }

                _writer = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
            }
            catch (Exception)
            {
                // A log that cannot be written must not stop the app; the in-memory copy still works.
                _writer = null;
            }
        }

        public void Log(LogLevel level, string message)
        {
            LogLine line = new LogLine { Time = DateTime.Now, Level = level, Message = message };
            lock (_lock)
            {
                _recent.AddLast(line);
                if (_recent.Count > Keep)
                {
                    _recent.RemoveFirst();
                }

                try
                {
                    _writer?.WriteLine(line.ToString());
                }
                catch (Exception)
                {
                    _writer = null;
                }
            }

            System.Threading.Interlocked.Increment(ref _version);
        }

        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warning, message);
        public void Error(string message) => Log(LogLevel.Error, message);

        public List<LogLine> Snapshot()
        {
            lock (_lock)
            {
                return new List<LogLine>(_recent);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
