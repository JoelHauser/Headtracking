using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.Tests;

/// <summary>0.3.0: what makes the view smooth instead of shaking.</summary>
public class SmoothnessTests
{
    [Theory]
    [InlineData(0.0, 1.0, 0.182)]   // OpenTrack's numbers: sigmoid((d - size) * 1.5)
    [InlineData(1.0, 1.0, 0.5)]
    [InlineData(3.0, 1.0, 0.953)]
    public void SteadinessAttenuatesAsOpenTrackDoes(double sigmas, double size, double expected)
    {
        Assert.Equal(expected, HeadTracker.SteadyAttenuation(sigmas, size), 3);
    }

    /// <summary>A still head with noisy poses, at 30 Hz, through the tracker; returns the output's mean frame-to-frame change.</summary>
    private static double Shake(double steadiness, double smoothing, double sigma, bool withSigma)
    {
        var random = new Random(3);
        var tracker = new HeadTracker(new ListLog());
        var settings = new TrackingSettings { AutoCenterOnStart = false, Stillness = 0, Steadiness = steadiness, Smoothing = smoothing, RecoveryFade = 0 };
        double previous = double.NaN, sum = 0;
        int n = 0;
        for (int i = 0; i < 300; i++)
        {
            double t = 10 + i / 30.0;
            double noise = (random.NextDouble() - 0.5) * 2 * sigma * 0.6;
            var snapshot = new PoseSnapshot
            {
                HasPose = true, Pose = new Pose(0, 0, 60, 12 + noise, 0, 0), ArrivalTime = t, LastChangeTime = t,
                ReportsValidity = true, Valid = true, RotationSigma = withSigma ? sigma : 0,
            };
            tracker.Tick(true, snapshot, t, settings);
            if (i > 30)
            {
                if (!double.IsNaN(previous)) { sum += Math.Abs(tracker.OutputYaw - previous); n++; }
                previous = tracker.OutputYaw;
            }
        }

        return sum / n;
    }

    [Fact]
    public void SteadinessCutsTheShakeOfAStillHead()
    {
        double smoothingOnly = Shake(0, 0.5, 1.8, true);
        double steady = Shake(1.0, 0.5, 1.8, true);
        Assert.True(steady < smoothingOnly * 0.6, "steady " + steady + " vs smoothing only " + smoothingOnly);
    }

    [Fact]
    public void SteadinessNeedsTheSourcesUncertainty()
    {
        // OpenTrack gives no uncertainty: steadiness must then do nothing at all.
        Assert.Equal(Shake(0, 0.5, 1.8, false), Shake(1.0, 0.5, 1.8, false), 9);
    }

    [Fact]
    public void ARealTurnStillGetsThrough()
    {
        var tracker = new HeadTracker(new ListLog());
        var settings = new TrackingSettings { AutoCenterOnStart = false, Stillness = 0, Steadiness = 1.0, Smoothing = 0.5, RecoveryFade = 0 };
        for (int i = 0; i < 60; i++)
        {
            double t = 10 + i / 30.0;
            double yaw = i < 10 ? 0 : 15;   // a 15 degree turn
            tracker.Tick(true, new PoseSnapshot
            {
                HasPose = true, Pose = new Pose(0, 0, 60, yaw, 0, 0), ArrivalTime = t, LastChangeTime = t,
                ReportsValidity = true, Valid = true, RotationSigma = 1.8,
            }, t, settings);
        }

        // 1.5 s after the turn the view is there: (15 - 1.5) * 2.5, capped at 40, game sign.
        Assert.Equal(-33.75, tracker.OutputYaw, 0.05);
    }

    [Fact]
    public void FiltersRunOncePerNewPoseNotOncePerTick()
    {
        // The engine ticks faster than the camera; repeated ticks on the same pose must not keep
        // moving the filtered value (that was 0.2.0's per-tick 1-euro, which made every new
        // frame a visible jerk).
        var rig = new Rig(new TrackingSettings { AutoCenterOnStart = false, Stillness = 0, Smoothing = 0.8, RecoveryFade = 0 });
        rig.Hold(0.5, 0, 0);
        rig.Send(20, 0);
        rig.Tick();
        double afterNewPose = rig.Tracker.OutputYaw;
        long samples = rig.Tracker.Samples;
        for (int i = 0; i < 10; i++)
        {
            rig.Now += 0.004;
            rig.Tick();
        }

        Assert.Equal(afterNewPose, rig.Tracker.OutputYaw);
        Assert.Equal(samples, rig.Tracker.Samples);
    }
}

public class SmoothFollowTests
{
    [Fact]
    public void ReachesTheTargetWithoutOvershoot()
    {
        var f = new SmoothFollow();
        double max = 0;
        for (int i = 0; i < 120; i++)
        {
            f.Step(10, 0.06, 1 / 120.0);
            max = Math.Max(max, f.Value);
        }

        Assert.True(max <= 10.0 + 1e-9, "overshoot " + max);
        Assert.Equal(10.0, f.Value, 2);
    }

    [Fact]
    public void TurnsCameraRateStepsIntoSmallRenderRateSteps()
    {
        // Targets change 30 times a second by 1 degree; the game draws at 144 Hz.
        var f = new SmoothFollow();
        double target = 0, last = 0, biggest = 0;
        for (int frame = 0; frame < 144 * 2; frame++)
        {
            if (frame % 5 == 0) target += 1.0;   // about 30 Hz
            f.Step(target, 0.06, 1 / 144.0);
            biggest = Math.Max(biggest, Math.Abs(f.Value - last));
            last = f.Value;
        }

        // Without the follow every fifth frame would jump 1 degree; with it no frame moves more than a fraction.
        Assert.True(biggest < 0.4, "biggest step " + biggest);
    }

    [Fact]
    public void SettlesOnExactlyZeroSoTheCameraCanBeVanillaAgain()
    {
        var f = new SmoothFollow();
        f.Reset(5);
        for (int i = 0; i < 400; i++)
        {
            f.Step(0, 0.06, 1 / 120.0);
            f.SettleOnZero(0);
        }

        Assert.True(f.Value == 0 && f.Velocity == 0);
    }

    [Fact]
    public void ZeroSmoothingTimeSnapsAndZeroDtHolds()
    {
        var f = new SmoothFollow();
        f.Step(7, 0, 1 / 60.0);
        Assert.Equal(7, f.Value);
        f.Step(3, 0.06, 0);
        Assert.Equal(7, f.Value);
    }
}
