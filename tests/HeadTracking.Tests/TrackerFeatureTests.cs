using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.Tests;

/// <summary>What the app-side tracker added over the 0.1.0 plugin: auto-centre, explicit
/// validity, curves, zoom, reset.</summary>
public class TrackerFeatureTests
{
    private static TrackingSettings Sharp(Action<TrackingSettings>? change = null)
    {
        var s = new TrackingSettings { Smoothing = 0, RecoveryFade = 0 };
        change?.Invoke(s);
        return s;
    }

    [Fact]
    public void AWebcamsAbsoluteAnglesAreCentredOnTheFirstGoodPose()
    {
        // The camera sits on top of the monitor, so "looking at the screen" reads as pitch -12.
        var rig = new Rig(Sharp());
        rig.Run(0.2, () => rig.SendWebcam(4, -12, 55, true));

        Assert.True(rig.Tracker.IsCentered);
        Assert.Equal(4, rig.Tracker.CenterYaw, 6);
        Assert.Equal(-12, rig.Tracker.CenterPitch, 6);
        Assert.Equal(55, rig.Tracker.CenterZ, 6);
        Assert.True(rig.Tracker.OutputYaw == 0 && rig.Tracker.OutputPitch == 0);

        rig.Run(0.2, () => rig.SendWebcam(14, -12, 55, true));
        Assert.Equal(-(10 - 1.5) * 2.5, rig.Tracker.OutputYaw, 6);
        Assert.True(rig.Log.Any("Centred automatically"), rig.Log.ToString());
    }

    [Fact]
    public void AutoCentreOnlyHappensOnceNotOnEveryRecovery()
    {
        var rig = new Rig(Sharp());
        rig.Run(0.2, () => rig.SendWebcam(0, 0, 50, true));
        rig.Run(2.0, () => rig.SendWebcam(0, 0, 50, false));
        rig.Run(0.2, () => rig.SendWebcam(10, 0, 50, true));

        Assert.Equal(0, rig.Tracker.CenterYaw, 6);
        Assert.NotEqual(0.0, rig.Tracker.OutputYaw);
    }

    [Fact]
    public void AnExplicitNoFaceIsLossAtOnceWithoutWaitingForAFrozenPose()
    {
        var rig = new Rig(Sharp(s => s.AutoCenterOnStart = false));
        rig.Run(0.2, () => rig.SendWebcam(10, 0, 50, true));
        Assert.Equal(TrackState.Tracking, rig.Tracker.State);

        rig.Run(2 * Rig.Frame, () => rig.SendWebcam(10, 0, 50, false));
        Assert.Equal(TrackState.Holding, rig.Tracker.State);
        Assert.Equal(LossKind.NoFace, rig.Tracker.Loss);
        Assert.True(rig.Log.Any("no face found"), rig.Log.ToString());
    }

    [Fact]
    public void ASourceThatReportsValidityIsNeverJudgedFrozen()
    {
        // The webcam tracker can legitimately produce identical poses (a still head, a camera
        // repeating a frame). Its own flag decides.
        var rig = new Rig(Sharp(s => s.AutoCenterOnStart = false));
        rig.Run(0.2, () => rig.SendWebcam(10, 0, 50, true));
        rig.Run(2.0, rig.Resend);

        Assert.Equal(TrackState.Tracking, rig.Tracker.State);
    }

    [Theory]
    [InlineData(1.0, 0.25, 10.0)]   // linear: a quarter of the way to the cap is a quarter of it
    [InlineData(2.0, 0.25, 2.5)]    // curve 2: (1/4)^2 of the cap
    [InlineData(2.0, 1.0, 40.0)]    // the cap itself is the same whatever the curve
    [InlineData(0.5, 0.25, 20.0)]   // curve below 1: more in the middle
    public void TheCurveBendsTheResponseButKeepsItsEnds(double curve, double fractionOfRange, double expected)
    {
        // Gain 2.5, cap 40: 16 degrees beyond the dead zone reaches the cap.
        double beyond = 16 * fractionOfRange;
        Assert.Equal(expected, HeadTracker.Shape(1.5 + beyond, 1.5, 2.5, 40, curve), 6);
        Assert.Equal(-expected, HeadTracker.Shape(-(1.5 + beyond), 1.5, 2.5, 40, curve), 6);
    }

    [Fact]
    public void YawAndPitchHaveTheirOwnDeadZones()
    {
        var rig = new Rig(Sharp(s => { s.AutoCenterOnStart = false; s.YawDeadZone = 5; s.PitchDeadZone = 0; }));
        rig.Hold(0.2, 4, 4);

        Assert.True(rig.Tracker.OutputYaw == 0);
        Assert.Equal(-8.0, rig.Tracker.OutputPitch, 6);
    }

    [Theory]
    [InlineData(1.0, 0.0)]    // inside the 2 cm dead zone
    [InlineData(6.0, 0.5)]    // halfway between 2 and 10
    [InlineData(10.0, 1.0)]
    [InlineData(25.0, 1.0)]
    [InlineData(-8.0, 0.0)]   // leaning back never zooms
    public void LeaningInZooms(double leanInCm, double expected)
    {
        Assert.Equal(expected, HeadTracker.ZoomShape(leanInCm, 2, 10), 6);
    }

    [Fact]
    public void ZoomFollowsDistanceFromTheCentreAndIsHeldAndReturnedLikeTheAngles()
    {
        var rig = new Rig(Sharp(s => s.ZoomEnabled = true));
        rig.Run(0.2, () => rig.SendWebcam(0, 0, 60, true));
        Assert.Equal(0.0, rig.Tracker.OutputZoom);

        // 6 cm closer than the centre distance: halfway.
        rig.Run(0.2, () => rig.SendWebcam(0, 0, 54, true));
        Assert.Equal(0.5, rig.Tracker.OutputZoom, 6);

        // Face lost: zoom is held (hold 0.5 s), then eases to nothing, never jumping.
        var zooms = new List<double>();
        rig.Run(2.0, () => { zooms.Add(rig.Tracker.OutputZoom); rig.SendWebcam(0, 0, 54, false); });
        zooms.Add(rig.Tracker.OutputZoom);

        Assert.All(zooms.Skip(1).Take(25), z => Assert.Equal(0.5, z, 6));
        Assert.Equal(0.0, zooms[^1]);
        Assert.True(zooms.Zip(zooms.Skip(1), (a, b) => Math.Abs(a - b)).Max() < 0.05);
    }

    [Fact]
    public void ZoomIsOffUnlessEnabled()
    {
        var rig = new Rig(Sharp());
        rig.Run(0.2, () => rig.SendWebcam(0, 0, 60, true));
        rig.Run(0.2, () => rig.SendWebcam(0, 0, 40, true));

        Assert.Equal(0.0, rig.Tracker.OutputZoom);
    }

    [Fact]
    public void ResetForgetsTheCentreForANewSource()
    {
        var rig = new Rig(Sharp());
        rig.Run(0.2, () => rig.SendWebcam(30, 0, 60, true));
        Assert.Equal(30, rig.Tracker.CenterYaw, 6);

        rig.Tracker.Reset();
        Assert.False(rig.Tracker.IsCentered);
        Assert.Equal(TrackState.NoData, rig.Tracker.State);

        rig.Run(0.2, () => rig.SendWebcam(-5, 0, 60, true));
        Assert.Equal(-5, rig.Tracker.CenterYaw, 6);
    }
}

public class LinkProtocolTests
{
    [Fact]
    public void PoseRoundTrips()
    {
        var pose = new PoseMessage { Sequence = 77, State = LinkTrackState.Holding, Enabled = true, Yaw = -12.5f, Pitch = 3.25f, Zoom = 0.4f };
        byte[] bytes = LinkProtocol.Encode(pose);

        Assert.True(LinkProtocol.TryReadHeader(bytes, bytes.Length, out LinkMessageType type));
        Assert.Equal(LinkMessageType.Pose, type);
        Assert.True(LinkProtocol.TryDecode(bytes, bytes.Length, out PoseMessage back));
        Assert.Equal(pose, back);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var settings = new GameSettingsMessage
        {
            Revision = 9, PauseWhileAiming = false, PauseWhenCursorVisible = true, PauseWhenUnfocused = false, ZoomEnabled = true,
            PauseFadeMs = 120, ZoomMaxFovReduction = 22, LinkTimeoutMs = 500, ToggleKey = 290, ToggleModifiers = KeyModifiers.Shift,
            RecenterKey = 277, RecenterModifiers = KeyModifiers.Control | KeyModifiers.Alt,
        };
        byte[] bytes = LinkProtocol.Encode(settings);

        Assert.True(LinkProtocol.TryDecode(bytes, bytes.Length, out GameSettingsMessage? back));
        Assert.NotNull(back);
        Assert.Equivalent(settings, back);
    }

    [Fact]
    public void StatusRoundTripsWithItsVersionString()
    {
        var status = new StatusMessage
        {
            Sequence = 3, InRaid = true, Applying = true, HasSettings = true, PauseReasons = 16, AppliedYaw = 4, AppliedPitch = -2,
            AppliedFovReduction = 7.5f, BaseFov = 75, HookMicros = 6.5f, ModVersion = "0.2.0",
        };
        byte[] bytes = LinkProtocol.Encode(status);

        Assert.True(LinkProtocol.TryDecode(bytes, bytes.Length, out StatusMessage back));
        Assert.Equal(status, back);
    }

    [Fact]
    public void CommandsRoundTrip()
    {
        foreach (LinkCommand command in Enum.GetValues<LinkCommand>())
        {
            byte[] bytes = LinkProtocol.Encode(command);
            Assert.True(LinkProtocol.TryDecode(bytes, bytes.Length, out LinkCommand back));
            Assert.Equal(command, back);
        }
    }

    [Fact]
    public void ForeignTruncatedAndMismatchedDatagramsAreRejected()
    {
        byte[] pose = LinkProtocol.Encode(new PoseMessage { Yaw = 1 });

        // An OpenTrack packet sent to the wrong port.
        Assert.False(LinkProtocol.TryReadHeader(new byte[48], 48, out _));
        // Cut short.
        Assert.False(LinkProtocol.TryDecode(pose, pose.Length - 3, out PoseMessage _));
        // Right header, wrong type asked for.
        Assert.False(LinkProtocol.TryDecode(pose, pose.Length, out StatusMessage _));
        // NaN never reaches the camera.
        byte[] nan = LinkProtocol.Encode(new PoseMessage { Yaw = float.NaN });
        Assert.False(LinkProtocol.TryDecode(nan, nan.Length, out PoseMessage _));
    }

    [Fact]
    public void TrailingBytesFromANewerSenderAreIgnored()
    {
        byte[] pose = LinkProtocol.Encode(new PoseMessage { Sequence = 5, Yaw = 2 }).Concat(new byte[12]).ToArray();
        Assert.True(LinkProtocol.TryDecode(pose, pose.Length, out PoseMessage back));
        Assert.Equal(5u, back.Sequence);
    }
}
