using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HeadTracking.App.Sources;
using HeadTracking.App.Webcam;
using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.App
{
    /// <summary>Everything the UI shows, copied out once per engine tick.</summary>
    public sealed class EngineSnapshot
    {
        public string SourceName = "";
        public SourceStatus Source = new SourceStatus();
        public TrackState State;
        public LossKind Loss;
        public bool Enabled;
        public double RelativeYaw, RelativePitch;
        public double OutputYaw, OutputPitch;
        public double RawYaw, RawPitch, RawZ;
        public double Sigma;
        public bool Centered;
        public double TicksPerSecond;
        public double CpuPercent;

        public bool GameConnected;
        public StatusMessage GameStatus;
        public string LinkError;
    }

    /// <summary>
    /// Runs the pipeline on one thread: newest pose from the source, through the tracker, out to
    /// the game; plus the game's commands back. Ticks the moment the source has a new pose, and
    /// about 30 times a second otherwise (the plugin follows smoothly at the game's frame rate, so
    /// sending faster than the camera only costs CPU).
    ///
    /// Opening and closing the camera takes up to a second, so it happens on a worker task: the
    /// engine keeps ticking (and the game keeps getting poses, marked lost) meanwhile.
    ///
    /// The UI talks to it only through <see cref="Apply"/>, <see cref="Recenter"/>,
    /// <see cref="RestartSource"/> and <see cref="GetSnapshot"/>, all thread safe.
    /// </summary>
    public sealed class TrackingEngine : IDisposable
    {
        private const int IdleTickMilliseconds = 33;
        private const double SourceRetrySeconds = 5.0;
        private const double SettingsResendSeconds = 1.0;
        private const double GameTimeoutSeconds = 1.0;
        private const double StatusRefreshSeconds = 0.25;
        private const double StatusLogSeconds = 10.0;

        private readonly AppLog _log;
        private readonly HeadTracker _tracker;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly ConcurrentQueue<Action> _work = new ConcurrentQueue<Action>();
        private readonly object _snapshotLock = new object();

        private AppSettings _settings;
        private TrackingSettings _trackingSettings;
        private WebcamTrackerOptions _webcamOptions;

        private ITrackerSource _source;
        private string _sourceIdentity;
        private Task<bool> _startTask;
        private ITrackerSource _starting;
        private Task _stopTask = Task.CompletedTask;
        private double _nextSourceAttempt;
        private double _nextHealthCheck;
        private string _lastSourceError;
        private SourceStatus _status = new SourceStatus();
        private double _nextStatusRefresh;

        private GameLink _link;
        private string _linkIdentity;
        private uint _settingsRevision;
        private double _nextSettingsSend;
        private uint _sequence;
        private bool _gameConnected;

        private Thread _thread;
        private volatile bool _stopping;
        private EngineSnapshot _snapshot = new EngineSnapshot();
        private long _ticks, _lastTicks;
        private double _lastTickRateTime, _tickRate;

        // For the periodic status line.
        private double _nextStatusLog;
        private long _statusTicks, _statusTrackingTicks;
        private long _lastSampleCount;
        private double _lastLiveYaw, _lastLivePitch, _shakeSum;
        private long _shakeCount;
        private TimeSpan _lastCpu;
        private double _lastCpuTime;
        private double _cpuPercent;

        /// <summary>Raised on the engine thread when the in-game toggle key changed Enabled.</summary>
        public event Action<bool> EnabledToggledFromGame;

        public TrackingEngine(AppSettings settings, AppLog log)
        {
            _log = log;
            _tracker = new HeadTracker(log);
            ApplyNow(settings.Clone());
        }

        public WebcamSource Webcam => _source as WebcamSource;

        /// <summary>Set by the UI: only make preview images while someone can see them.</summary>
        public volatile bool PreviewVisible = true;

        public void Start()
        {
            _thread = new Thread(RunGuarded) { IsBackground = true, Name = "Tracking engine", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        /// <summary>New settings from the UI. Applied on the engine thread at its next tick.</summary>
        public void Apply(AppSettings settings)
        {
            AppSettings copy = settings.Clone();
            _work.Enqueue(() => ApplyNow(copy));
            _wake.Set();
        }

        public void Recenter()
        {
            _work.Enqueue(() =>
            {
                _log.Info("Recenter requested from the app.");
                _tracker.RequestRecenter();
            });
            _wake.Set();
        }

        /// <summary>Close and reopen the source (the Restart button, or a changed camera list).</summary>
        public void RestartSource()
        {
            _work.Enqueue(() =>
            {
                _log.Info("Restarting the tracking source.");
                StopSource();
                _nextSourceAttempt = 0;
                _lastSourceError = null;
            });
            _wake.Set();
        }

        public EngineSnapshot GetSnapshot()
        {
            lock (_snapshotLock)
            {
                return _snapshot;
            }
        }

        private void ApplyNow(AppSettings settings)
        {
            AppSettings old = _settings;
            _settings = settings;
            _trackingSettings = settings.ToTracking();
            _webcamOptions = settings.ToWebcamOptions();

            if (_sourceIdentity != null && _sourceIdentity != settings.SourceIdentity && !SameCameraAsRunning(settings))
            {
                _log.Info("Tracking source settings changed (" + _sourceIdentity + " -> " + settings.SourceIdentity + "); restarting it.");
                StopSource();
                _nextSourceAttempt = 0;
                _lastSourceError = null;
                if (old == null || old.Source != settings.Source)
                {
                    // Another device: its angles mean something else, so forget the centre.
                    _tracker.Reset();
                }
            }

            if (_linkIdentity != null && _linkIdentity != settings.LinkIdentity)
            {
                _log.Info("Game link ports changed; reconnecting.");
                _link?.Dispose();
                _link = null;
            }

            _settingsRevision++;
            _nextSettingsSend = 0;
        }

        /// <summary>The settings name no camera, or the camera already running, with nothing else changed.</summary>
        private bool SameCameraAsRunning(AppSettings settings)
        {
            return _source is WebcamSource webcam && SameCamera(settings, webcam);
        }

        private static bool SameCamera(AppSettings settings, WebcamSource webcam)
        {
            if (settings.Source != SourceKind.Webcam)
            {
                return false;
            }

            WebcamConfig wanted = settings.ToWebcam();
            if (string.IsNullOrEmpty(wanted.CameraName))
            {
                wanted.CameraName = webcam.OpenedCamera;
            }

            return wanted.Identity == webcam.EffectiveConfig.Identity;
        }

        private void RunGuarded()
        {
            try
            {
                _log.Info("Tracking engine started.");
                Run();
            }
            catch (Exception e)
            {
                _log.Error("The tracking engine stopped after an error: " + e);
            }
            finally
            {
                StopSource();
                _stopTask.Wait(3000);
                _link?.Dispose();
            }
        }

        private void Run()
        {
            WaitHandle[] handles = new WaitHandle[2];
            double now = Clock.Now();
            _lastTickRateTime = now;
            _nextStatusLog = now + StatusLogSeconds;
            _lastCpuTime = now;
            _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
            while (!_stopping)
            {
                handles[0] = _wake;
                handles[1] = _source?.NewData ?? _wake;
                WaitHandle.WaitAny(handles, IdleTickMilliseconds);
                if (_stopping)
                {
                    break;
                }

                while (_work.TryDequeue(out Action work))
                {
                    work();
                }

                Tick(Clock.Now());
            }
        }

        private void Tick(double now)
        {
            EnsureLink();
            EnsureSource(now);

            if (_source is WebcamSource webcam)
            {
                webcam.PreviewWanted = _settings.ShowPreview && PreviewVisible;
            }

            PoseSnapshot snapshot = default;
            bool has = _source != null && _source.TryGetSnapshot(out snapshot);
            _tracker.Tick(has, snapshot, now, _trackingSettings);
            MeasureShake();

            HandleGameCommands();

            if (_link != null)
            {
                _link.Send(new PoseMessage
                {
                    Sequence = ++_sequence,
                    State = (LinkTrackState)_tracker.State,
                    Enabled = _settings.Enabled,
                    Yaw = (float)_tracker.OutputYaw,
                    Pitch = (float)_tracker.OutputPitch,
                });

                if (now >= _nextSettingsSend)
                {
                    _nextSettingsSend = now + SettingsResendSeconds;
                    _link.Send(_settings.ToGame(_settingsRevision));
                }
            }

            _ticks++;
            _statusTicks++;
            if (_tracker.State == TrackState.Tracking)
            {
                _statusTrackingTicks++;
            }

            if (now - _lastTickRateTime >= 1.0)
            {
                _tickRate = (_ticks - _lastTicks) / (now - _lastTickRateTime);
                _lastTicks = _ticks;
                _lastTickRateTime = now;
            }

            if (now >= _nextStatusRefresh)
            {
                _nextStatusRefresh = now + StatusRefreshSeconds;
                if (_source != null)
                {
                    _status = _source.GetStatus();
                }
                else if (_startTask != null)
                {
                    _status = new SourceStatus { Summary = "starting..." };
                }
            }

            PublishSnapshot(now);
            MaybeLogStatus(now);
        }

        /// <summary>Average frame-to-frame change of the shaped output: the shake you would see.</summary>
        private void MeasureShake()
        {
            if (_tracker.Samples == _lastSampleCount)
            {
                return;
            }

            if (_tracker.State == TrackState.Tracking && _lastSampleCount > 0)
            {
                double dy = _tracker.LiveYaw - _lastLiveYaw, dp = _tracker.LivePitch - _lastLivePitch;
                _shakeSum += Math.Sqrt(dy * dy + dp * dp);
                _shakeCount++;
            }

            _lastSampleCount = _tracker.Samples;
            _lastLiveYaw = _tracker.LiveYaw;
            _lastLivePitch = _tracker.LivePitch;
        }

        private void HandleGameCommands()
        {
            if (_link == null)
            {
                return;
            }

            while (_link.TryTakeCommand(out LinkCommand command))
            {
                switch (command)
                {
                    case LinkCommand.Hello:
                        _nextSettingsSend = 0;
                        break;
                    case LinkCommand.Recenter:
                        _log.Info("Recenter key pressed in game.");
                        _tracker.RequestRecenter();
                        break;
                    case LinkCommand.Toggle:
                        _settings.Enabled = !_settings.Enabled;
                        _log.Info("Toggle key pressed in game: head tracking " + (_settings.Enabled ? "ON" : "OFF") + ".");
                        EnabledToggledFromGame?.Invoke(_settings.Enabled);
                        break;
                }
            }
        }

        private void EnsureLink()
        {
            if (_link != null)
            {
                return;
            }

            // Sending works even if the status port is taken; Start logs that case.
            _link = new GameLink(_settings.GamePort, _settings.StatusPort, _log);
            _link.Start();
            _linkIdentity = _settings.LinkIdentity;
        }

        private void EnsureSource(double now)
        {
            if (_startTask != null)
            {
                if (!_startTask.IsCompleted)
                {
                    return;
                }

                FinishStart(now);
            }

            if (_source != null)
            {
                if (now >= _nextHealthCheck)
                {
                    _nextHealthCheck = now + 1.0;
                    if (_status.NeedsRestart)
                    {
                        _log.Warn(_source.Name + " has delivered nothing for a while; reopening it.");
                        StopSource();
                        _nextSourceAttempt = now + 1.0;
                    }
                }

                return;
            }

            if (now < _nextSourceAttempt)
            {
                return;
            }

            ITrackerSource source = _settings.Source == SourceKind.Webcam
                ? new WebcamSource(_settings.ToWebcam(), () => _webcamOptions, _log)
                : (ITrackerSource)new OpenTrackSource(_settings.OpenTrackPort, _log);
            _sourceIdentity = _settings.SourceIdentity;

            if (_settings.Source == SourceKind.Webcam && NativeLibraries.Error != null)
            {
                Fail(source, now, NativeLibraries.Error);
                return;
            }

            _log.Info("Starting " + source.Name + ".");
            _starting = source;
            Task previousStop = _stopTask;
            _startTask = Task.Run(() =>
            {
                // A camera that is still closing cannot be opened again yet.
                previousStop.Wait(5000);
                return source.Start();
            });
        }

        private void FinishStart(double now)
        {
            ITrackerSource source = _starting;
            bool ok = _startTask.Status == TaskStatus.RanToCompletion && _startTask.Result;
            _startTask = null;
            _starting = null;

            if (!ok)
            {
                Fail(source, now, source.GetStatus().Error ?? "unknown error");
                return;
            }

            bool stillWanted = _sourceIdentity == _settings.SourceIdentity || (source is WebcamSource started && SameCamera(_settings, started));
            if (!stillWanted)
            {
                // The settings changed while it was opening: close it and open the new one.
                _log.Info(source.Name + " started, but the settings changed meanwhile; reopening.");
                Dispose(source);
                return;
            }

            _source = source;
            _lastSourceError = null;
            _status = source.GetStatus();
            if (source is WebcamSource webcam)
            {
                // An empty camera name means "the first one"; record which that was, so the UI
                // filling the name in afterwards is not taken for a change.
                _sourceIdentity = "webcam|" + webcam.EffectiveConfig.Identity;
            }

            _log.Info(source.Name + " started: " + _status.Summary);
        }

        private void Fail(ITrackerSource source, double now, string error)
        {
            if (error != _lastSourceError)
            {
                _lastSourceError = error;
                _log.Warn(source.Name + " did not start: " + error + " Retrying every " + SourceRetrySeconds + " s.");
            }

            Dispose(source);
            _nextSourceAttempt = now + SourceRetrySeconds;
            _status = new SourceStatus { Error = error, Summary = source.Name };
        }

        private void StopSource()
        {
            ITrackerSource source = _source;
            _source = null;
            if (source != null)
            {
                _log.Info("Stopping " + source.Name + ".");
                Dispose(source);
            }
        }

        /// <summary>Closes a source on a worker task; the next start waits for it.</summary>
        private void Dispose(ITrackerSource source)
        {
            Task previous = _stopTask;
            _stopTask = Task.Run(() =>
            {
                previous.Wait(5000);
                try
                {
                    source.Dispose();
                }
                catch (Exception e)
                {
                    _log.Warn("Stopping " + source.Name + ": " + e.Message);
                }
            });
        }

        private void PublishSnapshot(double now)
        {
            StatusMessage status = default;
            double statusTime = double.NegativeInfinity;
            bool gameConnected = _link != null && _link.TryGetStatus(out status, out statusTime) && now - statusTime < GameTimeoutSeconds;
            if (gameConnected != _gameConnected)
            {
                _gameConnected = gameConnected;
                _log.Info(gameConnected ? "Game link up." : "Game link down (no status from the game for " + GameTimeoutSeconds + " s).");
            }

            EngineSnapshot s = new EngineSnapshot
            {
                SourceName = _source?.Name ?? _starting?.Name ?? (_settings.Source == SourceKind.Webcam ? "Webcam" : "OpenTrack"),
                Source = _status,
                State = _tracker.State,
                Loss = _tracker.Loss,
                Enabled = _settings.Enabled,
                RelativeYaw = _tracker.RelativeYaw,
                RelativePitch = _tracker.RelativePitch,
                OutputYaw = _tracker.OutputYaw,
                OutputPitch = _tracker.OutputPitch,
                RawYaw = _tracker.RawYaw,
                RawPitch = _tracker.RawPitch,
                RawZ = _tracker.RawZ,
                Sigma = _tracker.LastSigma,
                Centered = _tracker.IsCentered,
                TicksPerSecond = _tickRate,
                CpuPercent = _cpuPercent,
                GameConnected = gameConnected,
                GameStatus = status,
                LinkError = _link?.BindError,
            };

            lock (_snapshotLock)
            {
                _snapshot = s;
            }
        }

        private void MaybeLogStatus(double now)
        {
            if (now < _nextStatusLog)
            {
                return;
            }

            double span = now - (_nextStatusLog - StatusLogSeconds);
            _nextStatusLog = now + StatusLogSeconds;

            TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
            _cpuPercent = (cpu - _lastCpu).TotalSeconds / Math.Max(0.001, now - _lastCpuTime) * 100.0;
            _lastCpu = cpu;
            _lastCpuTime = now;

            SourceStatus src = _status;
            string source;
            if (_source is WebcamSource webcam)
            {
                source = "webcam " + src.CameraRate.ToString("0.0") + " fps from the camera, " + src.Rate.ToString("0.0") + " tracked, "
                         + src.InferenceMs.ToString("0.0") + " ms/frame, uncertainty " + webcam.TakeAverageSigma().ToString("0.00") + " deg, brightness "
                         + src.Brightness.ToString("0") + "/255";
            }
            else if (_source != null)
            {
                source = src.Summary;
            }
            else
            {
                source = _startTask != null ? "source starting" : "no source" + (src.Error != null ? " (" + src.Error + ")" : "");
            }

            double tracking = _statusTicks > 0 ? 100.0 * _statusTrackingTicks / _statusTicks : 0;
            string shake = _shakeCount > 0 ? (_shakeSum / _shakeCount).ToString("0.00") + " deg/frame" : "n/a";
            _statusTicks = _statusTrackingTicks = 0;
            _shakeSum = 0;
            _shakeCount = 0;

            StatusMessage game = GetSnapshot().GameStatus;
            string gameText = !_gameConnected ? "not connected"
                : !game.InRaid ? "connected, in menus"
                : "in raid, " + game.GameFps.ToString("0") + " fps, " + (game.PauseReasons == 0 ? "applying" : "paused: " + PauseFader.Describe((PauseReason)game.PauseReasons));

            _log.Info("Status: " + _tracker.State + " " + tracking.ToString("0") + "% of the last " + span.ToString("0") + " s | " + source
                      + " | output change " + shake + " | head yaw " + HeadTracker.Deg(_tracker.RelativeYaw) + " pitch " + HeadTracker.Deg(_tracker.RelativePitch)
                      + " -> game yaw " + HeadTracker.Deg(_tracker.OutputYaw) + " pitch " + HeadTracker.Deg(_tracker.OutputPitch)
                      + " | game " + gameText + " | engine " + _tickRate.ToString("0") + " ticks/s, app CPU " + _cpuPercent.ToString("0") + "% of one core");
        }

        public void Dispose()
        {
            _stopping = true;
            _wake.Set();
            _thread?.Join(4000);
        }
    }
}
