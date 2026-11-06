using Airglow.Core.Config;

namespace Airglow.Core.Fans;

public sealed class FanOutputShaper(FanTarget target)
{
    public int? Last { get; private set; }

    public int Next(double curvePercent)
    {
        var withOffset = curvePercent + target.OffsetPercent;
        var p = Math.Clamp((int)Math.Round(curvePercent, MidpointRounding.AwayFromZero) + target.OffsetPercent, 0, 100);
        var wanted = target.StopBelowPercent is { } stop && withOffset < stop ? 0 : Math.Max(p, target.MinPercent);

        var output = Last is not { } last ? wanted
            : wanted > last ? Math.Min(wanted, last + target.StepUpPercent)
            : Math.Max(wanted, last - target.StepDownPercent);

        if (Last == 0 && output > 0)
            output = Math.Max(output, target.StartPercent);

        Last = output;
        return output;
    }

    public void Reset(int percent) => Last = percent;
}
