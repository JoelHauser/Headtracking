using System;
using System.Threading;
using HeadTracking.Tracking;

namespace HeadTracking.App.Sources
{
    /// <summary>What a source reports about itself, for the app's status bar and log.</summary>
    public sealed class SourceStatus
    {
        public bool Running;

        /// <summary>One line: "Logitech C920, 640x480 YUY2 @ 30 fps".</summary>
        public string Summary = "";

        /// <summary>Why it is not working, in words a player can act on. Null when fine.</summary>
        public string Error;

        /// <summary>Poses produced per second.</summary>
        public double Rate;

        /// <summary>Camera frames arriving per second (webcam only).</summary>
        public double CameraRate;

        public double InferenceMs;
        public bool FaceFound;

        /// <summary>Average grey level of the camera picture, 0-255 (webcam only).</summary>
        public double Brightness = -1;

        /// <summary>The source has stopped delivering and should be closed and reopened (a camera unplugged and back).</summary>
        public bool NeedsRestart;
    }

    /// <summary>A head tracker: the built-in webcam tracker, or OpenTrack over UDP.</summary>
    public interface ITrackerSource : IDisposable
    {
        string Name { get; }

        /// <summary>Opens the device or socket. False on failure; <see cref="GetStatus"/> says why.</summary>
        bool Start();

        bool TryGetSnapshot(out PoseSnapshot snapshot);

        /// <summary>Signalled whenever a new pose is ready.</summary>
        WaitHandle NewData { get; }

        SourceStatus GetStatus();
    }
}
