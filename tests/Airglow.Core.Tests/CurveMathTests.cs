using Airglow.Core.Curves;

namespace Airglow.Core.Tests;

public class CurveMathTests
{
    static readonly CurvePoint[] Gpu = [new(32.1, 0), new(73.2, 100), new(80.9, 100)];

    [Theory]
    [InlineData(20, 0)]
    [InlineData(32.1, 0)]
    [InlineData(52.65, 50)]
    [InlineData(73.2, 100)]
    [InlineData(90, 100)]
    public void EvaluateGraph_interpolates_and_clamps(double celsius, double expected)
        => Assert.Equal(expected, CurveMath.EvaluateGraph(Gpu, celsius), 3);

    [Fact]
    public void EvaluateGraph_single_point_is_constant()
        => Assert.Equal(40, CurveMath.EvaluateGraph([new(50, 40)], 10), 3);
}
