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

    public EngineState? State { get; private set; }

    public bool Connected { get; private set; }

    public event Action? Changed;

    internal async Task RefreshAsync()
    {
        try
        {
            State = await _client.GetStateAsync(_cts.Token);
            Connected = true;
        }
        catch (Exception) when (!_cts.IsCancellationRequested)
        {
            Connected = false;
        }

        if (!_cts.IsCancellationRequested)
            Changed?.Invoke();
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

        _cts.Dispose();
    }
}
