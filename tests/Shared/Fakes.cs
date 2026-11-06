using Airglow.Core.Config;
using Airglow.Core.Hardware;

namespace Airglow.Tests.Shared;

public sealed class FakeFanChannel(string id, string name, bool supportsAuto) : IFanChannel
{
    public string Id => id;

    public string Name => name;

    public bool SupportsAuto => supportsAuto;

    public int? Rpm { get; set; }

    public List<string> Calls { get; } = [];

    public Exception? ThrowOnWrite { get; set; }

    public void SetPercent(int percent) => Record($"SetPercent({percent})");

    public void SetAuto() => Record("SetAuto()");

    private void Record(string call)
    {
        if (ThrowOnWrite is { } error)
            throw error;
        Calls.Add(call);
    }
}

public sealed class FakeBackend(string name, IReadOnlyList<FakeFanChannel> fans, IReadOnlyList<SensorInfo> sensors)
    : IHardwareBackend
{
    public string Name => name;

    public IReadOnlyList<FakeFanChannel> Fans => fans;

    IReadOnlyList<IFanChannel> IHardwareBackend.Fans => fans;

    public IReadOnlyList<SensorInfo> Sensors => sensors;

    public Dictionary<string, double> Temperatures { get; } = [];

    IReadOnlyDictionary<string, double> IHardwareBackend.Temperatures => Temperatures;

    public Exception? ThrowOnUpdate { get; set; }

    public int Updates { get; private set; }

    public bool Disposed { get; private set; }

    public FakeFanChannel Fan(string id) => fans.Single(f => f.Id == id);

    public void Update()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (ThrowOnUpdate is { } error)
            throw error;
        Updates++;
    }

    public void Dispose() => Disposed = true;

    public void Reopen() => Disposed = false;
}

public sealed class FakeFactory(FakeBackend backend, params string[] conflictingProcesses) : IBackendFactory
{
    public string Name => backend.Name;

    public IReadOnlyList<string> ConflictingProcesses => conflictingProcesses;

    public FakeBackend Backend => backend;

    public int Creates { get; private set; }

    public bool Present { get; set; } = true;

    public Exception? ThrowOnCreate { get; set; }

    public IHardwareBackend? TryCreate(AppConfig config)
    {
        Creates++;
        if (ThrowOnCreate is { } error)
            throw error;
        if (!Present)
            return null;
        backend.Reopen();
        return backend;
    }

    public static FakeFactory Gpu() => new(
        new FakeBackend(
            "Sensors and GPU",
            [new("gpu.fan1", "GPU fan 1", supportsAuto: true), new("gpu.fan2", "GPU fan 2", supportsAuto: true)],
            [new("gpu.core", "GPU Core", "GPU"), new("cpu.package", "CPU Package", "CPU")]),
        "FanControl");

    public static FakeFactory LianLi() => new(
        new FakeBackend(
            "Lian Li UNI HUB AL",
            Enumerable.Range(1, 4).Select(n => new FakeFanChannel($"lianli.g{n}", $"Group {n}", supportsAuto: false)).ToList(),
            []),
        "L-Connect 3",
        "L-Connect-Service");
}

public sealed class FakeConflicts : IConflictDetector
{
    public HashSet<string> RunningProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> Running(IEnumerable<string> processNames) =>
        processNames.Where(RunningProcesses.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
