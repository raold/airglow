using Airglow.Core;
using Airglow.Core.Config;
using Airglow.Core.Engine;
using Airglow.Core.Hardware;
using Airglow.Hardware;
using Airglow.Hardware.LianLi;
using Airglow.Hardware.Monitoring;
using Airglow.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "Airglow");

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.WebHost.UseNamedPipes(PipeAcl.Configure);
    builder.WebHost.ConfigureKestrel(k => k.ListenNamedPipe(builder.Configuration["Airglow:PipeName"] ?? AirglowPaths.PipeName));
}

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = ConfigJson.Options.PropertyNamingPolicy;
    o.SerializerOptions.WriteIndented = ConfigJson.Options.WriteIndented;
    foreach (var converter in ConfigJson.Options.Converters)
        o.SerializerOptions.Converters.Add(converter);
});

builder.Services.AddSingleton<IBackendFactory, LhmBackendFactory>();
builder.Services.AddSingleton<IBackendFactory, LianLiAlHubFactory>();
builder.Services.AddSingleton<IConflictDetector, ProcessConflictDetector>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
    new ConfigStore(sp.GetRequiredService<IConfiguration>()["Airglow:ConfigPath"] ?? AirglowPaths.ConfigFile));
builder.Services.AddSingleton<ConfigHolder>();
builder.Services.AddSingleton<StateCache>();
builder.Services.AddSingleton(sp => new FanEngine(
    sp.GetServices<IBackendFactory>().ToList(),
    sp.GetRequiredService<IConflictDetector>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<FanEngine>>()));
builder.Services.AddHostedService<EngineHost>();

var app = builder.Build();

app.MapGet("/api/state", (StateCache cache) =>
    cache.Current is { } state ? Results.Ok(state) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapGet("/api/config", (ConfigHolder holder) =>
    holder.Current is { } config ? Results.Ok(config) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapPut("/api/config", (AppConfig config, ConfigStore store, FanEngine engine, ConfigHolder holder) =>
{
    var errors = ConfigValidator.Validate(config);
    if (errors.Count > 0)
        return Results.BadRequest(new { errors });

    store.Save(config);
    engine.ApplyConfig(config);
    holder.Set(config, null);
    return Results.NoContent();
});

app.MapPut("/api/fans/{fanId}/manual", (string fanId, ManualRequest request, FanEngine engine) =>
{
    try
    {
        engine.SetManual(fanId, request.Percent);
        return Results.NoContent();
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (ArgumentOutOfRangeException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/sensors", (FanEngine engine) => engine.Sensors);

app.Run();

public sealed record ManualRequest(int? Percent);

public partial class Program;
