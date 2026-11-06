using Airglow.Core.Engine;

namespace Airglow.App;

public enum EngineHealth { NotRunning, Running, Faults, Stalled }

public sealed class LiveState : IDisposable
{
    private const int DefaultTickMilliseconds = 1000;

    private readonly IServiceClient _client;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public LiveState(IServiceClient client, TimeProvider time)
    {
        _client = client;
        _time = time;
        _loop = Task.Run(PollAsync);
    }

    private volatile Snapshot _snapshot = new(null, false, null);

    public EngineState? State => _snapshot.State;

    public bool Connected => _snapshot.Connected;

    public EngineHealth Health => _snapshot switch
    {
        { Connected: false } or { State: null } => EngineHealth.NotRunning,
        { State: { } state, TickMilliseconds: var tick } when IsStale(state, tick) => EngineHealth.Stalled,
        { State.Faults.Count: > 0 } => EngineHealth.Faults,
        _ => EngineHealth.Running,
    };

    public event Action? Changed;

    internal async Task RefreshAsync()
    {
        try
        {
            var state = await _client.GetStateAsync(_cts.Token);
            var previous = _snapshot;
            var tick = previous.TickMilliseconds;
            if (!previous.Connected || IsStale(state, tick))
                tick = await TryGetTickAsync() ?? tick;
            _snapshot = new Snapshot(state, true, tick);
        }
        catch (Exception) when (!_cts.IsCancellationRequested)
        {
            _snapshot = _snapshot with { Connected = false };
        }

        if (_cts.IsCancellationRequested)
            return;
        foreach (var handler in (Changed?.GetInvocationList() ?? []).Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LiveState subscriber failed: {ex}");
            }
        }
    }

    private bool IsStale(EngineState state, int? tickMilliseconds) =>
        _time.GetUtcNow() - state.At > TimeSpan.FromMilliseconds(3 * (tickMilliseconds ?? DefaultTickMilliseconds));

    private async Task<int?> TryGetTickAsync()
    {
        try
        {
            return (await _client.GetConfigAsync(_cts.Token)).Engine.TickMilliseconds;
        }
        catch (Exception) when (!_cts.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task PollAsync()
    {
        try
        {
            await RefreshAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
            while (await timer.WaitForNextTickAsync(_cts.Token))
                await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        if (_loop.IsCompleted)
            _cts.Dispose();
    }

    private sealed record Snapshot(EngineState? State, bool Connected, int? TickMilliseconds);
}
