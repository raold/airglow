namespace Airglow.Core.Curves;

public readonly record struct CurvePoint(double Celsius, double Percent);

public sealed record HysteresisSettings(
    double UpCelsius = 2,
    double DownCelsius = 2,
    double ResponseUpSeconds = 1,
    double ResponseDownSeconds = 1,
    bool IgnoreAtLimits = true);

public enum MixFunction { Max, Min, Average }

public abstract record FanCurve(string Id, string Name);

public sealed record GraphCurve(
    string Id,
    string Name,
    string SensorId,
    IReadOnlyList<CurvePoint> Points,
    HysteresisSettings Hysteresis) : FanCurve(Id, Name);

public sealed record FlatCurve(string Id, string Name, int Percent) : FanCurve(Id, Name);

public sealed record MixCurve(
    string Id,
    string Name,
    MixFunction Function,
    IReadOnlyList<string> CurveIds) : FanCurve(Id, Name);
