using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HeadTracking.App.Webcam;
using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.App.UI
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _run;

        public RelayCommand(Action run)
        {
            _run = run;
        }

        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _run();
    }

    public sealed class FormatChoice
    {
        public string Key { get; set; }
        public string Label { get; set; }

        public override string ToString() => Label;
    }

    public sealed class Choice<T>
    {
        public T Value { get; set; }
        public string Label { get; set; }

        public override string ToString() => Label;
    }

    /// <summary>
    /// The window's view model. Settings are bound straight to <see cref="Settings"/>; every change
    /// goes to the engine at once and is saved half a second after the last one. Live state is
    /// pulled from the engine 30 times a second.
    /// </summary>
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private readonly TrackingEngine _engine;
        private readonly AppLog _log;
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _saveTimer;
        private List<CameraInfo> _cameras = new List<CameraInfo>();
        private long _logVersion = -1;
        private bool _loadingCameras;
        private WriteableBitmap _previewBitmap;
        private LogLine _lastLogLine;
        private int _selectedPage;
        private bool _minimized;
        private SmoothFollow _viewYaw, _viewPitch;
        private DateTime _lastRefresh = DateTime.MinValue;

        public event PropertyChangedEventHandler PropertyChanged;

        public MainViewModel(AppSettings settings, TrackingEngine engine, AppLog log)
        {
            Settings = settings;
            _engine = engine;
            _log = log;

            Settings.PropertyChanged += OnSettingChanged;
            _engine.EnabledToggledFromGame += enabled => Application.Current?.Dispatcher.BeginInvoke(new Action(() => Settings.Enabled = enabled));

            RecenterCommand = new RelayCommand(() => _engine.Recenter());
            RefreshCamerasCommand = new RelayCommand(() => LoadCameras(true));
            RestartSourceCommand = new RelayCommand(() => _engine.RestartSource());
            WakeCameraCommand = new RelayCommand(() =>
            {
                Settings.Enabled = true;
                _engine.WakeCamera();
            });
            TurnCameraOffCommand = new RelayCommand(() => _engine.TurnCameraOff());
            DeleteLogsCommand = new RelayCommand(() =>
            {
                int deleted = _log.DeleteFiles();
                _logFilesNote = deleted == 0 ? "There were no log files to delete." : "Deleted " + deleted + " log file" + (deleted == 1 ? "" : "s") + ".";
                _log.Info("Log files deleted from the Privacy page (" + deleted + ").");
                UpdateLogFilesText();
            });
            ResetResponseCommand = new RelayCommand(() => Settings.ResetResponse());
            CameraSettingsCommand = new RelayCommand(OpenCameraSettings);
            ResetInGameCommand = new RelayCommand(() => Settings.ResetInGame());
            OpenLogFolderCommand = new RelayCommand(() => Open(AppPaths.LogDirectory));
            CopyLogCommand = new RelayCommand(() =>
            {
                try
                {
                    Clipboard.SetText(string.Join(Environment.NewLine, _log.Snapshot().Select(l => l.ToString())));
                }
                catch (Exception e)
                {
                    _log.Warn("Copy to clipboard failed: " + e.Message);
                }
            });

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                Settings.Save(AppPaths.SettingsFile, _log);
            };

            // 20 Hz is plenty for numbers and pads; skipped entirely while minimized.
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();

            LoadCameras(false);
        }

        public AppSettings Settings { get; }

        public string Version => "v" + AppInfo.Version;

        public ICommand RecenterCommand { get; }
        public ICommand RefreshCamerasCommand { get; }
        public ICommand RestartSourceCommand { get; }
        public ICommand ResetResponseCommand { get; }
        public ICommand CameraSettingsCommand { get; }

        /// <summary>The window handle, for the camera settings dialog's owner. Set by the window.</summary>
        public Func<IntPtr> OwnerHandle { get; set; }

        /// <summary>The page shown (bound to the navigation list). Drives what gets refreshed.</summary>
        public int SelectedPage
        {
            get => _selectedPage;
            set
            {
                _selectedPage = value;
                UpdatePreviewVisibility();
                // The overview's pads and graph want 30 updates a second; other pages 10 are plenty.
                _timer.Interval = TimeSpan.FromMilliseconds(value == OverviewPage ? 33 : 100);
                Raise();
                if (value == DiagnosticsPage)
                {
                    RefreshLog();
                }
            }
        }

        /// <summary>Set by the window: nothing is refreshed or previewed while minimized.</summary>
        public bool Minimized
        {
            get => _minimized;
            set
            {
                _minimized = value;
                UpdatePreviewVisibility();
            }
        }

        private const int OverviewPage = 0;
        private const int PrivacyPage = 4;
        private const int DiagnosticsPage = 5;

        private bool _windowActive = true;

        /// <summary>Set by the window. In front, the webcam tracker always runs at full quality.</summary>
        public bool WindowActive
        {
            get => _windowActive;
            set
            {
                _windowActive = value;
                UpdatePreviewVisibility();
            }
        }

        private void UpdatePreviewVisibility()
        {
            _engine.PreviewVisible = !_minimized && _selectedPage == OverviewPage;
            _engine.AppActive = _windowActive && !_minimized;
        }

        private void OpenCameraSettings()
        {
            var webcam = _engine.Webcam;
            if (webcam == null)
            {
                _log.Warn("Camera settings: no camera is open.");
                return;
            }

            IntPtr owner = OwnerHandle?.Invoke() ?? IntPtr.Zero;
            _log.Info("Opening the camera's settings dialog.");
            Task.Run(() =>
            {
                try
                {
                    if (!webcam.ShowCameraSettings(owner))
                    {
                        _log.Warn("This camera has no settings dialog.");
                    }
                }
                catch (Exception e)
                {
                    _log.Warn("Camera settings dialog failed: " + e.Message);
                }
            });
        }
        public ICommand ResetInGameCommand { get; }
        public ICommand OpenLogFolderCommand { get; }
        public ICommand CopyLogCommand { get; }

        public IReadOnlyList<KeyChoice> Keys => KeyChoices.All;

        public IReadOnlyList<Choice<ModelQuality>> Models { get; } = new[]
        {
            new Choice<ModelQuality> { Value = ModelQuality.Fast, Label = "Fast  (small network, least CPU)" },
            new Choice<ModelQuality> { Value = ModelQuality.Balanced, Label = "Balanced  (small network, recommended)" },
            new Choice<ModelQuality> { Value = ModelQuality.Accurate, Label = "Accurate  (large network, steadier, more CPU)" },
        };

        // ---- source selection (radio buttons) -------------------------------------------------

        public bool UseWebcam
        {
            get => Settings.Source == SourceKind.Webcam;
            set { if (value) Settings.Source = SourceKind.Webcam; }
        }

        public bool UseOpenTrack
        {
            get => Settings.Source == SourceKind.OpenTrack;
            set { if (value) Settings.Source = SourceKind.OpenTrack; }
        }

        public Visibility WebcamVisibility => Settings.Source == SourceKind.Webcam ? Visibility.Visible : Visibility.Collapsed;
        public Visibility OpenTrackVisibility => Settings.Source == SourceKind.OpenTrack ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FrozenTimeoutVisibility => OpenTrackVisibility;

        // ---- cameras ---------------------------------------------------------------------------------

        public ObservableCollection<string> CameraNames { get; } = new ObservableCollection<string>();
        public ObservableCollection<FormatChoice> Formats { get; } = new ObservableCollection<FormatChoice>();

        public string CameraListText { get; private set; } = "Looking for cameras...";

        public string SelectedCamera
        {
            get => Settings.CameraName;
            set
            {
                if (value == null || value == Settings.CameraName) return;
                Settings.CameraFormat = "";
                Settings.CameraName = value;
                UpdateFormats();
            }
        }

        public string SelectedFormat
        {
            get => Settings.CameraFormat;
            set
            {
                if (value != null) Settings.CameraFormat = value;
            }
        }

        private void LoadCameras(bool restart)
        {
            if (_loadingCameras)
            {
                return;
            }

            _loadingCameras = true;
            CameraListText = "Looking for cameras...";
            Raise(nameof(CameraListText));
            Task.Run(() =>
            {
                try
                {
                    return CameraCatalog.List();
                }
                catch (Exception e)
                {
                    _log.Warn("Listing cameras failed: " + e.Message);
                    return new List<CameraInfo>();
                }
            }).ContinueWith(t =>
            {
                _loadingCameras = false;
                _cameras = t.Result;
                _log.Info("Cameras found: " + (_cameras.Count == 0 ? "none" : string.Join(", ", _cameras.Select(c => c.Name + " (" + c.Formats.Count + " formats)"))) + ".");
                CameraNames.Clear();
                foreach (CameraInfo c in _cameras)
                {
                    CameraNames.Add(c.Name);
                }

                if (_cameras.Count > 0 && _cameras.All(c => c.Name != Settings.CameraName))
                {
                    Settings.CameraName = _cameras[0].Name;
                }

                CameraListText = _cameras.Count == 0
                    ? "No camera found. Plug in a webcam and press Refresh."
                    : _cameras.Count + " camera" + (_cameras.Count == 1 ? "" : "s") + " found.";
                Raise(nameof(CameraListText));
                Raise(nameof(SelectedCamera));
                UpdateFormats();
                if (restart)
                {
                    _engine.RestartSource();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void UpdateFormats()
        {
            Formats.Clear();
            Formats.Add(new FormatChoice { Key = "", Label = "Automatic (best for tracking)" });
            CameraInfo camera = _cameras.FirstOrDefault(c => c.Name == Settings.CameraName);
            if (camera != null)
            {
                foreach (CameraFormat f in camera.Formats)
                {
                    Formats.Add(new FormatChoice { Key = f.Key, Label = f.ToString() });
                }
            }

            Raise(nameof(SelectedFormat));
        }

        // ---- live state ------------------------------------------------------------------------------

        public EngineSnapshot Live { get; private set; } = new EngineSnapshot();

        public string TrackerStatus { get; private set; } = "Starting...";
        public Brush TrackerBrush { get; private set; } = Brushes.Gray;
        public string TrackerDetail { get; private set; } = "";
        public string GameStatus { get; private set; } = "";
        public Brush GameBrush { get; private set; } = Brushes.Gray;
        public string GameDetail { get; private set; } = "";
        public ICommand WakeCameraCommand { get; }
        public ICommand TurnCameraOffCommand { get; }
        public ICommand DeleteLogsCommand { get; }

        /// <summary>The Privacy page's first line: is the camera on, and if not, why.</summary>
        public string CameraStateText { get; private set; } = "";
        public Brush CameraStateBrush { get; private set; } = Brushes.Gray;

        /// <summary>What log files are on disk now.</summary>
        public string LogFilesText { get; private set; } = "";
        private string _logFilesNote;

        private void UpdateLogFilesText()
        {
            long bytes = 0;
            int files = 0;
            foreach (string path in new[] { _log.FilePath, _log.PreviousPath })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        files++;
                        bytes += new FileInfo(path).Length;
                    }
                }
                catch (Exception)
                {
                    // Gone between the check and the read: not on disk.
                }
            }

            string onDisk = files == 0 ? "No log files on disk." : "On disk: " + files + " file" + (files == 1 ? "" : "s") + ", " + (bytes / 1024.0).ToString("0") + " KB.";
            LogFilesText = (_logFilesNote != null ? _logFilesNote + " " : "") + onDisk;
            Raise(nameof(LogFilesText));
        }

        /// <summary>Shown on the Overview while the camera is off on purpose.</summary>
        public string CameraOffText { get; private set; }
        public Visibility CameraOffVisibility => string.IsNullOrEmpty(CameraOffText) ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>For the window: a line in the log.</summary>
        internal void Note(string message) => _log.Info(message);

        public string SourceError { get; private set; }
        public Visibility SourceErrorVisibility => string.IsNullOrEmpty(SourceError) ? Visibility.Collapsed : Visibility.Visible;

        public double HeadX { get; private set; }
        public double HeadY { get; private set; }
        public double OutX { get; private set; }
        public double OutY { get; private set; }
        public double GhostX { get; private set; } = double.NaN;
        public double GhostY { get; private set; } = double.NaN;
        public bool Tracking { get; private set; }

        public MotionHistory History { get; } = new MotionHistory();
        public long HistoryRevision { get; private set; }

        public string FeelText
        {
            get
            {
                double f = Settings.Feel;
                return f < 0.12 ? "Snappy" : f < 0.4 ? "Balanced" : f < 0.7 ? "Smooth" : "Very smooth";
            }
        }

        /// <summary>What the sensitivity slider means in head terms: how far to turn for the full look.</summary>
        public string SensitivityText
        {
            get
            {
                double s = Settings.YawSensitivity;
                if (s <= 0) return "";
                double head = Settings.YawDeadZone + Settings.MaxYaw / s;
                return "Full " + Settings.MaxYaw.ToString("0") + "\u00B0 look at " + head.ToString("0") + "\u00B0 of head turn";
            }
        }

        public string QualityText { get; private set; } = "";
        public Brush QualityBrush { get; private set; } = Brushes.Gray;
        public GridLength QualityFill { get; private set; } = new GridLength(0, GridUnitType.Star);
        public GridLength QualityRest { get; private set; } = new GridLength(1, GridUnitType.Star);
        public string QualityDetail { get; private set; } = "";
        public string QualityTip { get; private set; }
        public Visibility QualityTipVisibility => string.IsNullOrEmpty(QualityTip) ? Visibility.Collapsed : Visibility.Visible;
        public string NoiseText { get; private set; } = "";
        public string HeadText { get; private set; } = "";
        public string OutText { get; private set; } = "";
        public string RatesText { get; private set; } = "";
        public string FrameRateWarning { get; private set; }
        public Visibility FrameRateWarningVisibility => string.IsNullOrEmpty(FrameRateWarning) ? Visibility.Collapsed : Visibility.Visible;

        public ImageSource PreviewImage => _previewBitmap;
        public double PreviewWidth { get; private set; } = 320;
        public double PreviewHeight { get; private set; } = 240;
        public Rect FaceBox { get; private set; }
        public Visibility FaceBoxVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility PreviewVisibility => Settings.Source == SourceKind.Webcam && Settings.ShowPreview && _previewBitmap != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoPreviewVisibility => PreviewVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        public string NoPreviewText => Settings.Source == SourceKind.OpenTrack
            ? "OpenTrack shows its own camera view."
            : !Settings.ShowPreview ? "Preview off (Privacy page)."
            : _cameraOff ? "Camera off."
            : "Waiting for the camera...";

        private bool _cameraOff;

        public string PluginText => AppPaths.PluginInstalled
            ? "Plugin installed: BepInEx\\plugins\\HeadTracking\\HeadTracking.Plugin.dll"
            : AppPaths.InSptFolder
                ? "Plugin NOT found in BepInEx\\plugins\\HeadTracking. Unzip the whole release over your SPT folder."
                : "This app is not in an SPT folder (no EscapeFromTarkov.exe beside it). Put HeadTracking.exe in your SPT folder.";

        public ObservableCollection<LogLine> LogLines { get; } = new ObservableCollection<LogLine>();

        private void Refresh()
        {
            if (_minimized)
            {
                return;
            }

            EngineSnapshot s = _engine.GetSnapshot();
            Live = s;

            // Tracker status
            bool tracking = s.State == TrackState.Tracking;
            Tracking = tracking;
            SourceError = s.Source?.Error;
            CameraOffText = s.CameraOffReason == null ? null
                : s.CameraManuallyOff
                    ? "The camera is off because you turned it off. It stays off until you turn it on here, on the Privacy page, or with F7 in game."
                    : "The camera is off: " + s.CameraOffReason + ". It turns on by itself when a raid starts, when you bring this window to the front, "
                      + "or with the in-game keys; nothing is watched while it is off.";
            if (Settings.Source != SourceKind.Webcam)
            {
                CameraStateText = "OpenTrack is the source: this app does not use a camera.";
                CameraStateBrush = Brush("SubTextBrush");
            }
            else if (s.CameraOffReason != null)
            {
                CameraStateText = "Camera off (light off): " + s.CameraOffReason + ".";
                CameraStateBrush = Brush("SubTextBrush");
            }
            else if (s.CameraRunning)
            {
                CameraStateText = "Camera on: tracking your head, nothing saved or sent.";
                CameraStateBrush = Brush("GoodBrush");
            }
            else
            {
                CameraStateText = "Camera starting...";
                CameraStateBrush = Brush("WarnBrush");
            }

            if (_selectedPage == PrivacyPage)
            {
                UpdateLogFilesText();
            }
            if (s.CameraOffReason != null)
            {
                TrackerStatus = "Camera off";
                TrackerBrush = Brush("SubTextBrush");
            }
            else if (!string.IsNullOrEmpty(SourceError) && !tracking)
            {
                TrackerStatus = "Not tracking";
                TrackerBrush = Brush("BadBrush");
            }
            else if (tracking)
            {
                TrackerStatus = s.Enabled ? "Tracking" : "Tracking (off in game)";
                TrackerBrush = s.Enabled ? Brush("GoodBrush") : Brush("WarnBrush");
            }
            else if (s.State == TrackState.Holding || s.State == TrackState.Returning)
            {
                TrackerStatus = "Lost, " + (s.State == TrackState.Holding ? "holding" : "returning to centre");
                TrackerBrush = Brush("WarnBrush");
            }
            else
            {
                TrackerStatus = s.Loss == LossKind.NoFace ? "Looking for your face" : "Waiting for head data";
                TrackerBrush = Brush("WarnBrush");
            }

            TrackerDetail = s.CameraOffReason != null ? s.SourceName + " · light off"
                : s.SourceName + (s.Source != null && s.Source.Rate > 0 ? " · " + s.Source.Rate.ToString("0") + " poses/s" : "")
                  + (s.Source != null && s.Source.InferenceMs > 0 ? " · " + s.Source.InferenceMs.ToString("0.0") + " ms per frame" : "");

            // Game status
            if (!s.GameConnected)
            {
                GameStatus = "Game not connected";
                GameBrush = Brush("SubTextBrush");
                GameDetail = AppPaths.PluginInstalled ? "Start SPT; the plugin connects by itself." : "Plugin not installed";
            }
            else if (!s.GameStatus.InRaid)
            {
                GameStatus = "Game connected";
                GameBrush = Brush("GoodBrush");
                GameDetail = "In menus. Plugin " + s.GameStatus.ModVersion;
            }
            else
            {
                PauseReason reasons = (PauseReason)s.GameStatus.PauseReasons;
                GameStatus = reasons == PauseReason.None ? "In raid, applying" : "In raid, paused";
                GameBrush = reasons == PauseReason.None ? Brush("GoodBrush") : Brush("WarnBrush");
                GameDetail = reasons == PauseReason.None
                    ? "View offset yaw " + s.GameStatus.AppliedYaw.ToString("+0.0;-0.0;0.0") + "°, pitch " + s.GameStatus.AppliedPitch.ToString("+0.0;-0.0;0.0") + "°"
                      + " · game " + s.GameStatus.GameFps.ToString("0") + " fps"
                    : PauseFader.Describe(reasons);
            }

            // Pads: head right/up shown right/up; game output converted the same way (EFT yaw + is
            // left, pitch + is down). The head pad shows the filtered angle, with the raw reading as
            // a faint ghost; the game pad and graph show the view after the same render-rate glide
            // the plugin applies, so what is drawn here is what the game shows.
            DateTime nowTime = DateTime.UtcNow;
            double dt = _lastRefresh == DateTime.MinValue ? 0 : Math.Min(0.25, (nowTime - _lastRefresh).TotalSeconds);
            _lastRefresh = nowTime;
            double glide = Settings.MotionSmoothingMs / 1000.0;
            _viewYaw.Step(-s.OutputYaw, glide, dt);
            _viewPitch.Step(-s.OutputPitch, glide, dt);
            _viewYaw.SettleOnZero(-s.OutputYaw);
            _viewPitch.SettleOnZero(-s.OutputPitch);

            HeadX = tracking ? s.SettledYaw : s.RelativeYaw;
            HeadY = tracking ? s.SettledPitch : s.RelativePitch;
            GhostX = s.RelativeYaw;
            GhostY = s.RelativePitch;
            OutX = _viewYaw.Value;
            OutY = _viewPitch.Value;

            if (_selectedPage == OverviewPage)
            {
                // Unfiltered, but through the same response curve, so the gap between the lines is the filtering only.
                double rawYaw = -HeadTracker.YawSign * (Settings.InvertYaw ? -1 : 1)
                                * HeadTracker.Shape(s.RelativeYaw, Settings.YawDeadZone, Settings.YawSensitivity, Settings.MaxYaw, Settings.YawCurve);
                double rawPitch = -HeadTracker.PitchSign * (Settings.InvertPitch ? -1 : 1)
                                  * HeadTracker.Shape(s.RelativePitch, Settings.PitchDeadZone, Settings.PitchSensitivity, Settings.MaxPitch, Settings.PitchCurve);
                History.Add(Clamp(rawYaw, 60), OutX, Clamp(rawPitch, 60), OutY);
                HistoryRevision++;
                Raise(nameof(HistoryRevision));
            }

            UpdateQuality(s, tracking);
            HeadText = "Head  yaw " + s.RelativeYaw.ToString("+0.0;-0.0;0.0") + "°   pitch " + s.RelativePitch.ToString("+0.0;-0.0;0.0") + "°";
            OutText = "In game  yaw " + (-s.OutputYaw).ToString("+0.0;-0.0;0.0") + "°   pitch " + (-s.OutputPitch).ToString("+0.0;-0.0;0.0") + "°";
            RatesText = "Engine " + s.TicksPerSecond.ToString("0") + " ticks/s"
                        + (s.Source != null && s.Source.CameraRate > 0 ? " · camera " + s.Source.CameraRate.ToString("0") + " fps" : "")
                        + (s.Source != null && s.Source.Brightness >= 0 ? " · picture brightness " + s.Source.Brightness.ToString("0") + "/255" + (s.Source.Brightness < 40 ? " (too dark)" : "") : "")
                        + (s.GameConnected ? " · game hook " + s.GameStatus.HookMicros.ToString("0.0") + " µs" : "");

            double cameraFps = s.Source?.CameraRate ?? 0;
            FrameRateWarning = Settings.Source == SourceKind.Webcam && cameraFps > 1 && cameraFps < 25
                ? "The camera delivers only " + cameraFps.ToString("0") + " pictures a second, so head tracking updates that often. Webcams halve their frame rate in dim light: "
                  + "add light in front of you, keep \"Keep the full frame rate\" on (Tracking source page), or lower the exposure in Camera settings."
                : null;

            bool cameraOff = Settings.Source == SourceKind.Webcam && !s.CameraRunning;
            if (cameraOff != _cameraOff)
            {
                _cameraOff = cameraOff;
                Raise(nameof(NoPreviewText));
            }

            if (_selectedPage == OverviewPage)
            {
                RefreshPreview();
            }
            else if (cameraOff || !Settings.ShowPreview)
            {
                // Not only on the Overview: a closed camera's last picture is dropped wherever you are.
                ClearPreview();
            }

            if (_selectedPage == DiagnosticsPage)
            {
                RefreshLog();
            }

            Raise(nameof(Live));
            Raise(nameof(Tracking));
            Raise(nameof(TrackerStatus));
            Raise(nameof(TrackerBrush));
            Raise(nameof(TrackerDetail));
            Raise(nameof(GameStatus));
            Raise(nameof(GameBrush));
            Raise(nameof(GameDetail));
            Raise(nameof(SourceError));
            Raise(nameof(SourceErrorVisibility));
            Raise(nameof(CameraOffText));
            Raise(nameof(CameraOffVisibility));
            Raise(nameof(CameraStateText));
            Raise(nameof(CameraStateBrush));
            Raise(nameof(HeadX));
            Raise(nameof(HeadY));
            Raise(nameof(GhostX));
            Raise(nameof(GhostY));
            Raise(nameof(OutX));
            Raise(nameof(OutY));
            Raise(nameof(HeadText));
            Raise(nameof(OutText));
            Raise(nameof(RatesText));
            Raise(nameof(FrameRateWarning));
            Raise(nameof(FrameRateWarningVisibility));
        }

        private static double Clamp(double v, double limit) => v < -limit ? -limit : v > limit ? limit : v;

        /// <summary>
        /// The tracking quality card: the tracker's measured jitter rated, and the one thing most
        /// likely to improve it.
        /// </summary>
        private void UpdateQuality(EngineSnapshot s, bool tracking)
        {
            double noise = s.Noise;
            double cameraFps = s.Source?.CameraRate ?? 0;
            double brightness = s.Source?.Brightness ?? -1;
            if (!tracking)
            {
                QualityText = "No face";
                QualityBrush = Brush("SubTextBrush");
                QualityFill = new GridLength(0, GridUnitType.Star);
                QualityRest = new GridLength(1, GridUnitType.Star);
                QualityDetail = "Face the camera to measure.";
                QualityTip = brightness >= 0 && brightness < 40 ? "The picture is very dark (" + brightness.ToString("0") + "/255). Light your face from the front." : null;
            }
            else
            {
                double score = Math.Max(0.05, Math.Min(1.0, 1.0 - (noise - 0.2) / 1.0));
                QualityText = noise < 0.3 ? "Excellent" : noise < 0.5 ? "Good" : noise < 0.8 ? "Fair" : "Poor";
                QualityBrush = noise < 0.5 ? Brush("GoodBrush") : noise < 0.8 ? Brush("WarnBrush") : Brush("BadBrush");
                QualityFill = new GridLength(score, GridUnitType.Star);
                QualityRest = new GridLength(1 - score, GridUnitType.Star);
                QualityDetail = "Jitter " + noise.ToString("0.00") + "\u00B0 per frame"
                                + (cameraFps > 0 ? " \u00B7 camera " + cameraFps.ToString("0") + " fps" : "")
                                + (brightness >= 0 ? " \u00B7 light " + brightness.ToString("0") + "/255" : "");

                if (Settings.Source == SourceKind.Webcam && cameraFps > 1 && cameraFps < 25)
                    QualityTip = "The camera only gives " + cameraFps.ToString("0") + " pictures a second: turn on \"Keep the full frame rate\" (Tracking source page) or add light.";
                else if (noise >= 0.5 && brightness >= 0 && brightness < 60)
                    QualityTip = "More light on your face would cut the jitter most: a lamp behind or beside the monitor, facing you.";
                else if (noise >= 0.4 && Settings.Source == SourceKind.Webcam && !Settings.MirrorAverage)
                    QualityTip = "Turn on \"Check every frame twice\" (Tracking source page).";
                else if (noise >= 0.4 && Settings.Source == SourceKind.Webcam && Settings.Model == ModelQuality.Fast)
                    QualityTip = "The Balanced or Accurate model is steadier than Fast (Tracking source page).";
                else if (noise >= 0.8)
                    QualityTip = "Sit facing the camera with your whole face in the picture, about an arm's length away.";
                else
                    QualityTip = null;
            }

            NoiseText = tracking ? "Jitter now " + noise.ToString("0.00") + "\u00B0, stillness band " + s.Band.ToString("0.00") + "\u00B0" : "Jitter: no face";
            Raise(nameof(QualityText));
            Raise(nameof(QualityBrush));
            Raise(nameof(QualityFill));
            Raise(nameof(QualityRest));
            Raise(nameof(QualityDetail));
            Raise(nameof(QualityTip));
            Raise(nameof(QualityTipVisibility));
            Raise(nameof(NoiseText));
        }

        private void RefreshPreview()
        {
            PreviewFrame frame = Settings.Source == SourceKind.Webcam && Settings.ShowPreview ? _engine.Webcam?.Preview : null;
            if (frame == null)
            {
                ClearPreview();
                return;
            }

            if (_previewBitmap == null || _previewBitmap.PixelWidth != frame.Width || _previewBitmap.PixelHeight != frame.Height)
            {
                _previewBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Gray8, null);
                PreviewWidth = frame.Width;
                PreviewHeight = frame.Height;
                Raise(nameof(PreviewImage));
                Raise(nameof(PreviewWidth));
                Raise(nameof(PreviewHeight));
                Raise(nameof(PreviewVisibility));
                Raise(nameof(NoPreviewVisibility));
            }

            _previewBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width, 0);
            if (frame.FaceBox.HasValue && frame.FaceFound)
            {
                RectF b = frame.FaceBox.Value;
                FaceBox = new Rect(b.X, b.Y, Math.Max(1, b.Width), Math.Max(1, b.Height));
                FaceBoxVisibility = Visibility.Visible;
            }
            else
            {
                FaceBoxVisibility = Visibility.Collapsed;
            }

            Raise(nameof(FaceBox));
            Raise(nameof(FaceBoxVisibility));
        }

        /// <summary>
        /// Drops the preview picture when the camera is not running (or the preview is off): an
        /// empty panel saying why, never the last frame frozen on screen or kept in memory.
        /// </summary>
        private void ClearPreview()
        {
            if (_previewBitmap == null && FaceBoxVisibility == Visibility.Collapsed)
            {
                return;
            }

            _previewBitmap = null;
            FaceBoxVisibility = Visibility.Collapsed;
            Raise(nameof(PreviewImage));
            Raise(nameof(PreviewVisibility));
            Raise(nameof(NoPreviewVisibility));
            Raise(nameof(FaceBoxVisibility));
        }

        /// <summary>Appends only the lines added since last time, keeping the newest 300.</summary>
        private void RefreshLog()
        {
            long version = _log.Version;
            if (version == _logVersion)
            {
                return;
            }

            _logVersion = version;
            List<LogLine> lines = _log.Snapshot();
            int start = _lastLogLine == null ? -1 : lines.LastIndexOf(_lastLogLine);
            start = start < 0 ? Math.Max(0, lines.Count - 300) : start + 1;
            for (int i = start; i < lines.Count; i++)
            {
                LogLines.Add(lines[i]);
            }

            while (LogLines.Count > 300)
            {
                LogLines.RemoveAt(0);
            }

            if (lines.Count > 0)
            {
                _lastLogLine = lines[lines.Count - 1];
            }
        }

        private void OnSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppSettings.Feel))
            {
                // The one-slider control: sets stillness, motion smoothing, smoothing, steadiness.
                Settings.ApplyFeel();
                Raise(nameof(FeelText));
            }

            if (e.PropertyName == nameof(AppSettings.SaveLogFile))
            {
                _log.Info(Settings.SaveLogFile ? "Saving the log to disk: on." : "Saving the log to disk: off; from now on it is kept in memory only.");
                _log.SetFileEnabled(Settings.SaveLogFile);
                if (_selectedPage == PrivacyPage)
                {
                    UpdateLogFilesText();
                }
            }

            if (e.PropertyName == nameof(AppSettings.Sensitivity))
            {
                // The other one-slider control: scales yaw and pitch sensitivity together.
                Settings.ApplySensitivity();
            }

            if (e.PropertyName == nameof(AppSettings.YawSensitivity) || e.PropertyName == nameof(AppSettings.MaxYaw)
                || e.PropertyName == nameof(AppSettings.YawDeadZone))
            {
                Raise(nameof(SensitivityText));
            }

            _engine.Apply(Settings);
            _saveTimer.Stop();
            _saveTimer.Start();

            if (e.PropertyName == nameof(AppSettings.Source))
            {
                Raise(nameof(UseWebcam));
                Raise(nameof(UseOpenTrack));
                Raise(nameof(WebcamVisibility));
                Raise(nameof(OpenTrackVisibility));
                Raise(nameof(FrozenTimeoutVisibility));
            }

            if (e.PropertyName == nameof(AppSettings.Source) || e.PropertyName == nameof(AppSettings.ShowPreview))
            {
                Raise(nameof(PreviewVisibility));
                Raise(nameof(NoPreviewVisibility));
                Raise(nameof(NoPreviewText));
            }

            if (e.PropertyName == nameof(AppSettings.CameraName))
            {
                Raise(nameof(SelectedCamera));
            }

            if (e.PropertyName == nameof(AppSettings.CameraFormat))
            {
                Raise(nameof(SelectedFormat));
            }
        }

        public void SaveNow()
        {
            _saveTimer.Stop();
            Settings.Save(AppPaths.SettingsFile, _log);
        }

        private static Brush Brush(string key)
        {
            return (Brush)Application.Current.Resources[key] ?? Brushes.Gray;
        }

        private void Open(string path)
        {
            try
            {
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception e)
            {
                _log.Warn("Could not open " + path + ": " + e.Message);
            }
        }

        private void Raise([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
