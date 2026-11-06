using Airglow.Core.Config;
using Airglow.Hardware.LianLi;

namespace Airglow.Hardware.Tests;

public class LianLiAlHubTests
{
    private static readonly byte[] FirmwareReply = [0xE0, 0xE0, 0x50, 0x80, 0xC3, 0x15];
    private static readonly byte[] RpmReply = [0xE0, 0x03, 0xFC, 0x04, 0xB0, 0x04, 0x74, 0x04, 0x92];

    private static IReadOnlyList<LianLiGroup> Groups(params int[] counts) =>
        counts.Select((c, i) => new LianLiGroup(i, $"Group {i + 1}", c)).ToList();

    private static (LianLiAlHub Hub, FakeHidDevice Device) Create(params int[] counts)
    {
        var device = new FakeHidDevice();
        device.QueueInput(FirmwareReply);
        var hub = new LianLiAlHub(device, Groups(counts.Length == 0 ? [3, 3, 3, 3] : counts), _ => { });
        return (hub, device);
    }

    [Fact]
    public void Constructor_only_queries_firmware()
    {
        var (_, device) = Create();

        Assert.Equal([new byte[] { 0xE0, 0x50, 0x01, 0, 0, 0, 0 }], device.Sent);
    }

    [Fact]
    public void First_speed_command_configures_hub_first()
    {
        var (hub, device) = Create(3, 3, 2, 2);
        device.Sent.Clear();

        hub.SetGroupPercent(0, 50);
        hub.SetGroupPercent(0, 50);

        byte[][] expected =
        [
            [0xE0, 0x10, 0x42, 0x10, 0, 0, 0],
            [0xE0, 0x10, 0x42, 0x20, 0, 0, 0],
            [0xE0, 0x10, 0x42, 0x40, 0, 0, 0],
            [0xE0, 0x10, 0x42, 0x80, 0, 0, 0],
            [0xE0, 0x10, 0x40, 0x01, 0x03, 0, 0],
            [0xE0, 0x10, 0x40, 0x02, 0x03, 0, 0],
            [0xE0, 0x10, 0x40, 0x03, 0x02, 0, 0],
            [0xE0, 0x10, 0x40, 0x04, 0x02, 0, 0],
            [0xE0, 0x20, 0x00, 0x32, 0, 0, 0],
            [0xE0, 0x20, 0x00, 0x32, 0, 0, 0],
        ];
        Assert.Equal(expected, device.Sent);
    }

    [Fact]
    public void Parses_firmware_version()
    {
        var (hub, _) = Create();

        Assert.NotNull(hub.Firmware);
        Assert.True(hub.Firmware!.IsValid);
        Assert.Equal("1.7", hub.Firmware.Version);
    }

    [Theory]
    [InlineData(0x00, "1.0")]
    [InlineData(0x07, "1.0")]
    [InlineData(0x08, "1.0")]
    [InlineData(0x10, "1.2")]
    [InlineData(0x15, "1.7")]
    [InlineData(0x18, "2.0")]
    public void Firmware_version_from_fine_tune(byte fineTune, string expected)
    {
        Assert.Equal(expected, new AlFirmware(0xE0, 0x50, 0x80, 0xC3, fineTune).Version);
    }

    [Fact]
    public void Firmware_is_invalid_with_wrong_ids()
    {
        Assert.False(new AlFirmware(0xE0, 0x51, 0x80, 0xC3, 0x15).IsValid);
    }

    [Fact]
    public void Reads_rpms_big_endian()
    {
        var (hub, device) = Create();
        device.QueueInput(RpmReply);

        var rpms = hub.ReadRpms();

        Assert.Equal([1020, 1200, 1140, 1170], rpms);
        Assert.Equal(new byte[] { 0xE0, 0x50, 0x00, 0, 0, 0, 0 }, device.Sent[^1]);
    }

    [Fact]
    public void Sets_group_percent()
    {
        var (hub, device) = Create();

        hub.SetGroupPercent(2, 55);
        Assert.Equal(new byte[] { 0xE0, 0x22, 0x00, 0x37, 0, 0, 0 }, device.Sent[^1]);

        hub.SetGroupPercent(0, 120);
        Assert.Equal(new byte[] { 0xE0, 0x20, 0x00, 0x64, 0, 0, 0 }, device.Sent[^1]);

        hub.SetGroupPercent(1, -5);
        Assert.Equal(new byte[] { 0xE0, 0x21, 0x00, 0x00, 0, 0, 0 }, device.Sent[^1]);
    }

    [Fact]
    public void Channel_set_percent_targets_its_group()
    {
        var (hub, device) = Create();

        hub.Fans[3].SetPercent(70);

        Assert.Equal(new byte[] { 0xE0, 0x23, 0x00, 0x46, 0, 0, 0 }, device.Sent[^1]);
        Assert.Equal("lianli.g4", hub.Fans[3].Id);
        Assert.False(hub.Fans[3].SupportsAuto);
    }

    [Fact]
    public void Update_fills_channel_rpms()
    {
        var (hub, device) = Create();
        device.QueueInput(RpmReply);

        hub.Update();

        Assert.Equal([1020, 1200, 1140, 1170], hub.Fans.Select(f => f.Rpm));
        Assert.Empty(hub.Sensors);
        Assert.Empty(hub.Temperatures);
    }

    [Fact]
    public void Short_rpm_report_throws_io_exception()
    {
        var (hub, device) = Create();
        device.QueueInput(0xE0, 0x03, 0xFC, 0x04);

        Assert.Throws<IOException>(() => hub.ReadRpms());
    }

    [Fact]
    public void Firmware_query_is_tried_once_without_waiting()
    {
        var device = new FakeHidDevice();
        var sleeps = new List<TimeSpan>();
        var hub = new LianLiAlHub(device, Groups(3), sleeps.Add);

        Assert.Null(hub.Firmware);
        Assert.Single(device.Sent);
        Assert.All(sleeps, s => Assert.True(s < TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Channels_exist_only_for_configured_groups_in_index_order()
    {
        var device = new FakeHidDevice();
        device.QueueInput(FirmwareReply);
        var groups = new List<LianLiGroup> { new(2, "Rear", 1), new(0, "Top", 3) };
        var hub = new LianLiAlHub(device, groups, _ => { });

        Assert.Equal(["lianli.g1", "lianli.g3"], hub.Fans.Select(f => f.Id));
        Assert.Equal(["Top", "Rear"], hub.Fans.Select(f => f.Name));

        device.Sent.Clear();
        hub.SetGroupPercent(0, 10);
        Assert.Equal(
            [
                new byte[] { 0xE0, 0x10, 0x42, 0x10, 0, 0, 0 },
                new byte[] { 0xE0, 0x10, 0x42, 0x40, 0, 0, 0 },
                new byte[] { 0xE0, 0x10, 0x40, 0x01, 0x03, 0, 0 },
                new byte[] { 0xE0, 0x10, 0x40, 0x03, 0x01, 0, 0 },
                new byte[] { 0xE0, 0x20, 0x00, 0x0A, 0, 0, 0 },
            ],
            device.Sent);
    }
}
