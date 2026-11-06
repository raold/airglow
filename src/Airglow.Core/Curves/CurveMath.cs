namespace Airglow.Core.Curves;

public static class CurveMath
{
    public static double EvaluateGraph(IReadOnlyList<CurvePoint> points, double celsius)
    {
        if (celsius <= points[0].Celsius)
            return points[0].Percent;

        for (var i = 1; i < points.Count; i++)
        {
            var hi = points[i];
            if (celsius > hi.Celsius)
                continue;

            var lo = points[i - 1];
            var span = hi.Celsius - lo.Celsius;
            return span == 0
                ? hi.Percent
                : lo.Percent + (hi.Percent - lo.Percent) * (celsius - lo.Celsius) / span;
        }

        return points[^1].Percent;
    }
}
