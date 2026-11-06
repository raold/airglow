using Airglow.Core.Curves;

namespace Airglow.Core.Tests;

public class CurveEditingTests
{
    private static CurvePoint[] Pts(params (double C, double P)[] p) =>
        p.Select(x => new CurvePoint(x.C, x.P)).ToArray();

    [Fact]
    public void Add_inserts_in_order()
    {
        var result = CurveEditing.Add(Pts((30, 20), (80, 100)), new CurvePoint(50, 60));

        Assert.Equal(Pts((30, 20), (50, 60), (80, 100)), result);
    }

    [Fact]
    public void Add_rejects_point_too_close()
    {
        var points = Pts((30, 20), (80, 100));

        Assert.Same(points, CurveEditing.Add(points, new CurvePoint(30.3, 40)));
    }

    [Fact]
    public void Move_clamps_between_neighbours()
    {
        var result = CurveEditing.Move(Pts((30, 20), (50, 60), (80, 100)), 1, new CurvePoint(90, 120));

        Assert.Equal(Pts((30, 20), (79.5, 100), (80, 100)), result);
    }

    [Fact]
    public void Move_clamps_to_bounds()
    {
        var result = CurveEditing.Move(Pts((30, 20), (50, 60), (80, 100)), 0, new CurvePoint(-10, -5));

        Assert.Equal(new CurvePoint(0, 0), result[0]);
    }

    [Fact]
    public void Move_snaps_to_half_degree_and_whole_percent()
    {
        var result = CurveEditing.Move(Pts((30, 20), (80, 100)), 0, new CurvePoint(41.3, 55.4));

        Assert.Equal(new CurvePoint(41.5, 55), result[0]);
    }

    [Fact]
    public void Remove_keeps_two_points()
    {
        var points = Pts((30, 20), (80, 100));

        Assert.Same(points, CurveEditing.Remove(points, 0));
    }

    [Fact]
    public void Remove_drops_the_point()
    {
        var result = CurveEditing.Remove(Pts((30, 20), (50, 60), (80, 100)), 1);

        Assert.Equal(Pts((30, 20), (80, 100)), result);
    }
}
