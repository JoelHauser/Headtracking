using HeadTracking.Tracking;

namespace HeadTracking.Tests;

/// <summary>Privacy: the webcam runs only while it is needed.</summary>
public class CameraPolicyTests
{
    private static CameraInputs Base(bool active = false, bool connected = false, bool inRaid = false, bool face = true) => new()
    {
        Allowed = true, Enabled = true, OnlyWhenNeeded = true, AppActive = active, GameConnected = connected || inRaid, InRaid = inRaid,
        Running = true, FaceInView = face,
    };

    /// <summary>Steps the policy once a second from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static double Run(CameraPolicy p, double from, double to, CameraInputs i)
    {
        for (double t = from; t <= to; t += 1)
        {
            p.Update(t, i);
        }

        return to;
    }

    [Fact]
    public void OnWhileTheWindowIsInFrontOrTheGameRuns()
    {
        var p = new CameraPolicy(0);
        Run(p, 0, 600, Base(active: true));
        Assert.True(p.Wanted);
        Run(p, 601, 1200, Base(connected: true));
        Assert.True(p.Wanted);
    }

    [Fact]
    public void OffThirtySecondsAfterNeitherAndBackWhenTheWindowComesToTheFront()
    {
        var p = new CameraPolicy(0);
        Run(p, 0, 10, Base(active: true));
        Run(p, 11, 39, Base());
        Assert.True(p.Wanted);
        Run(p, 40, 42, Base());
        Assert.False(p.Wanted);
        Assert.Contains("background", p.OffReason);

        p.Update(43, Base(active: true));
        Assert.True(p.Wanted);
        Assert.Equal("this window came to the front", p.TakeWakeReason());
    }

    [Fact]
    public void SwitchedOffMeansOffAtOnce()
    {
        var p = new CameraPolicy(0);
        var off = Base(active: true, inRaid: true);
        off.Enabled = false;
        p.Update(1, off);
        Assert.False(p.Wanted);
        Assert.Contains("switched off", p.OffReason);
    }

    [Fact]
    public void NobodyInViewForThreeMinutesOutsideARaidSleeps()
    {
        var p = new CameraPolicy(0);
        Run(p, 0, 179, Base(active: true, face: false));
        Assert.True(p.Wanted);
        Run(p, 180, 182, Base(active: true, face: false));
        Assert.False(p.Wanted);
        Assert.Contains("nobody in view", p.OffReason);

        // The window staying in front does not wake it (it never left the front); the button does.
        Run(p, 183, 400, Base(active: true, face: false));
        Assert.False(p.Wanted);
        p.Wake(401, "turned on from the app");
        p.Update(401, Base(active: true, face: false));
        Assert.True(p.Wanted);
    }

    [Fact]
    public void ARaidStartingWakesItAndInARaidItNeverSleeps()
    {
        var p = new CameraPolicy(0);
        Run(p, 0, 200, Base(connected: true, face: false));
        Assert.False(p.Wanted);

        p.Update(201, Base(inRaid: true, face: false));
        Assert.True(p.Wanted);
        Assert.Equal("a raid started", p.TakeWakeReason());

        // Twenty minutes in raid looking away from the camera: still on.
        Run(p, 202, 1400, Base(inRaid: true, face: false));
        Assert.True(p.Wanted);
    }

    [Fact]
    public void SnapshotModeNeverOpensIt()
    {
        var p = new CameraPolicy(0);
        var i = Base(active: true, inRaid: true);
        i.Allowed = false;
        p.Wake(1, "turned on from the app");
        p.Update(1, i);
        Assert.False(p.Wanted);
    }

    [Fact]
    public void WithTheSettingOffItOnlyStopsWhenSwitchedOff()
    {
        var p = new CameraPolicy(0);
        var i = Base(face: false);
        i.OnlyWhenNeeded = false;
        Run(p, 0, 3600, i);
        Assert.True(p.Wanted);
    }
}
