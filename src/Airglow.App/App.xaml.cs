using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Airglow.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private TrayIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IServiceClient, ServiceClient>();
        services.AddSingleton<LiveState>();
        _services = services.BuildServiceProvider();

        _window = new MainWindow(_services);
        _tray = new TrayIcon(_services.GetRequiredService<LiveState>(), ShowWindow, Quit);

        if (!e.Args.Contains("--tray"))
            ShowWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _services?.Dispose();
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
