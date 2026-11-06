using System.Runtime.InteropServices;

namespace Airglow.App;

// Lets Windows Installer's Restart Manager start the tray app again after it closes it for an upgrade.
internal static partial class RestartRegistration
{
    internal const string CommandLine = "--tray";

    private const uint RestartNoCrash = 1;
    private const uint RestartNoHang = 2;
    private const uint RestartNoReboot = 8;

    internal const uint Flags = RestartNoCrash | RestartNoHang | RestartNoReboot;

    public static void Register()
    {
        try
        {
            var result = RegisterApplicationRestart(CommandLine, Flags);
            if (result != 0)
                System.Diagnostics.Debug.WriteLine($"RegisterApplicationRestart failed: 0x{result:X8}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RegisterApplicationRestart failed: {ex}");
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string commandLine, uint flags);
}
