using Airglow.Core.Config;
using Airglow.Core.Hardware;
using Airglow.Hardware.Hid;

namespace Airglow.Hardware.LianLi;

public sealed class LianLiAlHub : IHardwareBackend
{
    private const byte ReportId = 0xE0;
    private const int GroupCount = 4;
    private static readonly TimeSpan CommandDelay = TimeSpan.FromMilliseconds(20);

    private readonly IHidDevice _device;
    private readonly Action<TimeSpan> _sleep;
    private readonly List<LianLiGroup> _groups;
    private readonly List<Channel> _channels;
    private readonly object _lock = new();
    private bool _configured;

    public LianLiAlHub(IHidDevice device, IReadOnlyList<LianLiGroup> groups)
        : this(device, groups, Thread.Sleep)
    {
    }

    internal LianLiAlHub(IHidDevice device, IReadOnlyList<LianLiGroup> groups, Action<TimeSpan> sleep)
    {
        _device = device;
        _sleep = sleep;
        _groups = groups.OrderBy(g => g.Index).ToList();
        _channels = _groups.Select(g => new Channel(this, g)).ToList();
        Fans = _channels;
        Firmware = ReadFirmware();
    }

    public string Name => "Lian Li UNI HUB AL";

    public AlFirmware? Firmware { get; }

    public IReadOnlyList<IFanChannel> Fans { get; }

    public IReadOnlyList<SensorInfo> Sensors { get; } = [];

    public IReadOnlyDictionary<string, double> Temperatures { get; } = new Dictionary<string, double>();

    public IReadOnlyList<int> ReadRpms()
    {
        lock (_lock)
        {
            SendFeature(ReportId, 0x50, 0x00);
            _sleep(CommandDelay);

            var buffer = NewInputBuffer();
            var read = _device.GetInputReport(buffer);
            if (read < 9)
            {
                throw new IOException($"RPM report too short ({read} bytes).");
            }

            var rpms = new int[GroupCount];
            for (var i = 0; i < GroupCount; i++)
            {
                rpms[i] = (buffer[1 + i * 2] << 8) | buffer[2 + i * 2];
            }

            return rpms;
        }
    }

    public void SetGroupPercent(int group, int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(group);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(group, GroupCount);

        var clamped = (byte)Math.Clamp(percent, 0, 100);
        lock (_lock)
        {
            EnsureConfigured();
            SendFeature(ReportId, (byte)(0x20 | group), 0x00, clamped);
            _sleep(CommandDelay);
        }
    }

    public void Update()
    {
        var rpms = ReadRpms();
        foreach (var channel in _channels)
        {
            channel.Rpm = rpms[channel.Group];
        }
    }

    public void Dispose() => _device.Dispose();

    // One attempt only: the engine retries the whole hub every few seconds, and waiting here would hold its lock.
    private AlFirmware? ReadFirmware()
    {
        try
        {
            lock (_lock)
            {
                SendFeature(ReportId, 0x50, 0x01);
                _sleep(CommandDelay);

                var buffer = NewInputBuffer();
                var read = _device.GetInputReport(buffer);
                if (read >= 6)
                {
                    return new AlFirmware(buffer[1], buffer[2], buffer[3], buffer[4], buffer[5]);
                }
            }
        }
        catch (IOException)
        {
        }

        return null;
    }

    private void EnsureConfigured()
    {
        if (_configured)
        {
            return;
        }

        foreach (var group in _groups)
        {
            SendFeature(ReportId, 0x10, 0x42, (byte)(1 << (group.Index + 4)), 0x00, 0x00);
            _sleep(CommandDelay);
        }

        foreach (var group in _groups)
        {
            SendFeature(ReportId, 0x10, 0x40, (byte)(group.Index + 1), (byte)group.FanCount, 0x00);
            _sleep(CommandDelay);
        }

        _configured = true;
    }

    private byte[] NewInputBuffer()
    {
        var buffer = new byte[_device.Info.InputReportLength];
        buffer[0] = ReportId;
        return buffer;
    }

    private void SendFeature(params byte[] report) => _device.SetFeature(report);

    private sealed class Channel(LianLiAlHub hub, LianLiGroup group) : IFanChannel
    {
        public int Group => group.Index;

        public string Id => $"lianli.g{group.Index + 1}";

        public string Name => group.Name;

        public bool SupportsAuto => false;

        public int? Rpm { get; set; }

        public void SetPercent(int percent) => hub.SetGroupPercent(group.Index, percent);

        public void SetAuto() => throw new NotSupportedException("The Lian Li AL hub has no automatic mode.");
    }
}
