using Airglow.Core.Config;
using Airglow.Core.Fans;

namespace Airglow.Core.Tests;

public class FanOutputShaperTests
{
    private static FanTarget Gpu => new("gpu.fan1", "GPU fan 1", "gpu", StepUpPercent: 8, StepDownPercent: 8, StopBelowPercent: 1);
    private static FanTarget Lianli => new("lianli.g1", "Group 1", "lianli-g1", MinPercent: 20, StartPercent: 40);

    [Fact]
    public void First_tick_is_unrestricted() => Assert.Equal(55, new FanOutputShaper(Gpu).Next(55));

    [Fact]
    public void Step_up_is_limited()
    {
        var shaper = new FanOutputShaper(Gpu);
        shaper.Reset(40);
        Assert.Equal(48, shaper.Next(100));
    }

    [Fact]
    public void Step_down_is_limited()
    {
        var shaper = new FanOutputShaper(Gpu);
        shaper.Reset(48);
        Assert.Equal(40, shaper.Next(0));
    }

    [Fact]
    public void Stops_below_threshold()
    {
        var shaper = new FanOutputShaper(Gpu);
        shaper.Reset(8);
        Assert.Equal(0, shaper.Next(0.5));
    }

    [Fact]
    public void Respects_minimum() => Assert.Equal(20, new FanOutputShaper(Lianli).Next(10));

    [Fact]
    public void Kicks_from_stop_with_start_percent()
    {
        var shaper = new FanOutputShaper(Lianli);
        shaper.Reset(0);
        Assert.Equal(40, shaper.Next(25));
    }

    [Fact]
    public void Applies_offset_and_clamps()
    {
        var shaper = new FanOutputShaper(new FanTarget("gpu.fan1", "GPU fan 1", "gpu", OffsetPercent: 5));
        Assert.Equal(55, shaper.Next(50));
        Assert.Equal(100, shaper.Next(99));
    }
}
