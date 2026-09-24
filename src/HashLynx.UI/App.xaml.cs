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
        string? setupNotice = null;
        var productionRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HashLynx");
        var store = new PersistenceStore(new AppPaths(Path.Combine(productionRoot, "Experiments", "RecoveryWorkflows")));
        if (!File.Exists(Path.Combine(store.Paths.Root, "settings.json")))
        {
            try
            {
                var original = new PersistenceStore(new AppPaths(productionRoot));
                var settings = await original.LoadSettingsAsync();
                settings.DefaultOutputDirectory = store.Paths.ResultsDirectory;
                await store.SaveSettingsAsync(settings);
                await store.SaveWordlistLibraryAsync(await original.LoadWordlistLibraryAsync());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                setupNotice = "Some normal-app preferences could not be copied into this experiment. The originals were preserved. Review Settings and your saved wordlists.";
            }
        }
        _services = new AppServices(store);
        DispatcherUnhandledException += (_, eventArgs) => { _services.ReportError(eventArgs.Exception); eventArgs.Handled = true; };
        var shell = new ShellViewModel(_services);
        var window = new MainWindow { DataContext = shell, Title = "HashLynx - Recovery workflows experiment" };
        MainWindow = window; window.Show();
        try { await shell.InitializeAsync(); if (setupNotice is not null) _services.Notice = setupNotice; }
        catch (Exception exception) { _services.ReportError(exception); shell.ShowSettings(); }
    }
}
