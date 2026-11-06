using Airglow.Hardware.Hid;

namespace Airglow.Hardware.Tests;

internal sealed class FakeHidDevice : IHidDevice
{
    private readonly Queue<byte[]> _inputs = new();

    public HidDeviceInfo Info { get; } = new(
        "fake", 0x0CF2, 0xA101, 0xFF72, 0x01, InputReportLength: 65, OutputReportLength: 65, FeatureReportLength: 7, "fake", null);

    public List<byte[]> Sent { get; } = [];
    public bool Disposed { get; private set; }

    public void QueueInput(params byte[] report) => _inputs.Enqueue(report);

    public void SetFeature(ReadOnlySpan<byte> report)
    {
        var padded = new byte[Info.FeatureReportLength];
        report.CopyTo(padded);
        Sent.Add(padded);
    }

    public int GetInputReport(Span<byte> buffer)
    {
        if (_inputs.Count == 0)
        {
            throw new IOException("no input queued");
        }

        var report = _inputs.Dequeue();
        report.CopyTo(buffer);
        return report.Length;
    }

    public void Dispose() => Disposed = true;
}
