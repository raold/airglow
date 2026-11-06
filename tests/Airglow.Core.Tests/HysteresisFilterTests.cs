using Airglow.Core.Curves;
using Microsoft.Extensions.Time.Testing;

namespace Airglow.Core.Tests;

public class HysteresisFilterTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly HysteresisFilter _filter;

    public HysteresisFilterTests() => _filter = new HysteresisFilter(new HysteresisSettings(), _time);

    private double? Apply(double? raw) => _filter.Apply(raw, 30, 80);

    [Fact]
    public void First_sample_applies_immediately() => Assert.Equal(50, Apply(50));

    [Fact]
    public void Rise_below_band_never_applies()
    {
        Apply(50);
        Assert.Equal(50, Apply(51.9));
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(50, Apply(51.9));
    }

    [Fact]
    public void Rise_applies_after_response_time()
    {
        Apply(50);
        Assert.Equal(50, Apply(52));
        _time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(50, Apply(52));
        _time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(52, Apply(52));
    }

    [Fact]
    public void Pending_rise_resets_when_back_in_band()
    {
        Apply(50);
        _time.Advance(TimeSpan.FromSeconds(0.5));
        Apply(52);
        _time.Advance(TimeSpan.FromSeconds(0.2));
        Apply(51);
        _time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(50, Apply(52));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(52, Apply(52));
    }

    [Fact]
    public void Drop_applies_after_response_time()
    {
        Apply(50);
        Apply(47.5);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(47.5, Apply(47.5));
    }

    [Fact]
    public void Ignores_hysteresis_at_limits()
    {
        Apply(79);
        Assert.Equal(80.5, Apply(80.5));

        var low = new HysteresisFilter(new HysteresisSettings(), _time);
        low.Apply(31, 30, 80);
        Assert.Equal(29.5, low.Apply(29.5, 30, 80));
    }

    [Fact]
    public void Null_input_holds_current()
    {
        Apply(50);
        Assert.Equal(50, Apply(null));
    }
}
