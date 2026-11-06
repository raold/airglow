using Airglow.App;

namespace Airglow.App.Tests;

public sealed class RestartRegistrationTests
{
    [Fact]
    public void Restarts_only_after_an_installer_closes_it()
    {
        Assert.Equal(11u, RestartRegistration.Flags);
    }

    [Fact]
    public void Restarts_in_the_tray()
    {
        Assert.Equal("--tray", RestartRegistration.CommandLine);
    }
}
