using Airglow.Core.Config;
using Airglow.Core.Hardware;

namespace Airglow.Hardware.Monitoring;

public sealed class LhmBackendFactory : IBackendFactory
{
    public string Name => "Sensors and GPU";

    public IReadOnlyList<string> ConflictingProcesses { get; } = ["FanControl"];

    public IHardwareBackend? TryCreate(AppConfig config) => new LhmBackend();
}
