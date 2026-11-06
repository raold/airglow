using Airglow.App;

namespace Airglow.App.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void Second_instance_signals_first_and_exits()
    {
        var name = $"airglow-test-{Guid.NewGuid():N}";
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);
        Assert.True(first.IsFirst);
        Assert.False(second.IsFirst);

        using var raised = new ManualResetEventSlim();
        first.ShowRequested += raised.Set;

        second.SignalFirst();

        Assert.True(raised.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Only_a_launch_without_tray_shows_the_window()
    {
        Assert.True(App.ShowsWindow([]));
        Assert.True(App.ShowsWindow(["--other"]));
        Assert.False(App.ShowsWindow(["--tray"]));
    }
}
