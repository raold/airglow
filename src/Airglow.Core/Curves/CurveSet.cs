namespace Airglow.Core.Curves;

public sealed record CurveResult(double? Percent, bool InputMissing);

public sealed class CurveSet
{
    private readonly IReadOnlyList<FanCurve> _curves;
    private readonly Dictionary<string, FanCurve> _byId;
    private readonly Dictionary<string, HysteresisFilter> _filters = [];

    public CurveSet(IReadOnlyList<FanCurve> curves, TimeProvider time)
    {
        _curves = curves;
        _byId = curves.ToDictionary(c => c.Id);
        foreach (var graph in curves.OfType<GraphCurve>())
            _filters[graph.Id] = new HysteresisFilter(graph.Hysteresis, time);
    }

    public IReadOnlyDictionary<string, CurveResult> Evaluate(IReadOnlyDictionary<string, double> temperatures)
    {
        var results = new Dictionary<string, CurveResult>();
        foreach (var curve in _curves)
            Resolve(curve, temperatures, results);
        return results;
    }

    private CurveResult Resolve(
        FanCurve curve,
        IReadOnlyDictionary<string, double> temperatures,
        Dictionary<string, CurveResult> results)
    {
        if (results.TryGetValue(curve.Id, out var done))
            return done;

        var result = curve switch
        {
            GraphCurve g => EvaluateGraph(g, temperatures),
            FlatCurve f => new CurveResult(f.Percent, false),
            MixCurve m => EvaluateMix(m, temperatures, results),
            _ => throw new NotSupportedException($"Unknown curve type {curve.GetType().Name}"),
        };
        results[curve.Id] = result;
        return result;
    }

    private CurveResult EvaluateGraph(GraphCurve graph, IReadOnlyDictionary<string, double> temperatures)
    {
        double? raw = temperatures.TryGetValue(graph.SensorId, out var t) && !double.IsNaN(t) ? t : null;
        var filtered = _filters[graph.Id].Apply(raw, graph.Points[0].Celsius, graph.Points[^1].Celsius);
        double? percent = filtered is { } f ? CurveMath.EvaluateGraph(graph.Points, f) : null;
        return new CurveResult(percent, raw is null);
    }

    private CurveResult EvaluateMix(
        MixCurve mix,
        IReadOnlyDictionary<string, double> temperatures,
        Dictionary<string, CurveResult> results)
    {
        var members = mix.CurveIds.Select(id => Resolve(_byId[id], temperatures, results)).ToList();
        var values = members.Where(m => m.Percent is not null).Select(m => m.Percent!.Value).ToList();
        double? percent = values.Count == 0 ? null : mix.Function switch
        {
            MixFunction.Max => values.Max(),
            MixFunction.Min => values.Min(),
            _ => values.Average(),
        };
        return new CurveResult(percent, members.Any(m => m.InputMissing));
    }
}
