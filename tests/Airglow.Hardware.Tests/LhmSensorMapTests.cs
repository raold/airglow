using Airglow.Hardware.Monitoring;
using LibreHardwareMonitor.Hardware;

namespace Airglow.Hardware.Tests;

public class LhmSensorMapTests
{
    [Theory]
    [InlineData(HardwareType.Cpu, SensorType.Temperature, "CPU Package", "cpu.package")]
    [InlineData(HardwareType.GpuNvidia, SensorType.Temperature, "GPU Core", "gpu.core")]
    [InlineData(HardwareType.GpuNvidia, SensorType.Temperature, "GPU Hot Spot", "gpu.hotspot")]
    [InlineData(HardwareType.GpuNvidia, SensorType.Temperature, "GPU Memory Junction", "gpu.memory")]
    [InlineData(HardwareType.Cpu, SensorType.Temperature, "Core Max", null)]
    public void WellKnownId_maps_known_sensors(HardwareType hardware, SensorType type, string name, string? expected)
    {
        Assert.Equal(expected, LhmSensorMap.WellKnownId(hardware, type, name));
    }

    [Theory]
    [InlineData("GPU Fan 2", 2)]
    [InlineData("GPU Fan 1", 1)]
    [InlineData("GPU Fan", 1)]
    [InlineData("GPU Core", null)]
    public void GpuFanNumber_parses_fan_names(string name, int? expected)
    {
        Assert.Equal(expected, LhmSensorMap.GpuFanNumber(name));
    }

    [Theory]
    [InlineData(10, 30f, 100f, 30f)]
    [InlineData(55, 30f, 100f, 55f)]
    [InlineData(120, 30f, 100f, 100f)]
    public void ClampToControl_limits_to_control_range(int percent, float min, float max, float expected)
    {
        Assert.Equal(expected, LhmSensorMap.ClampToControl(percent, min, max));
    }
}
