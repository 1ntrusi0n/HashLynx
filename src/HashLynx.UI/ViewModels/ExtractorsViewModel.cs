using HashLynx.Extractors;
using HashLynx.Persistence;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed class ExtractorViewModel : ObservableObject
{
    private readonly AppServices _services;
    private IHashExtractor _extractor;
    private string _status = "Checking…", _diagnostic = "", _version = "", _toolPath = "", _interpreterPath = "";
    public string Id => _extractor.Id;
    public string Name => _extractor.DisplayName;
    public string Description => _extractor.Description;
    public string Implementation => _extractor.ImplementationType;
    public string Extensions => string.Join(", ", _extractor.SupportedExtensions);
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Diagnostic { get => _diagnostic; set => Set(ref _diagnostic, value); }
    public string Version { get => _version; set => Set(ref _version, value); }
    public string ToolPath { get => _toolPath; set => Set(ref _toolPath, value); }
    public string InterpreterPath { get => _interpreterPath; set => Set(ref _interpreterPath, value); }
    public ICommand BrowseToolCommand { get; }
    public ICommand BrowseInterpreterCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand TestCommand { get; }
    public ExtractorViewModel(AppServices services, IHashExtractor extractor)
    {
        _services = services; _extractor = extractor;
        if (services.Settings.ExtractorTools.TryGetValue(Id, out var configuration)) { ToolPath = configuration.ToolPath ?? ""; InterpreterPath = configuration.InterpreterPath ?? ""; }
        BrowseToolCommand = new RelayCommand(_ => { if (services.Dialogs.OpenFiles("Locate the extractor executable or script").FirstOrDefault() is { } path) ToolPath = path; });
        BrowseInterpreterCommand = new RelayCommand(_ => { if (services.Dialogs.OpenFiles("Locate Python or Perl executable").FirstOrDefault() is { } path) InterpreterPath = path; });
        SaveCommand = new AsyncCommand(async _ => { await SaveAsync(); await RefreshAsync(false); }, services.ReportError);
        TestCommand = new AsyncCommand(async _ => { await SaveAsync(); await RefreshAsync(true); }, services.ReportError);
    }
    private async Task SaveAsync()
    {
        _services.Settings.ExtractorTools[Id] = new ExtractorToolSettings { ToolPath = string.IsNullOrWhiteSpace(ToolPath) ? null : ToolPath, InterpreterPath = string.IsNullOrWhiteSpace(InterpreterPath) ? null : InterpreterPath };
        await _services.Store.SaveSettingsAsync(_services.Settings);
        _services.RebuildExtractors(); _extractor = _services.Extractors.All.First(item => item.Id == Id);
        Raise(nameof(Description)); Raise(nameof(Implementation));
    }
    public async Task RefreshAsync(bool validate)
    {
        var availability = validate ? await _extractor.ValidateAsync(_services.LifetimeToken) : await _extractor.GetAvailabilityAsync(_services.LifetimeToken);
        Status = availability.IsAvailable ? "Available" : "Missing dependency";
        Diagnostic = availability.Diagnostic ?? ""; Version = availability.Version ?? "Version not reported";
        if (string.IsNullOrWhiteSpace(ToolPath)) ToolPath = availability.ToolPath ?? "";
        if (string.IsNullOrWhiteSpace(InterpreterPath)) InterpreterPath = availability.InterpreterPath ?? "";
    }
}

public sealed class ExtractorsViewModel(AppServices services) : ObservableObject
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    public ObservableCollection<ExtractorViewModel> Extractors { get; } = [];
    public ICommand RefreshCommand => new AsyncCommand(_ => RefreshAsync(), services.ReportError);
    public async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync(services.LifetimeToken);
        try
        {
            services.RebuildExtractors(); Extractors.Clear();
            foreach (var extractor in services.Extractors.All)
            {
                var row = new ExtractorViewModel(services, extractor); Extractors.Add(row); await row.RefreshAsync(false);
            }
        }
        finally { _refreshGate.Release(); }
    }
}
