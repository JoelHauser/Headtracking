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
    /// The app's log: the newest lines in memory for the Diagnostics page and, when "Save a log
    /// file" is on (Privacy page), written to HeadTrackingApp\logs\HeadTracking.log, the previous
    /// run's kept as HeadTracking-prev.log. Nothing touches the disk until <see cref="SetFileEnabled"/>
    /// says so: the setting is not known until the settings load, and a second copy of the app
    /// that exits at once must not overwrite the running one's log. Any thread may write.
    /// </summary>
    public sealed class AppLog : ILogSink, IDisposable
    {
        private const int Keep = 1000;

        private readonly object _lock = new object();
        private readonly LinkedList<LogLine> _recent = new LinkedList<LogLine>();
        private StreamWriter _writer;
        private long _version;

        private readonly string _directory;
        private bool _fileEnabled, _fileStarted;

        public string FilePath { get; }
        public string PreviousPath { get; }

        /// <summary>Changes whenever a line is added; the UI polls it.</summary>
        public long Version => System.Threading.Interlocked.Read(ref _version);

        public bool FileEnabled
        {
            get
            {
                lock (_lock)
                {
                    return _fileEnabled;
                }
            }
        }

        public AppLog(string directory)
        {
            _directory = directory;
            FilePath = Path.Combine(directory, "HeadTracking.log");
            PreviousPath = Path.Combine(directory, "HeadTracking-prev.log");
        }

        /// <summary>
        /// Starts or stops writing to disk. The first start in a run keeps the last run's file as
        /// HeadTracking-prev.log and writes the lines logged so far (the startup, before the
        /// setting was known); a later start adds only new lines, so nothing logged while saving
        /// was off ever reaches the disk.
        /// </summary>
        public void SetFileEnabled(bool on)
        {
            lock (_lock)
            {
                _fileEnabled = on;
                if (!on)
                {
                    _writer?.Dispose();
                    _writer = null;
                    return;
                }

                if (_writer != null)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(_directory);
                    if (!_fileStarted)
                    {
                        if (File.Exists(FilePath))
                        {
                            File.Copy(FilePath, PreviousPath, true);
                        }

                        _writer = Open(FileMode.Create);
                        foreach (LogLine line in _recent)
                        {
                            _writer.WriteLine(line.ToString());
                        }

                        _fileStarted = true;
                    }
                    else
                    {
                        _writer = Open(FileMode.Append);
                    }
                }
                catch (Exception)
                {
                    // A log that cannot be written must not stop the app; the in-memory copy still works.
                    _writer = null;
                }
            }
        }

        /// <summary>Deletes this run's and the last run's log files; saving, if on, starts a fresh one.</summary>
        public int DeleteFiles()
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
                int deleted = 0;
                foreach (string path in new[] { FilePath, PreviousPath })
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            deleted++;
                        }
                    }
                    catch (Exception)
                    {
                        // In use by something else (an editor): left, and reported by the count.
                    }
                }

                if (_fileEnabled)
                {
                    try
                    {
                        _writer = Open(FileMode.Create);
                        _fileStarted = true;
                    }
                    catch (Exception)
                    {
                        _writer = null;
                    }
                }

                return deleted;
            }
        }

        private StreamWriter Open(FileMode mode)
        {
            return new StreamWriter(new FileStream(FilePath, mode, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }

        private static readonly System.Text.RegularExpressions.Regex UserProfile = ProfilePattern();

        /// <summary>
        /// Paths under the Windows user folder (SPT installed in C:\Users\name\...) would put the
        /// account name into every log someone shares for help; they read %USERPROFILE% instead.
        /// </summary>
        internal static string Redact(string message)
        {
            return UserProfile == null || string.IsNullOrEmpty(message) ? message : UserProfile.Replace(message, "%USERPROFILE%");
        }

        private static System.Text.RegularExpressions.Regex ProfilePattern()
        {
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(profile) || profile.Length < 4
                    ? null
                    : new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(profile.TrimEnd('\\')),
                                                               System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Log(LogLevel level, string message)
        {
            LogLine line = new LogLine { Time = DateTime.Now, Level = level, Message = Redact(message) };
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
