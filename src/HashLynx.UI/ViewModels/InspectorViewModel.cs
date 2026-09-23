using HashLynx.Core;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed class InspectorViewModel : ObservableObject
{
    private readonly AppServices _services;
    private List<HashMode> _catalog = [];
    private int _inputMode;
    private long _revision;
    public long TargetRevision => _revision;
    private CancellationTokenSource _targetCancellation;
    private string _hashText = "", _targetPath = "", _summary = "Choose a target, then analyze it with your installed Hashcat release.", _search = "", _context = "Unknown / Not sure", _problems = "";
    private HashMode? _selectedMode;
    private string _validationMessage = "";
    private string _extractionNotice = "";
    public string ExtractionNotice { get => _extractionNotice; private set => Set(ref _extractionNotice, value); }
    private IReadOnlyList<int> _extractionSuggestedModes = [];
    public int InputMode { get => _inputMode; set { if (Set(ref _inputMode, value)) Invalidate(); } }
    public string HashText { get => _hashText; set { if (Set(ref _hashText, value)) Invalidate(); } }
    public string TargetPath { get => _targetPath; set { if (Set(ref _targetPath, value)) Invalidate(); } }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string Problems { get => _problems; set => Set(ref _problems, value); }
    public string ValidationMessage { get => _validationMessage; set => Set(ref _validationMessage, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) Filter(); } }
    public string Context { get => _context; set => Set(ref _context, value); }
    public HashMode? SelectedMode { get => _selectedMode; set { if (Set(ref _selectedMode, value)) { Raise(nameof(SelectedMatch)); Raise(nameof(SelectedCatalogMode)); } } }
    public HashMode? SelectedMatch { get => SelectedMode; set { if (value is not null) SelectedMode = value; } }
    public HashMode? SelectedCatalogMode { get => SelectedMode; set { if (value is not null) SelectedMode = value; } }
    public string? PreparedTargetPath { get; private set; }
    public ObservableCollection<HashMode> Matches { get; } = [];
    public ObservableCollection<HashMode> Modes { get; } = [];
    public IReadOnlyList<string> Contexts { get; } = ["Unknown / Not sure", "Windows", "Active Directory", "Linux / Unix", "Web application", "Database", "Wi-Fi", "Encrypted archive", "Encrypted document", "Password manager", "Disk encryption", "Other"];
    public ICommand BrowseCommand { get; }
    public ICommand DropCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand RefreshCatalogCommand { get; }
    public InspectorViewModel(AppServices services)
    {
        _services = services;
        _targetCancellation = CancellationTokenSource.CreateLinkedTokenSource(services.LifetimeToken);
        BrowseCommand = new RelayCommand(_ => { if (services.Dialogs.OpenFiles(InputMode == 2 ? "Select an encrypted file or disk image" : "Select a hash file").FirstOrDefault() is { } path) TargetPath = path; });
        DropCommand = new RelayCommand(files => { if (files is string[] { Length: > 0 } paths) { if (InputMode == 0) InputMode = 1; TargetPath = paths[0]; } });
        AnalyzeCommand = new AsyncCommand(_ => AnalyzeAsync(), services.ReportError);
        RefreshCatalogCommand = new AsyncCommand(_ => LoadCatalogAsync(), services.ReportError);
    }
    private void Invalidate() { _targetCancellation.Cancel(); _targetCancellation.Dispose(); _targetCancellation = CancellationTokenSource.CreateLinkedTokenSource(_services.LifetimeToken); _revision++; PreparedTargetPath = null; ExtractionNotice = ""; _extractionSuggestedModes = []; Matches.Clear(); SelectedMode = null; Summary = "Target changed. Analyze it or choose a hash mode explicitly."; Problems = ""; }
    private void VerifyRevision(long revision) { if (revision != _revision) throw new InvalidOperationException("The target changed while analysis was running. Analyze the current target again."); }
    public async Task LoadCatalogAsync()
    {
        if (_services.Installation is null) return;
        _catalog = (await _services.Backend.GetHashModesAsync(_services.Installation, _services.LifetimeToken)).ToList();
        Filter();
    }
    public HashMode? FindMode(int mode) => _catalog.FirstOrDefault(item => item.Mode == mode);
    private void Filter()
    {
        Modes.Clear();
        foreach (var mode in _catalog.Where(mode => string.IsNullOrWhiteSpace(Search) || mode.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase) || mode.Category.Contains(Search, StringComparison.OrdinalIgnoreCase))) Modes.Add(mode);
    }
    public async Task<string> PrepareTargetAsync()
    {
        if (PreparedTargetPath is not null) return PreparedTargetPath;
        var revision = _revision;
        var cancellationToken = _targetCancellation.Token;
        var target = TargetPath;
        string? prepared = null;
        if (InputMode == 0)
        {
            if (string.IsNullOrWhiteSpace(HashText)) throw new InvalidOperationException("Paste a hash in Target Inspector first.");
            prepared = await _services.Store.Paths.CreateTargetAsync(HashText.Trim() + Environment.NewLine, cancellationToken);
        }
        else if (InputMode == 1)
        {
            if (!File.Exists(target)) throw new InvalidOperationException("Choose an existing hash file.");
            prepared = target;
        }
        else
        {
            if (!File.Exists(target)) throw new InvalidOperationException("Choose an existing encrypted file.");
            var candidates = await _services.Extractors.FindCandidatesAsync(target, cancellationToken);
            if (candidates.Count == 0) throw new InvalidOperationException("No registered extractor recognizes this file. See Extractors for supported formats.");
            var diagnostics = new List<string>();
            foreach (var extractor in candidates)
            {
                var availability = await extractor.GetAvailabilityAsync(cancellationToken);
                if (!availability.IsAvailable) { diagnostics.Add($"{extractor.DisplayName}: {availability.Diagnostic}"); continue; }
                VerifyRevision(revision);
                var result = await extractor.ExtractAsync(target, cancellationToken);
                if (!result.Success) { diagnostics.AddRange(result.Diagnostics); continue; }
                prepared = await _services.Store.Paths.CreateTargetAsync(string.Join(Environment.NewLine, result.Hashes) + Environment.NewLine, cancellationToken);
                VerifyRevision(revision);
                Summary = $"{result.ExtractorName} extracted {result.Hashes.Count} hash record(s).";
                ExtractionNotice = string.Join(Environment.NewLine, result.Diagnostics);
                _extractionSuggestedModes = result.SuggestedHashcatModes;
                break;
            }
            if (prepared is null) throw new InvalidOperationException(string.Join(Environment.NewLine, diagnostics));
        }
        VerifyRevision(revision);
        PreparedTargetPath = prepared;
        return PreparedTargetPath;
    }
    public async Task AnalyzeAsync()
    {
        var installation = _services.RequireBackend();
        var revision = _revision;
        var cancellationToken = _targetCancellation.Token;
        Summary = InputMode == 2 ? "Checking extractors and reading encrypted file metadata…" : "Analyzing with Hashcat…";
        var path = await PrepareTargetAsync();
        var analysis = await new HashFileAnalyzer().AnalyzeAsync(path, cancellationToken);
        var matches = await _services.Backend.IdentifyAsync(installation, path, cancellationToken);
        VerifyRevision(revision);
        Problems = string.Join(Environment.NewLine, analysis.Problems.Select(problem => $"Line {problem.LineNumber}: {problem.Reason}").Concat(analysis.StructuralGroups.Select(group => $"Structure: {group.Key} — {group.Value:N0} lines")));
        Matches.Clear(); foreach (var match in matches) Matches.Add(match);
        var suggestedMatches = matches.Where(mode => _extractionSuggestedModes.Contains(mode.Mode)).ToArray();
        SelectedMode = matches.Count == 1 ? matches[0] : suggestedMatches.Length == 1 ? suggestedMatches[0] : null;
        Summary = $"{analysis.TotalLines:N0} lines · {analysis.CandidateLines:N0} candidates · {analysis.BlankLines:N0} blank · {analysis.ProblemLines:N0} problem lines. " +
            (matches.Count == 1 ? "One Hashcat match selected; confirm it matches your source." : SelectedMode is not null ? "The extractor's mode was confirmed by Hashcat and selected from the compatible modes." : matches.Count > 1 ? $"{matches.Count} possible modes. Select the correct mode explicitly; shape alone is ambiguous." : "Hashcat found no matches. Check your input or select a mode manually.");
    }
}
