using Airglow.Core.Config;
using Airglow.Core.Engine;

namespace Airglow.Service;

public sealed record WatchdogOptions(TimeSpan HangAfter, TimeSpan CheckEvery, Action<string> FailFast)
{
    public static WatchdogOptions Default { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5), Environment.FailFast);
}

public sealed class EngineHost(
    FanEngine engine,
    ConfigStore store,
    ConfigHolder holder,
    StateCache cache,
    TimeProvider time,
    ILogger<EngineHost> log,
    WatchdogOptions? watchdog = null) : BackgroundService
{
    private const long NoTick = long.MinValue;

    private readonly WatchdogOptions _watchdog = watchdog ?? WatchdogOptions.Default;
    private readonly HashSet<string> _loggedFailures = [];
    private EngineState? _lastGood;
    private EngineState? _minimal;
    private int _failures;
    private long _failingSince;
    private bool _handedBack;
    private long _lastTick = NoTick;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stopWatching = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task? watching = null;
        try
        {
            LoadConfig();
            watching = Task.Run(() => WatchAsync(stopWatching.Token), CancellationToken.None);

            while (!stoppingToken.IsCancellationRequested)
            {
                RunTick();
                var delay = holder.Current?.Engine.TickMilliseconds ?? 1000;
                await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            stopWatching.Cancel();
            engine.Shutdown();
            if (watching is not null)
                await watching;
        }
    }

    internal void LoadConfig()
    {
        var loaded = store.Load();
        holder.Set(loaded.Config, loaded.Problem);
        engine.ApplyConfig(loaded.Config);
        if (loaded.Problem is { } problem)
            log.LogWarning("{Problem}", problem);
    }

    internal void RunTick()
    {
        try
        {
            var state = engine.Tick();
            if (_failures > 0)
                log.LogInformation("Engine tick recovered after {Failures} failures", _failures);
            _failures = 0;
            _handedBack = false;
            _loggedFailures.Clear();
            _lastGood = state;
            cache.Publish(WithConfigProblem(state, null));
        }
        catch (Exception ex)
        {
            OnTickFailed(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _lastTick, time.GetTimestamp());
        }
    }

    private void OnTickFailed(Exception error)
    {
        if (_failures++ == 0)
            _failingSince = time.GetTimestamp();
        if (_loggedFailures.Add(error.Message))
            log.LogError(error, "Engine tick failed");

        var failing = time.GetElapsedTime(_failingSince);
        if (!_handedBack && failing >= TimeSpan.FromSeconds(holder.Current?.Engine.FailsafeAfterSeconds ?? 5))
        {
            _handedBack = true;
            log.LogCritical("Engine ticks have failed for {Seconds:0.#} s; handing the fans back", failing.TotalSeconds);
            try
            {
                engine.EmergencyHandBack();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Emergency hand-back failed");
            }
        }

        var basis = _lastGood ?? (_minimal ??= new EngineState(time.GetUtcNow(), [], [], []));
        cache.Publish(WithConfigProblem(basis, $"Engine tick failed: {error.Message}"));
    }

    private async Task WatchAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(_watchdog.CheckEvery, time);
            while (await timer.WaitForNextTickAsync(token))
            {
                var last = Interlocked.Read(ref _lastTick);
                if (last == NoTick)
                    continue;
                var idle = time.GetElapsedTime(last);
                if (idle < _watchdog.HangAfter)
                    continue;

                log.LogCritical("No engine tick has completed for {Seconds:0.#} s; failing fast so the service restarts", idle.TotalSeconds);
                _watchdog.FailFast("airglow engine tick hung");
                return;
            }
        }
        catch (OperationCanceledException)
        {
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
