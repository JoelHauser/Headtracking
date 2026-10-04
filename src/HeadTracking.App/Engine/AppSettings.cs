using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using HeadTracking.App.Webcam;
using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.App
{
    public enum SourceKind
    {
        Webcam,
        OpenTrack,
    }

    /// <summary>
    /// Every setting the app has, bound directly by the UI and saved as JSON in
    /// HeadTrackingApp\settings.json. DataContract serialisation does not run constructors or
    /// field initialisers, so the defaults live in <see cref="SetDefaults"/>, which runs both on
    /// construction and before deserialising: a setting missing from an older file keeps its default.
    /// </summary>
    [DataContract]
    public sealed class AppSettings : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public AppSettings()
        {
            SetDefaults();
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SetDefaults();
        }

        public void SetDefaults()
        {
            _enabled = true;
            _source = SourceKind.Webcam;
            _cameraName = "";
            _cameraFormat = "";
            _model = ModelQuality.Balanced;
            _inferenceThreads = 1;
            _keepFullFrameRate = true;
            _cameraFov = 70;
            _detectionThreshold = 0.5;
            _faceLostAfterMs = 1000;
            _showPreview = true;
            _openTrackPort = 4242;
            ResetResponse();
            ResetInGame();
            _gamePort = LinkProtocol.DefaultModPort;
            _statusPort = LinkProtocol.DefaultAppPort;
        }

        public void ResetResponse()
        {
            YawSensitivity = 2.5;
            PitchSensitivity = 2.0;
            YawDeadZone = 1.5;
            PitchDeadZone = 1.5;
            MaxYaw = 40;
            MaxPitch = 30;
            YawCurve = 1.0;
            PitchCurve = 1.0;
            InvertYaw = false;
            InvertPitch = false;
            Steadiness = 1.0;
            Smoothing = 0.5;
            FastMoveResponse = 0.5;
            MotionSmoothingMs = 60;
            AutoCenterOnStart = true;
        }

        public void ResetInGame()
        {
            PauseWhileAiming = true;
            PauseWhenCursorVisible = true;
            PauseWhenUnfocused = true;
            PauseFadeMs = 250;
            HoldMs = 500;
            ReturnMs = 750;
            RecoveryFadeMs = 300;
            NoDataTimeoutMs = 250;
            FrozenTimeoutMs = 500;
            RecenterKey = 288;
            RecenterCtrl = false;
            RecenterShift = false;
            RecenterAlt = false;
            ToggleKey = 289;
            ToggleCtrl = false;
            ToggleShift = false;
            ToggleAlt = false;
        }

        // ---- general --------------------------------------------------------------------
        private bool _enabled;
        [DataMember] public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

        private SourceKind _source;
        [DataMember] public SourceKind Source { get => _source; set => Set(ref _source, value); }

        // ---- webcam -------------------------------------------------------------------------
        private string _cameraName;
        [DataMember] public string CameraName { get => _cameraName; set => Set(ref _cameraName, value ?? ""); }

        private string _cameraFormat;
        [DataMember] public string CameraFormat { get => _cameraFormat; set => Set(ref _cameraFormat, value ?? ""); }

        private ModelQuality _model;
        [DataMember] public ModelQuality Model { get => _model; set => Set(ref _model, value); }

        private int _inferenceThreads;
        [DataMember] public int InferenceThreads { get => _inferenceThreads; set => Set(ref _inferenceThreads, Clamp(value, 1, 8)); }

        private double _cameraFov;
        [DataMember] public double CameraFov { get => _cameraFov; set => Set(ref _cameraFov, Clamp(value, 30, 120)); }

        private double _detectionThreshold;
        [DataMember] public double DetectionThreshold { get => _detectionThreshold; set => Set(ref _detectionThreshold, Clamp(value, 0.1, 0.95)); }

        private double _faceLostAfterMs;
        [DataMember] public double FaceLostAfterMs { get => _faceLostAfterMs; set => Set(ref _faceLostAfterMs, Clamp(value, 0, 5000)); }

        private bool _keepFullFrameRate;
        [DataMember] public bool KeepFullFrameRate { get => _keepFullFrameRate; set => Set(ref _keepFullFrameRate, value); }

        private bool _showPreview;
        [DataMember] public bool ShowPreview { get => _showPreview; set => Set(ref _showPreview, value); }

        // ---- OpenTrack ------------------------------------------------------------------------
        private int _openTrackPort;
        [DataMember] public int OpenTrackPort { get => _openTrackPort; set => Set(ref _openTrackPort, Clamp(value, 1024, 65535)); }

        // ---- response -------------------------------------------------------------------------
        private double _yawSensitivity, _pitchSensitivity, _yawDeadZone, _pitchDeadZone, _maxYaw, _maxPitch, _yawCurve, _pitchCurve;
        private bool _invertYaw, _invertPitch, _autoCenterOnStart;
        private double _smoothing, _fastMoveResponse, _steadiness, _motionSmoothingMs;

        [DataMember] public double YawSensitivity { get => _yawSensitivity; set => Set(ref _yawSensitivity, Clamp(value, 0.1, 8)); }
        [DataMember] public double PitchSensitivity { get => _pitchSensitivity; set => Set(ref _pitchSensitivity, Clamp(value, 0.1, 8)); }
        [DataMember] public double YawDeadZone { get => _yawDeadZone; set => Set(ref _yawDeadZone, Clamp(value, 0, 15)); }
        [DataMember] public double PitchDeadZone { get => _pitchDeadZone; set => Set(ref _pitchDeadZone, Clamp(value, 0, 15)); }
        [DataMember] public double MaxYaw { get => _maxYaw; set => Set(ref _maxYaw, Clamp(value, 0, 90)); }
        [DataMember] public double MaxPitch { get => _maxPitch; set => Set(ref _maxPitch, Clamp(value, 0, 90)); }
        [DataMember] public double YawCurve { get => _yawCurve; set => Set(ref _yawCurve, Clamp(value, 0.5, 3)); }
        [DataMember] public double PitchCurve { get => _pitchCurve; set => Set(ref _pitchCurve, Clamp(value, 0.5, 3)); }
        [DataMember] public bool InvertYaw { get => _invertYaw; set => Set(ref _invertYaw, value); }
        [DataMember] public bool InvertPitch { get => _invertPitch; set => Set(ref _invertPitch, value); }
        [DataMember] public double Steadiness { get => _steadiness; set => Set(ref _steadiness, Clamp(value, 0, 3)); }
        [DataMember] public double Smoothing { get => _smoothing; set => Set(ref _smoothing, Clamp(value, 0, 1)); }
        [DataMember] public double MotionSmoothingMs { get => _motionSmoothingMs; set => Set(ref _motionSmoothingMs, Clamp(value, 0, 250)); }
        [DataMember] public double FastMoveResponse { get => _fastMoveResponse; set => Set(ref _fastMoveResponse, Clamp(value, 0, 1)); }
        [DataMember] public bool AutoCenterOnStart { get => _autoCenterOnStart; set => Set(ref _autoCenterOnStart, value); }

        // ---- in game --------------------------------------------------------------------------
        private bool _pauseWhileAiming, _pauseWhenCursorVisible, _pauseWhenUnfocused;
        private double _pauseFadeMs, _holdMs, _returnMs, _recoveryFadeMs, _noDataTimeoutMs, _frozenTimeoutMs;
        private int _recenterKey, _toggleKey;
        private bool _recenterCtrl, _recenterShift, _recenterAlt, _toggleCtrl, _toggleShift, _toggleAlt;

        [DataMember] public bool PauseWhileAiming { get => _pauseWhileAiming; set => Set(ref _pauseWhileAiming, value); }
        [DataMember] public bool PauseWhenCursorVisible { get => _pauseWhenCursorVisible; set => Set(ref _pauseWhenCursorVisible, value); }
        [DataMember] public bool PauseWhenUnfocused { get => _pauseWhenUnfocused; set => Set(ref _pauseWhenUnfocused, value); }
        [DataMember] public double PauseFadeMs { get => _pauseFadeMs; set => Set(ref _pauseFadeMs, Clamp(value, 0, 2000)); }
        [DataMember] public double HoldMs { get => _holdMs; set => Set(ref _holdMs, Clamp(value, 0, 5000)); }
        [DataMember] public double ReturnMs { get => _returnMs; set => Set(ref _returnMs, Clamp(value, 0, 5000)); }
        [DataMember] public double RecoveryFadeMs { get => _recoveryFadeMs; set => Set(ref _recoveryFadeMs, Clamp(value, 0, 3000)); }
        [DataMember] public double NoDataTimeoutMs { get => _noDataTimeoutMs; set => Set(ref _noDataTimeoutMs, Clamp(value, 50, 5000)); }
        [DataMember] public double FrozenTimeoutMs { get => _frozenTimeoutMs; set => Set(ref _frozenTimeoutMs, Clamp(value, 0, 5000)); }
        [DataMember] public int RecenterKey { get => _recenterKey; set => Set(ref _recenterKey, value); }
        [DataMember] public bool RecenterCtrl { get => _recenterCtrl; set => Set(ref _recenterCtrl, value); }
        [DataMember] public bool RecenterShift { get => _recenterShift; set => Set(ref _recenterShift, value); }
        [DataMember] public bool RecenterAlt { get => _recenterAlt; set => Set(ref _recenterAlt, value); }
        [DataMember] public int ToggleKey { get => _toggleKey; set => Set(ref _toggleKey, value); }
        [DataMember] public bool ToggleCtrl { get => _toggleCtrl; set => Set(ref _toggleCtrl, value); }
        [DataMember] public bool ToggleShift { get => _toggleShift; set => Set(ref _toggleShift, value); }
        [DataMember] public bool ToggleAlt { get => _toggleAlt; set => Set(ref _toggleAlt, value); }

        // ---- link -----------------------------------------------------------------------------
        private int _gamePort, _statusPort;
        [DataMember] public int GamePort { get => _gamePort; set => Set(ref _gamePort, Clamp(value, 1024, 65535)); }
        [DataMember] public int StatusPort { get => _statusPort; set => Set(ref _statusPort, Clamp(value, 1024, 65535)); }

        // ---- derived --------------------------------------------------------------------------

        public TrackingSettings ToTracking()
        {
            return new TrackingSettings
            {
                YawGain = YawSensitivity,
                PitchGain = PitchSensitivity,
                YawDeadZone = YawDeadZone,
                PitchDeadZone = PitchDeadZone,
                MaxYaw = MaxYaw,
                MaxPitch = MaxPitch,
                YawCurve = YawCurve,
                PitchCurve = PitchCurve,
                InvertYaw = InvertYaw,
                InvertPitch = InvertPitch,
                Steadiness = Steadiness,
                Smoothing = Smoothing,
                FastMoveResponse = FastMoveResponse,
                AutoCenterOnStart = AutoCenterOnStart,
                NoDataTimeout = NoDataTimeoutMs / 1000.0,
                FrozenTimeout = FrozenTimeoutMs / 1000.0,
                HoldTime = HoldMs / 1000.0,
                ReturnTime = ReturnMs / 1000.0,
                RecoveryFade = RecoveryFadeMs / 1000.0,
            };
        }

        public GameSettingsMessage ToGame(uint revision)
        {
            return new GameSettingsMessage
            {
                Revision = revision,
                PauseWhileAiming = PauseWhileAiming,
                PauseWhenCursorVisible = PauseWhenCursorVisible,
                PauseWhenUnfocused = PauseWhenUnfocused,
                PauseFadeMs = (float)PauseFadeMs,
                MotionSmoothingMs = (float)MotionSmoothingMs,
                LinkTimeoutMs = 300,
                RecenterKey = RecenterKey,
                RecenterModifiers = Modifiers(RecenterCtrl, RecenterShift, RecenterAlt),
                ToggleKey = ToggleKey,
                ToggleModifiers = Modifiers(ToggleCtrl, ToggleShift, ToggleAlt),
            };
        }

        public WebcamConfig ToWebcam()
        {
            return new WebcamConfig { CameraName = CameraName, FormatKey = CameraFormat, Quality = Model, Threads = InferenceThreads, KeepFrameRate = KeepFullFrameRate };
        }

        public WebcamTrackerOptions ToWebcamOptions()
        {
            return new WebcamTrackerOptions
            {
                CameraFov = (float)CameraFov,
                DetectionThreshold = (float)DetectionThreshold,
                UncertainTrustSeconds = FaceLostAfterMs / 1000.0,
            };
        }

        /// <summary>What decides whether the source has to be restarted.</summary>
        public string SourceIdentity => Source == SourceKind.Webcam ? "webcam|" + ToWebcam().Identity : "opentrack|" + OpenTrackPort;

        public string LinkIdentity => GamePort + "|" + StatusPort;

        public AppSettings Clone()
        {
            AppSettings clone = (AppSettings)MemberwiseClone();
            clone.PropertyChanged = null;
            return clone;
        }

        // ---- persistence ------------------------------------------------------------------------

        public static AppSettings Load(string path, ILogSink log)
        {
            try
            {
                if (File.Exists(path))
                {
                    using (FileStream stream = File.OpenRead(path))
                    {
                        AppSettings loaded = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(stream);
                        log.Log(LogLevel.Info, "Settings loaded from " + path + ".");
                        return loaded;
                    }
                }

                log.Log(LogLevel.Info, "No settings file yet; using defaults.");
            }
            catch (Exception e)
            {
                string broken = path + ".broken";
                try
                {
                    File.Copy(path, broken, true);
                }
                catch (Exception)
                {
                }

                log.Log(LogLevel.Warning, "Settings file could not be read (" + e.Message + "); using defaults. The old file was kept as " + broken + ".");
            }

            return new AppSettings();
        }

        public void Save(string path, ILogSink log)
        {
            try
            {
                string temp = path + ".tmp";
                using (FileStream stream = File.Create(temp))
                using (XmlDictionaryWriterHolder writer = new XmlDictionaryWriterHolder(stream))
                {
                    new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(writer.Writer, this);
                }

                // Replace in one step, so a crash mid-write never leaves a half file.
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception e)
            {
                log.Log(LogLevel.Warning, "Settings could not be saved to " + path + ": " + e.Message);
            }
        }

        /// <summary>Indented JSON, so the file is readable by hand.</summary>
        private sealed class XmlDictionaryWriterHolder : IDisposable
        {
            public readonly System.Xml.XmlDictionaryWriter Writer;

            public XmlDictionaryWriterHolder(Stream stream)
            {
                Writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true, "  ");
            }

            public void Dispose()
            {
                Writer.Flush();
                Writer.Dispose();
            }
        }

        private static KeyModifiers Modifiers(bool ctrl, bool shift, bool alt)
        {
            return (ctrl ? KeyModifiers.Control : 0) | (shift ? KeyModifiers.Shift : 0) | (alt ? KeyModifiers.Alt : 0);
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return double.IsNaN(v) ? lo : v < lo ? lo : v > hi ? hi : v;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
