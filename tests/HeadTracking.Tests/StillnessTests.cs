using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.Tests;

/// <summary>0.4.0: a still head gives a still view; real movement still gets through.</summary>
public class StillnessTests
{
    private static (HeadTracker Tracker, List<double> Outputs) Run(double noiseSd, Func<double, double> trueYaw, double seconds, TrackingSettings settings, int seed = 7)
    {
        var random = new Random(seed);
        var tracker = new HeadTracker(new ListLog());
        var outputs = new List<double>();
        for (int i = 0; i < seconds * 30; i++)
        {
            double t = 10 + i / 30.0;
            double n1 = Gauss(random) * noiseSd, n2 = Gauss(random) * noiseSd;
            tracker.Tick(true, new PoseSnapshot
            {
                HasPose = true, Pose = new Pose(0, 0, 60, trueYaw(i / 30.0) + n1, n2, 0), ArrivalTime = t, LastChangeTime = t,
                ReportsValidity = true, Valid = true, RotationSigma = 2.9,
            }, t, settings);
            outputs.Add(tracker.OutputYaw);
        }

        return (tracker, outputs);
    }

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    private static TrackingSettings Settings(double stillness) =>
        new() { AutoCenterOnStart = false, Stillness = stillness, YawDeadZone = 0, PitchDeadZone = 0, RecoveryFade = 0, MaxYaw = 90 };

    [Fact]
    public void TheNoiseEstimateFindsTheTrackersJitter()
    {
        var (tracker, _) = Run(0.5, _ => 8, 6, Settings(1));
        Assert.InRange(tracker.NoiseEstimate, 0.4, 0.6);
    }

    [Fact]
    public void AStillHeadGivesAStillView()
    {
        // Measured on a C920: 0.47 deg of frame-to-frame jitter on a still face.
        double Moving(List<double> o) => o.Skip(60).Zip(o.Skip(61), (a, b) => Math.Abs(b - a)).Sum();
        var (_, without) = Run(0.47, _ => 8, 8, Settings(0));
        var (_, with) = Run(0.47, _ => 8, 8, Settings(1));

        Assert.True(Moving(with) < Moving(without) * 0.35, "with " + Moving(with) + " vs without " + Moving(without));
    }

    [Fact]
    public void ARealTurnGetsThroughPromptly()
    {
        // Head turns 12 degrees at 1 s (in 0.3 s); 0.5 s after the turn started the view is most of the way there.
        double Turn(double t) => t < 1 ? 0 : t < 1.3 ? 12 * (t - 1) / 0.3 : 12;
        var (tracker, outputs) = Run(0.47, Turn, 3, Settings(1));
        double atHalfSecond = outputs[(int)(1.5 * 30)];
        double final = tracker.OutputYaw;

        Assert.True(Math.Abs(atHalfSecond) > 0.8 * 12 * 2.5, "after 0.5 s: " + atHalfSecond);
        Assert.InRange(Math.Abs(final), 12 * 2.5 - 3.0, 12 * 2.5 + 1.5);
    }

    [Fact]
    public void TheBandGrowsWithNoiseSoANoisierCameraIsHeldStillToo()
    {
        var (quiet, _) = Run(0.4, _ => 8, 6, Settings(1));
        var (noisy, _) = Run(1.1, _ => 8, 6, Settings(1), seed: 9);

        Assert.True(noisy.StillnessBandInUse > quiet.StillnessBandInUse * 2, quiet.StillnessBandInUse + " vs " + noisy.StillnessBandInUse);
    }

    [Fact]
    public void ZeroTurnsTheLockOff()
    {
        var (tracker, _) = Run(0.5, _ => 8, 3, Settings(0));
        Assert.Equal(0, tracker.StillnessBandInUse);
    }
}
