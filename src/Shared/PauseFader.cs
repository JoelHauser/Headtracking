using System;
using System.Collections.Generic;

namespace HeadTracking.Shared
{
    [Flags]
    public enum PauseReason
    {
        None = 0,
        /// <summary>Turned off with the toggle key or the config.</summary>
        Disabled = 1 << 0,
        /// <summary>A screen is open (inventory, menu, map...): the game is ignoring player input.</summary>
        ScreenOpen = 1 << 1,
        /// <summary>The mouse cursor is showing, which EFT does for every UI screen.</summary>
        CursorVisible = 1 << 2,
        /// <summary>NPC dialog or a scripted cutscene.</summary>
        DialogOrCutscene = 1 << 3,
        Aiming = 1 << 4,
        Unfocused = 1 << 5,
        NotFirstPerson = 1 << 6,
        /// <summary>No poses from HeadTracking.exe: it is closed, or was never started.</summary>
        NoLink = 1 << 7,
        /// <summary>HeadTracking.exe is running but tracking is switched off there.</summary>
        AppDisabled = 1 << 8,
    }

    /// <summary>
    /// A 0..1 weight on the head offset that slides to 0 while anything pauses tracking and back
    /// to 1 when nothing does, so pausing never snaps the view.
    /// </summary>
    public sealed class PauseFader
    {
        /// <summary>A longer gap than this between ticks means the camera was not ours meanwhile
        /// (raid start, death, a load) and the weight restarts from zero.</summary>
        public const double GapSeconds = 0.5;

        private double _lastTick = double.NaN;

        /// <summary>Linear weight. Use <see cref="Eased"/> to scale the offset.</summary>
        public double Weight { get; private set; }

        public PauseReason Reasons { get; private set; } = PauseReason.None;

        public double Eased => SmoothStep(Weight);

        /// <returns>True when the gap rule restarted the weight from zero this tick.</returns>
        public bool Tick(PauseReason reasons, double now, double fadeTime)
        {
            bool restarted = false;
            double dt;
            if (double.IsNaN(_lastTick) || now - _lastTick > GapSeconds)
            {
                Weight = 0;
                dt = 0;
                restarted = true;
            }
            else
            {
                dt = Math.Max(0, now - _lastTick);
            }

            _lastTick = now;
            Reasons = reasons;

            double target = reasons == PauseReason.None ? 1.0 : 0.0;
            double step = fadeTime <= 0 ? 1.0 : dt / fadeTime;
            if (Weight < target)
            {
                Weight = Math.Min(target, Weight + step);
            }
            else if (Weight > target)
            {
                Weight = Math.Max(target, Weight - step);
            }

            return restarted;
        }

        public static double SmoothStep(double t)
        {
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return t * t * (3 - 2 * t);
        }

        public static string Describe(PauseReason reasons)
        {
            if (reasons == PauseReason.None)
            {
                return "none";
            }

            List<string> parts = new List<string>();
            if ((reasons & PauseReason.Disabled) != 0) parts.Add("turned off");
            if ((reasons & PauseReason.ScreenOpen) != 0) parts.Add("screen open");
            if ((reasons & PauseReason.CursorVisible) != 0) parts.Add("cursor showing");
            if ((reasons & PauseReason.DialogOrCutscene) != 0) parts.Add("dialog or cutscene");
            if ((reasons & PauseReason.Aiming) != 0) parts.Add("aiming down sights");
            if ((reasons & PauseReason.Unfocused) != 0) parts.Add("game window not focused");
            if ((reasons & PauseReason.NotFirstPerson) != 0) parts.Add("not first person");
            if ((reasons & PauseReason.NoLink) != 0) parts.Add("HeadTracking.exe not running");
            if ((reasons & PauseReason.AppDisabled) != 0) parts.Add("turned off in HeadTracking.exe");
            return string.Join(", ", parts.ToArray());
        }
    }
}
