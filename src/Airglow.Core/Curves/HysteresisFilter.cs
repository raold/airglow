namespace Airglow.Core.Curves;

public sealed class HysteresisFilter(HysteresisSettings settings, TimeProvider time)
{
    private enum Direction { None, Up, Down }

    private Direction _pending;
    private long _pendingSince;

    public double? Current { get; private set; }

    public double? Apply(double? rawCelsius, double minPointCelsius, double maxPointCelsius)
    {
        if (rawCelsius is not { } raw)
            return Current;

        if (Current is not { } current)
        {
            Current = raw;
            return Current;
        }

        if (settings.IgnoreAtLimits && (raw >= maxPointCelsius || raw <= minPointCelsius) && raw != current)
        {
            Current = raw;
            _pending = Direction.None;
            return Current;
        }

        var delta = raw - current;
        var direction = delta >= settings.UpCelsius ? Direction.Up
            : delta <= -settings.DownCelsius ? Direction.Down
            : Direction.None;

        if (direction == Direction.None)
        {
            _pending = Direction.None;
            return Current;
        }

        var now = time.GetTimestamp();
        if (_pending != direction)
        {
            _pending = direction;
            _pendingSince = now;
        }

        var response = direction == Direction.Up ? settings.ResponseUpSeconds : settings.ResponseDownSeconds;
        if (time.GetElapsedTime(_pendingSince, now).TotalSeconds >= response)
        {
            Current = raw;
            _pending = Direction.None;
        }

        return Current;
    }
}
