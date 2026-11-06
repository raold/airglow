namespace Airglow.App;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread? _listener;

    public SingleInstance(string name)
    {
        _mutex = new Mutex(true, $@"Local\{name}", out var createdNew);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}-show");
        IsFirst = createdNew;

        if (IsFirst)
        {
            _listener = new Thread(Listen) { IsBackground = true, Name = "airglow single instance" };
            _listener.Start();
        }
    }

    public bool IsFirst { get; }

    public event Action? ShowRequested;

    public void SignalFirst() => _show.Set();

    private void Listen()
    {
        WaitHandle[] handles = [_show, _stop];
        while (WaitHandle.WaitAny(handles) == 0)
        {
            try
            {
                ShowRequested?.Invoke();
            }
            catch (Exception)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _listener?.Join(TimeSpan.FromSeconds(1));
        if (IsFirst)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
        _show.Dispose();
        _stop.Dispose();
    }
}
