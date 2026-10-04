using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.Tests;

/// <summary>
/// The centre is the "straight ahead" every angle is measured from, so its error is in every
/// frame of the session. One webcam frame is off by 0.3-0.6 deg; the median of half a second
/// is several times closer.
/// </summary>
public class CentringTests
{
    private sealed class Feed
    {
        public readonly HeadTracker Tracker = new(new ListLog());
        public readonly TrackingSettings Settings = new() { Stillness = 0, Smoothing = 0, RecoveryFade = 0 };
        private readonly Random _random;
        public double Now = 10;

        public Feed(int seed = 1, bool autoCentre = true)
        {
            _random = new Random(seed);
            Settings.AutoCenterOnStart = autoCentre;
        }

        /// <summary>Webcam frames at 30 per second: the true angle plus <paramref name="noise"/> of jitter.</summary>
        public void Frames(double seconds, Func<double, double> yaw, double pitch, double noise = 0.5)
        {
            double start = Now;
            for (int i = 0; i < (int)Math.Round(seconds * 30); i++)
            {
                Now += 1 / 30.0;
                double y = HeadTracker.Wrap180(yaw(Now - start) + Gauss() * noise);
                double p = pitch + Gauss() * noise;
                Tracker.Tick(true, new PoseSnapshot
                {
                    HasPose = true, Pose = new Pose(0, 0, 55, y, p, 0), ArrivalTime = Now, LastChangeTime = Now,
                    ReportsValidity = true, Valid = true, RotationSigma = 2.5,
                }, Now, Settings);
            }
        }

        private double Gauss() => Math.Sqrt(-2 * Math.Log(1 - _random.NextDouble())) * Math.Cos(2 * Math.PI * _random.NextDouble());
    }

    [Fact]
    public void ARecenterIsTheMedianOfTheLastHalfSecondNotOneFrame()
    {
        double worst = 0, sum = 0;
        for (int seed = 1; seed <= 40; seed++)
        {
            var f = new Feed(seed, autoCentre: false);
            f.Frames(1.0, _ => 10, -20);
            f.Tracker.RequestRecenter();
            f.Frames(1 / 30.0, _ => 10, -20);
            double error = Math.Max(Math.Abs(f.Tracker.CenterYaw - 10), Math.Abs(f.Tracker.CenterPitch + 20));
            worst = Math.Max(worst, error);
            sum += error;
        }

        // One frame of 0.5 deg jitter would average about 0.55 here (the larger of two axes) and reach 1.5.
        Assert.True(sum / 40 < 0.25, "mean error " + sum / 40);
        Assert.True(worst < 0.6, "worst error " + worst);
    }

    [Fact]
    public void AnAutomaticCentreWaitsForTheHeadToSettle()
    {
        var f = new Feed();
        // Glancing around for two seconds, then settled at yaw 3.
        f.Frames(2.0, t => 25 * Math.Sin(t * 3), -12);
        Assert.False(f.Tracker.IsCentered);
        Assert.Equal(0.0, f.Tracker.OutputYaw);

        f.Frames(1.2, _ => 3, -12);
        Assert.True(f.Tracker.IsCentered);
        Assert.InRange(f.Tracker.CenterYaw, 2.6, 3.4);
        Assert.InRange(f.Tracker.CenterPitch, -12.4, -11.6);
    }

    [Fact]
    public void AHeadThatNeverSettlesIsStillCentredAfterFiveSeconds()
    {
        var f = new Feed();
        f.Frames(4.5, t => 10 * Math.Sin(t * 2), 0);
        Assert.False(f.Tracker.IsCentered);
        f.Frames(1.0, t => 10 * Math.Sin((t + 4.5) * 2), 0);
        Assert.True(f.Tracker.IsCentered);
    }

    [Fact]
    public void ARaidStartReplacesAnAutomaticCentreTakenWhileLookingElsewhere()
    {
        // The app started while the player looked at another screen, 40 deg to the side.
        var f = new Feed();
        f.Frames(1.5, _ => -40, -25);
        Assert.InRange(f.Tracker.CenterYaw, -40.5, -39.5);

        f.Tracker.RequestAutoRecenter(f.Now);
        f.Frames(1.5, _ => 2, -21);
        Assert.InRange(f.Tracker.CenterYaw, 1.5, 2.5);
        Assert.InRange(f.Tracker.CenterPitch, -21.5, -20.5);
    }

    [Fact]
    public void ARaidStartNeverReplacesACentreThePlayerSet()
    {
        var f = new Feed();
        f.Frames(1.5, _ => -40, -25);
        f.Tracker.RequestRecenter();
        f.Frames(0.2, _ => -40, -25);
        Assert.True(f.Tracker.CenterSetByUser);

        f.Tracker.RequestAutoRecenter(f.Now);
        f.Frames(1.5, _ => 2, -21);
        Assert.InRange(f.Tracker.CenterYaw, -40.5, -39.5);
    }

    [Fact]
    public void ACentreNearTheBackOfTheRangeDoesNotAverageToZero()
    {
        // OpenTrack's yaw runs -180..180; a head facing the seam reads 179.8, -179.9, ...
        var f = new Feed(autoCentre: false);
        f.Frames(1.0, _ => 179.9, 0, noise: 0.3);
        f.Tracker.RequestRecenter();
        f.Frames(1 / 30.0, _ => 179.9, 0, noise: 0.3);
        Assert.True(Math.Abs(HeadTracker.Wrap180(f.Tracker.CenterYaw - 179.9)) < 0.3, "centre " + f.Tracker.CenterYaw);
    }
}
