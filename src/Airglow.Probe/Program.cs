using Airglow.Core.Config;
using Airglow.Hardware.Hid;
using Airglow.Hardware.LianLi;
using Airglow.Hardware.Monitoring;
using LibreHardwareMonitor.Hardware;

// Read-only hardware report. Never writes to a device: no SetPercent, SetAuto or SetGroupPercent.

static IEnumerable<IHardware> Walk(IEnumerable<IHardware> hardware)
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

Console.WriteLine("== LibreHardwareMonitor ==");
var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
computer.Open();
try
{
    foreach (var hardware in Walk(computer.Hardware))
    {
        hardware.Update();
        Console.WriteLine($"[{hardware.HardwareType}] {hardware.Name} ({hardware.Identifier})");
        foreach (var sensor in hardware.Sensors)
        {
            var value = sensor.Value is { } v ? v.ToString("0.##") : "n/a";
            var line = $"  {sensor.Identifier}  id={LhmSensorMap.IdFor(sensor)}  \"{sensor.Name}\"  {sensor.SensorType}  {value}";
            if (sensor.Control is { } control)
            {
                line += $"  software {control.MinSoftwareValue:0.#}-{control.MaxSoftwareValue:0.#}  mode={control.ControlMode}";
            }

            Console.WriteLine(line);
        }
    }
}
finally
{
    computer.Close();
}

Console.WriteLine();
Console.WriteLine("== GPU fan channels ==");
var config = AppConfig.CreateDefault();
var fanCount = 0;
using (var backend = new LhmBackendFactory().TryCreate(config))
{
    foreach (var fan in backend!.Fans)
    {
        fanCount++;
        Console.WriteLine($"  {fan.Id}  \"{fan.Name}\"  auto={fan.SupportsAuto}  rpm={fan.Rpm?.ToString() ?? "n/a"}");
    }

    if (fanCount == 0)
    {
        Console.WriteLine("  none found");
    }
}

Console.WriteLine();
Console.WriteLine("== Lian Li UNI HUB AL ==");
using (var hubBackend = new LianLiAlHubFactory().TryCreate(config))
{
    if (hubBackend is LianLiAlHub hub)
    {
        Console.WriteLine($"  firmware: {(hub.Firmware is { } fw ? $"v{fw.Version} (valid={fw.IsValid})" : "no reply")}");
        Console.WriteLine($"  rpms: {string.Join(", ", hub.ReadRpms())}");
    }
    else
    {
        Console.WriteLine("  not found");
    }
}

Console.WriteLine();
Console.WriteLine("== HID devices, usage page >= 0xFF00 ==");
foreach (var d in HidEnumerator.Enumerate().Where(d => d.UsagePage >= 0xFF00))
{
    Console.WriteLine(
        $"  {d.VendorId:X4}:{d.ProductId:X4}  page=0x{d.UsagePage:X4} usage=0x{d.Usage:X4}  " +
        $"in={d.InputReportLength} out={d.OutputReportLength} feature={d.FeatureReportLength}  {d.Product}");
}

return fanCount == 0 ? 2 : 0;
