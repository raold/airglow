using Airglow.Core.Hardware;
using LibreHardwareMonitor.Hardware;

namespace Airglow.Hardware.Monitoring;

public sealed class LhmBackend : IHardwareBackend
{
    private readonly Computer _computer;
    private readonly List<ISensor> _temperatureSensors = [];
    private readonly List<GpuFan> _fans = [];
    private Dictionary<string, double> _temperatures = [];

    public LhmBackend()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
        };
        _computer.Open();

        try
        {
            var controls = new Dictionary<int, ISensor>();
            var fanSensors = new Dictionary<int, ISensor>();
            foreach (var hardware in Walk(_computer.Hardware))
            {
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Temperature)
                    {
                        _temperatureSensors.Add(sensor);
                    }
                    else if (hardware.HardwareType == HardwareType.GpuNvidia
                        && LhmSensorMap.GpuFanNumber(sensor.Name) is { } number)
                    {
                        if (sensor.SensorType == SensorType.Control)
                        {
                            controls[number] = sensor;
                        }
                        else if (sensor.SensorType == SensorType.Fan)
                        {
                            fanSensors[number] = sensor;
                        }
                    }
                }
            }

            foreach (var (number, control) in controls.OrderBy(c => c.Key))
            {
                fanSensors.TryGetValue(number, out var rpmSensor);
                _fans.Add(new GpuFan(number, control, rpmSensor));
            }

            Sensors = _temperatureSensors
                .Select(s => new SensorInfo(LhmSensorMap.IdFor(s), s.Name, s.Hardware.Name))
                .ToList();
            Update();
        }
        catch
        {
            _computer.Close();
            throw;
        }
    }

    public string Name => "Sensors and GPU";

    public IReadOnlyList<IFanChannel> Fans => _fans;

    public IReadOnlyList<SensorInfo> Sensors { get; private set; } = [];

    public IReadOnlyDictionary<string, double> Temperatures => _temperatures;

    public void Update()
    {
        foreach (var hardware in Walk(_computer.Hardware))
        {
            hardware.Update();
        }

        var temperatures = new Dictionary<string, double>();
        foreach (var sensor in _temperatureSensors)
        {
            if (sensor.Value is { } value && !float.IsNaN(value))
            {
                temperatures[LhmSensorMap.IdFor(sensor)] = value;
            }
        }

        _temperatures = temperatures;
        foreach (var fan in _fans)
        {
            fan.Refresh();
        }
    }

    public void Dispose() => _computer.Close();

    private static IEnumerable<IHardware> Walk(IEnumerable<IHardware> hardware)
    {
        foreach (var item in hardware)
        {
            yield return item;
            foreach (var child in Walk(item.SubHardware))
            {
                yield return child;
            }
        }
    }

    private sealed class GpuFan(int number, ISensor control, ISensor? rpmSensor) : IFanChannel
    {
        public string Id => $"gpu.fan{number}";

        public string Name => control.Name;

        public bool SupportsAuto => true;

        public int? Rpm { get; private set; }

        public void SetPercent(int percent) =>
            control.Control!.SetSoftware(Math.Clamp(percent, 0, 100));

        public void SetAuto() => control.Control!.SetDefault();

        public void Refresh() =>
            Rpm = rpmSensor?.Value is { } v && !float.IsNaN(v) ? (int)Math.Round(v) : null;
    }
}
