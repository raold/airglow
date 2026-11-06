using System.Net.Http;
using Airglow.App;
using Airglow.App.Components.Pages;
using Airglow.Core.Config;
using Airglow.Core.Engine;
using Airglow.Core.Hardware;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Airglow.App.Tests;

public sealed class OverviewTests
{
    private sealed class FakeClient : IServiceClient
    {
        public EngineState? State { get; set; }

        public Task<EngineState> GetStateAsync(CancellationToken ct) =>
            State is { } state ? Task.FromResult(state) : throw new HttpRequestException("engine down");

        public Task<AppConfig> GetConfigAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> SaveConfigAsync(AppConfig config, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SetManualAsync(string fanId, int? percent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SensorInfo>> GetSensorsAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static FanReading Fan(string id, string name, FanMode mode = FanMode.Curve, string? note = null) =>
        new(id, name, "cpu", 45, 45, 1020, mode, note);

    private static EngineState StateOf(params FanReading[] fans) =>
        new(DateTimeOffset.UnixEpoch,
            [new TempReading("cpu.package", "CPU Package", 48.4), new TempReading("gpu.core", "GPU Core", 55)],
            fans,
            []);

    private static async Task<(BunitContext Ctx, IRenderedComponent<Overview> Page)> RenderAsync(FakeClient client)
    {
        var live = new LiveState(client, new FakeTimeProvider());
        await live.RefreshAsync();
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(live);
        return (ctx, ctx.Render<Overview>());
    }

    [Fact]
    public async Task Renders_a_card_per_fan()
    {
        var fans = Enumerable.Range(1, 6).Select(i => Fan($"fan{i}", $"Fan {i}")).ToArray();
        var (ctx, page) = await RenderAsync(new FakeClient { State = StateOf(fans) });
        using (ctx)
        {
            var cards = page.FindAll(".fan-card");
            Assert.Equal(6, cards.Count);
            Assert.Contains("1020", cards[0].TextContent);
            Assert.Contains("45%", cards[0].TextContent);
        }
    }

    [Fact]
    public async Task Shows_mode_tags()
    {
        var fan = Fan("gpu0", "GPU fan 1", FanMode.Paused, "FanControl is running");
        var (ctx, page) = await RenderAsync(new FakeClient { State = StateOf(fan) });
        using (ctx)
        {
            var card = page.Find(".fan-card");
            Assert.Equal("Paused", card.QuerySelector(".tag")!.TextContent.Trim());
            Assert.Contains("FanControl is running", card.TextContent);
        }
    }

    [Fact]
    public async Task Shows_engine_down_banner_when_disconnected()
    {
        var (ctx, page) = await RenderAsync(new FakeClient());
        using (ctx)
        {
            Assert.Contains("The airglow engine isn't running.", page.Find(".banner").TextContent);
            Assert.Empty(page.FindAll(".fan-card"));
        }
    }

    [Fact]
    public async Task Throwing_subscriber_does_not_stop_polling()
    {
        var time = new FakeTimeProvider();
        var client = new FakeClient { State = StateOf() };
        using var live = new LiveState(client, time);
        var calls = 0;
        live.Changed += () =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("bad handler");
        };

        await live.RefreshAsync();
        var before = Volatile.Read(ref calls);
        await live.RefreshAsync();

        Assert.True(Volatile.Read(ref calls) > before);
        Assert.True(live.Connected);
    }
}
