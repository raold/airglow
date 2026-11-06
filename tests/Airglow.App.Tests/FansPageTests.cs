using System.Net.Http;
using Airglow.App;
using Airglow.App.Components;
using Airglow.App.Components.Pages;
using Airglow.Core.Config;
using Airglow.Core.Curves;
using Airglow.Core.Engine;
using Airglow.Core.Hardware;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Airglow.App.Tests;

public sealed class FansPageTests
{
    private sealed class FakeClient : IServiceClient
    {
        public AppConfig Config { get; set; } = AppConfig.CreateDefault();

        public IReadOnlyList<string> SaveErrors { get; set; } = [];

        public List<AppConfig> Saved { get; } = [];

        public List<(string FanId, int? Percent)> Manual { get; } = [];

        public Task<EngineState> GetStateAsync(CancellationToken ct) => throw new HttpRequestException("engine down");

        public Task<AppConfig> GetConfigAsync(CancellationToken ct) => Task.FromResult(Config);

        public Task<IReadOnlyList<string>> SaveConfigAsync(AppConfig config, CancellationToken ct)
        {
            Saved.Add(config);
            return Task.FromResult(SaveErrors);
        }

        public Task SetManualAsync(string fanId, int? percent, CancellationToken ct)
        {
            Manual.Add((fanId, percent));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SensorInfo>> GetSensorsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SensorInfo>>(
            [
                new("cpu.core1", "CPU Core #1", "CPU"),
                new("cpu.tj", "CPU Core #1 Distance to TjMax", "CPU"),
                new("gpu.memory", "GPU Memory", "GPU"),
                new("cpu.package", "CPU Package", "CPU"),
                new("gpu.core", "GPU Core", "GPU"),
            ]);
    }

    private static BunitContext NewContext(FakeClient client)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<IServiceClient>(client);
        ctx.Services.AddSingleton(new LiveState(client, new FakeTimeProvider()));
        return ctx;
    }

    [Fact]
    public void Save_sends_edited_config()
    {
        var client = new FakeClient();
        using var ctx = NewContext(client);
        var page = ctx.Render<Fans>();

        page.Find("#fan-step-up").Change(12);
        page.Find("#save").Click();

        var saved = Assert.Single(client.Saved);
        Assert.Equal(12, saved.Fans.Single(f => f.FanId == "gpu.fan1").StepUpPercent);
    }

    [Fact]
    public void Shows_server_validation_errors()
    {
        var client = new FakeClient { SaveErrors = ["Curve 'gpu': needs at least 2 points"] };
        using var ctx = NewContext(client);
        var page = ctx.Render<Fans>();

        page.Find("#fan-step-up").Change(12);
        page.Find("#save").Click();

        Assert.Contains("Curve 'gpu': needs at least 2 points", page.Find(".errors").TextContent);
        Assert.False(page.Find("#save").HasAttribute("disabled"));
        Assert.Contains("Unsaved changes", page.Markup);
    }

    [Fact]
    public void Revert_reloads_the_saved_config()
    {
        var client = new FakeClient();
        using var ctx = NewContext(client);
        var page = ctx.Render<Fans>();
        page.Find("#fan-step-up").Change(12);

        page.Find("#revert").Click();

        Assert.Equal("8", page.Find("#fan-step-up").GetAttribute("value"));
        Assert.True(page.Find("#save").HasAttribute("disabled"));
    }

    [Fact]
    public void Sensor_select_hides_tjmax_and_lists_well_known_first()
    {
        using var ctx = NewContext(new FakeClient());
        var page = ctx.Render<Fans>();

        var options = page.FindAll("#curve-sensor option").Select(o => o.TextContent).ToList();

        Assert.Equal(["CPU Package", "GPU Core", "GPU Memory", "CPU Core #1"], options);
    }

    [Fact]
    public void Manual_slider_applies_immediately_and_auto_clears_it()
    {
        var client = new FakeClient();
        using var ctx = NewContext(client);
        var page = ctx.Render<Fans>();

        page.Find("#manual-auto").Change(false);
        page.Find("#manual-slider").Change(60);
        page.Find("#manual-auto").Change(true);

        Assert.Equal([("gpu.fan1", (int?)50), ("gpu.fan1", 60), ("gpu.fan1", null)], client.Manual);
    }

    [Fact]
    public async Task Curve_editor_drag_moves_a_point_within_its_neighbours()
    {
        IReadOnlyList<CurvePoint> points = [new(30, 20), new(50, 60), new(80, 100)];
        using var ctx = new BunitContext();
        var editor = ctx.Render<CurveEditor>(p => p
            .Add(c => c.Points, points)
            .Add(c => c.PointsChanged, EventCallback.Factory.Create<IReadOnlyList<CurvePoint>>(this, next => points = next)));

        await editor.FindAll("circle.point")[1].TriggerEventAsync("onpointerdown", new PointerEventArgs { Buttons = 1 });
        // x = 600 is about 105 C, clamped to 79.5 by the next point; y = 12 is 100%
        await editor.Find("rect.drag-overlay").TriggerEventAsync("onpointermove", new PointerEventArgs { OffsetX = 600, OffsetY = 12, Buttons = 1 });

        Assert.Equal(new CurvePoint(79.5, 100), points[1]);
    }

    [Fact]
    public void Curve_editor_renders_plain_svg_text_labels()
    {
        using var ctx = new BunitContext();
        var editor = ctx.Render<CurveEditor>(p => p.Add(c => c.Points, [new CurvePoint(30, 20), new CurvePoint(80, 100)]));

        Assert.Contains("<text", editor.Markup);
        Assert.DoesNotContain("svg:text", editor.Markup);
    }
}
