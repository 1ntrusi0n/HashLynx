using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;

namespace HashLynx.UI.ViewModels;

public sealed record NavigationItem(string Name, string Glyph, object Page);

public sealed class ShellViewModel : ObservableObject
{
    private NavigationItem _selected;
    private bool _initialized;
    public AppServices Services { get; }
    public AttackViewModel Attack { get; }
    public JobsViewModel Jobs { get; }
    public SettingsViewModel Settings { get; }
    public ExtractorsViewModel Extractors { get; }
    public ObservableCollection<NavigationItem> Navigation { get; }
    public NavigationItem Selected { get => _selected; set { if (Set(ref _selected, value)) { Raise(nameof(CurrentPage)); if (value.Page is ResultsViewModel results) results.SelectedJob = Jobs.Selected ?? results.SelectedJob; } } }
    public object CurrentPage => Selected.Page;
    public ShellViewModel(AppServices services)
    {
        Services = services;
        Jobs = new(services); Settings = new(services); Extractors = new(services);
        Attack = new(services, Jobs, () => Selected = Navigation!.First(item => item.Page == Jobs));
        Navigation = [new("Attack", "\uE945", Attack), new("Jobs", "\uE9D9", Jobs), new("Results", "\uE8D7", new ResultsViewModel(services, Jobs)), new("Hardware", "\uE7F4", new HardwareViewModel(services)), new("Extractors", "\uE8B7", Extractors), new("Settings / About", "\uE713", Settings)];
        Jobs.ResultsRequested += async job =>
        {
            var page = Navigation.Single(item => item.Page is ResultsViewModel);
            Selected = page;
            await ((ResultsViewModel)page.Page).OpenAsync(job, reveal: true);
        };
        _selected = Navigation[0];
        services.BackendChanged += () => { if (_initialized) _ = RefreshBackendDataAsync(); };
    }
    public async Task InitializeAsync()
    {
        await Services.InitializeAsync(); Settings.Load();
        await Jobs.InitializeAsync(); await Attack.InitializeAsync(); await Extractors.RefreshAsync();
        _initialized = true;
        if (Services.Installation is null) ShowSettings();
    }
    private async Task RefreshBackendDataAsync()
    {
        try { await Attack.Inspector.LoadCatalogAsync(); await Extractors.RefreshAsync(); }
        catch (Exception exception) { Services.ReportError(exception); }
    }
    public void ShowSettings() => Selected = Navigation.First(item => item.Page == Settings);
    public async Task<bool> RequestCloseAsync()
    {
        if (Jobs.HasRunning && !Services.Dialogs.Confirm("There are running recovery jobs. Close HashLynx and stop them? To preserve a checkpoint first, choose No and use Checkpoint in Jobs.", "Close HashLynx")) return false;
        Services.CancelPendingOperations(); await Jobs.StopAllAsync(); return true;
    }
}
