using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.Tests;

public class PacketTests
{
    [Fact]
    public void RoundTripsAllSixValuesInOpenTracksOrder()
    {
        var pose = new Pose(1.5, -2.25, 40, 12.5, -7.75, 3);
        byte[] bytes = OpenTrackPacket.Encode(pose);

        Assert.Equal(48, bytes.Length);
        Assert.Equal(PacketResult.Ok, OpenTrackPacket.TryParse(bytes, bytes.Length, out Pose back));
        Assert.True(pose.SameBits(back));

        // Yaw is the fourth double: bytes 24..31.
        Assert.Equal(12.5, BitConverter.ToDouble(bytes, 24));
    }

    [Fact]
    public void ShortPacketsAreRejected()
    {
        Assert.Equal(PacketResult.TooShort, OpenTrackPacket.TryParse(new byte[47], 47, out _));
        Assert.Equal(PacketResult.TooShort, OpenTrackPacket.TryParse(null!, 0, out _));
    }

    [Fact]
    public void LongerPacketsUseTheFirst48Bytes()
    {
        byte[] bytes = OpenTrackPacket.Encode(new Pose(0, 0, 0, 5, 6, 7)).Concat(new byte[8]).ToArray();
        Assert.Equal(PacketResult.OkOversized, OpenTrackPacket.TryParse(bytes, bytes.Length, out Pose pose));
        Assert.Equal(5, pose.Yaw);
    }

    [Fact]
    public void NaNAndNonsenseAreRejected()
    {
        byte[] nan = OpenTrackPacket.Encode(new Pose(0, 0, 0, double.NaN, 0, 0));
        Assert.Equal(PacketResult.NotFinite, OpenTrackPacket.TryParse(nan, 48, out _));

        byte[] huge = OpenTrackPacket.Encode(new Pose(0, 0, 0, 720, 0, 0));
        Assert.Equal(PacketResult.OutOfRange, OpenTrackPacket.TryParse(huge, 48, out _));
    }
}

/// <summary>The real receiver, on a real loopback socket.</summary>
public class ReceiverTests
{
    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static void Send(int port, byte[] bytes)
    {
        using var client = new UdpClient();
        client.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3))
            {
                throw new TimeoutException("Timed out waiting for " + what);
            }

            Thread.Sleep(5);
        }
    }

    [Fact]
    public void ReceivesPosesAndTracksWhenTheyLastChanged()
    {
        int port = FreePort();
        var log = new ListLog();
        using var receiver = new UdpPoseReceiver(port, false, log);
        Assert.True(receiver.Start(), receiver.BindError);
        Assert.True(receiver.IsListening);

        var pose = new Pose(0, 0, 60, 11, -4, 2);
        Send(port, OpenTrackPacket.Encode(pose));
        WaitFor(() => receiver.GetStats().Valid == 1, "the first pose");

        Assert.True(receiver.TryGetSnapshot(out PoseSnapshot first));
        Assert.True(pose.SameBits(first.Pose));

        // The identical pose again: it arrived, but it did not change.
        Thread.Sleep(20);
        Send(port, OpenTrackPacket.Encode(pose));
        WaitFor(() => receiver.GetStats().Valid == 2, "the repeated pose");
        receiver.TryGetSnapshot(out PoseSnapshot second);
        Assert.True(second.ArrivalTime > first.ArrivalTime);
        Assert.Equal(first.LastChangeTime, second.LastChangeTime);
        Assert.Equal(1, receiver.GetStats().Distinct);

        // A different pose moves the change time.
        Send(port, OpenTrackPacket.Encode(new Pose(0, 0, 60, 12, -4, 2)));
        WaitFor(() => receiver.GetStats().Valid == 3, "the new pose");
        receiver.TryGetSnapshot(out PoseSnapshot third);
        Assert.True(third.LastChangeTime > second.LastChangeTime);

        Assert.True(log.Any("First packet received from 127.0.0.1"), log.ToString());
        Assert.True(log.Any("First pose"), log.ToString());
    }

    [Fact]
    public void BadPacketsAreCountedAndExplainedButDoNotReplaceTheLastGoodPose()
    {
        int port = FreePort();
        var log = new ListLog();
        using var receiver = new UdpPoseReceiver(port, false, log);
        Assert.True(receiver.Start());

        Send(port, OpenTrackPacket.Encode(new Pose(0, 0, 0, 9, 0, 0)));
        Send(port, new byte[10]);
        Send(port, OpenTrackPacket.Encode(new Pose(0, 0, 0, double.PositiveInfinity, 0, 0)));
        WaitFor(() => receiver.GetStats().Packets == 3, "three packets");

        ReceiverStats stats = receiver.GetStats();
        Assert.Equal(1, stats.Valid);
        Assert.Equal(1, stats.TooShort);
        Assert.Equal(1, stats.NotFinite);
        receiver.TryGetSnapshot(out PoseSnapshot snapshot);
        Assert.Equal(9, snapshot.Pose.Yaw);
        Assert.True(log.Any("shorter than OpenTrack's 48"), log.ToString());
        Assert.True(log.Any("NaN or infinity"), log.ToString());
    }

    [Fact]
    public void APortAlreadyInUseIsReportedPlainly()
    {
        int port = FreePort();
        using var first = new UdpPoseReceiver(port, false, new ListLog());
        Assert.True(first.Start());

        using var second = new UdpPoseReceiver(port, false, new ListLog());
        Assert.False(second.Start());
        Assert.Contains("already in use", second.BindError);
        Assert.Contains(port.ToString(), second.BindError);
    }

    [Fact]
    public void StoppingStraightAfterStartingIsSafe()
    {
        // 0.1.0's first build crashed the whole process here: the thread read the socket field
        // after Stop() had cleared it. In game that process is EFT.
        int port = FreePort();
        for (int i = 0; i < 50; i++)
        {
            var log = new ListLog();
            var receiver = new UdpPoseReceiver(port, false, log);
            Assert.True(receiver.Start(), receiver.BindError);
            receiver.Stop();
            Assert.DoesNotContain(log.Lines, l => l.Level == LogLevel.Error);
        }
    }

    [Fact]
    public void StopEndsTheThreadAndFreesThePort()
    {
        int port = FreePort();
        var log = new ListLog();
        var receiver = new UdpPoseReceiver(port, false, log);
        Assert.True(receiver.Start());
        receiver.Stop();

        Assert.False(receiver.IsListening);
        WaitFor(() => log.Any("stopped"), "the stop line");

        using var again = new UdpPoseReceiver(port, false, new ListLog());
        Assert.True(again.Start(), again.BindError);
    }
}
