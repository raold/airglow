namespace Airglow.Core;

public static class AirglowPaths
{
    public const string PipeName = "airglow-v1";

    public static string ConfigFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Airglow", "config.json");
}
