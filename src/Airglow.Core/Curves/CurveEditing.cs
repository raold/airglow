namespace Airglow.Core.Curves;

public static class CurveEditing
{
    public const double MinGapCelsius = 0.5;

    private const double MaxCelsius = 120;
    private const double Epsilon = 1e-9;

    public static IReadOnlyList<CurvePoint> Add(IReadOnlyList<CurvePoint> points, CurvePoint p)
    {
        var point = Snap(p);
        if (points.Any(q => TooClose(q.Celsius, p.Celsius) || TooClose(q.Celsius, point.Celsius)))
            return points;

        var result = points.ToList();
        result.Insert(points.Count(q => q.Celsius < point.Celsius), point);
        return result;
    }

    public static IReadOnlyList<CurvePoint> Move(IReadOnlyList<CurvePoint> points, int index, CurvePoint p)
    {
        if (index < 0 || index >= points.Count)
            return points;

        var low = index > 0 ? points[index - 1].Celsius + MinGapCelsius : 0;
        var high = index < points.Count - 1 ? points[index + 1].Celsius - MinGapCelsius : MaxCelsius;
        var snapped = Snap(p);
        var result = points.ToList();
        result[index] = snapped with { Celsius = Math.Clamp(snapped.Celsius, low, Math.Max(low, high)) };
        return result;
    }

    public static IReadOnlyList<CurvePoint> Remove(IReadOnlyList<CurvePoint> points, int index)
    {
        if (points.Count <= 2 || index < 0 || index >= points.Count)
            return points;

        var result = points.ToList();
        result.RemoveAt(index);
        return result;
    }

    private static bool TooClose(double a, double b) => Math.Abs(a - b) < MinGapCelsius - Epsilon;

    private static CurvePoint Snap(CurvePoint p) => new(
        Math.Clamp(Math.Round(p.Celsius * 2, MidpointRounding.AwayFromZero) / 2, 0, MaxCelsius),
        Math.Clamp(Math.Round(p.Percent, MidpointRounding.AwayFromZero), 0, 100));
}
