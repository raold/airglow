using Airglow.App;
using Airglow.App.Components.Pages;
using Airglow.Core.Config;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Airglow.App.Tests;

[Collection("Autostart")]
public sealed class AutostartTests : IDisposable
{
    private sealed class FakeRunKeys : IRunKeys
    {
        public Dictionary<string, string> Machine { get; } = [];

        public Dictionary<string, string> User { get; } = [];

        public string? GetMachine(string name) => Machine.GetValueOrDefault(name);

        public string? GetUser(string name) => User.GetValueOrDefault(name);

        public void SetUser(string name, string? value)
        {
            if (value is null)
                User.Remove(name);
            else
                User[name] = value;
        }
    }

    private readonly IRunKeys _original = Autostart.Keys;

    public void Dispose() => Autostart.Keys = _original;

    [Fact]
    public void Autostart_reports_installer_managed()
    {
        var keys = new FakeRunKeys();
        Autostart.Keys = keys;
        Assert.False(Autostart.IsManagedByInstaller);

        keys.Machine["airglow"] = "\"C:\\x\\Airglow.exe\" --tray";
        Assert.True(Autostart.IsManagedByInstaller);

        var client = new OverviewTests.FakeClient { Config = AppConfig.CreateDefault() };
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IServiceClient>(client);
        var page = ctx.Render<Settings>();

        Assert.True(page.Find("#autostart").HasAttribute("disabled"));
        Assert.Contains("Starts at sign-in (set by the installer)", page.Markup);
        Assert.Empty(keys.User);
    }

    [Fact]
    public void User_toggle_still_writes_the_user_value()
    {
        var keys = new FakeRunKeys();
        Autostart.Keys = keys;

        Autostart.Set(true);
        Assert.True(Autostart.IsEnabled);
        Assert.Contains("--tray", keys.User["airglow"]);

        Autostart.Set(false);
        Assert.False(Autostart.IsEnabled);
    }
}
