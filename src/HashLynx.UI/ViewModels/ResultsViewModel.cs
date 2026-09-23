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
    private string _status = "Select a job and load recovered results from Hashcat’s local potfile.";
    public ObservableCollection<JobViewModel> Jobs { get; }
    public ObservableCollection<ResultViewModel> Results { get; } = [];
    public JobViewModel? SelectedJob { get => _selectedJob; set { if (Set(ref _selectedJob, value)) { Results.Clear(); SelectedResult = null; Reveal = false; Status = "Load results for the selected job."; } } }
    public ResultViewModel? SelectedResult { get => _selectedResult; set { if (Set(ref _selectedResult, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool Reveal { get => _reveal; set { if (Set(ref _reveal, value)) foreach (var row in Results) row.Reveal(value); } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public ICommand LoadCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand ExportCommand { get; }
    public ResultsViewModel(AppServices services, JobsViewModel jobs)
    {
        _services = services; Jobs = jobs.Jobs;
        LoadCommand = new AsyncCommand(_ => LoadAsync(), services.ReportError);
        CopyCommand = new RelayCommand(_ => { if (SelectedResult is not null && Results.Contains(SelectedResult)) { services.Dialogs.Copy(SelectedResult.Result.Plaintext); Status = "Selected plaintext copied to your Windows clipboard."; } }, _ => SelectedResult is not null && Results.Contains(SelectedResult));
        ExportCommand = new AsyncCommand(_ => ExportAsync(), services.ReportError);
    }
    private async Task LoadAsync()
    {
        if (SelectedJob is null) throw new InvalidOperationException("Choose a job first.");
        var job = SelectedJob;
        var results = await _services.Backend.ShowAsync(_services.RequireBackend(), job.Record.Configuration, _services.LifetimeToken);
        if (SelectedJob != job) return;
        Results.Clear(); SelectedResult = null;
        foreach (var result in results) { var row = new ResultViewModel(result); row.Reveal(Reveal); Results.Add(row); }
        Status = $"{Results.Count:N0} recovered result(s). Plaintext stays hidden until you choose to reveal it.";
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
