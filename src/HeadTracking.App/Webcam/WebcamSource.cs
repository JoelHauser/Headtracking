using System;
using System.Linq;
using System.Threading;
using FlashCap;
using HeadTracking.App.Sources;
using HeadTracking.Shared;
using HeadTracking.Tracking;
using Microsoft.ML.OnnxRuntime;

namespace HeadTracking.App.Webcam
{
    public sealed class WebcamConfig
    {
        public string CameraName = "";
        public string FormatKey = "";
        public ModelQuality Quality = ModelQuality.Balanced;
        public int Threads = 1;

        /// <summary>Turn the camera's low light compensation off, so it keeps its full frame rate.</summary>
        public bool KeepFrameRate = true;

        /// <summary>Restarting the source is needed when this changes.</summary>
        public string Identity => CameraName + "|" + FormatKey + "|" + Quality + "|" + Threads + "|" + KeepFrameRate;
    }

    /// <summary>A small greyscale copy of the newest frame with what the tracker saw, for the app's preview.</summary>
    public sealed class PreviewFrame
    {
        public int Width, Height;
        public byte[] Pixels;
        public RectF? FaceBox;
        public bool FaceFound;
        public HeadPose Pose;
    }

    /// <summary>
    /// The built-in webcam tracker as a source. Threads:
    ///  - FlashCap's capture thread: converts each frame to greyscale and drops it in a one-slot
    ///    mailbox, replacing any frame not yet taken. Never waits on the networks.
    ///  - this source's tracking thread: takes the newest frame, runs the networks, publishes a pose.
    /// So a slow network costs frames, never latency: what is tracked is always the newest image.
    /// </summary>
    public sealed class WebcamSource : ITrackerSource
    {
        private const double NoFramesWarningSeconds = 3.0;
        private const double NoFramesRestartSeconds = 10.0;
        private const double PreviewInterval = 1.0 / 15.0;
        private const int PreviewMaxWidth = 320;

        private readonly WebcamConfig _config;
        private readonly Func<WebcamTrackerOptions> _options;
        private readonly ILogSink _log;
        private readonly object _mailboxLock = new object();
        private readonly object _snapshotLock = new object();
        private readonly AutoResetEvent _frameArrived = new AutoResetEvent(false);
        private readonly AutoResetEvent _newData = new AutoResetEvent(false);

        private CaptureDevice _device;
        private Localizer _localizer;
        private PoseEstimator _poseEstimator;
        private WebcamTracker _tracker;
        private Thread _thread;
        private volatile bool _stopping;

        // Mailbox: the capture thread owns _captureBuffer, the tracking thread owns _workBuffer,
        // and _pending passes between them under the lock.
        private GrayImage _captureBuffer, _pending, _workBuffer;
        private bool _hasPending;
        private long _framesArrived, _framesDecodeFailed;
        private string _decodeError;
        private double _lastFrameTime = double.NaN;

        private PoseSnapshot _snapshot;
        private PreviewFrame _preview;
        private double _nextPreview;

        private string _summary = "";
        private string _error;
        private long _processed, _lastProcessed, _lastArrived;
        private double _rate, _cameraRate, _lastRateTime, _lastRateTimeStart;
        private double _inferenceMsAverage;
        private bool _faceFound;
        private string _lastLossReason;

        // While no face is found: the best detector score and the average brightness, reported
        // every few seconds. Numbers only; the picture itself is never kept or written anywhere.
        private double _nextNoFaceReport;
        private double _sigmaSum;
        private long _sigmaCount;
        private float _bestScoreSinceReport;
        private double _brightness;

        public WebcamSource(WebcamConfig config, Func<WebcamTrackerOptions> options, ILogSink log)
        {
            _config = config;
            _options = options;
            _log = log;
        }

        public string Name => "Webcam" + (string.IsNullOrEmpty(OpenedCamera ?? _config.CameraName) ? "" : " (" + (OpenedCamera ?? _config.CameraName) + ")");

        /// <summary>The camera actually opened (the configured one, or the first found when none is set).</summary>
        public string OpenedCamera { get; private set; }

        /// <summary>The configuration as it was actually applied, for deciding whether a settings change needs a restart.</summary>
        public WebcamConfig EffectiveConfig => new WebcamConfig
        {
            CameraName = OpenedCamera ?? _config.CameraName,
            FormatKey = _config.FormatKey,
            Quality = _config.Quality,
            Threads = _config.Threads,
            KeepFrameRate = _config.KeepFrameRate,
        };

        public WaitHandle NewData => _newData;

        /// <summary>Set by the UI: when false no preview copies are made.</summary>
        public volatile bool PreviewWanted;

        /// <summary>
        /// Set by the engine while nobody needs full quality (not in raid, a screen open, the game
        /// alt-tabbed, and this window in the background): track every other frame without the
        /// mirrored check, about a quarter of the CPU. Full quality is back on the next frame.
        /// </summary>
        public volatile bool Eco;
        private int _ecoFrame;

        public PreviewFrame Preview
        {
            get
            {
                lock (_snapshotLock)
                {
                    return _preview;
                }
            }
        }

        public bool Start()
        {
            try
            {
                if (!LoadModels())
                {
                    return false;
                }

                if (!OpenCamera())
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                _error = "Could not start the webcam tracker: " + e.Message;
                _log.Log(LogLevel.Error, _error + " " + e);
                return false;
            }

            _stopping = false;
            _lastRateTime = Clock.Now();
            _lastRateTimeStart = _lastRateTime;
            _thread = new Thread(RunGuarded) { IsBackground = true, Name = "Webcam tracking", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            return true;
        }

        private bool LoadModels()
        {
            string localizer = ModelLocator.Find(ModelLocator.LocalizerFile);
            string pose = ModelLocator.FindPoseModel(_config.Quality, out bool fellBack);
            if (localizer == null || pose == null)
            {
                _error = "The face models are missing. Expected " + ModelLocator.LocalizerFile + " and " + ModelLocator.PoseFileFor(_config.Quality)
                         + " in " + string.Join(" or ", ModelLocator.SearchDirectories()) + ". Reinstall Head Tracking.";
                _log.Log(LogLevel.Error, _error);
                return false;
            }

            if (fellBack)
            {
                _log.Log(LogLevel.Warning, ModelLocator.PoseFileFor(_config.Quality) + " not found; using " + System.IO.Path.GetFileName(pose) + " instead.");
            }

            SessionOptions options = CreateSessionOptions(_config.Threads);

            _localizer = new Localizer(localizer, options);
            _poseEstimator = new PoseEstimator(pose, options);
            _tracker = new WebcamTracker(_localizer, _poseEstimator);
            _log.Log(LogLevel.Info, "Models loaded (" + options.IntraOpNumThreads + " inference thread(s)): " + _localizer.Describe()
                                    + "; " + System.IO.Path.GetFileName(pose) + " " + _poseEstimator.Describe() + ".");
            return true;
        }

        /// <summary>
        /// ONNX Runtime's worker threads busy-wait ("spin") between runs by default, so a network
        /// run 30 times a second for 2 ms kept whole cores busy: the app measured 112% of a core.
        /// Spinning off, they sleep between frames. Two threads cost the same CPU as one but halve
        /// the time per frame; more add overhead (--benchmark).
        /// </summary>
        public static SessionOptions CreateSessionOptions(int threads)
        {
            SessionOptions options = new SessionOptions
            {
                IntraOpNumThreads = Math.Max(1, Math.Min(8, threads)),
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            };
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            return options;
        }

        private bool OpenCamera()
        {
            var cameras = CameraCatalog.List();
            if (cameras.Count == 0)
            {
                _error = "No webcam found. Plug one in, then press Refresh on the Tracking page.";
                _log.Log(LogLevel.Warning, _error);
                return false;
            }

            CameraInfo camera = cameras.FirstOrDefault(c => c.Name == _config.CameraName);
            if (camera == null)
            {
                if (!string.IsNullOrEmpty(_config.CameraName))
                {
                    _log.Log(LogLevel.Warning, "Camera '" + _config.CameraName + "' not found; using '" + cameras[0].Name + "'.");
                }

                camera = cameras[0];
            }

            CameraFormat format = CameraCatalog.Choose(camera, _config.FormatKey);
            if (format == null)
            {
                _error = camera.Name + " offers no format this tracker can read.";
                _log.Log(LogLevel.Error, _error);
                return false;
            }

            _log.Log(LogLevel.Info, CameraControl.SetFrameRatePriority(camera.Name, _config.KeepFrameRate));
            _log.Log(LogLevel.Info, "Opening " + camera.Name + " at " + format + " (" + camera.Formats.Count + " usable formats).");
            try
            {
                _device = camera.Descriptor.OpenAsync(format.Characteristics, TranscodeFormats.DoNotTranscode, false, 1, OnFrame)
                    .GetAwaiter().GetResult();
                _device.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _error = "Could not open " + camera.Name + ": " + e.Message
                         + ". If another program is using the camera (Discord, OBS, Teams, a browser), close it there.";
                _log.Log(LogLevel.Error, _error);
                return false;
            }

            OpenedCamera = camera.Name;
            _summary = camera.Name + ", " + format;
            _error = null;
            return true;
        }

        /// <summary>FlashCap's capture thread. Keep it short: decode and hand over.</summary>
        private void OnFrame(PixelBufferScope scope)
        {
            if (_stopping)
            {
                return;
            }

            try
            {
                ArraySegment<byte> image = scope.Buffer.ReferImage();
                GrayImage gray = FrameDecoder.Decode(image, _captureBuffer, out string error);
                if (gray == null)
                {
                    if (Interlocked.Increment(ref _framesDecodeFailed) <= 3 || error != _decodeError)
                    {
                        _decodeError = error;
                        _log.Log(LogLevel.Warning, "Camera frame not readable: " + error);
                    }

                    return;
                }

                lock (_mailboxLock)
                {
                    _captureBuffer = _pending;
                    _pending = gray;
                    _hasPending = true;
                    _lastFrameTime = Clock.Now();
                }

                Interlocked.Increment(ref _framesArrived);
                _frameArrived.Set();
            }
            catch (Exception e)
            {
                _log.Log(LogLevel.Error, "Camera frame handling failed: " + e.Message);
            }
            finally
            {
                scope.ReleaseNow();
            }
        }

        private void RunGuarded()
        {
            try
            {
                Run();
            }
            catch (Exception e)
            {
                _error = "Webcam tracking stopped after an error: " + e.Message;
                _log.Log(LogLevel.Error, _error + " " + e);
            }
        }

        private void Run()
        {
            double started = Clock.Now();
            bool warnedNoFrames = false;
            while (!_stopping)
            {
                _frameArrived.WaitOne(250);
                if (_stopping)
                {
                    break;
                }

                GrayImage frame = null;
                double lastFrame;
                lock (_mailboxLock)
                {
                    lastFrame = _lastFrameTime;
                    if (_hasPending)
                    {
                        GrayImage taken = _pending;
                        _pending = _workBuffer;
                        _workBuffer = taken;
                        _hasPending = false;
                        frame = taken;
                    }
                }

                double now = Clock.Now();
                if (frame == null)
                {
                    double since = double.IsNaN(lastFrame) ? now - started : now - lastFrame;
                    if (since > NoFramesWarningSeconds && !warnedNoFrames)
                    {
                        warnedNoFrames = true;
                        _error = "No pictures from the camera for " + (int)since + " s. It may be in use by another program (Discord, OBS, Teams, a browser), "
                                 + "unplugged, or blocked in Windows' camera privacy settings.";
                        _log.Log(LogLevel.Warning, _error);
                    }

                    continue;
                }

                if (warnedNoFrames)
                {
                    warnedNoFrames = false;
                    _error = null;
                    _log.Log(LogLevel.Info, "Camera pictures are arriving again.");
                }

                if (Eco && (_ecoFrame++ & 1) == 1)
                {
                    continue;
                }

                Track(frame, now);
            }
        }

        private void Track(GrayImage frame, double now)
        {
            WebcamTrackerOptions options = _options();
            if (Eco && options.MirrorAverage)
            {
                options = options.WithoutMirror();
            }

            TrackResult result = _tracker.Process(frame, now, options);
            _brightness = MeanBrightness(frame);
            ReportWhileNoFace(result, now);
            Interlocked.Increment(ref _processed);
            double ms = result.PoseMs + result.LocalizerMs;
            _inferenceMsAverage = _inferenceMsAverage <= 0 ? ms : _inferenceMsAverage * 0.9 + ms * 0.1;

            if (result.Valid != _faceFound)
            {
                _faceFound = result.Valid;
                if (!result.Valid && result.LossReason != _lastLossReason)
                {
                    _log.Log(LogLevel.Info, "Webcam: " + result.LossReason + ".");
                }
                else if (result.Valid)
                {
                    _log.Log(LogLevel.Info, "Webcam: face found (detector score " + result.LocalizerScore.ToString("0.00") + ").");
                }

                _lastLossReason = result.Valid ? null : result.LossReason;
            }

            lock (_snapshotLock)
            {
                if (result.Valid)
                {
                    HeadPose p = result.Pose;
                    _snapshot.Pose = new Pose(p.X, p.Y, p.Z, p.Yaw, p.Pitch, p.Roll);
                    _snapshot.RotationSigma = result.RotationSigma;
                    _sigmaSum += result.RotationSigma;
                    _sigmaCount++;
                }

                _snapshot.HasPose = true;
                _snapshot.ReportsValidity = true;
                _snapshot.Valid = result.Valid;
                _snapshot.ArrivalTime = now;
                _snapshot.LastChangeTime = now;

                if (PreviewWanted && now >= _nextPreview)
                {
                    _nextPreview = now + PreviewInterval;
                    _preview = MakePreview(frame, result);
                }
            }

            _newData.Set();
        }

        private void ReportWhileNoFace(TrackResult result, double now)
        {
            if (result.Valid)
            {
                _nextNoFaceReport = now + 5.0;
                _bestScoreSinceReport = 0;
                return;
            }

            if (result.RanLocalizer && result.LocalizerScore > _bestScoreSinceReport)
            {
                _bestScoreSinceReport = result.LocalizerScore;
            }

            if (now < _nextNoFaceReport)
            {
                return;
            }

            _nextNoFaceReport = now + 5.0;
            string hint = _brightness < 40
                ? " The picture is very dark: turn on a light in front of you, or check the camera is not covered."
                : _bestScoreSinceReport > 0.25f ? " Something face-like is seen; facing the camera or more light may help." : "";
            _log.Log(LogLevel.Info, "Webcam: still no face. Best detector score " + _bestScoreSinceReport.ToString("0.00") + " (needs over "
                                    + _options().DetectionThreshold.ToString("0.00") + "), average brightness " + _brightness.ToString("0") + " of 255." + hint);
            _bestScoreSinceReport = 0;
        }

        /// <summary>Mean grey level over a sparse grid of pixels: cheap, and enough to tell a dark room.</summary>
        private static double MeanBrightness(GrayImage frame)
        {
            long sum = 0;
            int count = 0;
            for (int y = 0; y < frame.Height; y += 8)
            {
                int row = y * frame.Width;
                for (int x = 0; x < frame.Width; x += 8)
                {
                    sum += frame.Data[row + x];
                    count++;
                }
            }

            return count > 0 ? (double)sum / count : 0;
        }

        private static PreviewFrame MakePreview(GrayImage frame, TrackResult result)
        {
            float scale = Math.Min(1f, PreviewMaxWidth / (float)frame.Width);
            int w = Math.Max(1, (int)(frame.Width * scale)), h = Math.Max(1, (int)(frame.Height * scale));
            byte[] pixels = new byte[w * h];
            ImageOps.ResampleRegion(frame, 0, 0, frame.Width, frame.Height, w, h, pixels);
            return new PreviewFrame
            {
                Width = w,
                Height = h,
                Pixels = pixels,
                FaceBox = result.FaceBox?.Scaled(scale),
                FaceFound = result.Valid,
                Pose = result.Pose,
            };
        }

        private double SecondsWithoutFrames(double now)
        {
            lock (_mailboxLock)
            {
                return double.IsNaN(_lastFrameTime) ? now - _lastRateTimeStart : now - _lastFrameTime;
            }
        }

        /// <summary>Average network uncertainty since the last call, degrees (for the status line).</summary>
        public double TakeAverageSigma()
        {
            lock (_snapshotLock)
            {
                double average = _sigmaCount > 0 ? _sigmaSum / _sigmaCount : 0;
                _sigmaSum = 0;
                _sigmaCount = 0;
                return average;
            }
        }

        /// <summary>
        /// The camera driver's own settings dialog (exposure, gain, white balance, low light
        /// compensation). Modal; returns once it is closed.
        /// </summary>
        public bool ShowCameraSettings(IntPtr owner)
        {
            CaptureDevice device = _device;
            if (device == null || !device.HasPropertyPage)
            {
                return false;
            }

            return device.ShowPropertyPageAsync(owner).GetAwaiter().GetResult();
        }

        public bool TryGetSnapshot(out PoseSnapshot snapshot)
        {
            lock (_snapshotLock)
            {
                snapshot = _snapshot;
            }

            return snapshot.HasPose;
        }

        public SourceStatus GetStatus()
        {
            double now = Clock.Now();
            if (now - _lastRateTime >= 1.0)
            {
                long processed = Interlocked.Read(ref _processed), arrived = Interlocked.Read(ref _framesArrived);
                _rate = (processed - _lastProcessed) / (now - _lastRateTime);
                _cameraRate = (arrived - _lastArrived) / (now - _lastRateTime);
                _lastProcessed = processed;
                _lastArrived = arrived;
                _lastRateTime = now;
            }

            return new SourceStatus
            {
                Running = _thread != null && _thread.IsAlive,
                Summary = _summary,
                Error = _error,
                Rate = _rate,
                CameraRate = _cameraRate,
                InferenceMs = _inferenceMsAverage,
                FaceFound = _faceFound,
                Brightness = _brightness,
                NeedsRestart = SecondsWithoutFrames(now) > NoFramesRestartSeconds,
            };
        }

        public void Dispose()
        {
            _stopping = true;
            _frameArrived.Set();
            try
            {
                if (_device != null)
                {
                    _device.StopAsync().GetAwaiter().GetResult();
                    _device.Dispose();
                }
            }
            catch (Exception e)
            {
                _log.Log(LogLevel.Warning, "Closing the camera: " + e.Message);
            }

            _thread?.Join(2000);
            _localizer?.Dispose();
            _poseEstimator?.Dispose();

            // The camera is closed: its last picture goes too, rather than waiting for the collector.
            lock (_snapshotLock)
            {
                _preview = null;
            }
        }
    }
}
