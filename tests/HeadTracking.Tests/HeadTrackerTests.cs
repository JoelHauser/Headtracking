using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.Tests;

public class HeadTrackerTests
{
    /// <summary>No smoothing and no fades, so the shaping maths can be checked exactly.</summary>
    private static TrackingSettings Sharp() => new() { AutoCenterOnStart = false, Smoothing = 0, RecoveryFade = 0 };

    // Defaults: dead zone 1.5, yaw gain 2.5, pitch gain 2.0, max yaw 40, max pitch 30.
    // OpenTrack yaw +10 -> (10 - 1.5) * 2.5 = 21.25, and the game sign flips it.
    private const double TenDegreesYaw = -21.25;

    [Fact]
    public void StartsWaitingAndOutputsNothing()
    {
        var rig = new Rig();
        rig.Tick();

        Assert.Equal(TrackState.NoData, rig.Tracker.State);
        Assert.Equal(LossKind.NoPackets, rig.Tracker.Loss);
        Assert.Equal(0.0, rig.Tracker.OutputYaw);
        Assert.True(rig.Log.Any("no head data yet"), rig.Log.ToString());
    }

    [Fact]
    public void FirstPoseStartsTracking()
    {
        var rig = new Rig(Sharp());
        rig.Hold(0.1, 10, 0);

        Assert.Equal(TrackState.Tracking, rig.Tracker.State);
        Assert.Equal(TenDegreesYaw, rig.Tracker.OutputYaw, 6);
        Assert.True(rig.Log.Any("Tracking started"), rig.Log.ToString());
    }

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(-1.4, 1.2)]
    [InlineData(0.0, -1.49)]
    public void InsideTheDeadZoneTheOffsetIsExactlyZero(double yaw, double pitch)
    {
        // Exactly, not nearly: EFT skips its hand-recoil camera motion for any non-zero head rotation.
        var rig = new Rig(Sharp());
        rig.Hold(0.2, yaw, pitch);

        Assert.True(rig.Tracker.OutputYaw == 0.0, "yaw " + rig.Tracker.OutputYaw);
        Assert.True(rig.Tracker.OutputPitch == 0.0, "pitch " + rig.Tracker.OutputPitch);
    }

    [Fact]
    public void GainIsAppliedBeyondTheDeadZoneWithoutAJumpAtItsEdge()
    {
        var rig = new Rig(Sharp());
        rig.Hold(0.1, 1.6, 0);

        Assert.Equal(-0.25, rig.Tracker.OutputYaw, 6);
    }

    [Fact]
    public void TheMaximumCapsTheTurn()
    {
        var rig = new Rig(Sharp());
        rig.Hold(0.1, 30, 30);

        Assert.Equal(-40.0, rig.Tracker.OutputYaw, 6);
        Assert.Equal(-30.0, rig.Tracker.OutputPitch, 6);
    }

    [Fact]
    public void PitchUsesItsOwnGainAndSign()
    {
        var rig = new Rig(Sharp());
        rig.Hold(0.1, 0, 10);

        Assert.Equal(-(10 - 1.5) * 2.0, rig.Tracker.OutputPitch, 6);
    }

    [Fact]
    public void InvertFlipsEachAxisOnItsOwn()
    {
        var settings = Sharp();
        settings.InvertYaw = true;
        var rig = new Rig(settings);
        rig.Hold(0.1, 10, 10);

        Assert.Equal(-TenDegreesYaw, rig.Tracker.OutputYaw, 6);
        Assert.Equal(-17.0, rig.Tracker.OutputPitch, 6);
    }

    [Fact]
    public void YawWrapsAcrossTheBackOfTheHead()
    {
        var rig = new Rig(Sharp());
        rig.Hold(0.1, 175, 0);
        rig.Tracker.RequestRecenter();
        rig.Hold(0.1, 175, 0);
        rig.Hold(0.1, -175, 0);

        Assert.Equal(10.0, rig.Tracker.RelativeYaw, 6);
        Assert.Equal(TenDegreesYaw, rig.Tracker.OutputYaw, 6);
    }

    [Fact]
    public void WhenOpenTrackStopsTheViewHoldsThenEasesHomeThenRests()
    {
        var settings = Sharp();
        var rig = new Rig(settings);
        var before = rig.Hold(0.2, 10, 0);
        double held = rig.Tracker.OutputYaw;

        // Nothing arrives at all: OpenTrack was stopped or closed.
        var states = new List<TrackState>();
        var after = rig.Run(2.0, () => states.Add(rig.Tracker.State));
        states.Add(rig.Tracker.State);

        Assert.Equal(TrackState.Lost, rig.Tracker.State);
        Assert.True(rig.Tracker.OutputYaw == 0.0);
        Assert.Contains(TrackState.Holding, states);
        Assert.Contains(TrackState.Returning, states);
        Assert.Equal(LossKind.Stale, rig.Tracker.Loss);

        // Held for the hold time: up to timeout (0.25) + hold (0.5) the output is the last value.
        int heldFrames = (int)((settings.NoDataTimeout + settings.HoldTime) / Rig.Frame) - 2;
        Assert.All(after.Take(heldFrames), o => Assert.Equal(held, o.Yaw, 6));

        // And the way home is smooth: no frame moves more than a degree here.
        Assert.True(Rig.MaxStep(after, before[^1].Yaw, before[^1].Pitch) < 1.0, "max step " + Rig.MaxStep(after));

        Assert.True(rig.Log.Any("Tracking lost: no head data for"), rig.Log.ToString());
        Assert.True(rig.Log.Any("easing back to centre"), rig.Log.ToString());
        Assert.True(rig.Log.Any("Back at centre"), rig.Log.ToString());
    }

    [Fact]
    public void AFrozenPoseCountsAsLostEvenWhilePacketsKeepArriving()
    {
        // OpenTrack resends its last value, bit for bit, when the neuralnet tracker loses the face.
        var rig = new Rig(Sharp());
        rig.Hold(0.2, 10, 0);
        rig.Run(0.45, rig.Resend);
        Assert.Equal(TrackState.Tracking, rig.Tracker.State);

        rig.Run(0.1, rig.Resend);
        Assert.Equal(TrackState.Holding, rig.Tracker.State);
        Assert.Equal(LossKind.Frozen, rig.Tracker.Loss);
        Assert.True(rig.Log.Any("pose has not changed"), rig.Log.ToString());
    }

    [Fact]
    public void FrozenDetectionCanBeTurnedOff()
    {
        var settings = Sharp();
        settings.FrozenTimeout = 0;
        var rig = new Rig(settings);
        rig.Hold(0.2, 10, 0);
        rig.Run(3.0, rig.Resend);

        Assert.Equal(TrackState.Tracking, rig.Tracker.State);
        Assert.Equal(TenDegreesYaw, rig.Tracker.OutputYaw, 6);
    }

    [Fact]
    public void RecoveringWhileHoldingGlidesToTheNewPositionInsteadOfJumping()
    {
        var rig = new Rig(new TrackingSettings { AutoCenterOnStart = false, Smoothing = 0 });
        var a = rig.Hold(0.5, 15, 0);
        var b = rig.Run(0.6, rig.Resend);
        Assert.Equal(TrackState.Holding, rig.Tracker.State);

        // Head was turned right when lost and is turned left when found again: 67.5 degrees apart.
        var c = rig.Hold(1.0, -15, 0);
        Assert.Equal(TrackState.Tracking, rig.Tracker.State);
        Assert.Equal(33.75, rig.Tracker.OutputYaw, 6);

        var all = a.Concat(b).Concat(c).ToList();
        Assert.True(Rig.MaxStep(all) < 8.0, "max step " + Rig.MaxStep(all));
        Assert.True(rig.Log.Any("Tracking recovered after"), rig.Log.ToString());
    }

    [Fact]
    public void RecoveringFromRestFadesInFromCentre()
    {
        var rig = new Rig(new TrackingSettings { AutoCenterOnStart = false, Smoothing = 0 });
        rig.Hold(0.5, 15, 0);
        rig.Run(3.0, rig.Resend);
        Assert.Equal(TrackState.Lost, rig.Tracker.State);

        var back = rig.Hold(0.5, 15, 0);
        Assert.True(Math.Abs(back[0].Yaw) < 5.0, "first frame " + back[0].Yaw);
        Assert.True(Rig.MaxStep(back, 0, 0) < 8.0, "max step " + Rig.MaxStep(back, 0, 0));
        Assert.Equal(-33.75, rig.Tracker.OutputYaw, 6);
    }

    [Fact]
    public void TheVeryFirstPoseFadesInToo()
    {
        var rig = new Rig(new TrackingSettings { AutoCenterOnStart = false, Smoothing = 0 });
        var first = rig.Hold(0.5, 15, 0);

        Assert.True(Math.Abs(first[0].Yaw) < 5.0, "first frame " + first[0].Yaw);
        Assert.True(Rig.MaxStep(first, 0, 0) < 8.0);
    }

    [Fact]
    public void RecenterMakesTheCurrentHeadPositionStraightAheadSmoothly()
    {
        var rig = new Rig(new TrackingSettings { AutoCenterOnStart = false, Smoothing = 0 });
        var a = rig.Hold(0.5, 20, -5);
        Assert.NotEqual(0.0, rig.Tracker.OutputYaw);

        rig.Tracker.RequestRecenter();
        var b = rig.Hold(1.0, 20, -5);

        Assert.True(rig.Tracker.OutputYaw == 0.0 && rig.Tracker.OutputPitch == 0.0);
        Assert.Equal(20.0, rig.Tracker.CenterYaw, 6);
        Assert.Equal(-5.0, rig.Tracker.CenterPitch, 6);
        Assert.True(Rig.MaxStep(a.Concat(b).ToList()) < 8.0);
        Assert.True(rig.Log.Any("Recentred"), rig.Log.ToString());
    }

    [Fact]
    public void RecenterWithoutTrackingIsIgnoredAndSaysSo()
    {
        var rig = new Rig();
        rig.Tracker.RequestRecenter();
        rig.Tick();

        Assert.Equal(0.0, rig.Tracker.CenterYaw);
        Assert.Contains(rig.Log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("Recenter ignored"));
    }

    [Fact]
    public void SmoothingCalmsJitterAtRest()
    {
        static double PeakToPeak(TrackingSettings settings)
        {
            var rig = new Rig(settings);
            int i = 0;
            var outputs = rig.Run(3.0, () => rig.Send(8.0 + (i++ % 2 == 0 ? 0.3 : -0.3), 0));
            var settled = outputs.Skip(outputs.Count / 2).Select(o => o.Yaw).ToList();
            return settled.Max() - settled.Min();
        }

        double raw = PeakToPeak(new TrackingSettings { AutoCenterOnStart = false, Smoothing = 0 });
        double smoothed = PeakToPeak(new TrackingSettings { AutoCenterOnStart = false, Smoothing = 0.5 });

        Assert.True(raw > 1.0, "raw " + raw);
        Assert.True(smoothed < raw * 0.25, "smoothed " + smoothed + " vs raw " + raw);
    }

    [Fact]
    public void SmoothingStillFollowsAFastTurnQuickly()
    {
        var rig = new Rig(new TrackingSettings { AutoCenterOnStart = false, RecoveryFade = 0 });
        rig.Hold(0.5, 0, 0);
        rig.Hold(0.15, 20, 0);

        Assert.True(rig.Tracker.SmoothedYaw > 15.0, "after 150 ms the smoothed yaw is only " + rig.Tracker.SmoothedYaw);
    }

    [Fact]
    public void RandomAbuseNeverProducesNaNOrExceedsTheCaps()
    {
        var random = new Random(1234);
        var rig = new Rig();
        for (int i = 0; i < 20000; i++)
        {
            int action = random.Next(10);
            if (action < 6)
            {
                rig.Send(random.NextDouble() * 360 - 180, random.NextDouble() * 180 - 90);
            }
            else if (action < 8)
            {
                rig.Resend();
            }
            else if (action == 8)
            {
                rig.Tracker.RequestRecenter();
            }
            else
            {
                rig.Now += random.NextDouble() * 3;
            }

            rig.Now += Rig.Frame;
            rig.Tick();

            Assert.False(double.IsNaN(rig.Tracker.OutputYaw) || double.IsNaN(rig.Tracker.OutputPitch));
            Assert.True(Math.Abs(rig.Tracker.OutputYaw) <= rig.Settings.MaxYaw + 1e-9);
            Assert.True(Math.Abs(rig.Tracker.OutputPitch) <= rig.Settings.MaxPitch + 1e-9);
        }
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(540, 180)]
    [InlineData(45, 45)]
    public void Wrap180(double input, double expected)
    {
        Assert.Equal(expected, HeadTracker.Wrap180(input), 6);
    }
}
