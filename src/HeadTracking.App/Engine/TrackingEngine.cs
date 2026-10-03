using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
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
        public double RelativeYaw, RelativePitch, RelativeZ;
        public double OutputYaw, OutputPitch, OutputZoom;
        public double RawYaw, RawPitch, RawZ;
        public bool Centered;
        public double TicksPerSecond;

        public bool GameConnected;
        public StatusMessage GameStatus;
        public string LinkError;
    }

    /// <summary>
    /// Runs the whole pipeline on one thread: newest pose from the source, through the tracker,
    /// out to the game; plus the game's commands back. Ticks the moment the source has a new pose,
    /// and at least every 8 ms otherwise, so the game sees each camera frame within a millisecond
    /// or two of the networks finishing with it.
    ///
    /// The UI talks to it only through <see cref="Apply"/>, <see cref="Recenter"/> and
    /// <see cref="GetSnapshot"/>, all thread safe.
    /// </summary>
    public sealed class TrackingEngine : IDisposable
    {
        private const int TickMilliseconds = 8;
        private const double SourceRetrySeconds = 5.0;
        private const double SettingsResendSeconds = 1.0;
        private const double GameTimeoutSeconds = 1.0;

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

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
        private double _nextSourceAttempt;
        private double _nextHealthCheck;
        private string _lastSourceError;
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

        /// <summary>Raised on the engine thread when the in-game toggle key changed Enabled.</summary>
        public event Action<bool> EnabledToggledFromGame;

        public TrackingEngine(AppSettings settings, AppLog log)
        {
            _log = log;
            _tracker = new HeadTracker(log);
            ApplyNow(settings.Clone());
        }

        public WebcamSource Webcam => _source as WebcamSource;

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

            if (Webcam != null)
            {
                Webcam.PreviewWanted = settings.ShowPreview;
            }

            _settingsRevision++;
            _nextSettingsSend = 0;
        }

        /// <summary>The settings name no camera, or the camera already running, with nothing else changed.</summary>
        private bool SameCameraAsRunning(AppSettings settings)
        {
            if (settings.Source != SourceKind.Webcam || !(_source is WebcamSource webcam))
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
            timeBeginPeriod(1);
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
                timeEndPeriod(1);
                StopSource();
                _link?.Dispose();
            }
        }

        private void Run()
        {
            WaitHandle[] handles = new WaitHandle[2];
            _lastTickRateTime = Clock.Now();
            while (!_stopping)
            {
                handles[0] = _wake;
                handles[1] = _source?.NewData ?? _wake;
                WaitHandle.WaitAny(handles, TickMilliseconds);
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

            PoseSnapshot snapshot = default;
            bool has = _source != null && _source.TryGetSnapshot(out snapshot);
            _tracker.Tick(has, snapshot, now, _trackingSettings);

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
                    Zoom = (float)_tracker.OutputZoom,
                });

                if (now >= _nextSettingsSend)
                {
                    _nextSettingsSend = now + SettingsResendSeconds;
                    _link.Send(_settings.ToGame(_settingsRevision));
                }
            }

            _ticks++;
            if (now - _lastTickRateTime >= 1.0)
            {
                _tickRate = (_ticks - _lastTicks) / (now - _lastTickRateTime);
                _lastTicks = _ticks;
                _lastTickRateTime = now;
            }

            PublishSnapshot(now);
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

            GameLink link = new GameLink(_settings.GamePort, _settings.StatusPort, _log);
            if (link.Start())
            {
                _link = link;
                _linkIdentity = _settings.LinkIdentity;
            }
            else
            {
                // Sending still works without the status listener; keep the link for poses.
                _link = link;
                _linkIdentity = _settings.LinkIdentity;
            }
        }

        private void EnsureSource(double now)
        {
            if (_source != null && now >= _nextHealthCheck)
            {
                _nextHealthCheck = now + 1.0;
                if (_source.GetStatus().NeedsRestart)
                {
                    _log.Warn(_source.Name + " has delivered nothing for a while; reopening it.");
                    StopSource();
                    _nextSourceAttempt = now + 1.0;
                }
            }

            if (_source != null || now < _nextSourceAttempt)
            {
                return;
            }

            ITrackerSource source = _settings.Source == SourceKind.Webcam
                ? new WebcamSource(_settings.ToWebcam(), () => _webcamOptions, _log) { PreviewWanted = _settings.ShowPreview }
                : (ITrackerSource)new OpenTrackSource(_settings.OpenTrackPort, _log);

            _sourceIdentity = _settings.SourceIdentity;
            if (_settings.Source == SourceKind.Webcam && NativeLibraries.Error != null)
            {
                Fail(source, now, NativeLibraries.Error);
                return;
            }

            _log.Info("Starting " + source.Name + ".");
            if (source.Start())
            {
                _source = source;
                if (source is WebcamSource webcam)
                {
                    // An empty camera name means "the first one"; record which that was, so the
                    // UI filling the name in afterwards is not taken for a change.
                    _sourceIdentity = "webcam|" + webcam.EffectiveConfig.Identity;
                }

                _lastSourceError = null;
                _log.Info(source.Name + " started: " + source.GetStatus().Summary);
                return;
            }

            Fail(source, now, source.GetStatus().Error ?? "unknown error");
        }

        private void Fail(ITrackerSource source, double now, string error)
        {
            if (error != _lastSourceError)
            {
                _lastSourceError = error;
                _log.Warn(source.Name + " did not start: " + error + " Retrying every " + SourceRetrySeconds + " s.");
            }

            try
            {
                source.Dispose();
            }
            catch (Exception)
            {
            }

            _nextSourceAttempt = now + SourceRetrySeconds;
            lock (_snapshotLock)
            {
                _snapshot.Source = new SourceStatus { Error = error, Summary = source.Name };
            }
        }

        private void StopSource()
        {
            ITrackerSource source = _source;
            _source = null;
            if (source != null)
            {
                _log.Info("Stopping " + source.Name + ".");
                try
                {
                    source.Dispose();
                }
                catch (Exception e)
                {
                    _log.Warn("Stopping " + source.Name + ": " + e.Message);
                }
            }
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
                SourceName = _source?.Name ?? (_settings.Source == SourceKind.Webcam ? "Webcam" : "OpenTrack"),
                Source = _source?.GetStatus() ?? GetSnapshot().Source,
                State = _tracker.State,
                Loss = _tracker.Loss,
                Enabled = _settings.Enabled,
                RelativeYaw = _tracker.RelativeYaw,
                RelativePitch = _tracker.RelativePitch,
                RelativeZ = _tracker.RelativeZ,
                OutputYaw = _tracker.OutputYaw,
                OutputPitch = _tracker.OutputPitch,
                OutputZoom = _tracker.OutputZoom,
                RawYaw = _tracker.RawYaw,
                RawPitch = _tracker.RawPitch,
                RawZ = _tracker.RawZ,
                Centered = _tracker.IsCentered,
                TicksPerSecond = _tickRate,
                GameConnected = gameConnected,
                GameStatus = status,
                LinkError = _link?.BindError,
            };

            lock (_snapshotLock)
            {
                _snapshot = s;
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _wake.Set();
            _thread?.Join(3000);
        }
    }
}
