using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Airglow.App;

public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;
    private ServiceProvider? _services;
    private TrayIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var showWindow = ShowsWindow(e.Args);
        _instance = new SingleInstance("airglow-app");
        if (!_instance.IsFirst)
        {
            if (showWindow)
                _instance.SignalFirst();
            Shutdown();
            return;
        }

        RestartRegistration.Register();
        _instance.ShowRequested += () => Dispatcher.BeginInvoke(ShowWindow);

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IServiceClient, ServiceClient>();
        services.AddSingleton<LiveState>();
        _services = services.BuildServiceProvider();

        _window = new MainWindow(_services);
        _tray = new TrayIcon(_services.GetRequiredService<LiveState>(), ShowWindow, Quit);

        if (showWindow)
            ShowWindow();
    }

    // Sign-in and the restart after an upgrade launch with --tray; only a launch without it brings the window up.
    internal static bool ShowsWindow(IReadOnlyCollection<string> args) => !args.Contains("--tray");

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _services?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Quit()
    {
        _window?.AllowClose();
        _window?.Close();
        Shutdown();
    }
}
