using Airglow.Core.Config;
using Airglow.Core.Engine;

namespace Airglow.Service;

public sealed class StateCache
{
    private volatile EngineState? _state;

    public EngineState? Current => _state;

    public void Publish(EngineState state) => _state = state;
}

public sealed class ConfigHolder
{
    private volatile AppConfig? _config;
    private volatile string? _problem;

    public AppConfig? Current => _config;

    public string? Problem => _problem;

    public void Set(AppConfig config, string? problem)
    {
        _config = config;
        _problem = problem;
    }
}
