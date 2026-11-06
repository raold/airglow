using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Json;
using Airglow.Core;
using Airglow.Core.Config;
using Airglow.Core.Engine;
using Airglow.Core.Hardware;

namespace Airglow.App;

public interface IServiceClient
{
    Task<EngineState> GetStateAsync(CancellationToken ct);

    Task<AppConfig> GetConfigAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> SaveConfigAsync(AppConfig config, CancellationToken ct);

    Task SetManualAsync(string fanId, int? percent, CancellationToken ct);

    Task<IReadOnlyList<SensorInfo>> GetSensorsAsync(CancellationToken ct);
}

public sealed class ServiceClient : IServiceClient, IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ConfigJson.Options;

    private readonly HttpClient _http;

    public ServiceClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", AirglowPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(1000, ct);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
            },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://airglow/"), Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<EngineState> GetStateAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<EngineState>("api/state", Json, ct)
        ?? throw new InvalidOperationException("Empty state response.");

    public async Task<AppConfig> GetConfigAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<AppConfig>("api/config", Json, ct)
        ?? throw new InvalidOperationException("Empty config response.");

    public async Task<IReadOnlyList<string>> SaveConfigAsync(AppConfig config, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync("api/config", config, Json, ct);
        if (response.IsSuccessStatusCode)
            return [];
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorsBody>(Json, ct);
            if (body?.Errors is { Count: > 0 } errors)
                return errors;
        }

        response.EnsureSuccessStatusCode();
        return [];
    }

    public async Task SetManualAsync(string fanId, int? percent, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync(
            $"api/fans/{Uri.EscapeDataString(fanId)}/manual", new ManualBody(percent), Json, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<SensorInfo>> GetSensorsAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<List<SensorInfo>>("api/sensors", Json, ct) ?? [];

    public void Dispose() => _http.Dispose();

    private sealed record ErrorsBody(List<string>? Errors);

    private sealed record ManualBody(int? Percent);
}
