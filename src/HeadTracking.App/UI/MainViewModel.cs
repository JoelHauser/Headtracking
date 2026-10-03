using System;
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
            ResetResponseCommand = new RelayCommand(() => Settings.ResetResponse());
            ResetZoomCommand = new RelayCommand(() => Settings.ResetZoom());
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

            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
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
        public ICommand ResetZoomCommand { get; }
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
        public string SourceError { get; private set; }
        public Visibility SourceErrorVisibility => string.IsNullOrEmpty(SourceError) ? Visibility.Collapsed : Visibility.Visible;

        public double HeadX { get; private set; }
        public double HeadY { get; private set; }
        public double OutX { get; private set; }
        public double OutY { get; private set; }
        public double HeadZ { get; private set; }
        public double ZoomPercent { get; private set; }
        public bool Tracking { get; private set; }
        public string HeadText { get; private set; } = "";
        public string OutText { get; private set; } = "";
        public string ZoomText { get; private set; } = "";
        public string RatesText { get; private set; } = "";

        public ImageSource PreviewImage => _previewBitmap;
        public double PreviewWidth { get; private set; } = 320;
        public double PreviewHeight { get; private set; } = 240;
        public Rect FaceBox { get; private set; }
        public Visibility FaceBoxVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility PreviewVisibility => Settings.Source == SourceKind.Webcam && Settings.ShowPreview && _previewBitmap != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoPreviewVisibility => PreviewVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        public string NoPreviewText => Settings.Source == SourceKind.OpenTrack
            ? "OpenTrack shows its own camera view."
            : !Settings.ShowPreview ? "Preview off (Tracking page)." : "Waiting for the camera...";

        public string PluginText => AppPaths.PluginInstalled
            ? "Plugin installed: BepInEx\\plugins\\HeadTracking.Plugin.dll"
            : AppPaths.InSptFolder
                ? "Plugin NOT found in BepInEx\\plugins. Unzip the whole release over your SPT folder."
                : "This app is not in an SPT folder (no EscapeFromTarkov.exe beside it). Put HeadTracking.exe in your SPT folder.";

        public ObservableCollection<LogLine> LogLines { get; } = new ObservableCollection<LogLine>();

        private void Refresh()
        {
            EngineSnapshot s = _engine.GetSnapshot();
            Live = s;

            // Tracker status
            bool tracking = s.State == TrackState.Tracking;
            Tracking = tracking;
            SourceError = s.Source?.Error;
            if (!string.IsNullOrEmpty(SourceError) && !tracking)
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

            TrackerDetail = s.SourceName + (s.Source != null && s.Source.Rate > 0 ? " · " + s.Source.Rate.ToString("0") + " fps" : "")
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
                      + (s.GameStatus.AppliedFovReduction > 0.05 ? ", zoom -" + s.GameStatus.AppliedFovReduction.ToString("0.0") + "° FOV" : "")
                    : PauseFader.Describe(reasons);
            }

            // Pads: head right/up shown right/up; game output converted the same way (EFT yaw + is left, pitch + is down).
            HeadX = s.RelativeYaw;
            HeadY = s.RelativePitch;
            OutX = -s.OutputYaw;
            OutY = -s.OutputPitch;
            HeadZ = -s.RelativeZ;
            ZoomPercent = s.OutputZoom * 100;
            HeadText = "Head  yaw " + s.RelativeYaw.ToString("+0.0;-0.0;0.0") + "°   pitch " + s.RelativePitch.ToString("+0.0;-0.0;0.0") + "°";
            OutText = "In game  yaw " + (-s.OutputYaw).ToString("+0.0;-0.0;0.0") + "°   pitch " + (-s.OutputPitch).ToString("+0.0;-0.0;0.0") + "°";
            ZoomText = "Lean " + (-s.RelativeZ).ToString("+0.0;-0.0;0.0") + " cm   zoom " + ZoomPercent.ToString("0") + "%";
            RatesText = "Engine " + s.TicksPerSecond.ToString("0") + " ticks/s"
                        + (s.Source != null && s.Source.CameraRate > 0 ? " · camera " + s.Source.CameraRate.ToString("0") + " fps" : "")
                        + (s.Source != null && s.Source.Brightness >= 0 ? " · picture brightness " + s.Source.Brightness.ToString("0") + "/255" + (s.Source.Brightness < 40 ? " (too dark)" : "") : "")
                        + (s.GameConnected ? " · game hook " + s.GameStatus.HookMicros.ToString("0.0") + " µs" : "");

            RefreshPreview();
            RefreshLog();

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
            Raise(nameof(HeadX));
            Raise(nameof(HeadY));
            Raise(nameof(OutX));
            Raise(nameof(OutY));
            Raise(nameof(HeadZ));
            Raise(nameof(ZoomPercent));
            Raise(nameof(HeadText));
            Raise(nameof(OutText));
            Raise(nameof(ZoomText));
            Raise(nameof(RatesText));
        }

        private void RefreshPreview()
        {
            PreviewFrame frame = Settings.Source == SourceKind.Webcam && Settings.ShowPreview ? _engine.Webcam?.Preview : null;
            if (frame == null)
            {
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

        private void RefreshLog()
        {
            long version = _log.Version;
            if (version == _logVersion)
            {
                return;
            }

            _logVersion = version;
            List<LogLine> lines = _log.Snapshot();
            int start = Math.Max(0, lines.Count - 300);
            LogLines.Clear();
            for (int i = start; i < lines.Count; i++)
            {
                LogLines.Add(lines[i]);
            }
        }

        private void OnSettingChanged(object sender, PropertyChangedEventArgs e)
        {
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
