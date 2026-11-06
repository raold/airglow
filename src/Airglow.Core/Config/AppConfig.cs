using Airglow.Core.Curves;

namespace Airglow.Core.Config;

public sealed record FanTarget(
    string FanId,
    string Name,
    string? CurveId,
    int MinPercent = 0,
    int StartPercent = 0,
    double? StopBelowPercent = null,
    int OffsetPercent = 0,
    int StepUpPercent = 100,
    int StepDownPercent = 100,
    int? ManualPercent = null);

public sealed record EngineSettings(
    int TickMilliseconds = 1000,
    int FailsafePercent = 100,
    double FailsafeAfterSeconds = 5,
    int ShutdownPercent = 60);

public sealed record LianLiGroup(int Index, string Name, int FanCount);

public sealed record AppConfig(
    int Version,
    EngineSettings Engine,
    IReadOnlyList<FanCurve> Curves,
    IReadOnlyList<FanTarget> Fans,
    IReadOnlyList<LianLiGroup> LianLiGroups)
{
    public static AppConfig CreateDefault()
    {
        var hysteresis = new HysteresisSettings();

        static CurvePoint[] Pts(params (double C, double P)[] p) =>
            p.Select(x => new CurvePoint(x.C, x.P)).ToArray();

        var curves = new List<FanCurve>
        {
            new GraphCurve("gpu", "4090", "gpu.core", Pts((32.1, 0), (73.2, 100), (80.9, 100)), hysteresis),
            new GraphCurve("lianli-g1", "Group 1", "cpu.package",
                Pts((0, 10), (25, 45), (34, 52), (48, 57), (61, 74), (71, 98), (100, 100)), hysteresis),
            new GraphCurve("lianli-g2", "Group 2", "cpu.package",
                Pts((0, 10), (25, 45), (35, 55), (49, 68), (61, 77), (78, 93), (100, 100)), hysteresis),
            new GraphCurve("lianli-g3", "Group 3", "cpu.package",
                Pts((0, 10), (25, 45), (35, 59), (49, 59), (61, 59), (78, 93), (100, 100)), hysteresis),
            new GraphCurve("lianli-g4", "Group 4", "cpu.package",
                Pts((0, 10), (25, 45), (35, 55), (49, 68), (61, 77), (78, 93), (100, 100)), hysteresis),
        };

        var fans = new List<FanTarget>
        {
            new("gpu.fan1", "GPU fan 1", "gpu", StepUpPercent: 8, StepDownPercent: 8, StopBelowPercent: 1),
            new("gpu.fan2", "GPU fan 2", "gpu", StepUpPercent: 8, StepDownPercent: 8, StopBelowPercent: 1),
        };
        fans.AddRange(Enumerable.Range(1, 4).Select(n => new FanTarget($"lianli.g{n}", $"Group {n}", $"lianli-g{n}")));

        var groups = Enumerable.Range(0, 4).Select(i => new LianLiGroup(i, $"Group {i + 1}", 3)).ToList();

        return new AppConfig(1, new EngineSettings(), curves, fans, groups);
    }
}
