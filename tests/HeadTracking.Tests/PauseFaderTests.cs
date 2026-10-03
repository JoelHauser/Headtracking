using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.Tests;

public class PauseFaderTests
{
    [Fact]
    public void StartsFromZeroAndFadesInOverTheFadeTime()
    {
        var fader = new PauseFader();
        double now = 10;
        Assert.True(fader.Tick(PauseReason.None, now, 0.25));
        Assert.Equal(0.0, fader.Weight);

        for (int i = 0; i < 14; i++)
        {
            now += 1.0 / 60;
            fader.Tick(PauseReason.None, now, 0.25);
        }

        Assert.True(fader.Weight > 0.8 && fader.Weight < 1.0, "weight " + fader.Weight);

        now += 0.1;
        fader.Tick(PauseReason.None, now, 0.25);
        Assert.Equal(1.0, fader.Weight);
        Assert.Equal(1.0, fader.Eased);
    }

    [Fact]
    public void AnyReasonFadesToExactlyZero()
    {
        var fader = new PauseFader();
        double now = 10;
        for (int i = 0; i < 60; i++)
        {
            fader.Tick(PauseReason.None, now += 1.0 / 60, 0.25);
        }

        for (int i = 0; i < 30; i++)
        {
            fader.Tick(PauseReason.ScreenOpen | PauseReason.CursorVisible, now += 1.0 / 60, 0.25);
        }

        Assert.True(fader.Weight == 0.0);
        Assert.True(fader.Eased == 0.0);
    }

    [Fact]
    public void AGapRestartsFromZero()
    {
        var fader = new PauseFader();
        double now = 10;
        for (int i = 0; i < 60; i++)
        {
            fader.Tick(PauseReason.None, now += 1.0 / 60, 0.25);
        }

        Assert.True(fader.Tick(PauseReason.None, now + 2.0, 0.25));
        Assert.Equal(0.0, fader.Weight);
    }

    [Fact]
    public void ZeroFadeTimeIsInstant()
    {
        var fader = new PauseFader();
        fader.Tick(PauseReason.None, 10, 0);
        Assert.Equal(1.0, fader.Weight);
        fader.Tick(PauseReason.Aiming, 10.01, 0);
        Assert.Equal(0.0, fader.Weight);
    }

    [Fact]
    public void DescribeNamesEveryReason()
    {
        string all = PauseFader.Describe((PauseReason)0x7F);
        foreach (string word in new[] { "turned off", "screen open", "cursor", "dialog", "aiming", "focused", "first person" })
        {
            Assert.Contains(word, all);
        }

        Assert.Equal("none", PauseFader.Describe(PauseReason.None));
    }
}
