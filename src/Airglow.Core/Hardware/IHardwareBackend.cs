using Airglow.Core.Config;

namespace Airglow.Core.Hardware;

public sealed record SensorInfo(string Id, string Name, string Group);

public interface IFanChannel
{
    string Id { get; }
    string Name { get; }
    bool SupportsAuto { get; }
    int? Rpm { get; }
    void SetPercent(int percent);
    void SetAuto();
}

public interface IHardwareBackend : IDisposable
{
    string Name { get; }
    IReadOnlyList<IFanChannel> Fans { get; }
    IReadOnlyList<SensorInfo> Sensors { get; }
    IReadOnlyDictionary<string, double> Temperatures { get; }
    void Update();
}

public interface IBackendFactory
{
    string Name { get; }
    IReadOnlyList<string> ConflictingProcesses { get; }
    IHardwareBackend? TryCreate(AppConfig config);
}

public interface IConflictDetector
{
    IReadOnlySet<string> Running(IEnumerable<string> processNames);
}
