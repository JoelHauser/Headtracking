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
        /// <summary>The head angle after all filtering, before sensitivity: what the view follows.</summary>
        public double SettledYaw, SettledPitch;
        /// <summary>Measured tracker noise (head degrees, one sigma) and the stillness band in use.</summary>
        public double Noise, Band;
        public double Sigma;
        public bool Centered;
        public double TicksPerSecond;
        public double CpuPercent;

        public bool GameConnected;
        public StatusMessage GameStatus;
        public string LinkError;

        /// <summary>Why the webcam is off on purpose (privacy), or null while it may run.</summary>
        public string CameraOffReason;
        /// <summary>True when the off was the user's own "Turn camera off now".</summary>
        public bool CameraManuallyOff;
        /// <summary>The webcam is open (its light is on).</summary>
        public bool CameraRunning;
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

        /// <summary>Set by the window: true while it is the active window and not minimized.</summary>
        public volatile bool AppActive = true;

        /// <summary>False in --snapshot: the camera is never opened.</summary>
        public volatile bool CameraAllowed = true;

        // The camera only when needed (privacy): see CameraPolicy.
        private CameraPolicy _camera = new CameraPolicy(0);
        private bool _cameraWanted = true;
        private string _cameraOffReason;

        // While the game shows one of these, nothing in raid needs the head: eco. Aiming is not
        // here: an aiming pause lasts a moment and must hand back at full quality.
        private const PauseReason IdleReasons = PauseReason.ScreenOpen | PauseReason.CursorVisible | PauseReason.DialogOrCutscene
                                                | PauseReason.Unfocused | PauseReason.NotFirstPerson | PauseReason.Disabled;

        private int _statusEcoTicks, _ecoChangesLogged;
        private CpuMeter _cpuMeter;

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
        /// <summary>The "Turn camera on" button: ends a sleep and keeps the camera on a while.</summary>
        public void WakeCamera()
        {
            _work.Enqueue(() => _camera.Wake(Clock.Now(), "turned on from the app", true));
            _wake.Set();
        }

        /// <summary>"Turn camera off now" on the Privacy page: off until the user turns it on.</summary>
        public void TurnCameraOff()
        {
            _work.Enqueue(() =>
            {
                _log.Info("Camera turned off from the Privacy page; it stays off until turned on again.");
                _camera.TurnOff();
            });
            _wake.Set();
        }

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
            _cpuMeter = new CpuMeter(now);
            _camera = new CameraPolicy(now);
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
            NoticeRaidStart(now);
            UpdateCameraPolicy(now);
            EnsureSource(now);

            if (_source is WebcamSource webcam)
            {
                webcam.PreviewWanted = _settings.ShowPreview && PreviewVisible;
                bool eco = !AppActive && !GameWantsTracking(now);
                if (eco != webcam.Eco)
                {
                    webcam.Eco = eco;
                    if (_ecoChangesLogged < 4)
                    {
                        _ecoChangesLogged++;
                        _log.Info(eco
                            ? "Eco: not playing and this window is in the background, so the webcam is tracked at half rate without the mirrored check."
                            : "Full quality: " + (AppActive ? "this window is in front." : "head tracking is applying in raid.")
                              + (_ecoChangesLogged == 4 ? " (Further switches only show in the Status line.)" : ""));
                    }
                }

                if (eco)
                {
                    _statusEcoTicks++;
                }
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
                        _camera.Wake(Clock.Now(), "recenter key pressed in game", true);
                        _tracker.RequestRecenter();
                        break;
                    case LinkCommand.Toggle:
                        _settings.Enabled = !_settings.Enabled;
                        _log.Info("Toggle key pressed in game: head tracking " + (_settings.Enabled ? "ON" : "OFF") + ".");
                        if (_settings.Enabled)
                        {
                            _camera.Wake(Clock.Now(), "toggle key pressed in game", true);
                        }

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

            if (_settings.Source == SourceKind.Webcam && !_cameraWanted)
            {
                if (_source != null)
                {
                    StopSource();
                }

                return;
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

            bool stillWanted = (_sourceIdentity == _settings.SourceIdentity || (source is WebcamSource started && SameCamera(_settings, started)))
                               && !(source is WebcamSource && !_cameraWanted);
            if (!stillWanted)
            {
                // The settings changed while it was opening: close it and open the new one.
                _log.Info(source.Name + " started, but the settings changed meanwhile; reopening.");
                Dispose(source);
                return;
            }

            _source = source;
            _lastSourceError = null;
            _camera.Opened(now);
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

        private bool _wasInRaidForCentre;

        /// <summary>
        /// A raid starting is when the player looks at the game: an automatic centre (taken when the
        /// app started, maybe while looking at another screen) is taken again there. One the player
        /// set with the recenter key or button is kept.
        /// </summary>
        private void NoticeRaidStart(double now)
        {
            StatusMessage status = default;
            double time = double.NegativeInfinity;
            bool inRaid = _link != null && _link.TryGetStatus(out status, out time) && now - time < GameTimeoutSeconds && status.InRaid;
            if (inRaid && !_wasInRaidForCentre && _settings.AutoCenterOnStart && !_tracker.CenterSetByUser)
            {
                _log.Info("Raid started: the centre will be taken again on the first steady second (it was set automatically, not by you).");
                _tracker.RequestAutoRecenter(now);
            }

            _wasInRaidForCentre = inRaid;
        }

        private void UpdateCameraPolicy(double now)
        {
            if (_settings.Source != SourceKind.Webcam)
            {
                SetCameraWanted(true, null);
                return;
            }

            StatusMessage status = default;
            double time = double.NegativeInfinity;
            bool connected = _link != null && _link.TryGetStatus(out status, out time) && now - time < GameTimeoutSeconds;
            _camera.Update(now, new CameraInputs
            {
                Allowed = CameraAllowed,
                Enabled = _settings.Enabled,
                OnlyWhenNeeded = _settings.CameraOnlyWhenNeeded,
                AwaySeconds = _settings.AwayMinutes * 60,
                GameConnected = connected,
                InRaid = connected && status.InRaid,
                AppActive = AppActive,
                Running = _source != null,
                FaceInView = _tracker.State == TrackState.Tracking,
            });

            string woke = _camera.TakeWakeReason();
            if (woke != null && _camera.Wanted)
            {
                _log.Info("Camera waking: " + woke + ".");
            }

            SetCameraWanted(_camera.Wanted, _camera.OffReason);
        }

        private void SetCameraWanted(bool wanted, string reason)
        {
            if (wanted == _cameraWanted && reason == _cameraOffReason)
            {
                return;
            }

            _cameraWanted = wanted;
            _cameraOffReason = reason;
            if (wanted)
            {
                _log.Info("Camera on.");
                _nextSourceAttempt = 0;
            }
            else
            {
                _log.Info("Camera off: " + reason + ".");
                if (_source is WebcamSource)
                {
                    StopSource();
                }
            }
        }

        /// <summary>In raid, nothing open, game in front, tracking switched on: the head matters now.</summary>
        private bool GameWantsTracking(double now)
        {
            if (!_settings.Enabled || _link == null || !_link.TryGetStatus(out StatusMessage status, out double time) || now - time >= GameTimeoutSeconds)
            {
                return false;
            }

            return status.InRaid && ((PauseReason)status.PauseReasons & IdleReasons) == 0;
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
                SettledYaw = _tracker.SettledYaw,
                SettledPitch = _tracker.SettledPitch,
                Noise = _tracker.NoiseEstimate,
                Band = _tracker.StillnessBandInUse,
                Sigma = _tracker.LastSigma,
                Centered = _tracker.IsCentered,
                TicksPerSecond = _tickRate,
                CpuPercent = _cpuPercent,
                GameConnected = gameConnected,
                GameStatus = status,
                LinkError = _link?.BindError,
                CameraOffReason = _settings.Source == SourceKind.Webcam ? _cameraOffReason : null,
                CameraManuallyOff = _settings.Source == SourceKind.Webcam && _camera.ManuallyOff && _cameraOffReason == CameraPolicy.ManualOffReason,
                CameraRunning = _source is WebcamSource,
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

            _cpuPercent = _cpuMeter?.Percent(now) ?? 0;

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
                source = _startTask != null ? "source starting"
                    : _cameraOffReason != null ? "camera off: " + _cameraOffReason
                    : "no source" + (src.Error != null ? " (" + src.Error + ")" : "");
            }

            double tracking = _statusTicks > 0 ? 100.0 * _statusTrackingTicks / _statusTicks : 0;
            double ecoShare = _statusTicks > 0 ? 100.0 * _statusEcoTicks / _statusTicks : 0;
            _statusEcoTicks = 0;
            string shake = _shakeCount > 0 ? (_shakeSum / _shakeCount).ToString("0.00") + " deg/frame" : "n/a";
            _statusTicks = _statusTrackingTicks = 0;
            _shakeSum = 0;
            _shakeCount = 0;

            StatusMessage game = GetSnapshot().GameStatus;
            string gameText = !_gameConnected ? "not connected"
                : !game.InRaid ? "connected, in menus"
                : "in raid, " + game.GameFps.ToString("0") + " fps, " + (game.PauseReasons == 0 ? "applying" : "paused: " + PauseFader.Describe((PauseReason)game.PauseReasons));

            _log.Info("Status: " + _tracker.State + " " + tracking.ToString("0") + "% of the last " + span.ToString("0") + " s | " + source
                      + " | noise " + _tracker.NoiseEstimate.ToString("0.00") + " deg, stillness band " + _tracker.StillnessBandInUse.ToString("0.00")
                      + " deg | output change " + shake + " | head yaw " + HeadTracker.Deg(_tracker.RelativeYaw) + " pitch " + HeadTracker.Deg(_tracker.RelativePitch)
                      + " -> game yaw " + HeadTracker.Deg(_tracker.OutputYaw) + " pitch " + HeadTracker.Deg(_tracker.OutputPitch)
                      + " | game " + gameText + " | engine " + _tickRate.ToString("0") + " ticks/s, app CPU " + _cpuPercent.ToString("0") + "% of one core"
                      + (_source is WebcamSource ? ", eco " + ecoShare.ToString("0") + "% of the time" : ""));
        }

        public void Dispose()
        {
            _stopping = true;
            _wake.Set();
            _thread?.Join(4000);
        }
    }
}
