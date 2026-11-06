namespace Airglow.Hardware.Hid;

public interface IHidDevice : IDisposable
{
    HidDeviceInfo Info { get; }

    /// <summary>Sends a feature report, zero-padded to the device's feature report length. Throws <see cref="IOException"/> on failure.</summary>
    void SetFeature(ReadOnlySpan<byte> report);

    /// <summary>Reads an input report; buffer[0] is the report ID. Returns the number of bytes valid in the buffer. Throws <see cref="IOException"/> on failure.</summary>
    int GetInputReport(Span<byte> buffer);
}
