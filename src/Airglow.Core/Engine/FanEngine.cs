using Airglow.Core.Config;
using Airglow.Core.Curves;
using Airglow.Core.Fans;
using Airglow.Core.Hardware;
using Microsoft.Extensions.Logging;

namespace Airglow.Core.Engine;

public sealed class FanEngine(
    IReadOnlyList<IBackendFactory> factories,
    IConflictDetector conflicts,
    TimeProvider time,
    ILogger<FanEngine> log) : IDisposable
{
    public const int ResendSeconds = 10;
    public const int RetrySeconds = 5;

    public const int SensorMissingSeconds = 30;
    public const int SensorReopenSeconds = 60;

    private static readonly TimeSpan Resend = TimeSpan.FromSeconds(ResendSeconds);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(RetrySeconds);
    private static readonly TimeSpan SensorMissing = TimeSpan.FromSeconds(SensorMissingSeconds);
    private static readonly TimeSpan SensorReopen = TimeSpan.FromSeconds(SensorReopenSeconds);

    private readonly object _lock = new();
    private readonly List<Slot> _slots = factories.Select(f => new Slot(f)).ToList();
    private readonly Dictionary<string, Slot> _owners = [];
    private readonly Dictionary<string, int> _manual = [];
    private readonly Dictionary<string, long> _inputSeen = [];
    private readonly HashSet<string> _loggedFaults = [];
    private readonly Dictionary<string, long> _sensorSeen = [];
    private long? _lastSensorReopen;
    private IReadOnlyDictionary<string, CurveResult> _lastResults = new Dictionary<string, CurveResult>();
    private Dictionary<string, FanOutputShaper> _shapers = [];
    private AppConfig? _config;
    private CurveSet? _curves;
    private long _firstApplied;
    private bool _stopped;

    public IReadOnlyList<SensorInfo> Sensors
    {
        get
        {
            lock (_lock)
                return LiveSensors();
        }
    }

    private IEnumerable<Slot> Live => _slots.Where(s => s.Backend is not null);

    public void ApplyConfig(AppConfig config)
    {
        lock (_lock)
        {
            var curves = new CurveSet(config.Curves, time);
            var shapers = new Dictionary<string, FanOutputShaper>();
            foreach (var fan in config.Fans)
            {
                var shaper = new FanOutputShaper(fan);
                if (_shapers.TryGetValue(fan.FanId, out var old) && old.Last is { } last)
                    shaper.Reset(last);
                shapers[fan.FanId] = shaper;
            }

            foreach (var id in _manual.Keys.Where(id => !shapers.ContainsKey(id)).ToList())
                _manual.Remove(id);
            if (_config is null)
                _firstApplied = time.GetTimestamp();
            foreach (var sensor in CurveSensors(config))
                _sensorSeen.TryAdd(sensor, time.GetTimestamp());
            var groupsChanged = _config is { } previous && !previous.LianLiGroups.SequenceEqual(config.LianLiGroups);
            _curves = curves;
            _shapers = shapers;
            _config = config;
            if (groupsChanged)
                foreach (var slot in _slots.Where(s => s.Backend is not null))
                    Drop(slot);
        }
    }

    public void SetManual(string fanId, int? percent)
    {
        if (percent is { } p)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(p, nameof(percent));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(p, 100, nameof(percent));
        }

        lock (_lock)
        {
            if (_config is null || !_config.Fans.Any(f => f.FanId == fanId))
                throw new KeyNotFoundException($"Fan '{fanId}' is not configured.");
            if (percent is { } value)
                _manual[fanId] = value;
            else
                _manual.Remove(fanId);
        }
    }

    public EngineState Tick()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_config is not { } config || _curves is not { } curves)
                throw new InvalidOperationException("ApplyConfig must be called before Tick.");

            var now = time.GetTimestamp();
            var faults = new List<string>();
            try
            {
                CheckConflicts();
            }
            catch (Exception ex)
            {
                var fault = $"Conflict check failed: {ex.Message}";
                Report(faults, fault, ex);
                foreach (var slot in _slots)
                {
                    slot.Pause = fault;
                    slot.Writes.Clear();
                }
            }

            foreach (var slot in _slots)
            {
                if (slot.Pause is null)
                    slot.ClosedForPause = false;
                if (!slot.ClosedForPause)
                    Connect(slot, config, now);
                if (slot.Pause is not null && slot.Backend is { } backend)
                    CloseIfSensorless(slot, backend, now);
            }
            foreach (var slot in _slots)
                Refresh(slot, now);

            var temperatures = new Dictionary<string, double>();
            foreach (var slot in Live)
                foreach (var (id, celsius) in slot.Temperatures)
                    temperatures.TryAdd(id, celsius);

            IReadOnlyDictionary<string, CurveResult> results;
            try
            {
                results = curves.Evaluate(temperatures);
                _lastResults = results;
            }
            catch (Exception ex)
            {
                Report(faults, $"Curve evaluation failed: {ex.Message}", ex);
                results = _lastResults.ToDictionary(r => r.Key, r => r.Value with { InputMissing = true });
            }

            var plans = new List<Plan>();
            foreach (var fan in config.Fans)
            {
                try
                {
                    plans.Add(Decide(fan, results, config.Engine, now));
                }
                catch (Exception ex)
                {
                    var fault = $"{fan.Name}: {ex.Message}";
                    Report(faults, fault, ex);
                    Forget(fan);
                    plans.Add(new Plan(fan, null, null, null, FanMode.Unavailable, null, false, fault));
                }
            }
            foreach (var plan in plans)
            {
                try
                {
                    Send(plan, now);
                }
                catch (Exception ex)
                {
                    Report(faults, $"{plan.Fan.Name}: {ex.Message}", ex);
                }
            }

            var configured = config.Fans.Select(f => f.FanId).ToHashSet();
            foreach (var slot in Live.Where(s => s.Pause is null).ToList())
                HandBackRemoved(slot, configured, config.Engine.ShutdownPercent, now);

            foreach (var slot in Live.Where(s => s.LoggedFault is not null))
            {
                log.LogInformation("{Backend} recovered", slot.Factory.Name);
                slot.LoggedFault = null;
            }
            foreach (var cleared in _loggedFaults.Where(f => !faults.Contains(f)).ToList())
            {
                log.LogInformation("Cleared: {Fault}", cleared);
                _loggedFaults.Remove(cleared);
            }

            var state = Snapshot(temperatures, plans, faults);
            ReopenIfSensorsMissing(config, temperatures, now);
            return state;
        }
    }

    public void EmergencyHandBack()
    {
        lock (_lock)
        {
            if (_stopped || _config is not { } config)
                return;

            foreach (var slot in Live.Where(s => s.Pause is null))
            {
                foreach (var channel in slot.Channels.Values)
                    HandBack(channel, config.Engine.FailsafePercent);
                slot.Writes.Clear();
            }
        }
    }

    public void Shutdown()
    {
        lock (_lock)
        {
            if (_stopped)
                return;
            _stopped = true;

            try
            {
                CheckConflicts();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Checking for conflicting apps failed; using the last result");
            }

            foreach (var slot in _slots)
            {
                if (slot.Backend is not { } backend)
                    continue;
                if (slot.Pause is null)
                    foreach (var channel in slot.Channels.Values)
                        HandBack(channel, _config!.Engine.ShutdownPercent);
                slot.Backend = null;
                Close(slot, backend);
            }
        }
    }

    public void Dispose() => Shutdown();

    private void Connect(Slot slot, AppConfig config, long now)
    {
        if (slot.Backend is not null || (slot.RetryFrom is { } from && time.GetElapsedTime(from, now) < Retry))
            return;

        try
        {
            slot.Backend = slot.Factory.TryCreate(config);
        }
        catch (Exception ex)
        {
            Fail(slot, ex, now);
            return;
        }

        if (slot.Backend is null)
        {
            slot.RetryFrom = now;
            return;
        }
        slot.RetryFrom = null;
        slot.Fault = null;
    }

    private void Refresh(Slot slot, long now)
    {
        if (slot.Backend is not { } backend)
            return;

        try
        {
            var channels = backend.Fans.ToDictionary(f => f.Id);
            foreach (var id in channels.Keys)
                _owners[id] = slot;
            backend.Update();
            slot.Channels = channels;
            slot.Sensors = [.. backend.Sensors];
            slot.Temperatures = new Dictionary<string, double>(backend.Temperatures);
        }
        catch (Exception ex)
        {
            Fail(slot, ex, now);
        }
    }

    private void CheckConflicts()
    {
        var running = conflicts.Running(_slots.SelectMany(s => s.Factory.ConflictingProcesses).Distinct());
        foreach (var slot in _slots)
        {
            var process = slot.Factory.ConflictingProcesses.FirstOrDefault(running.Contains);
            slot.Pause = process is null ? null : $"{process} is running";
            if (slot.Pause is not null)
                slot.Writes.Clear();
        }
    }

    private void Report(List<string> faults, string fault, Exception error)
    {
        faults.Add(fault);
        if (_loggedFaults.Add(fault))
            log.LogWarning(error, "Engine fault: {Fault}", fault);
    }

    private Plan Decide(FanTarget fan, IReadOnlyDictionary<string, CurveResult> results, EngineSettings engine, long now)
    {
        var result = fan.CurveId is { } curveId && results.TryGetValue(curveId, out var r) ? r : null;
        if (result is { InputMissing: false })
            _inputSeen[fan.FanId] = now;

        _owners.TryGetValue(fan.FanId, out var owner);
        IFanChannel? channel = null;
        if (owner is { Backend: not null })
            owner.Channels.TryGetValue(fan.FanId, out channel);
        var plan = new Plan(fan, owner, channel, result?.Percent, FanMode.Unavailable, null, false, owner?.Fault ?? "not found");

        if (owner?.Pause is { } reason)
        {
            Forget(fan);
            return plan with { Mode = FanMode.Paused, Note = reason };
        }
        if (channel is null)
        {
            Forget(fan);
            return plan;
        }

        var shaper = _shapers[fan.FanId];
        var missingFor = time.GetElapsedTime(_inputSeen.GetValueOrDefault(fan.FanId, _firstApplied), now);
        int command;
        FanMode mode;
        if ((_manual.TryGetValue(fan.FanId, out var o) ? o : fan.ManualPercent) is { } manual)
        {
            shaper.Reset(manual);
            (command, mode) = (manual, FanMode.Manual);
        }
        else if (result is { Percent: { } percent }
            && !(result.InputMissing && missingFor >= TimeSpan.FromSeconds(engine.FailsafeAfterSeconds)))
        {
            (command, mode) = (shaper.Next(percent), FanMode.Curve);
        }
        else
        {
            shaper.Reset(engine.FailsafePercent);
            (command, mode) = (engine.FailsafePercent, FanMode.Failsafe);
        }

        var auto = command == 0 && channel.SupportsAuto;
        return plan with
        {
            Mode = auto && mode == FanMode.Curve ? FanMode.Auto : mode,
            Command = command,
            Auto = auto,
            Note = null,
        };
    }

    private void Send(Plan plan, long now)
    {
        if (plan is not { Command: { } command, Channel: { } channel, Owner: { Backend: not null } slot })
            return;
        if (slot.Writes.TryGetValue(channel.Id, out var last)
            && last.Percent == command
            && last.Auto == plan.Auto
            && time.GetElapsedTime(last.At, now) < Resend)
            return;

        try
        {
            if (plan.Auto)
                channel.SetAuto();
            else
                channel.SetPercent(command);
            slot.Writes[channel.Id] = new LastWrite(command, plan.Auto, now);
        }
        catch (Exception ex)
        {
            Fail(slot, ex, now);
        }
    }

    private EngineState Snapshot(Dictionary<string, double> temperatures, List<Plan> plans, List<string> engineFaults)
    {
        var temps = LiveSensors()
            .Select(s => new TempReading(s.Id, s.Name, temperatures.TryGetValue(s.Id, out var c) ? c : null))
            .ToList();

        var fans = plans.Select(p =>
        {
            if (p is { Channel: not null, Owner: { Backend: null } faulted })
                p = p with { Channel = null, Mode = FanMode.Unavailable, Command = null, Note = faulted.Fault };
            return new FanReading(p.Fan.FanId, p.Fan.Name, p.Fan.CurveId, p.CurvePercent, p.Command, p.Channel?.Rpm, p.Mode, p.Note);
        }).ToList();

        var faults = _slots.Select(s => s.Fault).OfType<string>().Concat(engineFaults).ToList();
        return new EngineState(time.GetUtcNow(), temps, fans, faults);
    }

    private static IEnumerable<string> CurveSensors(AppConfig config) =>
        config.Curves.OfType<GraphCurve>().Select(g => g.SensorId).Where(id => !string.IsNullOrEmpty(id)).Distinct();

    // A sensor driver that wasn't ready when the backend opened (cold boot) only shows up after a reopen.
    private void ReopenIfSensorsMissing(AppConfig config, Dictionary<string, double> temperatures, long now)
    {
        string? missing = null;
        foreach (var sensor in CurveSensors(config))
        {
            if (temperatures.TryGetValue(sensor, out var celsius) && !double.IsNaN(celsius))
                _sensorSeen[sensor] = now;
            else if (time.GetElapsedTime(_sensorSeen.GetValueOrDefault(sensor, now), now) > SensorMissing)
                missing ??= sensor;
        }
        if (missing is null || (_lastSensorReopen is { } last && time.GetElapsedTime(last, now) < SensorReopen))
            return;

        var suppliers = Live.Where(s => s.Sensors.Count > 0).ToList();
        if (suppliers.Count == 0)
            return;
        _lastSensorReopen = now;
        foreach (var slot in suppliers)
        {
            log.LogWarning("Sensor {Sensor} has been missing for over {Seconds} s; reopening {Backend}",
                missing, SensorMissingSeconds, slot.Factory.Name);
            Drop(slot);
        }
    }

    private List<SensorInfo> LiveSensors() => Live.SelectMany(s => s.Sensors).DistinctBy(s => s.Id).ToList();

    private void Fail(Slot slot, Exception error, long now)
    {
        var fault = $"{slot.Factory.Name}: {error.Message}";
        if (fault != slot.LoggedFault)
        {
            log.LogWarning(error, "Backend fault: {Fault}", fault);
            slot.LoggedFault = fault;
        }

        foreach (var fan in _config?.Fans ?? [])
            if (slot.Channels.ContainsKey(fan.FanId))
                Forget(fan);
        slot.Fault = fault;
        slot.RetryFrom = now;
        Drop(slot);
    }

    private void CloseIfSensorless(Slot slot, IHardwareBackend backend, long now)
    {
        try
        {
            foreach (var fan in backend.Fans)
                _owners[fan.Id] = slot;
            if (backend.Sensors.Count > 0)
                return;
        }
        catch (Exception ex)
        {
            Fail(slot, ex, now);
            return;
        }

        log.LogInformation("Closing {Backend} while paused: {Reason}", slot.Factory.Name, slot.Pause);
        slot.RetryFrom = null;
        slot.ClosedForPause = true;
        Drop(slot);
    }

    private void Drop(Slot slot)
    {
        slot.Channels = [];
        slot.Sensors = [];
        slot.Temperatures = new Dictionary<string, double>();
        slot.Writes.Clear();
        if (slot.Backend is { } backend)
        {
            slot.Backend = null;
            Close(slot, backend);
        }
    }

    private void Forget(FanTarget fan) => _shapers[fan.FanId] = new FanOutputShaper(fan);

    private void HandBackRemoved(Slot slot, HashSet<string> configured, int percent, long now)
    {
        foreach (var id in slot.Writes.Keys.Where(id => !configured.Contains(id)).ToList())
        {
            slot.Writes.Remove(id);
            if (!slot.Channels.TryGetValue(id, out var channel))
                continue;
            try
            {
                if (channel.SupportsAuto)
                    channel.SetAuto();
                else
                    channel.SetPercent(percent);
            }
            catch (Exception ex)
            {
                Fail(slot, ex, now);
                return;
            }
        }
    }

    private void Close(Slot slot, IHardwareBackend backend)
    {
        try
        {
            backend.Dispose();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Closing {Backend} failed", slot.Factory.Name);
        }
    }

    private void HandBack(IFanChannel channel, int percent)
    {
        try
        {
            if (channel.SupportsAuto)
                channel.SetAuto();
            else
                channel.SetPercent(percent);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Handing {Fan} back failed", channel.Id);
        }
    }

    private sealed record Plan(
        FanTarget Fan,
        Slot? Owner,
        IFanChannel? Channel,
        double? CurvePercent,
        FanMode Mode,
        int? Command,
        bool Auto,
        string? Note);

    private readonly record struct LastWrite(int Percent, bool Auto, long At);

    private sealed class Slot(IBackendFactory factory)
    {
        public IBackendFactory Factory { get; } = factory;

        public IHardwareBackend? Backend { get; set; }

        public long? RetryFrom { get; set; }

        public string? Fault { get; set; }

        public string? LoggedFault { get; set; }

        public string? Pause { get; set; }

        public bool ClosedForPause { get; set; }

        public Dictionary<string, IFanChannel> Channels { get; set; } = [];

        public IReadOnlyList<SensorInfo> Sensors { get; set; } = [];

        public IReadOnlyDictionary<string, double> Temperatures { get; set; } = new Dictionary<string, double>();

        public Dictionary<string, LastWrite> Writes { get; } = [];
    }
}
