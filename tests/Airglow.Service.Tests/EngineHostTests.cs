using Airglow.Core.Config;
using Airglow.Core.Engine;
using Airglow.Tests.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Airglow.Service.Tests;

public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

public sealed class EngineHostTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("airglow-host-").FullName;
    private readonly FakeTimeProvider _time = new();
    private readonly FakeFactory _gpu = FakeFactory.Gpu();
    private readonly FakeFactory _lianli = FakeFactory.LianLi();
    private readonly FakeConflicts _conflicts = new();
    private readonly ListLogger<EngineHost> _log = new();
    private readonly StateCache _cache = new();
    private readonly FanEngine _engine;
    private readonly EngineHost _host;

    public EngineHostTests()
    {
        _gpu.Backend.Temperatures["gpu.core"] = 52.65;
        _gpu.Backend.Temperatures["lhm/intelcpu/0/temperature/1"] = 40;
        _engine = new FanEngine([_gpu, _lianli], _conflicts, _time, NullLogger<FanEngine>.Instance);
        var store = new ConfigStore(Path.Combine(_directory, "config.json"));
        _host = new EngineHost(_engine, store, new ConfigHolder(), _cache, _time, _log);
        _host.LoadConfig();
        _host.RunTick();
    }

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private FakeFanChannel Fan(string id) => (id.StartsWith("gpu.") ? _gpu : _lianli).Backend.Fan(id);

    private static readonly string[] GpuFans = ["gpu.fan1", "gpu.fan2"];
    private static readonly string[] LianLiFans = ["lianli.g1", "lianli.g2", "lianli.g3", "lianli.g4"];

    private void Ticks(int seconds)
    {
        for (var second = 0; second < seconds; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _host.RunTick();
        }
    }

    private static int Count(FakeFanChannel fan, string call) => fan.Calls.Count(c => c == call);

    [Fact]
    public void Failing_ticks_hand_fans_back_once_per_streak_after_the_failsafe_delay()
    {
        Fan("lianli.g1").ThrowOnRpm = new InvalidOperationException("engine bug");

        Ticks(5);

        Assert.All(GpuFans, id => Assert.DoesNotContain("SetAuto()", Fan(id).Calls));
        Assert.All(LianLiFans, id => Assert.DoesNotContain("SetPercent(100)", Fan(id).Calls));

        Ticks(1);

        Assert.All(GpuFans, id => Assert.Equal("SetAuto()", Fan(id).Calls[^1]));
        Assert.All(LianLiFans, id => Assert.Equal("SetPercent(100)", Fan(id).Calls[^1]));

        Ticks(10);

        Assert.All(GpuFans, id => Assert.Equal(1, Count(Fan(id), "SetAuto()")));
        Assert.All(LianLiFans, id => Assert.Equal(1, Count(Fan(id), "SetPercent(100)")));

        Fan("lianli.g1").ThrowOnRpm = null;
        Ticks(1);
        Fan("lianli.g1").ThrowOnRpm = new InvalidOperationException("engine bug");
        Ticks(6);

        Assert.All(GpuFans, id => Assert.Equal(2, Count(Fan(id), "SetAuto()")));
    }

    [Fact]
    public void Emergency_hand_back_leaves_paused_fans_alone()
    {
        _conflicts.RunningProcesses.Add("FanControl");
        Ticks(1);
        Fan("lianli.g1").ThrowOnRpm = new InvalidOperationException("engine bug");

        Ticks(6);

        Assert.All(GpuFans, id => Assert.Equal(["SetPercent(50)"], Fan(id).Calls));
        Assert.All(LianLiFans, id => Assert.Equal(1, Count(Fan(id), "SetPercent(100)")));
    }

    [Fact]
    public void Failing_ticks_publish_the_last_good_state_with_one_fault()
    {
        var good = _cache.Current!;
        Fan("lianli.g1").ThrowOnRpm = new InvalidOperationException("engine bug");

        Ticks(3);

        var state = _cache.Current!;
        Assert.Equal(good.At, state.At);
        Assert.Equal(6, state.Fans.Count);
        Assert.Equal(["Engine tick failed: engine bug"], state.Faults);
    }

    [Fact]
    public void Tick_failures_are_logged_once_per_message_and_recovery_once()
    {
        Fan("lianli.g1").ThrowOnRpm = new InvalidOperationException("engine bug");
        Ticks(3);
        Fan("lianli.g1").ThrowOnRpm = new InvalidOperationException("other bug");
        Ticks(1);
        Fan("lianli.g1").ThrowOnRpm = new InvalidOperationException("engine bug");
        Ticks(1);
        Fan("lianli.g1").ThrowOnRpm = null;
        Ticks(3);

        Assert.Equal(2, _log.Entries.Count(e => e is { Level: LogLevel.Error, Message: "Engine tick failed" }));
        Assert.Single(_log.Entries, e => e.Message == "Engine tick recovered after 5 failures");
    }

    private EngineHost NewHost(List<string> failFast, bool tick)
    {
        var options = new WatchdogOptions(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5), failFast.Add);
        var host = new EngineHost(_engine, new ConfigStore(Path.Combine(_directory, "config.json")), new ConfigHolder(), _cache, _time, _log, options);
        host.StartWatchdogClock();
        if (tick)
            host.RunTick();
        return host;
    }

    [Fact]
    public void Watchdog_rearms_after_its_own_suspension()
    {
        var failFast = new List<string>();
        var host = NewHost(failFast, tick: true);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(host.CheckWatchdog());

        _time.Advance(TimeSpan.FromSeconds(120));
        Assert.False(host.CheckWatchdog());
        Assert.Single(_log.Entries, e => e is { Level: LogLevel.Information, Message: "Watchdog re-armed after a 120 s pause (sleep or resume)" });

        for (var second = 5; second < 15; second += 5)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            Assert.False(host.CheckWatchdog());
        }

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(host.CheckWatchdog());
        Assert.Single(failFast);
    }

    [Fact]
    public void Watchdog_watches_the_first_tick()
    {
        var failFast = new List<string>();
        var host = NewHost(failFast, tick: false);
        for (var second = 5; second < 60; second += 5)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            Assert.False(host.CheckWatchdog());
        }

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(host.CheckWatchdog());
        Assert.Single(failFast);
    }

    [Fact]
    public void Watchdog_accepts_a_first_tick_that_completes_in_time()
    {
        var failFast = new List<string>();
        var host = NewHost(failFast, tick: false);
        for (var second = 5; second <= 30; second += 5)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            Assert.False(host.CheckWatchdog());
        }

        host.RunTick();
        for (var second = 5; second <= 60; second += 5)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            host.RunTick();
            Assert.False(host.CheckWatchdog());
        }

        Assert.Empty(failFast);
    }
}
