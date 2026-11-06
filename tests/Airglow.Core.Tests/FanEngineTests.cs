using Airglow.Core.Config;
using Airglow.Core.Engine;
using Airglow.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Airglow.Core.Tests;

public class FanEngineTests
{
    private static readonly string[] GpuFans = ["gpu.fan1", "gpu.fan2"];
    private static readonly string[] LianLiFans = ["lianli.g1", "lianli.g2", "lianli.g3", "lianli.g4"];

    private readonly FakeTimeProvider _time = new();
    private readonly FakeFactory _gpu = FakeFactory.Gpu();
    private readonly FakeFactory _lianli = FakeFactory.LianLi();
    private readonly FakeConflicts _conflicts = new();
    private readonly AppConfig _config = AppConfig.CreateDefault();
    private readonly FanEngine _engine;

    public FanEngineTests()
    {
        _engine = NewEngine();
        _engine.ApplyConfig(_config);
        GpuCore = 52.65;
        _gpu.Backend.Temperatures["cpu.package"] = 40;
    }

    private double GpuCore { set => _gpu.Backend.Temperatures["gpu.core"] = value; }

    private FanEngine NewEngine() => new([_gpu, _lianli], _conflicts, _time, NullLogger<FanEngine>.Instance);

    private FakeFanChannel Fan(string id) => (id.StartsWith("gpu.") ? _gpu : _lianli).Backend.Fan(id);

    private static FanReading Reading(EngineState state, string id) => state.Fans.Single(f => f.Id == id);

    private EngineState Next()
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        return _engine.Tick();
    }

    [Fact]
    public void Tick_commands_curve_output()
    {
        var reading = Reading(_engine.Tick(), "gpu.fan1");

        Assert.Equal("SetPercent(50)", Fan("gpu.fan1").Calls[^1]);
        Assert.Equal(FanMode.Curve, reading.Mode);
        Assert.Equal(50, reading.CommandedPercent);
    }

    [Fact]
    public void Hands_gpu_back_to_auto_at_zero()
    {
        GpuCore = 30;

        var reading = Reading(_engine.Tick(), "gpu.fan1");

        Assert.Equal(["SetAuto()"], Fan("gpu.fan1").Calls);
        Assert.Equal(FanMode.Auto, reading.Mode);
        Assert.Equal(0, reading.CommandedPercent);
    }

    [Fact]
    public void Missing_sensor_holds_then_failsafe_after_5s()
    {
        _engine.Tick();
        _gpu.Backend.Temperatures.Remove("gpu.core");

        for (var second = 1; second <= 4; second++)
        {
            var held = Reading(Next(), "gpu.fan1");
            Assert.Equal(FanMode.Curve, held.Mode);
            Assert.Equal(50, held.CommandedPercent);
        }
        Assert.Equal(["SetPercent(50)"], Fan("gpu.fan1").Calls);

        var failsafe = Reading(Next(), "gpu.fan1");
        Assert.Equal("SetPercent(100)", Fan("gpu.fan1").Calls[^1]);
        Assert.Equal(FanMode.Failsafe, failsafe.Mode);
        Assert.Equal(100, failsafe.CommandedPercent);

        GpuCore = 52.65;
        Assert.Equal(FanMode.Curve, Reading(Next(), "gpu.fan1").Mode);
    }

    [Fact]
    public void Never_seen_sensor_goes_to_failsafe_at_once()
    {
        _gpu.Backend.Temperatures.Remove("gpu.core");

        var reading = Reading(_engine.Tick(), "gpu.fan1");

        Assert.Equal(["SetPercent(100)"], Fan("gpu.fan1").Calls);
        Assert.Equal(FanMode.Failsafe, reading.Mode);
    }

    [Fact]
    public void Paused_when_conflicting_process_running()
    {
        _conflicts.RunningProcesses.Add("FanControl");

        var state = _engine.Tick();

        foreach (var id in GpuFans)
        {
            Assert.Empty(Fan(id).Calls);
            Assert.Equal(FanMode.Paused, Reading(state, id).Mode);
            Assert.Equal("FanControl is running", Reading(state, id).Note);
        }
        foreach (var id in LianLiFans)
        {
            Assert.NotEmpty(Fan(id).Calls);
            Assert.Equal(FanMode.Curve, Reading(state, id).Mode);
        }
    }

    [Fact]
    public void Resumed_fan_is_written_at_once()
    {
        _engine.Tick();
        _conflicts.RunningProcesses.Add("FanControl");
        Next();
        Assert.Equal(["SetPercent(50)"], Fan("gpu.fan1").Calls);

        _conflicts.RunningProcesses.Clear();
        var reading = Reading(Next(), "gpu.fan1");

        Assert.Equal(["SetPercent(50)", "SetPercent(50)"], Fan("gpu.fan1").Calls);
        Assert.Equal(FanMode.Curve, reading.Mode);
    }

    [Fact]
    public void Backend_fault_is_isolated_and_retried()
    {
        _lianli.Backend.ThrowOnUpdate = new IOException("gone");

        var state = _engine.Tick();

        Assert.Contains("Lian Li UNI HUB AL: gone", state.Faults);
        foreach (var id in LianLiFans)
        {
            Assert.Empty(Fan(id).Calls);
            Assert.Equal(FanMode.Unavailable, Reading(state, id).Mode);
            Assert.Equal("Lian Li UNI HUB AL: gone", Reading(state, id).Note);
        }
        Assert.Equal("SetPercent(50)", Fan("gpu.fan1").Calls[^1]);
        Assert.True(_lianli.Backend.Disposed);

        for (var second = 1; second <= 4; second++)
            Next();
        Assert.Equal(1, _lianli.Creates);

        _lianli.Backend.ThrowOnUpdate = null;
        var recovered = Next();

        Assert.Equal(2, _lianli.Creates);
        Assert.Empty(recovered.Faults);
        foreach (var id in LianLiFans)
        {
            Assert.NotEmpty(Fan(id).Calls);
            Assert.Equal(FanMode.Curve, Reading(recovered, id).Mode);
        }
    }

    [Fact]
    public void Write_failure_faults_the_backend_and_retries()
    {
        Fan("gpu.fan1").ThrowOnWrite = new IOException("write failed");

        var state = _engine.Tick();

        Assert.Contains("Sensors and GPU: write failed", state.Faults);
        Assert.True(_gpu.Backend.Disposed);
        Assert.Empty(Fan("gpu.fan2").Calls);
        foreach (var id in GpuFans)
            Assert.Equal(FanMode.Unavailable, Reading(state, id).Mode);
        Assert.DoesNotContain(state.Temperatures, t => t.Id == "gpu.core");

        Fan("gpu.fan1").ThrowOnWrite = null;
        for (var second = 1; second <= 4; second++)
            Next();
        Assert.Equal(1, _gpu.Creates);

        var recovered = Next();

        Assert.Equal(2, _gpu.Creates);
        Assert.Empty(recovered.Faults);
        Assert.Equal(["SetPercent(50)"], Fan("gpu.fan1").Calls);
        Assert.Equal(["SetPercent(50)"], Fan("gpu.fan2").Calls);
    }

    [Fact]
    public void Create_failure_is_reported_and_retried()
    {
        _lianli.ThrowOnCreate = new IOException("access denied");

        var state = _engine.Tick();

        Assert.Equal(["Lian Li UNI HUB AL: access denied"], state.Faults);
        Assert.All(LianLiFans, id => Assert.Equal(FanMode.Unavailable, Reading(state, id).Mode));
        Assert.Equal("SetPercent(50)", Fan("gpu.fan1").Calls[^1]);

        _lianli.ThrowOnCreate = null;
        _lianli.Present = false;
        for (var second = 1; second <= 5; second++)
            state = Next();

        Assert.Equal(2, _lianli.Creates);
        Assert.Equal(["Lian Li UNI HUB AL: access denied"], state.Faults);

        _lianli.Present = true;
        for (var second = 1; second <= 5; second++)
            state = Next();

        Assert.Equal(3, _lianli.Creates);
        Assert.Empty(state.Faults);
        Assert.All(LianLiFans, id => Assert.Equal(FanMode.Curve, Reading(state, id).Mode));
    }

    [Fact]
    public void Absent_device_is_not_found()
    {
        _lianli.Present = false;

        var state = _engine.Tick();

        Assert.Empty(state.Faults);
        Assert.All(LianLiFans, id => Assert.Equal("not found", Reading(state, id).Note));
        Assert.All(LianLiFans, id => Assert.Equal(FanMode.Unavailable, Reading(state, id).Mode));
    }

    [Fact]
    public void Resends_unchanged_command_every_10s()
    {
        _engine.Tick();
        for (var second = 1; second <= 9; second++)
            Next();
        Assert.Equal(["SetPercent(50)"], Fan("gpu.fan1").Calls);

        Next();

        Assert.Equal(["SetPercent(50)", "SetPercent(50)"], Fan("gpu.fan1").Calls);
    }

    [Fact]
    public void Manual_override_bypasses_curve_and_step()
    {
        _engine.Tick();
        _engine.SetManual("gpu.fan1", 70);

        var manual = Reading(Next(), "gpu.fan1");

        Assert.Equal("SetPercent(70)", Fan("gpu.fan1").Calls[^1]);
        Assert.Equal(FanMode.Manual, manual.Mode);
        Assert.Equal(70, manual.CommandedPercent);

        _engine.SetManual("gpu.fan1", null);
        var curve = Reading(Next(), "gpu.fan1");

        Assert.Equal(FanMode.Curve, curve.Mode);
        Assert.Equal("SetPercent(62)", Fan("gpu.fan1").Calls[^1]);
    }

    [Fact]
    public void Manual_override_survives_apply_config()
    {
        _engine.SetManual("gpu.fan1", 70);
        _engine.ApplyConfig(AppConfig.CreateDefault());

        Assert.Equal(FanMode.Manual, Reading(_engine.Tick(), "gpu.fan1").Mode);
        Assert.Equal("SetPercent(70)", Fan("gpu.fan1").Calls[^1]);
    }

    [Fact]
    public void Set_manual_on_unknown_fan_throws() =>
        Assert.Throws<KeyNotFoundException>(() => _engine.SetManual("gpu.fan9", 50));

    [Fact]
    public void Set_manual_out_of_range_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _engine.SetManual("gpu.fan1", 101));

    [Fact]
    public void Tick_before_apply_config_throws() =>
        Assert.Throws<InvalidOperationException>(() => NewEngine().Tick());

    [Fact]
    public void Tick_after_shutdown_throws_without_touching_hardware()
    {
        _engine.Tick();
        _engine.Shutdown();

        Assert.Throws<ObjectDisposedException>(() => Next());

        Assert.Equal(1, _gpu.Creates);
        Assert.True(_gpu.Backend.Disposed);
        Assert.Equal("SetAuto()", Fan("gpu.fan1").Calls[^1]);
    }

    [Fact]
    public void Shutdown_sets_auto_for_gpu_and_shutdown_percent_for_lianli()
    {
        _engine.Tick();

        _engine.Shutdown();

        Assert.All(GpuFans, id => Assert.Equal("SetAuto()", Fan(id).Calls[^1]));
        Assert.All(LianLiFans, id => Assert.Equal("SetPercent(60)", Fan(id).Calls[^1]));
        Assert.True(_gpu.Backend.Disposed);
        Assert.True(_lianli.Backend.Disposed);

        _engine.Shutdown();
        _engine.Dispose();

        Assert.All(GpuFans, id => Assert.Equal(2, Fan(id).Calls.Count));
        Assert.All(LianLiFans, id => Assert.Equal(2, Fan(id).Calls.Count));
    }

    [Fact]
    public void Shutdown_leaves_paused_fans_alone()
    {
        _engine.Tick();
        _conflicts.RunningProcesses.Add("FanControl");

        _engine.Shutdown();

        Assert.All(GpuFans, id => Assert.Equal(["SetPercent(50)"], Fan(id).Calls));
        Assert.All(LianLiFans, id => Assert.Equal("SetPercent(60)", Fan(id).Calls[^1]));
        Assert.True(_gpu.Backend.Disposed);
    }

    [Fact]
    public void Apply_config_keeps_last_commands()
    {
        _engine.Tick();
        _engine.ApplyConfig(_config);
        GpuCore = 80;

        Next();

        Assert.Equal(["SetPercent(50)", "SetPercent(58)"], Fan("gpu.fan1").Calls);
    }
}
