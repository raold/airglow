using Microsoft.Win32;

namespace Airglow.App;

public interface IRunKeys
{
    string? GetMachine(string name);

    string? GetUser(string name);

    void SetUser(string name, string? value);
}

internal sealed class WindowsRunKeys : IRunKeys
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetMachine(string name) => Read(Registry.LocalMachine, name);

    public string? GetUser(string name) => Read(Registry.CurrentUser, name);

    public void SetUser(string name, string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (value is null)
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, value);
    }

    private static string? Read(RegistryKey hive, string name)
    {
        using var key = hive.OpenSubKey(RunKey);
        return key?.GetValue(name)?.ToString();
    }
}

public static class Autostart
{
    private const string ValueName = "airglow";

    public static IRunKeys Keys { get; set; } = new WindowsRunKeys();

    public static bool IsEnabled => Keys.GetUser(ValueName) is not null;

    public static bool IsManagedByInstaller => Keys.GetMachine(ValueName) is not null;

    public static void Set(bool enabled) =>
        Keys.SetUser(ValueName, enabled ? $"\"{Environment.ProcessPath}\" --tray" : null);
}
