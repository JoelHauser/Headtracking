using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HeadTracking.Game;
using HeadTracking.Link;
using HeadTracking.Shared;
using UnityEngine;

namespace HeadTracking
{
    /// <summary>
    /// The game half of Head Tracking. HeadTracking.exe (in the SPT folder) reads the webcam or
    /// OpenTrack, does all the head maths and holds every setting; it sends this plugin the final
    /// head offset over local UDP. The plugin follows it smoothly at the game's frame rate, applies
    /// it to the freelook camera when that is safe, and reports game state back to the app.
    ///
    /// Threads: <see cref="ModLink"/> receives on its own thread. Everything else runs on Unity's
    /// main thread: <see cref="Update"/> reads the link, handles hotkeys and sends status, and
    /// <see cref="CameraPatch"/> applies the offset in LateUpdate.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class HeadTrackingPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.mybutthasarash.headtracking";
        public const string PluginName = "Head Tracking";

        /// <summary>Must match the csproj's Version. Two places, and they have to agree.</summary>
        public const string PluginVersion = "0.7.0";

        private const double BindRetrySeconds = 5.0;
        private const double HelloIntervalSeconds = 1.0;
        private const double StatusIntervalSeconds = 0.1;

        /// <summary>What the camera hook reads each frame. Rebuilt in <see cref="Update"/>.</summary>
        internal struct LinkView
        {
            public bool Connected;
            public PoseMessage Pose;
            public GameSettingsMessage Settings;
            public bool SettingsFromApp;
        }

        internal static ManualLogSource Log;
        internal static LinkView Link = new LinkView { Settings = new GameSettingsMessage() };

        internal static ConfigEntry<int> ModPort;
        internal static ConfigEntry<int> AppPort;
        internal static ConfigEntry<int> StatusLogSeconds;
        internal static ConfigEntry<bool> Trace;
        internal static ConfigEntry<int> TraceIntervalMs;

        internal static bool TraceEnabled => Trace.Value;
        internal static double TraceIntervalSeconds => TraceIntervalMs.Value / 1000.0;

        private readonly QueuedLog _threadLog = new QueuedLog();
        private readonly GameSettingsMessage _defaults = new GameSettingsMessage();

        private ModLink _link;
        private bool _linkWanted = true;
        private double _nextBindAttempt;
        private string _lastBindError;

        private bool _wasConnected;
        private double _nextHello;
        private double _nextStatus;
        private uint _statusSequence;
        private double _nextStatusLog;
        private long _lastPoseCount;
        private double _lastPoseCountTime;
        private int _frames;
        private double _framesSince;
        private double _gameFps;

        private void Awake()
        {
            Log = Logger;

            ModPort = Config.Bind("1. Connection", "Mod port", LinkProtocol.DefaultModPort, new ConfigDescription(
                "UDP port this plugin listens on for HeadTracking.exe. Must match 'Game port' in the app.",
                new AcceptableValueRange<int>(1024, 65535)));
            AppPort = Config.Bind("1. Connection", "App port", LinkProtocol.DefaultAppPort, new ConfigDescription(
                "UDP port HeadTracking.exe listens on for game status and in-game hotkeys. Must match 'Status port' in the app.",
                new AcceptableValueRange<int>(1024, 65535)));
            StatusLogSeconds = Config.Bind("2. Logging", "Status line every (seconds)", 10, new ConfigDescription(
                "Write a status line to LogOutput.log this often: link rate, app state, offset, pause, camera hook cost. 0 turns it off.",
                new AcceptableValueRange<int>(0, 600)));
            Trace = Config.Bind("2. Logging", "Detailed trace", false,
                "Log what the camera hook does a few times a second, plus aiming pauses. For diagnosing; leave off in normal play.");
            TraceIntervalMs = Config.Bind("2. Logging", "Trace interval (ms)", 200, new ConfigDescription(
                "Time between detailed trace lines.",
                new AcceptableValueRange<int>(16, 5000)));

            Logger.LogInfo(PluginName + " " + PluginVersion + " starting. Unity " + Application.unityVersion + ", game " + Application.version
                           + ". Settings come from HeadTracking.exe; until it connects, defaults apply (recenter F7, toggle F8).");

            Harmony harmony = new Harmony(PluginGuid);
            try
            {
                GameAccess.Resolve(Logger);
                CameraPatch.Apply(harmony);
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();
                Logger.LogError(PluginName + " " + PluginVersion + " could not hook the camera, so the game is unmodified: " + e);
                _linkWanted = false;
                return;
            }

            Config.SettingChanged += OnSettingChanged;
            StartLink();
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded.");
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            ConfigEntryBase entry = e.ChangedSetting;
            Logger.LogInfo("Setting changed: [" + entry.Definition.Section + "] " + entry.Definition.Key + " = " + entry.BoxedValue);
            if (entry == ModPort || entry == AppPort)
            {
                StopLink();
                _lastBindError = null;
                StartLink();
            }
        }

        private void StartLink()
        {
            if (!_linkWanted)
            {
                return;
            }

            ModLink link = new ModLink(ModPort.Value, AppPort.Value, _threadLog);
            if (link.Start())
            {
                _link = link;
                Logger.LogInfo("Listening for HeadTracking.exe on UDP 127.0.0.1:" + ModPort.Value + "; status goes to 127.0.0.1:" + AppPort.Value + ".");
                _lastBindError = null;
                _nextHello = 0;
                return;
            }

            if (link.BindError != _lastBindError)
            {
                _lastBindError = link.BindError;
                Logger.LogError(link.BindError + " Retrying every " + BindRetrySeconds + " s.");
            }

            _nextBindAttempt = Clock.Now() + BindRetrySeconds;
        }

        private void StopLink()
        {
            _link?.Dispose();
            _link = null;
        }

        private void Update()
        {
            double now = Clock.Now();
            _threadLog.Drain(WriteLog);

            if (_link == null)
            {
                if (_linkWanted && now >= _nextBindAttempt)
                {
                    StartLink();
                }

                Link = new LinkView { Settings = _defaults };
                return;
            }

            RefreshLinkView(now);
            HandleKeys();
            CountFrame(now);

            if (!Link.SettingsFromApp && now >= _nextHello)
            {
                _nextHello = now + HelloIntervalSeconds;
                _link.Send(LinkCommand.Hello);
            }

            if (now >= _nextStatus)
            {
                _nextStatus = now + StatusIntervalSeconds;
                SendStatus();
            }

            MaybeLogStatus(now);
            _threadLog.Drain(WriteLog);
        }

        private void RefreshLinkView(double now)
        {
            GameSettingsMessage fromApp = _link.Settings;
            GameSettingsMessage settings = fromApp ?? _defaults;
            bool has = _link.TryGetPose(out PoseMessage pose, out double arrival);
            bool connected = has && now - arrival <= settings.LinkTimeoutMs / 1000.0;

            if (connected != _wasConnected)
            {
                _wasConnected = connected;
                Logger.LogInfo(connected
                    ? "HeadTracking.exe connected (app state " + pose.State + ")."
                    : "HeadTracking.exe stopped sending (nothing for " + settings.LinkTimeoutMs + " ms); head tracking fades out.");
            }

            Link = new LinkView
            {
                Connected = connected,
                Pose = connected ? pose : default(PoseMessage),
                Settings = settings,
                SettingsFromApp = fromApp != null,
            };
        }

        private void HandleKeys()
        {
            GameSettingsMessage settings = Link.Settings;
            if (Pressed(settings.RecenterKey, settings.RecenterModifiers))
            {
                Logger.LogInfo("Recenter key pressed" + (Link.Connected ? "; asking HeadTracking.exe to recentre." : ", but HeadTracking.exe is not connected."));
                _link.Send(LinkCommand.Recenter);
            }

            if (Pressed(settings.ToggleKey, settings.ToggleModifiers))
            {
                Logger.LogInfo("Toggle key pressed" + (Link.Connected
                    ? "; asking HeadTracking.exe to turn tracking " + (Link.Pose.Enabled ? "OFF." : "ON.")
                    : ", but HeadTracking.exe is not connected."));
                _link.Send(LinkCommand.Toggle);
            }
        }

        /// <summary>
        /// Main key pressed this frame with its modifiers held. Other keys held at the same time
        /// do not matter (BepInEx's KeyboardShortcut.IsDown would refuse while sprinting).
        /// </summary>
        private static bool Pressed(int keyCode, KeyModifiers modifiers)
        {
            if (keyCode <= 0 || !Enum.IsDefined(typeof(KeyCode), keyCode))
            {
                return false;
            }

            if (!Input.GetKeyDown((KeyCode)keyCode))
            {
                return false;
            }

            if ((modifiers & KeyModifiers.Control) != 0 && !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            {
                return false;
            }

            if ((modifiers & KeyModifiers.Shift) != 0 && !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
            {
                return false;
            }

            if ((modifiers & KeyModifiers.Alt) != 0 && !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
            {
                return false;
            }

            return true;
        }

        private void SendStatus()
        {
            bool inRaid = CameraPatch.InRaid && Clock.Now() - CameraPatch.LastDriveTime < 0.5;
            _link.Send(new StatusMessage
            {
                Sequence = ++_statusSequence,
                InRaid = inRaid,
                Applying = inRaid && (Math.Abs(CameraPatch.LastYaw) > 0 || Math.Abs(CameraPatch.LastPitch) > 0),
                HasSettings = Link.SettingsFromApp,
                PauseReasons = inRaid ? (int)CameraPatch.Fader.Reasons : 0,
                AppliedYaw = (float)CameraPatch.LastYaw,
                AppliedPitch = (float)CameraPatch.LastPitch,
                HookMicros = CameraPatch.LastHookMicros,
                GameFps = (float)_gameFps,
                ModVersion = PluginVersion,
            });
        }

        private void MaybeLogStatus(double now)
        {
            int interval = StatusLogSeconds.Value;
            if (interval <= 0 || now < _nextStatusLog)
            {
                return;
            }

            bool first = _nextStatusLog == 0;
            _nextStatusLog = now + interval;
            long poses = _link.PosesReceived;
            double rate = (poses - _lastPoseCount) / Math.Max(0.001, now - _lastPoseCountTime);
            _lastPoseCount = poses;
            _lastPoseCountTime = now;
            if (first)
            {
                return;
            }

            CameraPatch.TakePerf(out int frames, out double avgMicros, out double maxMicros, out int applied, out int moved);
            string camera = CameraPatch.InRaid && now - CameraPatch.LastDriveTime < 0.5
                ? "camera hook " + frames + " frames (" + applied + " with an offset, head moved the view on " + moved + "), " + avgMicros.ToString("0.0") + " us avg, "
                  + maxMicros.ToString("0") + " us max; pause " + PauseFader.Describe(CameraPatch.Fader.Reasons)
                  + " (weight " + CameraPatch.Fader.Eased.ToString("0.00") + ")"
                : "no local player (menus or loading)";

            Logger.LogInfo("Status: link " + (Link.Connected ? "up" : "DOWN") + ", " + rate.ToString("0") + " poses/s"
                           + (Link.SettingsFromApp ? "" : ", no settings from the app yet (defaults)")
                           + " | app " + Link.Pose.State + (Link.Connected && !Link.Pose.Enabled ? " (turned off in the app)" : "")
                           + " | offset yaw " + Link.Pose.Yaw.ToString("+0.0;-0.0;0.0") + " pitch " + Link.Pose.Pitch.ToString("+0.0;-0.0;0.0")
                           + " | game " + _gameFps.ToString("0") + " fps, motion smoothing " + Link.Settings.MotionSmoothingMs.ToString("0") + " ms"
                           + " | " + camera);
        }

        private void WriteLog(Shared.LogLevel level, string message)
        {
            switch (level)
            {
                case Shared.LogLevel.Error:
                    Logger.LogError(message);
                    break;
                case Shared.LogLevel.Warning:
                    Logger.LogWarning(message);
                    break;
                default:
                    Logger.LogInfo(message);
                    break;
            }
        }

        internal static void Debug(string message)
        {
            if (Trace != null && Trace.Value)
            {
                Log.LogInfo(message);
            }
        }

        private void CountFrame(double now)
        {
            _frames++;
            if (now - _framesSince >= 1.0)
            {
                _gameFps = _frames / (now - _framesSince);
                _frames = 0;
                _framesSince = now;
            }
        }

        private void OnApplicationQuit()
        {
            Logger.LogInfo("Game quitting; closing the link.");
            _linkWanted = false;
            StopLink();
            _threadLog.Drain(WriteLog);
        }

        private void OnDestroy()
        {
            _linkWanted = false;
            StopLink();
        }
    }
}
