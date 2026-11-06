using System.Runtime.InteropServices;

namespace Airglow.Hardware.Hid;

public static unsafe class HidEnumerator
{
    public static IReadOnlyList<HidDeviceInfo> Enumerate()
    {
        var result = new List<HidDeviceInfo>();
        foreach (var path in GetInterfacePaths())
        {
            var info = TryRead(path);
            if (info is not null)
            {
                result.Add(info);
            }
        }

        return result;
    }

    private static List<string> GetInterfacePaths()
    {
        HidNative.HidD_GetHidGuid(out var guid);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (HidNative.CM_Get_Device_Interface_List_Size(
                    out var length, ref guid, null, HidNative.CmGetDeviceInterfaceListPresent) != HidNative.CrSuccess
                || length == 0)
            {
                return [];
            }

            var buffer = new char[length];
            uint status;
            fixed (char* p = buffer)
            {
                status = HidNative.CM_Get_Device_Interface_List(
                    ref guid, null, p, length, HidNative.CmGetDeviceInterfaceListPresent);
            }

            if (status == HidNative.CrBufferSmall)
            {
                continue;
            }

            if (status != HidNative.CrSuccess)
            {
                return [];
            }

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        return [];
    }

    private static HidDeviceInfo? TryRead(string path)
    {
        // Zero access rights: attributes and strings stay readable on interfaces another process holds exclusively.
        using var handle = HidNative.CreateFile(
            path, 0, HidNative.FileShareRead | HidNative.FileShareWrite, 0, HidNative.OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var attributes = new HidNative.HiddAttributes { Size = (uint)sizeof(HidNative.HiddAttributes) };
        if (!HidNative.HidD_GetAttributes(handle, ref attributes))
        {
            return null;
        }

        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed))
        {
            return null;
        }

        HidNative.HidpCaps caps = default;
        try
        {
            if (HidNative.HidP_GetCaps(preparsed, ref caps) != HidNative.HidpStatusSuccess)
            {
                return null;
            }
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }

        return new HidDeviceInfo(
            path,
            attributes.VendorId,
            attributes.ProductId,
            caps.UsagePage,
            caps.Usage,
            caps.InputReportByteLength,
            caps.OutputReportByteLength,
            caps.FeatureReportByteLength,
            ReadString(handle, product: true),
            ReadString(handle, product: false));
    }

    private static string? ReadString(Microsoft.Win32.SafeHandles.SafeFileHandle handle, bool product)
    {
        const int chars = 127;
        var buffer = stackalloc char[chars + 1];
        for (var i = 0; i <= chars; i++)
        {
            buffer[i] = '\0';
        }

        var ok = product
            ? HidNative.HidD_GetProductString(handle, buffer, chars * sizeof(char))
            : HidNative.HidD_GetSerialNumberString(handle, buffer, chars * sizeof(char));
        if (!ok)
        {
            return null;
        }

        var text = new string(buffer);
        return text.Length == 0 ? null : text;
    }
}
