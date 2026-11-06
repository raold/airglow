using Airglow.App;
using Airglow.App.Components.Layout;
using Airglow.Core.Config;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Airglow.App.Tests;

public sealed class EngineIndicatorTests
{
    private static readonly DateTimeOffset Now = OverviewTests.Now;

    private static async Task<(string Text, string[] Dot)> IndicatorAsync(OverviewTests.FakeClient client, TimeSpan age)
    {
        var time = new FakeTimeProvider(Now);
        using var live = new LiveState(client, time);
        await live.RefreshAsync();
        time.Advance(age);
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton(live);
        var layout = ctx.Render<MainLayout>(p => p.Add(l => l.Body, b => b.AddContent(0, "body")));
        var status = layout.Find("#engine-status");
        return (status.TextContent.Trim(), status.QuerySelector(".dot")!.ClassList.ToArray());
    }

    private static AppConfig WithTick(int milliseconds)
    {
        var config = AppConfig.CreateDefault();
        return config with { Engine = config.Engine with { TickMilliseconds = milliseconds } };
    }

    [Fact]
    public async Task Fresh_state_without_faults_is_running()
    {
        var (text, dot) = await IndicatorAsync(new OverviewTests.FakeClient { State = OverviewTests.StateOf() }, TimeSpan.FromSeconds(2));

        Assert.Equal("Running", text);
        Assert.Contains("ok", dot);
    }

    [Fact]
    public async Task Faults_show_a_warning()
    {
        var state = OverviewTests.StateOf() with { Faults = ["Lian Li UNI HUB AL: gone"] };

        var (text, dot) = await IndicatorAsync(new OverviewTests.FakeClient { State = state }, TimeSpan.Zero);

        Assert.Equal("Faults", text);
        Assert.Contains("warn", dot);
    }

    [Fact]
    public async Task State_older_than_three_ticks_is_stalled()
    {
        var (text, dot) = await IndicatorAsync(new OverviewTests.FakeClient { State = OverviewTests.StateOf() }, TimeSpan.FromSeconds(3.5));

        Assert.Equal("Stalled", text);
        Assert.Contains("bad", dot);
    }

    [Fact]
    public async Task Stalled_uses_the_configured_tick()
    {
        var client = new OverviewTests.FakeClient { State = OverviewTests.StateOf(), Config = WithTick(5000) };

        var (text, _) = await IndicatorAsync(client, TimeSpan.FromSeconds(10));

        Assert.Equal("Running", text);
    }

    [Fact]
    public async Task Disconnected_is_not_running()
    {
        var (text, dot) = await IndicatorAsync(new OverviewTests.FakeClient(), TimeSpan.Zero);

        Assert.Equal("Not running", text);
        Assert.Contains("bad", dot);
    }
}
