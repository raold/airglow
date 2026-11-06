using Airglow.Core.Engine;

namespace Airglow.App;

public sealed class LiveState : IDisposable
{
    private readonly IServiceClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public LiveState(IServiceClient client, TimeProvider time)
    {
        _client = client;
        _loop = Task.Run(() => PollAsync(time));
    }

    private volatile Snapshot _snapshot = new(null, false);

    public EngineState? State => _snapshot.State;

    public bool Connected => _snapshot.Connected;

    public event Action? Changed;

    internal async Task RefreshAsync()
    {
        try
        {
            _snapshot = new Snapshot(await _client.GetStateAsync(_cts.Token), true);
        }
        catch (Exception) when (!_cts.IsCancellationRequested)
        {
            _snapshot = new Snapshot(_snapshot.State, false);
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

    private async Task PollAsync(TimeProvider time)
    {
        try
        {
            await RefreshAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
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

    private sealed record Snapshot(EngineState? State, bool Connected);
}
