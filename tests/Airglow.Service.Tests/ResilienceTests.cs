using System.Net;
using System.Net.Http.Json;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using Airglow.Core.Config;
using Airglow.Core.Engine;
using Airglow.Core.Hardware;
using Airglow.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Airglow.Service.Tests;

public sealed class BlockingConflicts : IConflictDetector, IDisposable
{
    private readonly ManualResetEventSlim _released = new(false);

    public volatile bool Blocking;

    public IReadOnlySet<string> Running(IEnumerable<string> processNames)
    {
        if (Blocking)
            _released.Wait();
        return new HashSet<string>();
    }

    public void Release()
    {
        Blocking = false;
        _released.Set();
    }

    public void Dispose()
    {
        Release();
        _released.Dispose();
    }
}

public sealed class ResilienceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("airglow-res-").FullName;
    private readonly string _configPath;
    private readonly FakeFactory _gpu = FakeFactory.Gpu();
    private readonly FakeFactory _lianli = FakeFactory.LianLi();
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public ResilienceTests() => _configPath = Path.Combine(_directory, "config.json");

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private void SeedValid()
    {
        var seed = AppConfig.CreateDefault();
        new ConfigStore(_configPath).Save(seed with { Engine = seed.Engine with { TickMilliseconds = 250 } });
    }

    private void Start(IConflictDetector conflicts, WatchdogOptions? watchdog = null, string? configPath = null)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("Airglow:ConfigPath", configPath ?? _configPath);
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<IBackendFactory>();
                services.RemoveAll<IConflictDetector>();
                services.AddSingleton<IBackendFactory>(_gpu);
                services.AddSingleton<IBackendFactory>(_lianli);
                services.AddSingleton(conflicts);
                if (watchdog is not null)
                    services.AddSingleton(watchdog);
            });
        });
        _client = _factory.CreateClient();
    }

    private async Task<EngineState> WaitForState(Func<EngineState, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            var response = await _client!.GetAsync("/api/state");
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

    private FakeFanChannel BrokenFan => _lianli.Backend.Fan("lianli.g1");

    [Fact]
    public async Task Tick_failure_keeps_one_fault_and_clears_when_ticks_recover()
    {
        SeedValid();
        Start(new FakeConflicts());
        await WaitForState(s => s.Fans.Count == 6);

        BrokenFan.ThrowOnRpm = new InvalidOperationException("engine bug");
        var failing = await WaitForState(s => s.Faults.Any(f => f.StartsWith("Engine tick failed")));
        Assert.Equal(6, failing.Fans.Count);
        await Task.Delay(1000);

        var later = await WaitForState(_ => true);
        Assert.Single(later.Faults, f => f.StartsWith("Engine tick failed"));
        Assert.Single(later.Faults);

        BrokenFan.ThrowOnRpm = null;
        var recovered = await WaitForState(s => s.Faults.Count == 0);
        Assert.Equal(6, recovered.Fans.Count);
    }

    [Fact]
    public async Task First_tick_failure_publishes_a_minimal_state()
    {
        SeedValid();
        BrokenFan.ThrowOnRpm = new InvalidOperationException("engine bug");
        Start(new FakeConflicts());

        var state = await WaitForState(_ => true);

        Assert.Empty(state.Fans);
        Assert.Empty(state.Temperatures);
        var fault = Assert.Single(state.Faults);
        Assert.StartsWith("Engine tick failed", fault);

        BrokenFan.ThrowOnRpm = null;
        await WaitForState(s => s.Fans.Count == 6 && s.Faults.Count == 0);
    }

    [Fact]
    public async Task Watchdog_fails_fast_when_a_tick_hangs()
    {
        SeedValid();
        var failFast = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var conflicts = new BlockingConflicts();
        Start(conflicts, new WatchdogOptions(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), m => failFast.TrySetResult(m)));
        await WaitForState(s => s.Fans.Count == 6);

        conflicts.Blocking = true;
        try
        {
            var message = await failFast.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("airglow engine tick hung", message);
        }
        finally
        {
            conflicts.Release();
        }
    }

    [Fact]
    public async Task Watchdog_stays_quiet_while_ticks_complete()
    {
        SeedValid();
        var calls = 0;
        Start(new FakeConflicts(), new WatchdogOptions(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), _ => Interlocked.Increment(ref calls)));
        await WaitForState(s => s.Fans.Count == 6);

        await Task.Delay(2500);

        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Config_problem_shows_as_a_fault_until_a_valid_put()
    {
        File.WriteAllText(_configPath, "{ this is not json");
        Start(new FakeConflicts());

        var state = await WaitForState(s => s.Fans.Count == 6);
        Assert.Contains(state.Faults, f => f.StartsWith("config.json was unreadable"));

        var config = (await _client!.GetFromJsonAsync<AppConfig>("/api/config", ConfigJson.Options))!;
        var put = await _client!.PutAsJsonAsync("/api/config", config, ConfigJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        await WaitForState(s => s.Faults.Count == 0);
    }

    [Fact]
    public async Task Unloadable_config_falls_back_to_defaults_and_keeps_ticking()
    {
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "a file where the config folder should be");
        Start(new FakeConflicts(), configPath: Path.Combine(blocker, "config.json"));

        var state = await WaitForState(s => s.Fans.Count == 6);

        Assert.Contains(state.Faults, f => f.StartsWith("config.json could not be loaded: "));
        var config = (await _client!.GetFromJsonAsync<AppConfig>("/api/config", ConfigJson.Options))!;
        Assert.Equal(AppConfig.CreateDefault().Engine, config.Engine);
    }

    [Fact]
    public async Task Stopping_the_host_hands_fans_back()
    {
        SeedValid();
        Start(new FakeConflicts());
        await WaitForState(s => s.Fans.Count == 6);

        _client!.Dispose();
        _factory!.Dispose();

        Assert.Contains("SetAuto()", _gpu.Backend.Fan("gpu.fan1").Calls);
        Assert.Contains("SetAuto()", _gpu.Backend.Fan("gpu.fan2").Calls);
        foreach (var n in Enumerable.Range(1, 4))
            Assert.Contains("SetPercent(60)", _lianli.Backend.Fan($"lianli.g{n}").Calls);
    }

    [Fact]
    public void Pipe_acl_denies_network_first_and_allows_system_admins_and_users()
    {
        var rules = PipeAcl.Create().GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<AuthorizationRule>().ToList();

        static SecurityIdentifier Sid(WellKnownSidType t) => new(t, null);
        Assert.Equal(AccessControlType.Deny, ((System.IO.Pipes.PipeAccessRule)rules[0]).AccessControlType);
        Assert.Equal(Sid(WellKnownSidType.NetworkSid), rules[0].IdentityReference);

        var allowed = rules.Cast<System.IO.Pipes.PipeAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => r.IdentityReference).ToList();
        Assert.Contains(Sid(WellKnownSidType.LocalSystemSid), allowed);
        Assert.Contains(Sid(WellKnownSidType.BuiltinAdministratorsSid), allowed);
        Assert.Contains(Sid(WellKnownSidType.AuthenticatedUserSid), allowed);
    }

    [Fact]
    public void Pipe_acl_configure_turns_off_current_user_only()
    {
        var options = new Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes.NamedPipeTransportOptions();

        PipeAcl.Configure(options);

        Assert.False(options.CurrentUserOnly);
        Assert.NotNull(options.PipeSecurity);
    }

    [Fact]
    public async Task State_is_served_over_a_real_named_pipe()
    {
        SeedValid();
        var pipe = $"airglow-test-{Guid.NewGuid():N}";
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Staging");
            host.UseSetting("Airglow:ConfigPath", _configPath);
            host.UseSetting("Airglow:PipeName", pipe);
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<IBackendFactory>();
                services.RemoveAll<IConflictDetector>();
                services.AddSingleton<IBackendFactory>(_gpu);
                services.AddSingleton<IBackendFactory>(_lianli);
                services.AddSingleton<IConflictDetector>(new FakeConflicts());
            });
        });
        _factory.UseKestrel();
        _factory.StartServer();

        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await stream.ConnectAsync(token);
                return stream;
            },
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://airglow") };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var response = await client.GetAsync("/api/state", timeout.Token);
            if (response.StatusCode == HttpStatusCode.OK)
                break;
            await Task.Delay(50, timeout.Token);
        }
    }
}
