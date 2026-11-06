using Airglow.Core.Config;
using Airglow.Core.Hardware;
using Airglow.Hardware.Hid;

namespace Airglow.Hardware.LianLi;

public sealed class LianLiAlHubFactory : IBackendFactory
{
    private const ushort VendorId = 0x0CF2;
    private const ushort ProductId = 0xA101;
    private const ushort UsagePage = 0xFF72;

    public string Name => "Lian Li UNI HUB AL";

    public IReadOnlyList<string> ConflictingProcesses { get; } = ["L-Connect 3", "L-Connect-Service"];

    public IHardwareBackend? TryCreate(AppConfig config)
    {
        var info = HidEnumerator.Enumerate().FirstOrDefault(
            d => d.VendorId == VendorId && d.ProductId == ProductId && d.UsagePage == UsagePage);
        if (info is null)
        {
            return null;
        }

        var device = WindowsHidDevice.Open(info);
        try
        {
            return new LianLiAlHub(device, config.LianLiGroups);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }
}
