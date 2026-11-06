using System.ComponentModel;
using System.Windows;

namespace Airglow.App;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        WebView.Services = services;
        System.Windows.Application.Current.SessionEnding += (_, _) => _allowClose = true;
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
