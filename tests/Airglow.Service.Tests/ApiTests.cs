using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Airglow.Core.Config;
using Airglow.Core.Curves;
using Airglow.Core.Engine;
using Airglow.Core.Hardware;
using Airglow.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Airglow.Service.Tests;

public sealed class ApiTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("airglow-api-").FullName;
    private readonly string _configPath;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiTests()
    {
        _configPath = Path.Combine(_directory, "config.json");
        var seed = AppConfig.CreateDefault();
        new ConfigStore(_configPath).Save(seed with { Engine = seed.Engine with { TickMilliseconds = 250 } });

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("Airglow:ConfigPath", _configPath);
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<IBackendFactory>();
                services.RemoveAll<IConflictDetector>();
                services.AddSingleton<IBackendFactory>(FakeFactory.Gpu());
                services.AddSingleton<IBackendFactory>(FakeFactory.LianLi());
                services.AddSingleton<IConflictDetector>(new FakeConflicts());
            });
        });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<EngineState> WaitForState(Func<EngineState, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            var response = await _client.GetAsync("/api/state");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var state = (await response.Content.ReadFromJsonAsync<EngineState>(ConfigJson.Options))!;
                if (predicate(state))
                    return state;
            }
            Assert.True(DateTime.UtcNow < deadline, "state did not reach the expected shape within 2 s");
            await Task.Delay(25);
        }
    }

    private async Task<AppConfig> GetConfig() =>
        (await _client.GetFromJsonAsync<AppConfig>("/api/config", ConfigJson.Options))!;

    [Fact]
    public async Task Get_state_lists_six_fans()
    {
        var state = await WaitForState(s => s.Fans.Count == 6);

        Assert.Equal(6, state.Fans.Count);
    }

    [Fact]
    public async Task State_json_writes_mode_as_a_string()
    {
        await WaitForState(_ => true);

        var body = await _client.GetStringAsync("/api/state");

        Assert.Contains("\"mode\":\"", Compact(body));
    }

    [Fact]
    public async Task Config_json_carries_the_curve_type_discriminator()
    {
        var body = await _client.GetStringAsync("/api/config");

        Assert.Contains("\"type\":\"graph\"", Compact(body));
    }

    [Fact]
    public async Task Get_sensors_lists_the_backend_sensors()
    {
        await WaitForState(_ => true);

        var sensors = await _client.GetFromJsonAsync<SensorInfo[]>("/api/sensors", ConfigJson.Options);

        Assert.Contains(sensors!, s => s.Id == "gpu.core");
    }

    [Fact]
    public async Task Put_invalid_config_keeps_running_config()
    {
        var original = await GetConfig();
        var reversed = original with
        {
            Curves = original.Curves
                .Select(c => c is GraphCurve { Id: "gpu" } g ? g with { Points = g.Points.Reverse().ToList() } : c)
                .ToList(),
        };

        var response = await _client.PutAsJsonAsync("/api/config", reversed, ConfigJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Curve 'gpu': temperatures must increase from point to point", errors);

        var after = await GetConfig();
        var gpu = Assert.IsType<GraphCurve>(after.Curves.Single(c => c.Id == "gpu"));
        Assert.Equal(((GraphCurve)original.Curves.Single(c => c.Id == "gpu")).Points, gpu.Points);
    }

    [Fact]
    public async Task Put_valid_config_is_saved_and_applied()
    {
        var original = await GetConfig();
        var changed = original with
        {
            Fans = original.Fans.Select(f => f.FanId == "gpu.fan1" ? f with { StepUpPercent = 20 } : f).ToList(),
        };

        var response = await _client.PutAsJsonAsync("/api/config", changed, ConfigJson.Options);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(20, (await GetConfig()).Fans.Single(f => f.FanId == "gpu.fan1").StepUpPercent);
        var onDisk = new ConfigStore(_configPath).Load().Config;
        Assert.Equal(20, onDisk.Fans.Single(f => f.FanId == "gpu.fan1").StepUpPercent);
    }

    [Fact]
    public async Task Manual_override_shows_in_state()
    {
        var response = await _client.PutAsJsonAsync("/api/fans/gpu.fan1/manual", new { percent = 70 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var state = await WaitForState(s =>
            s.Fans.Single(f => f.Id == "gpu.fan1") is { Mode: FanMode.Manual, CommandedPercent: 70 });
        Assert.Equal(FanMode.Manual, state.Fans.Single(f => f.Id == "gpu.fan1").Mode);
    }

    [Fact]
    public async Task Manual_override_unknown_fan_is_404()
    {
        var response = await _client.PutAsJsonAsync("/api/fans/nope/manual", new { percent = 70 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Manual_override_out_of_range_is_400()
    {
        var response = await _client.PutAsJsonAsync("/api/fans/gpu.fan1/manual", new { percent = 101 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string Compact(string json) => new(json.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
