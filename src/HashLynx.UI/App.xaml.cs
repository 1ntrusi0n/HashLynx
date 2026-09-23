using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;
using System.Windows;

namespace HashLynx.UI;

public partial class App : Application
{
    private AppServices? _services;
    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        _services = new AppServices();
        DispatcherUnhandledException += (_, eventArgs) => { _services.ReportError(eventArgs.Exception); eventArgs.Handled = true; };
        var shell = new ShellViewModel(_services);
        var window = new MainWindow { DataContext = shell };
        MainWindow = window; window.Show();
        try { await shell.InitializeAsync(); }
        catch (Exception exception) { _services.ReportError(exception); shell.ShowSettings(); }
    }
}
