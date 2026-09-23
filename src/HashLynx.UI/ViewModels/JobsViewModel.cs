using HashLynx.Core;
using HashLynx.Hashcat;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;

namespace HashLynx.UI.ViewModels;

public sealed class JobViewModel(JobRecord record) : ObservableObject
{
    public override string ToString() => Name;
    public JobRecord Record { get; } = record;
    public string Name => Record.Name;
    public string State => Record.LatestStatus?.State is { } status && Record.State is JobState.Running or JobState.Paused ? status : Record.State.ToString();
    public double Progress => Record.LatestStatus?.ProgressPercent ?? 0;
    public string Speed => Record.LatestStatus is { } status ? $"{status.SpeedHashesPerSecond:N0} H/s" : "Awaiting status";
    public string Recovered => Record.LatestStatus is { } status ? $"{status.RecoveredHashes:N0} / {status.TotalHashes:N0}" : "—";
    public string Eta => Record.LatestStatus?.EstimatedCompletion?.LocalDateTime.ToString("g") ?? "—";
    public string Elapsed => Record.StartedAt is { } start ? ((Record.FinishedAt ?? DateTimeOffset.UtcNow) - start).ToString(@"hh\:mm\:ss") : "—";
    public string Rejected => Record.LatestStatus?.RejectedCandidates.ToString("N0") ?? "—";
    public string RestorePoint => Record.LatestStatus?.RestorePoint.ToString("N0") ?? "—";
    public string Diagnostic => Record.Diagnostic ?? "Waiting for Hashcat status updates.";
    public string Session => Record.Configuration.Options.SessionName;
    public string Output => Record.Configuration.Options.OutputPath ?? "";
    public string Created => Record.CreatedAt.LocalDateTime.ToString("g");
    public IReadOnlyList<DeviceStatus> Devices => Record.LatestStatus?.Devices ?? [];
    public bool HasRecovered => Record.State == JobState.Cracked || Record.LatestStatus?.RecoveredHashes > 0;
    public string RecoveryMessage => HasRecovered ? "Passwords recovered. Open the results to see each hash and its password." : "Open results to check for passwords recovered during this session.";
    public bool CanRestore => Record.State is not (JobState.Running or JobState.Paused) && File.Exists(Record.RestorePath ?? Record.Configuration.Options.RestorePath);
    public void Refresh() => Raise("");
}

public sealed class JobsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<Guid, HashcatRunningJob> _running = [];
    private readonly List<Task> _observers = [];
    private Task _periodicSave = Task.CompletedTask;
    private DateTimeOffset _lastPersisted = DateTimeOffset.UtcNow;
    private readonly DispatcherTimer _clock;
    private JobViewModel? _selected;
    private readonly Stack<(JobViewModel Job, int Index)> _deleted = [];
    public ObservableCollection<JobViewModel> Jobs { get; } = [];
    public JobViewModel? Selected { get => _selected; set { if (Set(ref _selected, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool HasRunning => _running.Count > 0;
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CheckpointCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ViewResultsCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand UndoDeleteCommand { get; }
    public event Func<JobViewModel, Task>? ResultsRequested;
    public JobsViewModel(AppServices services)
    {
        _services = services;
        ViewResultsCommand = new AsyncCommand(async _ => { if (Selected is { } job && ResultsRequested is { } open) await open(job); }, services.ReportError, _ => Selected is not null);
        DeleteCommand = new AsyncCommand(_ => DeleteSelectedAsync(), services.ReportError, _ => Selected is { } job && CanDelete(job));
        UndoDeleteCommand = new AsyncCommand(_ => UndoDeleteAsync(), services.ReportError, _ => _deleted.Count > 0);
        PauseCommand = Control(HashcatControl.Pause); ResumeCommand = Control(HashcatControl.Resume); CheckpointCommand = Control(HashcatControl.Checkpoint); RefreshCommand = Control(HashcatControl.Status);
        StopCommand = new AsyncCommand(async _ => { if (Selected is { } job && _running.TryGetValue(job.Record.Id, out var running) && services.Dialogs.Confirm("Stop this recovery job? A checkpoint stop is available separately if you want Hashcat to finish the current checkpoint.", "Stop job")) await running.StopAsync(); }, services.ReportError, _ => Selected is not null && _running.ContainsKey(Selected.Record.Id));
        RestoreCommand = new AsyncCommand(async _ => { if (Selected is { CanRestore: true } job) await LaunchAsync(job, true); }, services.ReportError, _ => Selected?.CanRestore == true);
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _clock.Start();
    }
    private bool CanDelete(JobViewModel job) => Jobs.Contains(job) && !_running.ContainsKey(job.Record.Id) && job.Record.State is not (JobState.Running or JobState.Paused);
    private async Task DeleteSelectedAsync()
    {
        if (Selected is not { } job || !CanDelete(job)) return;
        var index = Jobs.IndexOf(job);
        Jobs.Remove(job);
        Selected = Jobs.ElementAtOrDefault(Math.Min(index, Jobs.Count - 1));
        try { await SaveAsync(); }
        catch { Jobs.Insert(Math.Min(index, Jobs.Count), job); Selected = job; throw; }
        _deleted.Push((job, index));
        _services.Notice = "Session removed from history. Undo delete restores it while HashLynx remains open. Recovery files were kept.";
        CommandManager.InvalidateRequerySuggested();
    }
    private async Task UndoDeleteAsync()
    {
        if (!_deleted.TryPop(out var deleted)) return;
        Jobs.Insert(Math.Min(deleted.Index, Jobs.Count), deleted.Job);
        Selected = deleted.Job;
        try { await SaveAsync(); }
        catch { Jobs.Remove(deleted.Job); Selected = Jobs.FirstOrDefault(); _deleted.Push(deleted); throw; }
        _services.Notice = "Session restored to history.";
        CommandManager.InvalidateRequerySuggested();
    }
    private void Tick()
    {
        foreach (var job in Jobs.Where(item => _running.ContainsKey(item.Record.Id))) job.Refresh();
        if (_running.Count > 0 && _periodicSave.IsCompleted && DateTimeOffset.UtcNow - _lastPersisted >= TimeSpan.FromSeconds(10))
        {
            _lastPersisted = DateTimeOffset.UtcNow; _periodicSave = PersistSnapshotAsync();
        }
    }
    private async Task PersistSnapshotAsync() { try { await SaveAsync(); } catch (Exception exception) { _services.ReportError(exception); } }
    private ICommand Control(HashcatControl control) => new AsyncCommand(async _ => { if (Selected is { } job && _running.TryGetValue(job.Record.Id, out var running)) { await running.SendControlAsync(control); _services.Notice = $"{control} requested. Hashcat may defer controls until its current work completes."; } }, _services.ReportError, _ => Selected is not null && _running.TryGetValue(Selected.Record.Id, out var running) && running.CanSendInteractiveControls);
    public async Task InitializeAsync()
    {
        foreach (var record in (await _services.Store.LoadJobsAsync()).OrderByDescending(record => record.CreatedAt))
        {
            if (record.State is JobState.Running or JobState.Paused) { record.State = JobState.Interrupted; record.Diagnostic = "HashLynx closed while this job was active. Restore is available when Hashcat saved a restore file."; }
            Jobs.Add(new JobViewModel(record));
        }
        Selected = Jobs.FirstOrDefault();
    }
    public async Task StartAsync(HashcatJob configuration)
    {
        var output = _services.Backend.Commands.GetOutputPath(configuration);
        if (Jobs.Any(job => string.Equals(_services.Backend.Commands.GetOutputPath(job.Record.Configuration), output, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another session already uses this output file. Choose a different output file so each session keeps its own results.");
        var record = new JobRecord { Id = configuration.Id, Name = configuration.Name, Configuration = configuration, RestorePath = configuration.Options.RestorePath };
        var viewModel = new JobViewModel(record);
        Jobs.Insert(0, viewModel); Selected = viewModel;
        await LaunchAsync(viewModel, false);
    }
    private async Task LaunchAsync(JobViewModel job, bool restore)
    {
        var backend = _services.RequireBackend();
        var progress = new Progress<HashcatEvent>(item =>
        {
            if (item.Status is not null) job.Record.LatestStatus = item.Status;
            if (item.Kind == "error" && item.Message is not null)
            {
                if (string.IsNullOrWhiteSpace(job.Record.Diagnostic) || job.Record.Diagnostic.StartsWith("Hashcat is running", StringComparison.Ordinal)) job.Record.Diagnostic = item.Message;
                else if (!job.Record.Diagnostic.Contains(item.Message, StringComparison.Ordinal) && job.Record.Diagnostic.Length < 1500) job.Record.Diagnostic += Environment.NewLine + item.Message;
            }
            job.Refresh();
        });
        HashcatRunningJob running;
        try { running = restore ? _services.Backend.RestoreJob(backend, job.Record.Configuration, progress) : _services.Backend.StartJob(backend, job.Record.Configuration, progress); }
        catch
        {
            job.Record.State = JobState.Failed; job.Record.Diagnostic = "Hashcat could not start. Validate the backend and review the preflight inputs."; job.Record.FinishedAt = DateTimeOffset.UtcNow; job.Refresh();
            await SaveAsync(); throw;
        }
        _running[job.Record.Id] = running;
        job.Record.State = JobState.Running; job.Record.StartedAt = DateTimeOffset.UtcNow; job.Record.FinishedAt = null;
        job.Record.Diagnostic = "Hashcat is running locally. Live statistics appear as the backend reports them.";
        job.Refresh(); Raise(nameof(HasRunning));
        _observers.RemoveAll(task => task.IsCompleted);
        _observers.Add(ObserveAsync(job, running));
        try { await SaveAsync(); await _services.Log.WriteAsync("job.started", "A local recovery session started."); }
        catch (Exception exception) { _services.ReportError(exception); }
    }
    private async Task ObserveAsync(JobViewModel job, HashcatRunningJob running)
    {
        try
        {
            var result = await running.Completion;
            job.Record.State = result.State; job.Record.ExitCode = result.ExitCode;
            var backendDiagnostic = job.Record.Diagnostic;
            job.Record.Diagnostic = result.Diagnostic;
            if (result.State == JobState.Failed && !string.IsNullOrWhiteSpace(backendDiagnostic) && !backendDiagnostic.StartsWith("Hashcat is running", StringComparison.Ordinal) && !result.Diagnostic.Contains(backendDiagnostic, StringComparison.Ordinal)) job.Record.Diagnostic += Environment.NewLine + backendDiagnostic;
        }
        catch (Exception exception) { job.Record.State = JobState.Failed; job.Record.Diagnostic = "The backend stopped unexpectedly. See the sanitized application log."; _services.ReportError(exception); }
        finally
        {
            _running.Remove(job.Record.Id); job.Record.FinishedAt = DateTimeOffset.UtcNow; job.Refresh(); Raise(nameof(HasRunning)); CommandManager.InvalidateRequerySuggested();
            if (job.HasRecovered) _services.Notice = "Passwords recovered. On Jobs, select the session and choose View recovered passwords.";
            try { await SaveAsync(); await _services.Log.WriteAsync("job.finished", "A local recovery session finished."); } catch (Exception exception) { _services.ReportError(exception); }
        }
    }
    public Task SaveAsync() => _services.Store.SaveJobsAsync(Jobs.Select(job => job.Record).ToList());
    public async Task StopAllAsync()
    {
        _clock.Stop();
        foreach (var running in _running.Values.ToArray()) await running.StopAsync();
        await Task.WhenAll(_observers.ToArray());
        await _periodicSave;
        await SaveAsync();
    }
}
