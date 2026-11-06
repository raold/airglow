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
    internal static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    internal sealed class FakeClient : IServiceClient
    {
        public EngineState? State { get; set; }

        public AppConfig? Config { get; set; }

        public Task<EngineState> GetStateAsync(CancellationToken ct) =>
            State is { } state ? Task.FromResult(state) : throw new HttpRequestException("engine down");

        public Task<AppConfig> GetConfigAsync(CancellationToken ct) =>
            Config is { } config ? Task.FromResult(config) : throw new NotSupportedException();

        public Task<IReadOnlyList<string>> SaveConfigAsync(AppConfig config, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SetManualAsync(string fanId, int? percent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SensorInfo>> GetSensorsAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static FanReading Fan(string id, string name, FanMode mode = FanMode.Curve, string? note = null) =>
        new(id, name, "cpu", 45, 45, 1020, mode, note);

    internal static EngineState StateOf(params FanReading[] fans) =>
        new(Now,
            [new TempReading("cpu.package", "CPU Package", 48.4), new TempReading("gpu.core", "GPU Core", 55)],
            fans,
            []);

    private static async Task<(BunitContext Ctx, IRenderedComponent<Overview> Page)> RenderAsync(FakeClient client, FakeTimeProvider? time = null)
    {
        var live = new LiveState(client, time ?? new FakeTimeProvider(Now));
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

    private static EngineState WithTemps(IEnumerable<TempReading> temps) =>
        new(Now, temps.ToArray(), [Fan("f1", "Fan 1")], []);

    [Fact]
    public async Task Fresh_state_shows_no_stale_banner()
    {
        var (ctx, page) = await RenderAsync(new FakeClient { State = StateOf(Fan("f1", "Fan 1")) });
        using (ctx)
        {
            Assert.Empty(page.FindAll(".banner"));
        }
    }

    [Fact]
    public async Task Stale_state_shows_when_the_engine_last_updated()
    {
        var time = new FakeTimeProvider(Now);
        var (ctx, page) = await RenderAsync(new FakeClient { State = StateOf(Fan("f1", "Fan 1")) }, time);
        using (ctx)
        {
            time.Advance(TimeSpan.FromSeconds(4));
            page.Render();

            Assert.Equal($"The engine hasn't updated since {Now.ToLocalTime():HH:mm:ss}.", page.Find(".banner").TextContent.Trim());
            Assert.Single(page.FindAll(".fan-card"));
        }
    }

    private static IEnumerable<TempReading> WellKnownTemps() =>
    [
        new("gpu.memory", "GPU Memory", 50),
        new("cpu.package", "CPU Package", 40),
        new("gpu.hotspot", "GPU Hot Spot", 60),
        new("gpu.core", "GPU Core", 45),
    ];

    [Fact]
    public async Task Fans_panel_renders_before_temperatures()
    {
        var (ctx, page) = await RenderAsync(new FakeClient { State = StateOf(Fan("f1", "Fan 1")) });
        using (ctx)
        {
            var titles = page.FindAll(".panel-title").Select(t => t.TextContent).ToList();
            Assert.Equal(["Fans", "Temperatures"], titles);
        }
    }

    [Fact]
    public async Task Temperatures_show_only_well_known_until_toggled()
    {
        var others = Enumerable.Range(1, 10).Select(i => new TempReading($"core{i}", $"Core {i:00}", 30 + i));
        var (ctx, page) = await RenderAsync(new FakeClient { State = WithTemps(WellKnownTemps().Concat(others)) });
        using (ctx)
        {
            var tiles = page.FindAll(".temp");
            Assert.Equal(4, tiles.Count);
            Assert.Equal(["CPU Package", "GPU Core", "GPU Hot Spot", "GPU Memory"],
                tiles.Select(t => t.QuerySelector(".stat-label")!.TextContent));
            var toggle = page.Find(".toggle");
            Assert.Equal("Show all sensors (10)", toggle.TextContent);

            toggle.Click();

            Assert.Equal(14, page.FindAll(".temp").Count);
            Assert.Equal("Hide other sensors", page.Find(".toggle").TextContent);
        }
    }

    [Fact]
    public async Task Distance_to_tjmax_sensors_never_show()
    {
        var temps = WellKnownTemps().Append(new TempReading("x", "CPU Core #1 Distance to TjMax", 60));
        var (ctx, page) = await RenderAsync(new FakeClient { State = WithTemps(temps) });
        using (ctx)
        {
            Assert.Empty(page.FindAll(".toggle"));
            Assert.DoesNotContain("TjMax", page.Markup);
        }
    }
}
