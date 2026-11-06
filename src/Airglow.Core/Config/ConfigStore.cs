using System.Text.Json;

namespace Airglow.Core.Config;

public sealed record LoadResult(AppConfig Config, string? Problem);

public sealed class ConfigStore(string path)
{
    public string FilePath => path;

    public LoadResult Load()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        if (!File.Exists(path))
        {
            var created = AppConfig.CreateDefault();
            Save(created);
            return new LoadResult(created, null);
        }

        string problem;
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), ConfigJson.Options)
                ?? throw new JsonException("file is empty");
            var errors = ConfigValidator.Validate(config);
            if (errors.Count == 0)
                return new LoadResult(config, null);
            problem = $"config.json was invalid: {string.Join("; ", errors)}";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            problem = $"config.json was unreadable: {ex.Message}";
        }

        File.Move(path, Path.Combine(directory, $"config.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);
        var fallback = AppConfig.CreateDefault();
        Save(fallback);
        return new LoadResult(fallback, problem);
    }

    public void Save(AppConfig config)
    {
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
            throw new ArgumentException("Invalid config: " + string.Join("; ", errors), nameof(config));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.Delete(tmp);
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, ConfigJson.Options));
        File.Move(tmp, path, overwrite: true);
    }
}
