using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.Tests;

internal sealed class ListLog : ILogSink
{
    public readonly List<(LogLevel Level, string Message)> Lines = new();

    public void Log(LogLevel level, string message)
    {
        lock (Lines)
        {
            Lines.Add((level, message));
        }
    }

    public bool Any(string fragment)
    {
        lock (Lines)
        {
            return Lines.Any(l => l.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    public override string ToString()
    {
        lock (Lines)
        {
            return string.Join("\n", Lines.Select(l => l.Level + ": " + l.Message));
        }
    }
}

/// <summary>
/// Drives a <see cref="HeadTracker"/> the way the plugin does, frame by frame, with a fake
/// OpenTrack behind it: a pose that changes every camera frame while "tracking", and is resent
/// bit-for-bit unchanged while "lost", exactly as OpenTrack's pipeline does on a NaN from the
/// neuralnet tracker.
/// </summary>
internal sealed class Rig
{
    public const double Frame = 1.0 / 60.0;

    public readonly ListLog Log = new();
    public readonly HeadTracker Tracker;
    public readonly TrackingSettings Settings;
    public double Now = 1000.0;

    private PoseSnapshot _snapshot;
    private bool _has;
    private int _jitter;

    public Rig(TrackingSettings? settings = null)
    {
        Settings = settings ?? new TrackingSettings { AutoCenterOnStart = false };
        Tracker = new HeadTracker(Log);
    }

    /// <summary>OpenTrack sends a pose that differs from the last (tracking).</summary>
    public void Send(double yaw, double pitch)
    {
        // A tiny alternating jitter makes each pose distinct in its bits, as real tracking is,
        // without moving the angle by anything a test would notice.
        _jitter++;
        double tiny = (_jitter % 2 == 0 ? 1e-9 : -1e-9);
        Pose pose = new Pose(0, 0, 50, yaw + tiny, pitch, 0);
        if (!_has || !pose.SameBits(_snapshot.Pose))
        {
            _snapshot.LastChangeTime = Now;
        }

        _snapshot.Pose = pose;
        _snapshot.ArrivalTime = Now;
        _snapshot.HasPose = true;
        _has = true;
    }

    /// <summary>
    /// The built-in webcam tracker: every frame carries an explicit "face found" flag, and a
    /// distance from the camera in cm.
    /// </summary>
    public void SendWebcam(double yaw, double pitch, double z, bool faceFound)
    {
        Send(yaw, pitch);
        _snapshot.Pose.Z = z;
        _snapshot.ReportsValidity = true;
        _snapshot.Valid = faceFound;
    }

    /// <summary>OpenTrack resends the identical last pose (face lost).</summary>
    public void Resend()
    {
        _snapshot.ArrivalTime = Now;
    }

    public void Tick()
    {
        Tracker.Tick(_has, _snapshot, Now, Settings);
    }

    /// <summary>Advance time frame by frame, calling <paramref name="each"/> before every tick.</summary>
    public List<(double Yaw, double Pitch)> Run(double seconds, Action each)
    {
        var outputs = new List<(double, double)>();
        int frames = (int)Math.Round(seconds / Frame);
        for (int i = 0; i < frames; i++)
        {
            Now += Frame;
            each();
            Tick();
            outputs.Add((Tracker.OutputYaw, Tracker.OutputPitch));
        }

        return outputs;
    }

    public List<(double Yaw, double Pitch)> Hold(double seconds, double yaw, double pitch)
    {
        return Run(seconds, () => Send(yaw, pitch));
    }

    public static double MaxStep(IReadOnlyList<(double Yaw, double Pitch)> outputs, double startYaw = double.NaN, double startPitch = double.NaN)
    {
        double max = 0;
        double py = startYaw, pp = startPitch;
        foreach (var (y, p) in outputs)
        {
            if (!double.IsNaN(py))
            {
                max = Math.Max(max, Math.Max(Math.Abs(y - py), Math.Abs(p - pp)));
            }

            py = y;
            pp = p;
        }

        return max;
    }
}
