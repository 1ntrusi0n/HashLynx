using HashLynx.Core;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed class ResultViewModel(RecoveredResult result, JobViewModel job) : ObservableObject
{
    private bool _reveal;
    public RecoveredResult Result { get; } = result;
    public Guid SessionId => job.Record.Id;
    public string Session => job.Name;
    public string Hash => Result.Hash;
    public string Plaintext => _reveal ? Result.Plaintext : "••••••••";
    public void Reveal(bool reveal) { _reveal = reveal; Raise(nameof(Plaintext)); }
}

public sealed class ResultsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _reveal, _showAllSessions;
    private JobViewModel? _selectedJob;
    private ResultViewModel? _selectedResult;
    private string _status = "Select a session to see its recovered hashes and passwords.";
    private int _loadVersion;
    private CancellationTokenSource? _loadCancellation;
    public Task Loading { get; private set; } = Task.CompletedTask;
    public ObservableCollection<JobViewModel> Jobs { get; }
    public ObservableCollection<ResultViewModel> Results { get; } = [];
    public JobViewModel? SelectedJob { get => _selectedJob; set { if (Set(ref _selectedJob, value) && !ShowAllSessions) ResetAndLoad(); } }
    public bool ShowAllSessions { get => _showAllSessions; set { if (Set(ref _showAllSessions, value)) { Raise(nameof(SingleSession)); ResetAndLoad(); } } }
    public bool SingleSession => !ShowAllSessions;
    private void ResetAndLoad()
    {
        _loadVersion++; _loadCancellation?.Cancel(); Results.Clear(); SelectedResult = null; Reveal = false;
        Status = "Select a session to see its recovered results.";
        if (ShowAllSessions || SelectedJob is not null) Loading = LoadReportedAsync();
        CommandManager.InvalidateRequerySuggested();
    }
    public ResultViewModel? SelectedResult { get => _selectedResult; set { if (Set(ref _selectedResult, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool Reveal { get => _reveal; set { if (Set(ref _reveal, value)) foreach (var row in Results) row.Reveal(value); } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public ICommand LoadCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand ExportCommand { get; }
    public ResultsViewModel(AppServices services, JobsViewModel jobs)
    {
        _services = services; Jobs = jobs.Jobs;
        Jobs.CollectionChanged += (_, _) => { if (SelectedJob is not null && !Jobs.Contains(SelectedJob)) SelectedJob = null; if (ShowAllSessions) ResetAndLoad(); };
        LoadCommand = new AsyncCommand(_ => RefreshAsync(), services.ReportError, _ => ShowAllSessions || SelectedJob is not null);
        CopyCommand = new RelayCommand(_ => { if (SelectedResult is not null && Results.Contains(SelectedResult)) { services.Dialogs.Copy(SelectedResult.Result.Plaintext); Status = "Selected plaintext copied to your Windows clipboard."; } }, _ => SelectedResult is not null && Results.Contains(SelectedResult));
        ExportCommand = new AsyncCommand(_ => ExportAsync(), services.ReportError);
    }
    public async Task OpenAsync(JobViewModel job, bool reveal)
    {
        if (!Jobs.Contains(job)) return;
        if (ShowAllSessions) { SelectedJob = job; ShowAllSessions = false; }
        else if (SelectedJob == job) { if (Loading.IsCompleted) Loading = LoadReportedAsync(); }
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
        catch (Exception exception) { if (version == _loadVersion) { Results.Clear(); SelectedResult = null; Status = "Could not load results. Check the session output files and permissions, then refresh."; _services.ReportError(exception); } }
        finally { if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null; }
    }
    private async Task LoadAsync(int version, CancellationToken cancellationToken)
    {
        var selected = SelectedJob;
        var all = ShowAllSessions;
        if (!all && selected is null) return;
        var history = Jobs.ToArray();
        var jobs = all ? history : new[] { selected! };
        Status = all ? "Loading results from all sessions in history..." : "Loading this session's recovered hashes and passwords...";
        var rows = new List<ResultViewModel>();
        var failures = 0;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var output = _services.Backend.Commands.GetOutputPath(job.Record.Configuration);
                if (history.Count(item => string.Equals(_services.Backend.Commands.GetOutputPath(item.Record.Configuration), output, StringComparison.OrdinalIgnoreCase)) > 1)
                    throw new InvalidOperationException("Multiple sessions share a result file, so their recovered passwords cannot be attributed safely. Use separate output files for new sessions.");
                var recovered = await _services.Backend.ReadSessionResultsAsync(job.Record.Configuration, cancellationToken);
                rows.AddRange(recovered.Select(result => new ResultViewModel(result, job)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures++;
                if (version == _loadVersion) _services.ReportError(exception);
            }
        }
        if (version != _loadVersion || all != ShowAllSessions || (!all && SelectedJob != selected)) return;
        Results.Clear(); SelectedResult = null;
        foreach (var row in rows) { row.Reveal(Reveal); Results.Add(row); }
        Status = Results.Count == 0
            ? "No saved recoveries found for this selection. These sessions may have found no new passwords, or their output files may be missing."
            : all ? $"{Results.Count:N0} result(s) from {rows.Select(row => row.SessionId).Distinct().Count():N0} session(s) in history. Each row identifies its source session."
            : $"{Results.Count:N0} result(s) written by this session. Select a row to copy its password, or export the results.";
        if (failures > 0) Status += $" Results from {failures:N0} session(s) could not be read or attributed. Check their output files; those rows were excluded.";
    }

    private async Task ExportAsync()
    {
        if (Results.Count == 0) throw new InvalidOperationException("Load recovered results before exporting.");
        if (_services.Dialogs.SaveFile("Export recovered credentials", "hashlynx-results.csv", "CSV file|*.csv") is not { } path) return;
        static string Escape(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        var lines = new[] { "Session,SessionId,Hash,Plaintext" }.Concat(Results.Select(row => Escape(row.Session) + "," + Escape(row.SessionId.ToString()) + "," + Escape(row.Result.Hash) + "," + Escape(row.Result.Plaintext))).ToArray();
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true));
        Status = $"Exported {Results.Count:N0} recovered results to your chosen CSV file.";
    }
}
