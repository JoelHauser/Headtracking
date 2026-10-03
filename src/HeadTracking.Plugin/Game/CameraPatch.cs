using System;
using System.Diagnostics;
using System.Reflection;
using EFT;
using EFT.Animations;
using HarmonyLib;
using HeadTracking.Shared;
using UnityEngine;

namespace HeadTracking.Game
{
    /// <summary>
    /// Adds the head offset from HeadTracking.exe to the camera, through the game's own freelook.
    ///
    /// How EFT's freelook reaches the screen (read from the 0.16.9 client):
    ///   Update:     MoveInputTranslator.TranslateAxes -> Player.Look -> writes Player.HeadRotation,
    ///               then ProceduralWeaponAnimation.SetHeadRotation(HeadRotation).
    ///   LateUpdate: Player.LateUpdate -> VisualPass -> ProcessEffectors, which builds the camera's
    ///               rotation from that head rotation; later in VisualPass the head bone turns too.
    ///
    /// This is a prefix on VisualPass: after Look (vanilla, or a mod's replacement of it), before the
    /// camera is placed. It overwrites only ProceduralWeaponAnimation's copy with
    /// mouse freelook + head offset. Player.HeadRotation, which Look writes from scratch every frame
    /// and which SPT and FIKA serialise for other players, is never touched; so nothing builds up
    /// from frame to frame, and the head pose stays on this PC.
    ///
    /// Aim is unaffected: weapon direction comes from Player.Rotate, not Look.
    /// </summary>
    internal static class CameraPatch
    {
        private const double ZeroDegrees = 1e-3;
        private const int MaxLoggedErrors = 3;
        private const int ErrorsBeforeGivingUp = 50;
        private const double DirectionCheckDegrees = 8.0;

        internal static readonly PauseFader Fader = new PauseFader();
        private static readonly PauseFader ZoomFader = new PauseFader();

        private static Player _player;
        private static bool _applied;
        private static PauseReason _loggedReasons = PauseReason.None;
        private static bool _loggedAiming;
        private static int _errors;
        private static bool _broken;
        private static double _nextTrace;
        private static bool _yawChecked, _pitchChecked;
        private static bool _firstApplyLogged;

        private static long _perfTicks, _perfMaxTicks;
        private static int _perfFrames;
        private static int _appliedFrames;

        /// <summary><see cref="Clock.Now"/> of the last frame the hook ran for the local player.</summary>
        internal static double LastDriveTime { get; private set; } = double.NegativeInfinity;

        internal static bool InRaid => _player != null;
        internal static double LastYaw { get; private set; }
        internal static double LastPitch { get; private set; }
        internal static float LastHookMicros { get; private set; }

        internal static void Apply(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(Player), nameof(Player.VisualPass));
            if (target == null)
            {
                throw new MissingMethodException("EFT.Player", nameof(Player.VisualPass));
            }

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(CameraPatch), nameof(Prefix)));
            HeadTrackingPlugin.Log.LogInfo("Patched " + target.DeclaringType.FullName + "." + target.Name + " (prefix).");
        }

        internal static void ResetDirectionChecks()
        {
            _yawChecked = false;
            _pitchChecked = false;
        }

        /// <summary>Hook cost since the last call, for the status line.</summary>
        internal static void TakePerf(out int frames, out double averageMicros, out double maxMicros, out int appliedFrames)
        {
            frames = _perfFrames;
            appliedFrames = _appliedFrames;
            double toMicros = 1e6 / Stopwatch.Frequency;
            averageMicros = frames > 0 ? _perfTicks * toMicros / frames : 0;
            maxMicros = _perfMaxTicks * toMicros;
            _perfTicks = 0;
            _perfMaxTicks = 0;
            _perfFrames = 0;
            _appliedFrames = 0;
        }

        private static void Prefix(Player __instance)
        {
            // VisualPass runs for every player and bot each frame; everything but the local
            // player leaves on this one property read.
            if (_broken || !__instance.IsYourPlayer)
            {
                return;
            }

            long start = Stopwatch.GetTimestamp();
            try
            {
                Drive(__instance);
            }
            catch (Exception e)
            {
                _errors++;
                if (_errors <= MaxLoggedErrors)
                {
                    HeadTrackingPlugin.Log.LogError("Camera hook error #" + _errors + ": " + e);
                }

                if (_errors >= ErrorsBeforeGivingUp)
                {
                    _broken = true;
                    ZoomController.Reduction = 0;
                    HeadTrackingPlugin.Log.LogError("Camera hook failed " + _errors
                                                    + " times; head tracking is off until the game restarts. The first errors are above.");
                }
            }

            long elapsed = Stopwatch.GetTimestamp() - start;
            _perfTicks += elapsed;
            if (elapsed > _perfMaxTicks)
            {
                _perfMaxTicks = elapsed;
            }

            _perfFrames++;
            LastHookMicros = (float)(elapsed * 1e6 / Stopwatch.Frequency);
        }

        private static void Drive(Player player)
        {
            double now = Clock.Now();
            LastDriveTime = now;
            HeadTrackingPlugin.LinkView link = HeadTrackingPlugin.Link;
            GameSettingsMessage settings = link.Settings;

            if (!ReferenceEquals(player, _player))
            {
                OnNewPlayer(player);
            }

            PauseReason reasons = GatherReasons(player, link, settings);
            if (Fader.Tick(reasons, now, settings.PauseFadeMs / 1000.0) && !_applied)
            {
                HeadTrackingPlugin.Debug("Camera hook (re)started after a gap of more than "
                                         + (int)(PauseFader.GapSeconds * 1000) + " ms; head tracking fades in.");
            }

            LogReasonChanges(reasons);

            // Zoom: always paused while aiming (the scope owns the FOV then), whatever the
            // freelook setting says.
            PauseReason zoomReasons = reasons;
            if (IsAiming(player))
            {
                zoomReasons |= PauseReason.Aiming;
            }

            ZoomFader.Tick(zoomReasons, now, settings.PauseFadeMs / 1000.0);
            ZoomController.Reduction = settings.ZoomEnabled
                ? (float)(Mathf.Clamp01(link.Pose.Zoom) * settings.ZoomMaxFovReduction * ZoomFader.Eased)
                : 0f;

            double weight = Fader.Eased;
            double yaw = link.Pose.Yaw * weight;
            double pitch = link.Pose.Pitch * weight;

            ProceduralWeaponAnimation pwa = player.ProceduralWeaponAnimation;
            if (pwa == null)
            {
                return;
            }

            if (Math.Abs(yaw) < ZeroDegrees && Math.Abs(pitch) < ZeroDegrees)
            {
                LastYaw = 0;
                LastPitch = 0;
                if (_applied)
                {
                    // Hand the camera back exactly as vanilla left it. Needed when Look is not
                    // running (a screen is open): nothing else would clear our last offset.
                    pwa.SetHeadRotation(player.HeadRotation);
                    _applied = false;
                    HeadTrackingPlugin.Debug("Head offset back to exactly zero; camera is vanilla again.");
                }

                Trace(player, link, weight, reasons, null, now);
                return;
            }

            Vector3 composed = Compose(player, pwa, yaw, pitch, out string limits);
            pwa.SetHeadRotation(composed);
            _applied = true;
            _appliedFrames++;
            LastYaw = yaw;
            LastPitch = pitch;

            if (!_firstApplyLogged)
            {
                _firstApplyLogged = true;
                HeadTrackingPlugin.Log.LogInfo("First head offset applied to the camera: yaw " + Deg(yaw) + " pitch " + Deg(pitch)
                                               + " -> camera head rotation (pitch " + Deg(composed.x) + ", yaw " + Deg(composed.y) + "). " + limits);
            }

            DirectionCheck(yaw, pitch);
            Trace(player, link, weight, reasons, composed, now, limits);
        }

        /// <summary>
        /// Mouse freelook + head offset, clamped and shaped exactly as Player.Look shapes mouse
        /// freelook alone: the same limits (read live, so a mod that changes them is respected),
        /// narrower while aiming, no further down-look while sprinting with the camera pitched
        /// down, and less down-look the further the head is turned.
        /// </summary>
        private static Vector3 Compose(Player player, ProceduralWeaponAnimation pwa, double yaw, double pitch, out string limits)
        {
            EFTHardSettings hard = EFTHardSettings.Instance;
            Vector2 hl = hard != null ? hard.MOUSE_LOOK_HORIZONTAL_LIMIT : new Vector2(-40f, 40f);
            Vector2 vl = hard != null ? hard.MOUSE_LOOK_VERTICAL_LIMIT : new Vector2(-50f, 20f);
            bool aiming = IsAiming(player);
            if (aiming && hard != null)
            {
                hl *= hard.MOUSE_LOOK_LIMIT_IN_AIMING_COEF;
            }

            PlayerSpring hands = pwa.HandsContainer;
            if (hands != null && hands.CameraTransform != null && player.MovementContext != null)
            {
                float pitchedDown = hands.CameraTransform.eulerAngles.x;
                if (pitchedDown >= 50f && pitchedDown <= 90f && player.MovementContext.IsSprintEnabled)
                {
                    vl.y = 0f;
                }
            }

            GameAccess.ReadLook(player, out float mouseH, out float mouseV);
            float h = Mathf.Clamp(mouseH + (float)yaw, hl.x, hl.y);
            float v = Mathf.Clamp(mouseV + (float)pitch, vl.x, vl.y);

            float x = v;
            if (v > 0f && hl.y > 0.01f)
            {
                float turned = h / hl.y;
                x = v * Mathf.Max(0f, 1f - turned * turned);
            }

            limits = "mouse freelook h " + Deg(mouseH) + " v " + Deg(mouseV) + ", limits h [" + Deg(hl.x) + ", " + Deg(hl.y)
                     + "] v [" + Deg(vl.x) + ", " + Deg(vl.y) + "]" + (aiming ? " (aiming)" : "");
            return new Vector3(x, h, 0f);
        }

        private static PauseReason GatherReasons(Player player, HeadTrackingPlugin.LinkView link, GameSettingsMessage settings)
        {
            PauseReason reasons = PauseReason.None;
            if (!link.Connected)
            {
                reasons |= PauseReason.NoLink;
            }
            else if (!link.Pose.Enabled)
            {
                reasons |= PauseReason.AppDisabled;
            }

            if (!player.FirstPersonPointOfView)
            {
                reasons |= PauseReason.NotFirstPerson;
            }

            if (GameAccess.IgnoreInput())
            {
                reasons |= PauseReason.ScreenOpen;
            }

            if (settings.PauseWhenCursorVisible && Cursor.visible)
            {
                reasons |= PauseReason.CursorVisible;
            }

            if (GamePlayerOwner.IgnoreInputInNPCDialog || GamePlayerOwner.IgnoreInputWithKeepResetLook)
            {
                reasons |= PauseReason.DialogOrCutscene;
            }

            if (settings.PauseWhileAiming && IsAiming(player))
            {
                reasons |= PauseReason.Aiming;
            }

            if (settings.PauseWhenUnfocused && !Application.isFocused)
            {
                reasons |= PauseReason.Unfocused;
            }

            return reasons;
        }

        private static bool IsAiming(Player player)
        {
            Player.AbstractHandsController hands = player.HandsController;
            return hands != null && hands.IsAiming && !player.IsAI;
        }

        private static void OnNewPlayer(Player player)
        {
            _player = player;
            _applied = false;
            _firstApplyLogged = false;
            ResetDirectionChecks();

            EFTHardSettings hard = EFTHardSettings.Instance;
            string limits = hard == null
                ? "EFTHardSettings not loaded"
                : "horizontal " + hard.MOUSE_LOOK_HORIZONTAL_LIMIT + ", vertical " + hard.MOUSE_LOOK_VERTICAL_LIMIT
                  + ", aiming x" + hard.MOUSE_LOOK_LIMIT_IN_AIMING_COEF;
            HeadTrackingPlugin.Log.LogInfo("Local player found (" + player.GetType().Name + "); driving its camera. Game look limits: " + limits + ".");
        }

        /// <summary>
        /// Pauses are logged at Info except aiming, which flips on every ADS and would bury the
        /// log; that one is logged when the trace is on.
        /// </summary>
        private static void LogReasonChanges(PauseReason reasons)
        {
            PauseReason notAiming = reasons & ~PauseReason.Aiming;
            if (notAiming != _loggedReasons)
            {
                string message = notAiming == PauseReason.None
                    ? "Resumed (was paused: " + PauseFader.Describe(_loggedReasons) + ")."
                    : "Paused: " + PauseFader.Describe(notAiming) + ".";
                _loggedReasons = notAiming;
                HeadTrackingPlugin.Log.LogInfo(message);
            }

            bool aiming = (reasons & PauseReason.Aiming) != 0;
            if (aiming != _loggedAiming)
            {
                _loggedAiming = aiming;
                HeadTrackingPlugin.Debug(aiming ? "Aiming down sights: head offset fading out." : "Stopped aiming: head offset fading back in.");
            }
        }

        /// <summary>
        /// The first clear head turn each raid logs which way the view went, so a wrong default
        /// sign shows up in the first test as one line instead of a feeling.
        /// </summary>
        private static void DirectionCheck(double yaw, double pitch)
        {
            if (!_yawChecked && Math.Abs(yaw) >= DirectionCheckDegrees)
            {
                _yawChecked = true;
                HeadTrackingPlugin.Log.LogInfo("Direction check, yaw: camera yaw " + Deg(yaw) + " turns the view "
                                               + (yaw > 0 ? "LEFT" : "RIGHT")
                                               + " (EFT: positive freelook yaw is left). If that is the opposite of your head, turn on 'Invert yaw' in HeadTracking.exe.");
            }

            if (!_pitchChecked && Math.Abs(pitch) >= DirectionCheckDegrees)
            {
                _pitchChecked = true;
                HeadTrackingPlugin.Log.LogInfo("Direction check, pitch: camera pitch " + Deg(pitch) + " should look "
                                               + (pitch > 0 ? "DOWN" : "UP")
                                               + " (EFT's pitch sign is inferred from its look limits, not yet seen in game). "
                                               + "If the view went the opposite way to your head, turn on 'Invert pitch' in HeadTracking.exe.");
            }
        }

        private static void Trace(Player player, HeadTrackingPlugin.LinkView link, double weight, PauseReason reasons, Vector3? composed, double now, string limits = null)
        {
            if (!HeadTrackingPlugin.TraceEnabled || now < _nextTrace)
            {
                return;
            }

            _nextTrace = now + HeadTrackingPlugin.TraceIntervalSeconds;
            string camera = composed.HasValue
                ? "camera (pitch " + Deg(composed.Value.x) + ", yaw " + Deg(composed.Value.y) + ") | " + limits
                : "camera vanilla (pitch " + Deg(player.HeadRotation.x) + ", yaw " + Deg(player.HeadRotation.y) + ")";
            HeadTrackingPlugin.Log.LogInfo("Trace: app " + link.Pose.State + (link.Connected ? "" : " (link lost)")
                                           + " | app offset yaw " + Deg(link.Pose.Yaw) + " pitch " + Deg(link.Pose.Pitch) + " zoom " + link.Pose.Zoom.ToString("0.00")
                                           + " | pause weight " + weight.ToString("0.00") + " (" + PauseFader.Describe(reasons) + ")"
                                           + " | fov -" + ZoomController.Reduction.ToString("0.0")
                                           + " | " + camera);
        }

        private static string Deg(double v)
        {
            return v.ToString("+0.0;-0.0;0.0");
        }
    }
}
