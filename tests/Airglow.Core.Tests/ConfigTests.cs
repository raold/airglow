using Airglow.Core.Config;
using Airglow.Core.Curves;

namespace Airglow.Core.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "airglow-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static AppConfig With(AppConfig c, IReadOnlyList<FanCurve>? curves = null, IReadOnlyList<FanTarget>? fans = null) =>
        c with { Curves = curves ?? c.Curves, Fans = fans ?? c.Fans };

    [Fact]
    public void Default_config_is_valid() =>
        Assert.Empty(ConfigValidator.Validate(AppConfig.CreateDefault()));

    [Fact]
    public void Default_config_copies_current_setup()
    {
        var c = AppConfig.CreateDefault();
        var gpu = Assert.IsType<GraphCurve>(c.Curves.Single(x => x.Id == "gpu"));
        Assert.Equal(new[] { new CurvePoint(32.1, 0), new CurvePoint(73.2, 100), new CurvePoint(80.9, 100) }, gpu.Points);
        var g3 = Assert.IsType<GraphCurve>(c.Curves.Single(x => x.Id == "lianli-g3"));
        Assert.Equal(new CurvePoint(61, 59), g3.Points[4]);
        var fan2 = c.Fans.Single(x => x.FanId == "gpu.fan2");
        Assert.Equal(8, fan2.StepUpPercent);
        Assert.Equal(1, fan2.StopBelowPercent);
        Assert.Equal(4, c.LianLiGroups.Count);
        Assert.All(c.LianLiGroups, g => Assert.Equal(3, g.FanCount));
    }

    [Fact]
    public void Rejects_unsorted_points()
    {
        var c = AppConfig.CreateDefault();
        var bad = new GraphCurve("x", "X", "cpu.package", [new CurvePoint(50, 10), new CurvePoint(40, 20)], new HysteresisSettings());
        var errors = ConfigValidator.Validate(With(c, [bad]) with { Fans = [] });
        Assert.Contains("Curve 'x': temperatures must increase from point to point", errors);
    }

    [Fact]
    public void Rejects_unknown_fan_curve()
    {
        var c = AppConfig.CreateDefault();
        var errors = ConfigValidator.Validate(With(c, fans: [new FanTarget("f", "F", "nope")]));
        Assert.Contains("Fan 'f': unknown curve 'nope'", errors);
    }

    [Fact]
    public void Rejects_mix_cycle()
    {
        var c = AppConfig.CreateDefault();
        var errors = ConfigValidator.Validate(With(c, [
            new MixCurve("a", "A", MixFunction.Max, ["b"]),
            new MixCurve("b", "B", MixFunction.Max, ["a"])], []));
        Assert.Contains("Curve 'a': mix cycle", errors);
    }

    [Fact]
    public void Rejects_duplicate_curve_id()
    {
        var c = AppConfig.CreateDefault();
        var errors = ConfigValidator.Validate(With(c, [new FlatCurve("a", "A", 10), new FlatCurve("a", "A2", 20)], []));
        Assert.Contains("Duplicate curve id 'a'", errors);
    }

    [Fact]
    public void Rejects_out_of_range_fan_and_engine_values()
    {
        var c = AppConfig.CreateDefault();
        var errors = ConfigValidator.Validate(c with
        {
            Engine = new EngineSettings(FailsafePercent: 101, FailsafeAfterSeconds: 0, ShutdownPercent: -1),
            Fans = [new FanTarget("gpu.fan1", "G", "gpu", MinPercent: 101, StepUpPercent: 0, OffsetPercent: 101, StopBelowPercent: 101),
                    new FanTarget("gpu.fan1", "G", null)]
        });
        Assert.Contains("Fan 'gpu.fan1': MinPercent must be 0-100", errors);
        Assert.Contains("Fan 'gpu.fan1': StepUpPercent must be 1-100", errors);
        Assert.Contains("Fan 'gpu.fan1': OffsetPercent must be -100-100", errors);
        Assert.Contains("Fan 'gpu.fan1': StopBelowPercent must be 0-100", errors);
        Assert.Contains("Duplicate fan id 'gpu.fan1'", errors);
        Assert.Contains("Fan 'gpu.fan1': needs a curve or a manual percent", errors);
        Assert.Contains("Engine: FailsafePercent must be 0-100", errors);
        Assert.Contains("Engine: ShutdownPercent must be 0-100", errors);
        Assert.Contains("Engine: FailsafeAfterSeconds must be 1-60", errors);
    }

    [Fact]
    public void Json_round_trips()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(AppConfig.CreateDefault(), ConfigJson.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options)!;
        var json2 = System.Text.Json.JsonSerializer.Serialize(back, ConfigJson.Options);
        Assert.Equal(json, json2);
        Assert.Contains("\"type\": \"graph\"", json);
    }

    [Fact]
    public void Load_missing_file_writes_default()
    {
        var path = Path.Combine(_dir, "config.json");
        var result = new ConfigStore(path).Load();
        Assert.Null(result.Problem);
        Assert.Equal(AppConfig.CreateDefault().Fans.Count, result.Config.Fans.Count);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_corrupt_file_falls_back_and_keeps_copy()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{ not json");
        var result = new ConfigStore(path).Load();
        Assert.StartsWith("config.json was unreadable", result.Problem);
        Assert.Equal(AppConfig.CreateDefault().Fans.Count, result.Config.Fans.Count);
        Assert.Single(Directory.GetFiles(_dir, "config.invalid-*.json"));
    }

    [Fact]
    public void Load_invalid_config_falls_back_and_keeps_copy()
    {
        var path = Path.Combine(_dir, "config.json");
        var store = new ConfigStore(path);
        store.Load();
        var bad = System.Text.Json.JsonSerializer.Serialize(AppConfig.CreateDefault() with { Engine = new EngineSettings(TickMilliseconds: 1) }, ConfigJson.Options);
        File.WriteAllText(path, bad);
        var result = store.Load();
        Assert.StartsWith("config.json was invalid: ", result.Problem);
        Assert.Contains("Engine: TickMilliseconds must be 250-5000", result.Problem);
        Assert.Single(Directory.GetFiles(_dir, "config.invalid-*.json"));
    }

    [Fact]
    public void Save_rejects_invalid_config()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        Assert.Throws<ArgumentException>(() =>
            store.Save(AppConfig.CreateDefault() with { Engine = new EngineSettings(TickMilliseconds: 1) }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"curves\":null}")]
    public void Load_incomplete_file_falls_back(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        var result = new ConfigStore(path).Load();
        Assert.StartsWith("config.json was invalid:", result.Problem);
        Assert.Equal(AppConfig.CreateDefault().Fans.Count, result.Config.Fans.Count);
    }

    [Fact]
    public void Rejects_bad_and_duplicate_lian_li_group_index()
    {
        var c = AppConfig.CreateDefault();
        var errors = ConfigValidator.Validate(c with
        {
            LianLiGroups = [new LianLiGroup(4, "A", 3), new LianLiGroup(1, "B", 3), new LianLiGroup(1, "C", 3)]
        });
        Assert.Contains("Lian Li group 4: Index must be 0-3", errors);
        Assert.Contains("Duplicate Lian Li group index 1", errors);
    }

    [Fact]
    public void Rejects_missing_curve_and_fan_ids()
    {
        var c = AppConfig.CreateDefault();
        var errors = ConfigValidator.Validate(c with
        {
            Curves = [new FlatCurve(null!, "X", 10), new FlatCurve("", "Y", 10)],
            Fans = [new FanTarget(null!, "F", null), new FanTarget("", "G", null)]
        });
        Assert.Contains("Curve: missing id", errors);
        Assert.Contains("Fan: missing id", errors);
    }
}
