using Airglow.Core.Config;

namespace Airglow.App;

public sealed class ConfigDraft(IServiceClient client)
{
    public AppConfig? Value { get; private set; }

    public bool Dirty { get; private set; }

    public bool Busy { get; private set; }

    public string? LoadError { get; private set; }

    public string? Notice { get; private set; }

    public IReadOnlyList<string> Errors { get; set; } = [];

    public async Task LoadAsync()
    {
        try
        {
            Value = await client.GetConfigAsync(default);
            LoadError = null;
            Dirty = false;
            Errors = [];
        }
        catch (Exception ex)
        {
            LoadError = $"Couldn't load the configuration: {ex.Message}";
        }
    }

    public void Update(Func<AppConfig, AppConfig> change)
    {
        Value = change(Value!);
        Dirty = true;
        Notice = null;
    }

    public async Task SaveAsync()
    {
        if (Value is null)
            return;
        Busy = true;
        try
        {
            Errors = await client.SaveConfigAsync(Value, default);
            if (Errors.Count == 0)
            {
                Dirty = false;
                Notice = "Saved.";
            }
        }
        catch (Exception ex)
        {
            Errors = [$"Couldn't save: {ex.Message}"];
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task RevertAsync()
    {
        Busy = true;
        try
        {
            await LoadAsync();
            Notice = null;
        }
        finally
        {
            Busy = false;
        }
    }

    public void ClearNotice() => Notice = null;
}
