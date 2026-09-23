using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;
using System.Windows;
using HashLynx.Persistence;
using System.IO;

namespace HashLynx.UI;

public partial class App : Application
{
    private AppServices? _services;
    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        // This branch is a separate test workspace; production history and profiles stay untouched.
        var store = new PersistenceStore(new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HashLynx", "Experiments", "BitLockerDrive")));
        _services = new AppServices(store);
        DispatcherUnhandledException += (_, eventArgs) => { _services.ReportError(eventArgs.Exception); eventArgs.Handled = true; };
        var shell = new ShellViewModel(_services);
        var window = new MainWindow { DataContext = shell, Title = "HashLynx TEST - BitLocker drive reader" };
        MainWindow = window; window.Show();
        try
        {
            // Copy only first-run preferences, including the working device choice, never sessions or targets.
            if (!File.Exists(Path.Combine(store.Paths.Root, "settings.json")))
                await store.SaveSettingsAsync(await new PersistenceStore().LoadSettingsAsync());
            await shell.InitializeAsync();
        }
        catch (Exception exception) { _services.ReportError(exception); shell.ShowSettings(); }
    }
}
