using Airglow.Core.Curves;
using Microsoft.Extensions.Time.Testing;

namespace Airglow.Core.Tests;

public class CurveSetTests
{
    private static CurveSet Build() => new(
        [
            new GraphCurve("gpu", "GPU", "gpu.core", [new(32.1, 0), new(73.2, 100), new(80.9, 100)], new()),
            new GraphCurve("cpu", "CPU", "cpu.package", [new(30, 20), new(80, 100)], new()),
            new MixCurve("mix", "Mix", MixFunction.Max, ["gpu", "cpu"]),
            new FlatCurve("flat", "Flat", 35),
        ],
        new FakeTimeProvider());

    private static Dictionary<string, double> Temps(params (string Id, double C)[] t) =>
        t.ToDictionary(x => x.Id, x => x.C);

    [Fact]
    public void Evaluates_graph_flat_and_mix()
    {
        var r = Build().Evaluate(Temps(("gpu.core", 52.65), ("cpu.package", 80)));

        Assert.Equal(50, r["gpu"].Percent!.Value, 6);
        Assert.Equal(100, r["cpu"].Percent!.Value, 6);
        Assert.Equal(100, r["mix"].Percent!.Value, 6);
        Assert.Equal(35, r["flat"].Percent!.Value, 6);
        Assert.All(r.Values, v => Assert.False(v.InputMissing));
    }

    [Fact]
    public void Missing_sensor_holds_last_value_and_flags_missing()
    {
        var set = Build();
        set.Evaluate(Temps(("gpu.core", 52.65)));
        var r = set.Evaluate(Temps());

        Assert.Equal(50, r["gpu"].Percent!.Value, 6);
        Assert.True(r["gpu"].InputMissing);
        Assert.True(r["mix"].InputMissing);
    }

    [Fact]
    public void Nan_temperature_counts_as_missing()
    {
        var set = Build();
        set.Evaluate(Temps(("gpu.core", 52.65)));
        var r = set.Evaluate(Temps(("gpu.core", double.NaN)));

        Assert.Equal(50, r["gpu"].Percent!.Value, 6);
        Assert.True(r["gpu"].InputMissing);
        Assert.True(r["mix"].InputMissing);
    }

    [Fact]
    public void Never_seen_sensor_gives_null_percent()
    {
        var r = Build().Evaluate(Temps());

        Assert.Null(r["gpu"].Percent);
        Assert.True(r["gpu"].InputMissing);
    }
}
