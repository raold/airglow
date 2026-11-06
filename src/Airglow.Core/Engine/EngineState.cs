namespace Airglow.Core.Engine;

public enum FanMode { Curve, Manual, Failsafe, Auto, Paused, Unavailable }

public sealed record TempReading(string Id, string Name, double? Celsius);

public sealed record FanReading(
    string Id,
    string Name,
    string? CurveId,
    double? CurvePercent,
    int? CommandedPercent,
    int? Rpm,
    FanMode Mode,
    string? Note);

public sealed record EngineState(
    DateTimeOffset At,
    IReadOnlyList<TempReading> Temperatures,
    IReadOnlyList<FanReading> Fans,
    IReadOnlyList<string> Faults);
