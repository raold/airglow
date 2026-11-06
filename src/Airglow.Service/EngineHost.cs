using Airglow.Core.Config;
using Airglow.Core.Engine;

namespace Airglow.Service;

public sealed class EngineHost(
    FanEngine engine,
    ConfigStore store,
    ConfigHolder holder,
    StateCache cache,
    TimeProvider time,
    ILogger<EngineHost> log) : BackgroundService
{
    private EngineState? _lastGood;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var loaded = store.Load();
            holder.Set(loaded.Config, loaded.Problem);
            engine.ApplyConfig(loaded.Config);
            if (loaded.Problem is { } problem)
                log.LogWarning("{Problem}", problem);

            while (!stoppingToken.IsCancellationRequested)
            {
                Tick();
                var delay = holder.Current?.Engine.TickMilliseconds ?? 1000;
                await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            engine.Shutdown();
        }
    }

    private void Tick()
    {
        try
        {
            var state = engine.Tick();
            _lastGood = state;
            cache.Publish(WithConfigProblem(state, null));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Engine tick failed");
            var basis = _lastGood ?? new EngineState(time.GetUtcNow(), [], [], []);
            cache.Publish(WithConfigProblem(basis, $"Engine tick failed: {ex.Message}"));
        }
    }

    private EngineState WithConfigProblem(EngineState state, string? tickFault)
    {
        var faults = state.Faults.ToList();
        if (holder.Problem is { } problem)
            faults.Add(problem);
        if (tickFault is not null)
            faults.Add(tickFault);
        return state with { Faults = faults };
    }
}
