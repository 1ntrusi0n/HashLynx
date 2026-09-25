using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using HashLynx.Core;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed record NavigationItem(string Name, string Glyph, object Page);

public sealed class ShellViewModel : ObservableObject
{
    private NavigationItem _selected;
    private bool _initialized;
    public AppServices Services { get; }
    public AttackViewModel Attack { get; }
    public JobsViewModel Jobs { get; }
    public RecoveryQueueViewModel Queue { get; }
    private readonly DesktopNotificationService _notifications = new();
    private JobViewModel? _completionJob;
    private CompletionFeedback? _completion;
    public bool CompletionVisible => _completion is not null;
    public string CompletionTitle => _completion?.Title ?? "";
    public string CompletionDetail => _completion?.Detail ?? "";
    public bool CompletionHasResults => _completion?.OfferResults == true;
    public string CompletionActionLabel => _completionJob?.NextActionLabel ?? "";
    public bool CompletionHasAction => _completionJob?.HasNextAction == true;
    public ICommand DismissCompletionCommand { get; }
    public ICommand OpenCompletionCommand { get; }
    public ICommand OpenCompletionSessionCommand { get; }
    public ICommand TryAnotherAttemptCommand { get; }
    public ICommand CompletionNextActionCommand { get; }
    public SettingsViewModel Settings { get; }
    public ExtractorsViewModel Extractors { get; }
    public ObservableCollection<NavigationItem> Navigation { get; }
    public NavigationItem Selected { get => _selected; set { if (Set(ref _selected, value)) { Raise(nameof(CurrentPage)); if (value.Page is ResultsViewModel results) results.SelectedJob = Jobs.Selected ?? results.SelectedJob; } } }
    public object CurrentPage => Selected.Page;
    public ShellViewModel(AppServices services)
    {
        Services = services;
        Jobs = new(services); Settings = new(services); Extractors = new(services);
        Queue = new(services, Jobs);
        Attack = new(services, Jobs, () => Selected = Navigation!.First(item => item.Page == Jobs));
        Navigation = [new("Attack", "\uE945", Attack), new("Jobs", "\uE9D9", Jobs), new("Results", "\uE8D7", new ResultsViewModel(services, Jobs)), new("Hardware", "\uE7F4", new HardwareViewModel(services)), new("Settings / About", "\uE713", Settings)];
        Navigation.Insert(1, new("Queue", "\uE8FD", Queue));
        Attack.ConnectQueue(Queue, () => Selected = Navigation.First(item => item.Page == Queue));
        Attack.NavigationRequested += destination =>
        {
            if (destination == "Hardware") ShowHardware();
            else if (destination == "Settings") ShowSettings();
        };
        Jobs.NextActionRequested += action =>
        {
            if (action == JobNextAction.CheckHardware) ShowHardware();
            else if (action == JobNextAction.CheckSettings) ShowSettings();
            else if (action == JobNextAction.CheckEarlierResults)
            {
                var page = Navigation.Single(item => item.Page is ResultsViewModel);
                ((ResultsViewModel)page.Page).ShowAllSessions = true;
                Selected = page;
            }
            else if (action is JobNextAction.ChooseAttempt or JobNextAction.ReviewInputs) Selected = Navigation.First(item => item.Page == Attack);
        };
        Queue.SessionRequested += () => Selected = Navigation.First(item => item.Page == Jobs);
        Jobs.BeforeStop = Queue.PauseBeforeStopAsync;
        Jobs.JobFinished += ShowCompletion;
        DismissCompletionCommand = new RelayCommand(_ => { _completion = null; RaiseCompletion(); });
        OpenCompletionSessionCommand = new RelayCommand(_ => { if (_completionJob is not null) Jobs.Selected = _completionJob; Selected = Navigation.First(item => item.Page == Jobs); });
        OpenCompletionCommand = new AsyncCommand(async _ =>
        {
            if (_completionJob is null) return;
            var page = Navigation.Single(item => item.Page is ResultsViewModel); Selected = page;
            await ((ResultsViewModel)page.Page).OpenAsync(_completionJob, reveal: true);
        }, services.ReportError);
        TryAnotherAttemptCommand = new RelayCommand(_ => Selected = Navigation.First(item => item.Page == Attack));
        CompletionNextActionCommand = new RelayCommand(_ =>
        {
            if (_completionJob is null) return;
            Jobs.Selected = _completionJob;
            if (Jobs.NextActionCommand.CanExecute(null)) Jobs.NextActionCommand.Execute(null);
        }, _ => _completionJob?.HasNextAction == true && (_completionJob.Outcome.Action != JobNextAction.Restore || (!Services.IsComputeBusy && !Services.IsQueueActive)));
        if (services.Extractors.All.Any(extractor => !extractor.IsBuiltIn))
            Navigation.Insert(Navigation.Count - 1, new("Extractors", "\uE8B7", Extractors));
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
        await Jobs.InitializeAsync(); await Queue.InitializeAsync(); await Attack.InitializeAsync(); await Extractors.RefreshAsync();
        _initialized = true;
        if (Services.Installation is null) ShowSettings();
    }
    private async Task RefreshBackendDataAsync()
    {
        try { await Attack.Inspector.LoadCatalogAsync(); await Extractors.RefreshAsync(); }
        catch (Exception exception) { Services.ReportError(exception); }
    }
    public void ShowSettings() => Selected = Navigation.First(item => item.Page == Settings);
    public void ShowHardware() => Selected = Navigation.First(item => item.Page is HardwareViewModel);
    public void ShowCompletion(JobViewModel job)
    {
        _completionJob = job; _completion = CompletionFeedback.From(job.Outcome); RaiseCompletion();
        if (Services.Settings.CompletionNotifications) _notifications.Show(_completion.OfferResults, () => { Jobs.Selected = job; Selected = Navigation.First(item => item.Page == Jobs); });
    }
    private void RaiseCompletion() { Raise(nameof(CompletionVisible)); Raise(nameof(CompletionTitle)); Raise(nameof(CompletionDetail)); Raise(nameof(CompletionHasResults)); Raise(nameof(CompletionActionLabel)); Raise(nameof(CompletionHasAction)); }
    public async Task<bool> RequestCloseAsync()
    {
        if (Jobs.HasRunning && !Services.Dialogs.Confirm("There are running recovery jobs. Close HashLynx and stop them? To preserve a checkpoint first, choose No and use Checkpoint in Jobs.", "Close HashLynx")) return false;
        Queue.RequestPause(); Services.CancelPendingOperations(); await Jobs.StopAllAsync(); await Queue.ShutdownAsync(); _notifications.Dispose(); return true;
    }
}
