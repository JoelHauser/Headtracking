using HeadTracking.Tracking;

namespace HeadTracking.Tests;

/// <summary>
/// 0.5.0: the response curve that ships (gain 2.0, curve 1.5, dead zone 1.5, cap 40). 0.4.0's
/// straight 2.5x swung the view on every small head movement and made Joel feel sick.
/// </summary>
public class ComfortTests
{
    private const double DeadZone = 1.5, Cap = 40;
    private static double Now(double head) => HeadTracker.Shape(head, DeadZone, 2.5, Cap, 1.0);
    private static double Calm(double head) => HeadTracker.Shape(head, DeadZone, 2.0, Cap, 1.5);

    [Fact]
    public void SmallHeadMovementsStaySmall()
    {
        // Talking, shifting in the seat, the shoulder that moves with a mouse flick: 2-3 degrees.
        Assert.True(Calm(3) < 1.0, "3 deg -> " + Calm(3));
        Assert.True(Calm(3) < Now(3) / 4, Calm(3) + " vs " + Now(3));
        Assert.True(Calm(5) < Now(5) / 2.5, Calm(5) + " vs " + Now(5));
    }

    [Fact]
    public void NearCentreItIsAboutOneToOne()
    {
        // The slope a few degrees out is close to real life, not amplified.
        double slope = (Calm(4.05) - Calm(3.95)) / 0.1;
        Assert.InRange(slope, 0.8, 1.4);
    }

    [Fact]
    public void ADeliberateTurnStillReachesTheFullLook()
    {
        Assert.InRange(Calm(10), 10, 12);
        Assert.Equal(Cap, Calm(DeadZone + Cap / 2.0), 6);
        Assert.Equal(Cap, Calm(30), 6);
        Assert.Equal(-Cap, Calm(-30), 6);
    }

    [Fact]
    public void TheResponseNeverRunsBackwardsOrJumps()
    {
        double last = 0;
        for (double head = 0; head <= 30; head += 0.05)
        {
            double v = Calm(head);
            Assert.True(v >= last - 1e-12, "not monotonic at " + head);
            Assert.True(v - last < 0.05 * 3.0 + 1e-9, "jump at " + head + ": " + (v - last));
            last = v;
        }
    }
}
