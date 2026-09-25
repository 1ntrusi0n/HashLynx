using HashLynx.Core;
using HashLynx.Hashcat;
using HashLynx.Persistence;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed record RulePresetChoice(string? Id, string DisplayName, string Description, string Effort)
{
    public override string ToString() => DisplayName;
}

public sealed partial class AttackViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly JobsViewModel _jobs;
    private readonly Action _showJobs;
    private readonly BundledWordlists _bundledWordlists;
    private readonly RulePresetCatalog _rulePresets;
    private string? _starterWordlist;
    private RulePresetChoice _selectedRulePreset;
    private bool _useCustomRules, _rulesHelpVisible;
    private Guid _draftId = Guid.NewGuid();
    private int _family, _hybridDirection;
    private string _preview = "Prepare a target and attack, then run Preflight to generate the exact command.", _preflight = "Preflight has not run.", _session = "hashlynx-" + Guid.NewGuid().ToString("N")[..10];
    private bool _expert;
    private string _attackErrors = "", _commonErrors = "";
    private string _startFeedback = "HashLynx checks your target and files automatically before starting.";
    public InspectorViewModel Inspector { get; }
    public ObservableCollection<string> Wordlists { get; } = [];
    public ObservableCollection<string> Rules { get; } = [];
    public int Family { get => _family; set { if (Set(ref _family, value)) { Raise(nameof(UsesWordlists)); Raise(nameof(UsesMask)); Raise(nameof(IsHybrid)); Raise(nameof(IsCombinator)); Raise(nameof(IsDictionary)); Raise(nameof(UsesSingleRules)); RaiseRuleVisibility(); RaiseMaskVisibility(); } } }
    public bool IsDictionary => Family == 0;
    public bool UsesSingleRules => Expert && Family != 1;
    public bool UsesWordlists => Family is 0 or 2;
    public bool UsesMask => Family is 1 or 2;
    public bool IsHybrid => Family == 2;
    public bool IsCombinator => Family == 3;
    public int HybridDirection { get => _hybridDirection; set => Set(ref _hybridDirection, value); }
    public bool Expert { get => _expert; set { if (Set(ref _expert, value)) { RaiseRuleVisibility(); Raise(nameof(UsesSingleRules)); Raise(nameof(WordlistButtonLabel)); Raise(nameof(BasicMode)); Raise(nameof(RecoveryDeviceSummary)); if (!value) StartFeedback = "Basic mode uses the selected preset and saved recovery device. Expert custom options apply only in Expert mode."; } } }
    public bool BasicMode => !Expert;
    public IReadOnlyList<RulePresetChoice> RulePresets { get; }
    public RulePresetChoice SelectedRulePreset { get => _selectedRulePreset; set { if (value is not null && Set(ref _selectedRulePreset, value)) { Raise(nameof(PresetSummary)); Raise(nameof(PresetEffort)); } } }
    public bool UseCustomRules { get => _useCustomRules; set { if (Set(ref _useCustomRules, value)) RaiseRuleVisibility(); } }
    public bool ShowPresets => IsDictionary && !(Expert && UseCustomRules);
    public bool ShowCustomRuleChoice => IsDictionary && Expert;
    public bool ShowCustomRules => ShowCustomRuleChoice && UseCustomRules;
    public bool RulesHelpVisible { get => _rulesHelpVisible; set => Set(ref _rulesHelpVisible, value); }
    public string PresetSummary => SelectedRulePreset.Description;
    public string PresetEffort => SelectedRulePreset.Effort;
    public string StartFeedback { get => _startFeedback; set => Set(ref _startFeedback, value); }
    public string WordlistButtonLabel => "Add wordlists…";
    public string WordlistSummary => Wordlists.Any(path => !File.Exists(path)) ? "A selected wordlist is missing or unavailable. Reconnect its drive, add its new location, or choose another list before starting." : Wordlists.Count == 0 ? "Choose a wordlist or use the built-in starter list." : Wordlists.Count == 1 && Wordlists[0] == _starterWordlist ? "Built-in starter · 848 example words. Choose a suitable personal wordlist for broader coverage." : $"{Wordlists.Count} wordlist(s) selected. Each line supplies a starting word.";
    public string Preview { get => _preview; set => Set(ref _preview, value); }
    public string Preflight { get => _preflight; set => Set(ref _preflight, value); }
    public string AttackErrors { get => _attackErrors; set => Set(ref _attackErrors, value); }
    public string CommonErrors { get => _commonErrors; set => Set(ref _commonErrors, value); }
    public string Session { get => _session; set => Set(ref _session, value); }
    public string? SelectedWordlist { get; set; }
    public string? SelectedRule { get; set; }
    private string _mask = "?u?l?l?l?l?l?d?d";
    public string Mask { get => _mask; set => Set(ref _mask, value); }
    private string _maskFile = "";
    public string MaskFile { get => _maskFile; set => Set(ref _maskFile, value); }
    private string _charset1 = "";
    public string Charset1 { get => _charset1; set => Set(ref _charset1, value); }
    private string _charset2 = "";
    public string Charset2 { get => _charset2; set => Set(ref _charset2, value); }
    private string _charset3 = "";
    public string Charset3 { get => _charset3; set => Set(ref _charset3, value); }
    private string _charset4 = "";
    public string Charset4 { get => _charset4; set => Set(ref _charset4, value); }
    private bool _increment;
    public bool Increment { get => _increment; set => Set(ref _increment, value); }
    private int _incrementMinimum = 1;
    public int IncrementMinimum { get => _incrementMinimum; set => Set(ref _incrementMinimum, value); }
    private int _incrementMaximum = 8;
    public int IncrementMaximum { get => _incrementMaximum; set => Set(ref _incrementMaximum, value); }
    private string _leftWordlist = "";
    public string LeftWordlist { get => _leftWordlist; set => Set(ref _leftWordlist, value); }
    private string _rightWordlist = "";
    public string RightWordlist { get => _rightWordlist; set => Set(ref _rightWordlist, value); }
    private string _leftRule = "";
    public string LeftRule { get => _leftRule; set => Set(ref _leftRule, value); }
    private string _rightRule = "";
    public string RightRule { get => _rightRule; set => Set(ref _rightRule, value); }
    private bool _optimizedKernel;
    public bool OptimizedKernel { get => _optimizedKernel; set => Set(ref _optimizedKernel, value); }
    private bool _loopback;
    public bool Loopback { get => _loopback; set => Set(ref _loopback, value); }
    private bool _disablePotfile;
    public bool DisablePotfile { get => _disablePotfile; set => Set(ref _disablePotfile, value); }
    private string _potfilePath = "";
    public string PotfilePath { get => _potfilePath; set => Set(ref _potfilePath, value); }
    private string _outputPath = "";
    public string OutputPath { get => _outputPath; set => Set(ref _outputPath, value); }
    private string _devices = "";
    public string Devices { get => _devices; set { if (Set(ref _devices, value)) Raise(nameof(RecoveryDeviceSummary)); } }
    public string RecoveryDeviceSummary => Expert && !string.IsNullOrWhiteSpace(Devices)
        ? $"Recovery device: {Devices} (Expert override)."
        : $"Recovery device: {_services.DefaultDeviceSummary}. Change the default in Hardware.";
    private string _temperature = "";
    public string Temperature { get => _temperature; set => Set(ref _temperature, value); }
    private string _extraArguments = "";
    public string ExtraArguments { get => _extraArguments; set => Set(ref _extraArguments, value); }
    private int _workload = 2;
    public int Workload { get => _workload; set => Set(ref _workload, value); }
    public IReadOnlyList<int> Workloads { get; } = [1, 2, 3, 4];
    public IReadOnlyList<string> HybridDirections { get; } = ["Wordlist + Mask  →  word123", "Mask + Wordlist  →  123word"];
    public ICommand AddWordlistsCommand { get; }
    public ICommand DropWordlistsCommand { get; }
    public ICommand RemoveWordlistCommand { get; }
    public ICommand MoveWordlistUpCommand { get; }
    public ICommand MoveWordlistDownCommand { get; }
    public ICommand AddRulesCommand { get; }
    public ICommand BundledRulesCommand { get; }
    public ICommand DropRulesCommand { get; }
    public ICommand RemoveRuleCommand { get; }
    public ICommand BrowseMaskCommand { get; }
    public ICommand BrowseLeftCommand { get; }
    public ICommand BrowseRightCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand PreflightCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand ToggleRulesHelpCommand { get; }
    public ICommand UseStarterCommand { get; }
    public AttackViewModel(AppServices services, JobsViewModel jobs, Action showJobs)
    {
        _services = services; _jobs = jobs; _showJobs = showJobs; Inspector = new(services);
        services.DefaultDevicesChanged += () => Raise(nameof(RecoveryDeviceSummary));
        _bundledWordlists = new BundledWordlists(services.Store.Paths);
        _rulePresets = new RulePresetCatalog(Path.Combine(services.Store.Paths.Root, "rule-presets"));
        RulePresets = [new(null, "No Rules", "Try the words exactly as they appear in your wordlist.", "One candidate per input word, with no rule transformations."),
            .. _rulePresets.Presets.Select(preset => new RulePresetChoice(preset.Id, preset.DisplayName, preset.Description, $"{preset.CandidateCountLabel} · {preset.Coverage}"))];
        _selectedRulePreset = RulePresets[0];
        Wordlists.CollectionChanged += (_, _) => { Raise(nameof(WordlistSummary)); SyncSavedWordlistSelection(); };
        AddWordlistsCommand = new AsyncCommand(_ => AddWordlistsAsync(services.Dialogs.OpenFiles("Add wordlists to your saved library", true)), services.ReportError);
        DropWordlistsCommand = new AsyncCommand(files => files is string[] paths ? AddWordlistsAsync(paths) : Task.CompletedTask, services.ReportError);
        RemoveWordlistCommand = new RelayCommand(_ => { if (SelectedWordlist is { } item) Wordlists.Remove(item); });
        MoveWordlistUpCommand = new RelayCommand(_ => MoveWordlist(-1)); MoveWordlistDownCommand = new RelayCommand(_ => MoveWordlist(1));
        AddRulesCommand = new RelayCommand(_ => AddFiles(Rules, services.Dialogs.OpenFiles("Add rule files", true, "Hashcat rules|*.rule|All files|*.*")));
        BundledRulesCommand = new RelayCommand(_ => AddFiles(Rules, services.Dialogs.OpenFiles("Bundled Hashcat rules", true, "Hashcat rules|*.rule|All files|*.*", Path.Combine(services.Installation?.DirectoryPath ?? "", "rules"))));
        DropRulesCommand = new RelayCommand(files => { if (files is string[] paths) AddFiles(Rules, paths); });
        RemoveRuleCommand = new RelayCommand(_ => { if (SelectedRule is { } item) Rules.Remove(item); });
        BrowseMaskCommand = new RelayCommand(_ => { if (services.Dialogs.OpenFiles("Select a mask file", false, "Hashcat masks|*.hcmask|All files|*.*").FirstOrDefault() is { } path) { MaskFile = path; SelectedMaskSource = MaskSourceChoice.File; Raise(nameof(MaskFile)); } });
        BrowseLeftCommand = new AsyncCommand(_ => BrowseCombinatorWordlistAsync(true), services.ReportError);
        BrowseRightCommand = new AsyncCommand(_ => BrowseCombinatorWordlistAsync(false), services.ReportError);
        BrowseOutputCommand = new RelayCommand(_ => { if (services.Dialogs.SaveFile("Results output", "recovered.txt") is { } path) { OutputPath = path; Raise(nameof(OutputPath)); } });
        PreflightCommand = new AsyncCommand(async _ => { await PrepareAsync(); }, ReportAttackError);
        StartCommand = new AsyncCommand(async _ =>
        {
            var job = await PrepareAsync(); if (job is null) return;
            try { var launch = jobs.StartAsync(job); _showJobs(); await launch; }
            finally { if (jobs.Jobs.Any(item => item.Record.Id == job.Id)) ResetDraft(); }
        }, ReportAttackError, _ => !services.IsComputeBusy && !services.IsQueueActive);
        CopyCommand = new RelayCommand(_ => services.Dialogs.Copy(Preview));
        ToggleRulesHelpCommand = new RelayCommand(_ => RulesHelpVisible = !RulesHelpVisible);
        UseStarterCommand = new AsyncCommand(_ => UseStarterAsync(), services.ReportError);
        InitializeReadiness();
    }
    public async Task InitializeAsync()
    {
        Expert = _services.Settings.ExpertMode; Workload = _services.Settings.DefaultWorkloadProfile; Raise(nameof(Workload));
        await InitializeWordlistLibraryAsync();
        if (Wordlists.Count == 0) await UseStarterAsync();
        await Inspector.LoadCatalogAsync();
    }
    private void RaiseRuleVisibility() { Raise(nameof(ShowPresets)); Raise(nameof(ShowCustomRuleChoice)); Raise(nameof(ShowCustomRules)); }
    private void ReportAttackError(Exception exception) { if (exception is not OperationCanceledException) StartFeedback = exception.Message; _services.ReportError(exception); }
    private async Task UseStarterAsync()
    {
        _starterWordlist = await _bundledWordlists.GetStarterPathAsync(_services.LifetimeToken);
        Wordlists.Clear(); Wordlists.Add(_starterWordlist);
    }
    private void SelectWordlists(IEnumerable<string> paths)
    {
        var available = paths.Where(File.Exists).ToArray();
        if (available.Length == 0) return;
        if (!Expert) { Wordlists.Clear(); Wordlists.Add(available[0]); }
        else
        {
            if (Wordlists.Count == 1 && Wordlists[0] == _starterWordlist) Wordlists.Clear();
            AddFiles(Wordlists, available);
        }
    }
    private static void AddFiles(ObservableCollection<string> collection, IEnumerable<string> paths) { foreach (var path in paths.Where(File.Exists)) if (!collection.Contains(path)) collection.Add(path); }
    private void MoveWordlist(int delta) { var index = SelectedWordlist is null ? -1 : Wordlists.IndexOf(SelectedWordlist); if (index >= 0 && index + delta >= 0 && index + delta < Wordlists.Count) Wordlists.Move(index, index + delta); }
    private HashcatJob BuildDraft(string targetPath)
    {
        var kinds = new[] { AttackFamilies.Dictionary, AttackFamilies.Mask, HybridDirection == 0 ? AttackFamilies.HybridWordlistMask : AttackFamilies.HybridMaskWordlist, AttackFamilies.Combinator };
        var charsets = UsesCustomMaskOptions ? new[] { Charset1, Charset2, Charset3, Charset4 }.Select((text, index) => (text, index)).Where(item => !string.IsNullOrEmpty(item.text)).ToDictionary(item => item.index + 1, item => item.text) : [];
        var directory = _services.Store.Paths.GetJobDirectory(_draftId.ToString("N"));
        var session = Expert ? Session : "hashlynx-" + _draftId.ToString("N")[..10];
        return new HashcatJob
        {
            Id = _draftId, Name = session, TargetPath = targetPath, HashMode = Inspector.SelectedMode?.Mode ?? -1,
            Attack = new()
            {
                Kind = kinds[Family],
                Wordlists = IsCombinator ? [LeftWordlist, RightWordlist] : UsesWordlists ? Wordlists.ToList() : [],
                RulePresetId = ShowPresets ? SelectedRulePreset.Id : null,
                RuleFiles = ShowCustomRules ? Rules.ToList() : [],
                Mask = EffectiveMask,
                MaskFile = EffectiveMaskFile,
                MaskPresetId = EffectiveMaskPresetId,
                CustomCharsets = charsets,
                Increment = UsesCustomMaskOptions && Increment,
                IncrementMinimum = IncrementMinimum,
                IncrementMaximum = IncrementMaximum,
                LeftRule = UsesSingleRules ? LeftRule : null,
                RightRule = UsesSingleRules ? RightRule : null,
                Loopback = Expert && IsDictionary && Loopback
            },
            Options = BuildCommonOptions(directory, session)
        };
    }
    private CommonOptions BuildCommonOptions(string directory, string session)
    {
        var devices = new List<int>();
        if (Expert && !string.IsNullOrWhiteSpace(Devices))
            foreach (var token in Devices.Split(',', StringSplitOptions.TrimEntries)) { if (!int.TryParse(token, out var id) || id <= 0) throw new InvalidOperationException("Device selection must contain comma-separated positive IDs from Hardware."); devices.Add(id); }
        else devices.AddRange(_services.Settings.DefaultDeviceIds);
        int? temperature = null;
        if (Expert && !string.IsNullOrWhiteSpace(Temperature)) { if (!int.TryParse(Temperature, out var value)) throw new InvalidOperationException("Temperature threshold must be a number in degrees Celsius."); temperature = value; }
        var defaultOutput = Path.Combine(string.IsNullOrWhiteSpace(_services.Settings.DefaultOutputDirectory) ? directory : _services.Settings.DefaultOutputDirectory, $"{_draftId:N}.txt");
        return new CommonOptions
        {
            Devices = devices,
            WorkloadProfile = Expert ? Workload : _services.Settings.DefaultWorkloadProfile,
            OptimizedKernel = Expert && OptimizedKernel,
            SessionName = session,
            OutputPath = Expert && !string.IsNullOrWhiteSpace(OutputPath) ? OutputPath : defaultOutput,
            PotfilePath = Expert && !string.IsNullOrWhiteSpace(PotfilePath) ? PotfilePath : Path.Combine(_services.Store.Paths.ResultsDirectory, "hashlynx.potfile"),
            DisablePotfile = Expert && DisablePotfile,
            RestorePath = Path.Combine(directory, "session.restore"),
            TemperatureAbort = temperature,
            StatusIntervalSeconds = _services.Settings.StatusIntervalSeconds,
            ExtraArguments = Expert ? ExtraArguments.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList() : []
        };
    }
    private async Task<HashcatJob?> PrepareAsync(AttackConfiguration? attackOverride = null, CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_services.LifetimeToken, cancellationToken);
        var token = cancellation.Token;
        Inspector.ValidationMessage = ""; AttackErrors = ""; CommonErrors = "";
        StartFeedback = "Checking the target and attack settings…";
        var backend = _services.RequireBackend();
        var targetRevision = Inspector.TargetRevision;
        string target;
        try { target = await Inspector.PrepareTargetAsync(token); }
        catch (Exception exception) when (exception is not OperationCanceledException) { Inspector.ValidationMessage = exception.Message; throw; }
        if (Inspector.SelectedMode is null) await Inspector.AnalyzeAsync(token);
        if (targetRevision != Inspector.TargetRevision || target != Inspector.PreparedTargetPath) throw new InvalidOperationException("The target changed during the checks. Start again to use your current target.");
        var job = BuildDraft(target);
        var originalDraftSnapshot = JsonSerializer.Serialize(job);
        if (attackOverride is not null) job.Attack = CloneAttack(attackOverride);
        var configurationSnapshot = JsonSerializer.Serialize(job);
        var validation = await Task.Run(() => _services.Backend.ValidateJob(backend, job), token);
        var targetFields = new[] { "Hashcat executable", "Target", "Hash mode" };
        var attackFields = new[] { "Attack", "Wordlists", "Wordlist", "Rule file", "Rules", "Mask", "Mask file", "Mask preset", "Increment", "Charset", "Loopback" };
        Inspector.ValidationMessage = string.Join(Environment.NewLine, validation.Errors.Where(item => targetFields.Contains(item.Field)).Select(item => item.Message));
        AttackErrors = string.Join(Environment.NewLine, validation.Errors.Where(item => attackFields.Contains(item.Field)).Select(item => item.Message));
        CommonErrors = string.Join(Environment.NewLine, validation.Errors.Where(item => !targetFields.Contains(item.Field) && !attackFields.Contains(item.Field)).Select(item => item.Message));
        Preflight = string.Join(Environment.NewLine, validation.Errors.Select(item => $"{item.Field}: {item.Message}").Concat(validation.Warnings.Select(item => $"Notice — {item.Message}")));
        if (!validation.IsValid) { Preview = "Resolve the preflight errors before building a command."; StartFeedback = Preflight; return null; }
        await _services.Backend.ProbeAsync(backend.ExecutablePath, token);
        if (job.Options.Devices.Count > 0)
        {
            var available = await _services.Backend.GetDevicesAsync(backend, token);
            if (job.Options.Devices.Any(id => available.All(device => device.Id != id))) { Preflight = "Devices: at least one selected device is unavailable. Refresh Hardware and check IDs."; StartFeedback = Preflight; return null; }
        }
        var currentDraft = BuildDraft(target);
        if (attackOverride is not null) currentDraft.Attack = CloneAttack(attackOverride);
        if (!ReferenceEquals(backend, _services.Installation) || targetRevision != Inspector.TargetRevision || target != Inspector.PreparedTargetPath || originalDraftSnapshot != JsonSerializer.Serialize(BuildDraft(target)) || configurationSnapshot != JsonSerializer.Serialize(currentDraft)) throw new InvalidOperationException("The target, backend, or attack settings changed during the checks. Start again to use the current configuration.");
        Preview = _services.Backend.BuildCommand(backend, job).Preview;
        Preflight = "Ready to start. Target, attack inputs, session and output paths passed preflight." + (Preflight.Length == 0 ? "" : Environment.NewLine + Preflight);
        StartFeedback = "Ready to start. Target and attack settings passed the automatic checks.";
        return job;
    }
}
