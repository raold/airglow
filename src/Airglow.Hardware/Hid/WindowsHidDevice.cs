using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Airglow.Hardware.Hid;

public sealed unsafe class WindowsHidDevice : IHidDevice
{
    private readonly SafeFileHandle _handle;

    private WindowsHidDevice(HidDeviceInfo info, SafeFileHandle handle)
    {
        Info = info;
        _handle = handle;
    }

    public HidDeviceInfo Info { get; }

    public static WindowsHidDevice Open(HidDeviceInfo info)
    {
        var handle = HidNative.CreateFile(
            info.Path,
            HidNative.GenericRead | HidNative.GenericWrite,
            HidNative.FileShareRead | HidNative.FileShareWrite,
            0,
            HidNative.OpenExisting,
            0,
            0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Cannot open HID device (Win32 error {error}).", new Win32Exception(error));
        }

        return new WindowsHidDevice(info, handle);
    }

    public void SetFeature(ReadOnlySpan<byte> report)
    {
        var length = Info.FeatureReportLength;
        if (report.Length > length)
        {
            throw new ArgumentException(
                $"Report is {report.Length} bytes; the feature report length is {length}.", nameof(report));
        }

        var padded = new byte[length];
        report.CopyTo(padded);
        fixed (byte* p = padded)
        {
            if (!HidNative.HidD_SetFeature(_handle, p, (uint)length))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"HidD_SetFeature failed (Win32 error {error}).", new Win32Exception(error));
            }
        }
    }

    public int GetInputReport(Span<byte> buffer)
    {
        if (buffer.Length != Info.InputReportLength)
        {
            throw new ArgumentException(
                $"Buffer is {buffer.Length} bytes; the input report length is {Info.InputReportLength}.", nameof(buffer));
        }

        fixed (byte* p = buffer)
        {
            if (!HidNative.HidD_GetInputReport(_handle, p, (uint)buffer.Length))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"HidD_GetInputReport failed (Win32 error {error}).", new Win32Exception(error));
            }
        }

        return buffer.Length;
    }

    public void Dispose() => _handle.Dispose();
}
