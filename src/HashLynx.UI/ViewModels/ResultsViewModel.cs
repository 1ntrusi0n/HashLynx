using HashLynx.Core;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed class ResultViewModel(RecoveredResult result) : ObservableObject
{
    private bool _reveal;
    public RecoveredResult Result { get; } = result;
    public string Hash => Result.Hash;
    public string Plaintext => _reveal ? Result.Plaintext : "••••••••";
    public void Reveal(bool reveal) { _reveal = reveal; Raise(nameof(Plaintext)); }
}

public sealed class ResultsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _reveal;
    private JobViewModel? _selectedJob;
    private ResultViewModel? _selectedResult;
    private string _status = "Select a session to see its recovered hashes and passwords.";
    private int _loadVersion;
    private CancellationTokenSource? _loadCancellation;
    public Task Loading { get; private set; } = Task.CompletedTask;
    public ObservableCollection<JobViewModel> Jobs { get; }
    public ObservableCollection<ResultViewModel> Results { get; } = [];
    public JobViewModel? SelectedJob { get => _selectedJob; set { if (Set(ref _selectedJob, value)) { _loadVersion++; _loadCancellation?.Cancel(); Results.Clear(); SelectedResult = null; Reveal = false; Status = "Select a session to see its recovered results."; if (value is not null) Loading = LoadReportedAsync(); } } }
    public ResultViewModel? SelectedResult { get => _selectedResult; set { if (Set(ref _selectedResult, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool Reveal { get => _reveal; set { if (Set(ref _reveal, value)) foreach (var row in Results) row.Reveal(value); } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public ICommand LoadCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand ExportCommand { get; }
    public ResultsViewModel(AppServices services, JobsViewModel jobs)
    {
        _services = services; Jobs = jobs.Jobs;
        Jobs.CollectionChanged += (_, _) => { if (SelectedJob is not null && !Jobs.Contains(SelectedJob)) SelectedJob = null; };
        LoadCommand = new AsyncCommand(_ => RefreshAsync(), services.ReportError, _ => SelectedJob is not null);
        CopyCommand = new RelayCommand(_ => { if (SelectedResult is not null && Results.Contains(SelectedResult)) { services.Dialogs.Copy(SelectedResult.Result.Plaintext); Status = "Selected plaintext copied to your Windows clipboard."; } }, _ => SelectedResult is not null && Results.Contains(SelectedResult));
        ExportCommand = new AsyncCommand(_ => ExportAsync(), services.ReportError);
    }
    public async Task OpenAsync(JobViewModel job, bool reveal)
    {
        if (!Jobs.Contains(job)) return;
        if (SelectedJob == job) { if (Loading.IsCompleted) Loading = LoadReportedAsync(); }
        else SelectedJob = job;
        Reveal = reveal;
        await Loading;
    }
    private Task RefreshAsync() => Loading = LoadReportedAsync();
    private async Task LoadReportedAsync()
    {
        var version = ++_loadVersion;
        _loadCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_services.LifetimeToken);
        _loadCancellation = cancellation;
        try { await LoadAsync(version, cancellation.Token); }
        catch (OperationCanceledException) { if (version == _loadVersion) Status = "Result loading cancelled."; }
        catch (Exception exception) { if (version == _loadVersion) { Results.Clear(); SelectedResult = null; Status = "Could not load results. Check the backend and the session's target/output files, then refresh."; _services.ReportError(exception); } }
        finally { if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null; }
    }
    private async Task LoadAsync(int version, CancellationToken cancellationToken)
    {
        if (SelectedJob is null) throw new InvalidOperationException("Choose a job first.");
        var job = SelectedJob;
        Status = "Loading recovered hashes and passwords…";
        var results = await _services.Backend.ShowAsync(_services.RequireBackend(), job.Record.Configuration, cancellationToken);
        if (version != _loadVersion || SelectedJob != job || !Jobs.Contains(job)) return;
        Results.Clear(); SelectedResult = null;
        foreach (var result in results) { var row = new ResultViewModel(result); row.Reveal(Reveal); Results.Add(row); }
        Status = Results.Count == 0 ? "No recovered passwords found for this session. If it is still running, refresh after a recovery." : $"{Results.Count:N0} recovered result(s). Select a row to copy its password, or export the results.";
    }
    private async Task ExportAsync()
    {
        if (Results.Count == 0) throw new InvalidOperationException("Load recovered results before exporting.");
        if (_services.Dialogs.SaveFile("Export recovered credentials", "hashlynx-results.csv", "CSV file|*.csv") is not { } path) return;
        static string Escape(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        var lines = new[] { "Hash,Plaintext" }.Concat(Results.Select(row => Escape(row.Result.Hash) + "," + Escape(row.Result.Plaintext))).ToArray();
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true));
        Status = $"Exported {Results.Count:N0} recovered results to your chosen CSV file.";
    }
}
