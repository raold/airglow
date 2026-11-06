using Airglow.Core.Curves;

namespace Airglow.Core.Config;

public static class ConfigValidator
{
    public static IReadOnlyList<string> Validate(AppConfig config)
    {
        var errors = new List<string>();

        if (config.Engine.TickMilliseconds is < 250 or > 5000)
            errors.Add("Engine: TickMilliseconds must be 250-5000");
        if (config.Engine.FailsafePercent is < 0 or > 100)
            errors.Add("Engine: FailsafePercent must be 0-100");
        if (config.Engine.ShutdownPercent is < 0 or > 100)
            errors.Add("Engine: ShutdownPercent must be 0-100");
        if (!(config.Engine.FailsafeAfterSeconds is >= 1 and <= 60))
            errors.Add("Engine: FailsafeAfterSeconds must be 1-60");

        var byId = new Dictionary<string, FanCurve>();
        foreach (var curve in config.Curves)
        {
            if (!byId.TryAdd(curve.Id, curve))
                errors.Add($"Duplicate curve id '{curve.Id}'");
        }

        foreach (var curve in config.Curves)
        {
            switch (curve)
            {
                case GraphCurve g:
                    ValidateGraph(g, errors);
                    break;
                case FlatCurve f:
                    if (f.Percent is < 0 or > 100)
                        errors.Add($"Curve '{f.Id}': percent must be 0-100");
                    break;
                case MixCurve m:
                    foreach (var member in m.CurveIds)
                        if (!byId.ContainsKey(member))
                            errors.Add($"Curve '{m.Id}': unknown curve '{member}' in mix");
                    if (ReachesItself(m, byId))
                        errors.Add($"Curve '{m.Id}': mix cycle");
                    break;
            }
        }

        var fanIds = new HashSet<string>();
        foreach (var fan in config.Fans)
        {
            var id = fan.FanId;
            if (!fanIds.Add(id))
                errors.Add($"Duplicate fan id '{id}'");
            if (fan.CurveId is not null && !byId.ContainsKey(fan.CurveId))
                errors.Add($"Fan '{id}': unknown curve '{fan.CurveId}'");
            if (fan.CurveId is null && fan.ManualPercent is null)
                errors.Add($"Fan '{id}': needs a curve or a manual percent");
            Range(errors, id, nameof(fan.MinPercent), fan.MinPercent, 0, 100);
            Range(errors, id, nameof(fan.StartPercent), fan.StartPercent, 0, 100);
            Range(errors, id, nameof(fan.StepUpPercent), fan.StepUpPercent, 1, 100);
            Range(errors, id, nameof(fan.StepDownPercent), fan.StepDownPercent, 1, 100);
            Range(errors, id, nameof(fan.OffsetPercent), fan.OffsetPercent, -100, 100);
            if (fan.StopBelowPercent is { } stop && !(stop >= 0 && stop <= 100))
                errors.Add($"Fan '{id}': StopBelowPercent must be 0-100");
            if (fan.ManualPercent is { } manual)
                Range(errors, id, nameof(fan.ManualPercent), manual, 0, 100);
        }

        foreach (var group in config.LianLiGroups)
            if (group.FanCount is < 0 or > 4)
                errors.Add($"Lian Li group {group.Index}: FanCount must be 0-4");

        return errors;
    }

    private static void Range(List<string> errors, string fanId, string field, int value, int min, int max)
    {
        if (value < min || value > max)
            errors.Add($"Fan '{fanId}': {field} must be {min}-{max}");
    }

    private static void ValidateGraph(GraphCurve g, List<string> errors)
    {
        if (g.Points.Count < 2)
        {
            errors.Add($"Curve '{g.Id}': needs at least 2 points");
            return;
        }
        for (var i = 1; i < g.Points.Count; i++)
        {
            if (g.Points[i].Celsius <= g.Points[i - 1].Celsius)
            {
                errors.Add($"Curve '{g.Id}': temperatures must increase from point to point");
                break;
            }
        }
        if (g.Points.Any(p => p.Celsius is < 0 or > 120 || p.Percent is < 0 or > 100))
            errors.Add($"Curve '{g.Id}': points must be within 0-120 °C and 0-100 %");
    }

    private static bool ReachesItself(MixCurve start, Dictionary<string, FanCurve> byId)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>(start.CurveIds);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id == start.Id) return true;
            if (!seen.Add(id)) continue;
            if (byId.TryGetValue(id, out var c) && c is MixCurve m)
                foreach (var next in m.CurveIds) stack.Push(next);
        }
        return false;
    }
}
