using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.Tests;

/// <summary>
/// 0.5.0: a held view must not move at all, not even by a sliver. DLSS, FSR and TAA only sharpen a
/// view that stops; one that inches along every frame stays soft and reads as motion blur.
/// </summary>
public class StillViewTests
{
    [Fact]
    public void TheGlideLandsExactlyAndStops()
    {
        var follow = new SmoothFollow();
        double dt = 1.0 / 144;
        int frames = 0;
        while (follow.Value != 7.5 && frames < 1000)
        {
            follow.Step(7.5, 0.035, dt);
            frames++;
        }

        Assert.Equal(7.5, follow.Value);
        Assert.True(frames < 144 / 2, "took " + frames + " frames to land");
        Assert.Equal(0, follow.Velocity);

        follow.Step(7.5, 0.035, dt);
        Assert.Equal(7.5, follow.Value);
    }

    [Fact]
    public void AStillNoisyHeadGivesAnUnchangingOutputMostOfTheTime()
    {
        // 0.47 deg of frame-to-frame jitter, as measured on a C920; the head itself does not move.
        var random = new Random(3);
        var tracker = new HeadTracker(new ListLog());
        var settings = new TrackingSettings { AutoCenterOnStart = false, YawDeadZone = 0, PitchDeadZone = 0, RecoveryFade = 0, MaxYaw = 90 };
        double previous = double.NaN;
        int compared = 0, unchanged = 0;
        for (int i = 0; i < 30 * 12; i++)
        {
            double t = 10 + i / 30.0;
            double n1 = Gauss(random) * 0.47, n2 = Gauss(random) * 0.47;
            tracker.Tick(true, new PoseSnapshot
            {
                HasPose = true, Pose = new Pose(0, 0, 60, 8 + n1, n2, 0), ArrivalTime = t, LastChangeTime = t,
                ReportsValidity = true, Valid = true, RotationSigma = 2.9,
            }, t, settings);

            // After 4 s, once the noise estimate has settled.
            if (i >= 30 * 4)
            {
                if (!double.IsNaN(previous))
                {
                    compared++;
                    if (tracker.OutputYaw == previous) unchanged++;
                }

                previous = tracker.OutputYaw;
            }
        }

        Assert.True(unchanged > compared * 0.8, unchanged + " of " + compared + " frames unchanged");
    }

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}
