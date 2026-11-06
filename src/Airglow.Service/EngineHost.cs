using Airglow.Core.Config;
using Airglow.Core.Engine;

namespace Airglow.Service;

public sealed class EngineHost(
    FanEngine engine,
    ConfigStore store,
    ConfigHolder holder,
    StateCache cache,
    ILogger<EngineHost> log) : BackgroundService
{
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
            if (holder.Problem is { } problem)
                state = state with { Faults = [.. state.Faults, problem] };
            cache.Publish(state);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Engine tick failed");
            if (cache.Current is { } previous)
                cache.Publish(previous with { Faults = [.. previous.Faults, $"Engine tick failed: {ex.Message}"] });
        }
    }
}
