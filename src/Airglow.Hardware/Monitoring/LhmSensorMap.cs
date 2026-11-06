using LibreHardwareMonitor.Hardware;

namespace Airglow.Hardware.Monitoring;

public static class LhmSensorMap
{
    public static string? WellKnownId(HardwareType hardware, SensorType type, string name) =>
        (hardware, type, name) switch
        {
            (HardwareType.Cpu, SensorType.Temperature, "CPU Package") => "cpu.package",
            (HardwareType.GpuNvidia, SensorType.Temperature, "GPU Core") => "gpu.core",
            (HardwareType.GpuNvidia, SensorType.Temperature, "GPU Hot Spot") => "gpu.hotspot",
            (HardwareType.GpuNvidia, SensorType.Temperature, "GPU Memory Junction") => "gpu.memory",
            _ => null,
        };

    public static string IdFor(ISensor sensor) =>
        WellKnownId(sensor.Hardware.HardwareType, sensor.SensorType, sensor.Name) ?? "lhm" + sensor.Identifier;

    public static float ClampToControl(int percent, float min, float max) =>
        Math.Clamp((float)percent, min, max);

    public static int? GpuFanNumber(string name)
    {
        const string prefix = "GPU Fan";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = name[prefix.Length..].Trim();
        if (rest.Length == 0)
        {
            return 1;
        }

        return int.TryParse(rest, out var n) && n > 0 ? n : null;
    }
}
