using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Input;
using HashLynx.Core;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;

namespace HashLynx.UI.ViewModels;

public sealed class RecoveryQueueRow(RecoveryPlan plan, RecoveryStep step, int number) : ObservableObject
{
    public RecoveryPlan Plan { get; } = plan;
    public RecoveryStep Step { get; } = step;
    public string Sequence => Plan.Name;
    public int Number => number;
    public string Name => Step.Name;
    public string State => Step.State.ToString();
    public string Message => Step.Message;
    public void Refresh() => Raise("");
}

public sealed class RecoveryQueueViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly JobsViewModel _jobs;
    private readonly Func<HashcatJob, Task<JobRecord>> _run;
    private RecoveryQueueDocument _document = new();
    private bool _running, _pause, _loaded, _editing;
    private Task _worker = Task.CompletedTask;
    private RecoveryQueueRow? _selected;
    private string _status = "Add attacks or a guided sequence from the Attack page.";
    public ObservableCollection<RecoveryQueueRow> Rows { get; } = [];
    public RecoveryQueueRow? Selected { get => _selected; set { Set(ref _selected, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsRunning { get => _running; private set { Set(ref _running, value); _services.IsQueueActive = value; CommandManager.InvalidateRequerySuggested(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool CanEdit => _loaded && !IsRunning && !_editing;
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand ViewSessionCommand { get; }
    public event Action? SessionRequested;

    public RecoveryQueueViewModel(AppServices services, JobsViewModel jobs, Func<HashcatJob, Task<JobRecord>>? run = null)
    {
        _services = services; _jobs = jobs; _run = run ?? jobs.StartAndWaitAsync;
        StartCommand = new AsyncCommand(_ => StartAsync(), services.ReportError, _ => CanEdit && !_services.IsComputeBusy && Rows.Any(row => row.Step.State == RecoveryStepState.Pending
            || (row.Step.State is RecoveryStepState.Interrupted or RecoveryStepState.Failed && jobs.Jobs.Any(job => job.Record.Id == row.Step.LastJobId && job.Record.State is JobState.Cracked or JobState.Exhausted))));
        PauseCommand = new RelayCommand(_ => RequestPause(), _ => IsRunning);
        RemoveCommand = new AsyncCommand(_ => EditAsync(() => { if (Selected is { } row) _document.Plans.Remove(row.Plan); }), services.ReportError, _ => CanEdit && Selected is not null);
        RetryCommand = new AsyncCommand(_ => EditAsync(() => { if (Selected is { } row) { row.Step.Configuration = FreshAttempt(row.Step.Configuration, row.Plan.Id); row.Step.State = RecoveryStepState.Pending; row.Step.Message = "Retry queued; prior session results are kept."; row.Step.LastJobId = null; } }), services.ReportError,
            _ => CanEdit && Selected?.Step.State is RecoveryStepState.Failed or RecoveryStepState.Interrupted);
        SkipCommand = new AsyncCommand(_ => EditAsync(() => { if (Selected is { } row) { row.Step.State = RecoveryStepState.Skipped; row.Step.Message = "Skipped by you."; } }), services.ReportError,
            _ => CanEdit && Selected?.Step.State is RecoveryStepState.Pending or RecoveryStepState.Failed or RecoveryStepState.Interrupted);
        MoveUpCommand = new AsyncCommand(_ => MoveAsync(-1), services.ReportError, _ => CanMove(-1));
        MoveDownCommand = new AsyncCommand(_ => MoveAsync(1), services.ReportError, _ => CanMove(1));
        ViewSessionCommand = new RelayCommand(_ => { if (FindSelectedJob() is { } job) { jobs.Selected = job; SessionRequested?.Invoke(); } }, _ => FindSelectedJob() is not null);
    }

    public async Task InitializeAsync()
    {
        _document = await _services.Store.LoadQueueAsync(_services.LifetimeToken);
        RecoveryQueueTransitions.Reconcile(_document, _jobs.Jobs.Select(job => job.Record).ToArray());
        await SaveAsync(); _loaded = true; Rebuild();
        if (Rows.Count > 0) Status = "Saved queue loaded and paused. Review the steps, then choose Start queue.";
    }

    public async Task AddAsync(HashcatJob template, IReadOnlyList<(string Name, AttackConfiguration Attack)> attacks, bool append = false, Func<bool>? stillCurrent = null)
    {
        if (!CanEdit) throw new InvalidOperationException("Pause the queue and wait for its current step to finish before editing it.");
        if (attacks.Count is < 1 or > 64) throw new InvalidOperationException("A sequence needs between 1 and 64 steps.");
        _editing = true;
        var snapshot = Clone(_document);
        try
        {
            RecoveryPlan plan;
            if (append)
            {
                plan = Selected?.Plan ?? throw new InvalidOperationException("Select a sequence on Queue before appending an attack.");
                if (plan.Steps.Any(step => step.State != RecoveryStepState.Pending) || plan.Steps.Count + attacks.Count > 64)
                    throw new InvalidOperationException("Append to an unstarted sequence with room for these steps, or create a new sequence.");
                var first = plan.Steps[0].Configuration;
                if (first.HashMode != template.HashMode || !await SameFileContentsAsync(first.TargetPath, template.TargetPath))
                    throw new InvalidOperationException("The selected sequence has a different target or hash mode. Create a new sequence for this target.");
                template = Clone(template); template.TargetPath = first.TargetPath;
            }
            else
            {
                plan = new RecoveryPlan { Name = $"Sequence {_document.Plans.Count + 1} - {DateTime.Now:HH:mm}" };
                var directory = PlanDirectory(plan.Id); Directory.CreateDirectory(directory);
                var target = Path.Combine(directory, "target.hashes");
                await using (var input = new FileStream(template.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await input.CopyToAsync(output, _services.LifetimeToken);
                template = Clone(template); template.TargetPath = target;
                _document.Plans.Add(plan);
            }
            foreach (var (name, attack) in attacks)
            {
                var job = FreshAttempt(template, plan.Id); job.Attack = Clone(attack); job.Name = $"{plan.Name} / {name}";
                var validation = _services.Backend.ValidateJob(_services.RequireBackend(), job);
                if (!validation.IsValid) throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message)));
                plan.Steps.Add(new() { Name = name, Configuration = job });
            }
            if (stillCurrent is not null && !stillCurrent()) throw new InvalidOperationException("Hints changed while preparing the sequence. Preview the updated hints again.");
            await SaveAsync(); Rebuild(); Selected = Rows.Last(row => row.Plan == plan);
            Status = $"Added {attacks.Count} step(s). Review the queue and choose Start queue when ready.";
        }
        catch { _document = snapshot; Rebuild(); throw; }
        finally { _editing = false; CommandManager.InvalidateRequerySuggested(); }
    }

    public Task StartAsync()
    {
        if (!CanEdit || _services.IsComputeBusy) throw new InvalidOperationException("Wait for the current operation to finish before starting the queue.");
        RecoveryQueueTransitions.Reconcile(_document, _jobs.Jobs.Select(job => job.Record).ToArray());
        Rebuild(); _pause = false; IsRunning = true; _worker = RunAsync();
        return Task.CompletedTask;
    }

    public void RequestPause() { _pause = true; _jobs.CancelPendingQueueLaunch(); Status = "Queue will pause after the current step. To stop that step too, use Stop on Jobs."; }
    public Task PauseBeforeStopAsync() { RequestPause(); return Task.CompletedTask; }
    public async Task ShutdownAsync() { RequestPause(); await _worker; }
    public Task WaitForIdleAsync() => _worker;

    private async Task RunAsync()
    {
        try
        {
            while (!_pause && !_services.LifetimeToken.IsCancellationRequested)
            {
                var next = RecoveryQueueTransitions.Next(_document);
                if (next is null) { Status = Rows.Any(row => row.Step.State is RecoveryStepState.Failed or RecoveryStepState.Interrupted)
                    ? "Queue paused at an unfinished step. View its session, then retry or skip it." : "Queue finished. View each session for its recovered passwords."; break; }
                var (plan, step) = next.Value;
                step.State = RecoveryStepState.Running; step.LastJobId = step.Configuration.Id; step.Message = "Running.";
                Selected = Rows.First(row => row.Step == step); Refresh();
                await SaveAsync();
                if (_pause || _services.LifetimeToken.IsCancellationRequested)
                {
                    step.State = RecoveryStepState.Pending; step.LastJobId = null; step.Message = "Waiting to run.";
                    await SaveAsync(); Status = "Queue paused before starting the next step."; break;
                }
                Status = $"Running {plan.Name}: {step.Name}";
                JobRecord record;
                try { record = await _run(Clone(step.Configuration)); }
                catch (Exception exception)
                {
                    step.State = exception is OperationCanceledException ? RecoveryStepState.Interrupted : RecoveryStepState.Failed;
                    step.Message = exception is OperationCanceledException ? "Stopped before recovery started. Retry when ready." : "Could not finish this step. Review the application message and session diagnostic.";
                    _services.ReportError(exception); _pause = true; await SaveAsync(); Refresh();
                    Status = "Queue paused because the step could not finish."; break;
                }
                RecoveryQueueTransitions.Complete(plan, step, record.State);
                await SaveAsync(); Refresh();
                if (step.State is RecoveryStepState.Failed or RecoveryStepState.Interrupted) { _pause = true; Status = "Queue paused. Review the stopped or failed session, then retry or skip its step."; }
            }
            if (_pause && Status.StartsWith("Running", StringComparison.Ordinal)) Status = "Queue paused. Remaining steps are saved.";
        }
        catch (Exception exception)
        {
            _pause = true;
            foreach (var step in _document.Plans.SelectMany(plan => plan.Steps).Where(step => step.State == RecoveryStepState.Running))
            { step.State = RecoveryStepState.Interrupted; step.Message = "Queue state could not be saved. Review its session before retrying."; }
            Status = "Queue paused: its state could not be saved. Resolve the storage error before continuing."; _services.ReportError(exception);
        }
        finally { IsRunning = false; Refresh(); }
    }

    private async Task EditAsync(Action edit)
    {
        if (!CanEdit) return;
        _editing = true; var snapshot = Clone(_document);
        try { edit(); await SaveAsync(); Rebuild(); Status = "Queue changes saved. Session results and files are kept."; }
        catch { _document = snapshot; Rebuild(); throw; }
        finally { _editing = false; CommandManager.InvalidateRequerySuggested(); }
    }
    private bool CanMove(int delta)
    {
        if (!CanEdit || Selected is not { } row || row.Step.State != RecoveryStepState.Pending) return false;
        var index = row.Plan.Steps.IndexOf(row.Step) + delta;
        return index >= 0 && index < row.Plan.Steps.Count && row.Plan.Steps[index].State == RecoveryStepState.Pending;
    }
    private Task MoveAsync(int delta) => !CanMove(delta) ? Task.CompletedTask : EditAsync(() =>
    {
        var row = Selected!; var index = row.Plan.Steps.IndexOf(row.Step);
        row.Plan.Steps.RemoveAt(index); row.Plan.Steps.Insert(index + delta, row.Step);
    });
    private JobViewModel? FindSelectedJob() => Selected?.Step.LastJobId is { } id ? _jobs.Jobs.FirstOrDefault(job => job.Record.Id == id) : null;
    private string PlanDirectory(Guid id) => Path.Combine(_services.Store.Paths.Root, "queue", id.ToString("N"));
    private HashcatJob FreshAttempt(HashcatJob template, Guid planId)
    {
        var job = Clone(template); job.Id = Guid.NewGuid();
        var directory = _services.Store.Paths.GetJobDirectory(job.Id);
        job.Options.SessionName = "hashlynx-" + job.Id.ToString("N")[..12];
        job.Options.OutputPath = Path.Combine(directory, "recovered.txt"); job.Options.RestorePath = Path.Combine(directory, "session.restore");
        job.Options.DisablePotfile = false; job.Options.PotfilePath = Path.Combine(PlanDirectory(planId), "sequence.potfile");
        return job;
    }
    private Task SaveAsync() => _services.Store.SaveQueueAsync(Clone(_document));
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private void Rebuild()
    {
        var previous = Selected?.Step.Configuration.Id;
        Rows.Clear(); foreach (var plan in _document.Plans) for (var i = 0; i < plan.Steps.Count; i++) Rows.Add(new(plan, plan.Steps[i], i + 1));
        Selected = Rows.FirstOrDefault(row => row.Step.Configuration.Id == previous) ?? Rows.FirstOrDefault(); Refresh();
    }
    private void Refresh() { foreach (var row in Rows) row.Refresh(); Raise(nameof(CanEdit)); CommandManager.InvalidateRequerySuggested(); }
    private async Task<bool> SameFileContentsAsync(string first, string second)
    {
        await using var left = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var right = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (left.Length != right.Length) return false;
        var leftHash = await SHA256.HashDataAsync(left, _services.LifetimeToken);
        var rightHash = await SHA256.HashDataAsync(right, _services.LifetimeToken);
        return leftHash.SequenceEqual(rightHash);
    }
}
