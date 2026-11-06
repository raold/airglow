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
