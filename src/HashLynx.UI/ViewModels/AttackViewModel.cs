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

public sealed class AttackViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly JobsViewModel _jobs;
    private readonly Action _showJobs;
    private readonly BundledWordlists _bundledWordlists;
    private readonly RulePresetCatalog _rulePresets;
    private string? _starterWordlist, _localFullWordlist;
    private RulePresetChoice _selectedRulePreset;
    private bool _useCustomRules, _rulesHelpVisible;
    private Guid _draftId = Guid.NewGuid();
    private int _family, _hybridDirection;
    private string _preview = "Prepare a target and attack, then run Preflight to generate the exact command.", _preflight = "Preflight has not run.", _profileName = "", _session = "hashlynx-" + Guid.NewGuid().ToString("N")[..10];
    private bool _expert;
    private string _attackErrors = "", _commonErrors = "";
    private string _startFeedback = "HashLynx checks your target and files automatically before starting.";
    private AttackProfile? _selectedProfile;
    public InspectorViewModel Inspector { get; }
    public ObservableCollection<string> Wordlists { get; } = [];
    public ObservableCollection<string> Rules { get; } = [];
    public ObservableCollection<AttackProfile> Profiles { get; } = [];
    public AttackProfile? SelectedProfile { get => _selectedProfile; set => Set(ref _selectedProfile, value); }
    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
    public int Family { get => _family; set { if (Set(ref _family, value)) { Raise(nameof(UsesWordlists)); Raise(nameof(UsesMask)); Raise(nameof(IsHybrid)); Raise(nameof(IsCombinator)); Raise(nameof(IsDictionary)); Raise(nameof(UsesSingleRules)); RaiseRuleVisibility(); } } }
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
    public string WordlistButtonLabel => Expert ? "Add wordlists…" : "Choose wordlist…";
    public bool HasLocalWordlist => _localFullWordlist is not null;
    public string WordlistSummary => Wordlists.Count == 0 ? "Choose a wordlist or use the built-in starter list." : Wordlists.Count == 1 && Wordlists[0] == _starterWordlist ? "Built-in starter · 848 example words. Choose a suitable personal wordlist for broader coverage." : Wordlists.Count == 1 && Wordlists[0] == _localFullWordlist ? "Local full wordlist selected. Larger lists take longer, especially with heavier rule presets." : $"{Wordlists.Count} wordlist(s) selected. Each line supplies a starting word.";
    public string Preview { get => _preview; set => Set(ref _preview, value); }
    public string Preflight { get => _preflight; set => Set(ref _preflight, value); }
    public string AttackErrors { get => _attackErrors; set => Set(ref _attackErrors, value); }
    public string CommonErrors { get => _commonErrors; set => Set(ref _commonErrors, value); }
    public string Session { get => _session; set => Set(ref _session, value); }
    public string? SelectedWordlist { get; set; }
    public string? SelectedRule { get; set; }
    public string Mask { get; set; } = "?u?l?l?l?l?l?d?d";
    public string MaskFile { get; set; } = "";
    public string Charset1 { get; set; } = "";
    public string Charset2 { get; set; } = "";
    public string Charset3 { get; set; } = "";
    public string Charset4 { get; set; } = "";
    public bool Increment { get; set; }
    public int IncrementMinimum { get; set; } = 1;
    public int IncrementMaximum { get; set; } = 8;
    public string LeftWordlist { get; set; } = "";
    public string RightWordlist { get; set; } = "";
    public string LeftRule { get; set; } = "";
    public string RightRule { get; set; } = "";
    public bool OptimizedKernel { get; set; }
    public bool Loopback { get; set; }
    public bool DisablePotfile { get; set; }
    public string PotfilePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    private string _devices = "";
    public string Devices { get => _devices; set { if (Set(ref _devices, value)) Raise(nameof(RecoveryDeviceSummary)); } }
    public string RecoveryDeviceSummary => Expert && !string.IsNullOrWhiteSpace(Devices)
        ? $"Recovery device: {Devices} (Expert override)."
        : $"Recovery device: {_services.DefaultDeviceSummary}. Change the default in Hardware.";
    public string Temperature { get; set; } = "";
    public string ExtraArguments { get; set; } = "";
    public int Workload { get; set; } = 2;
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
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand ToggleRulesHelpCommand { get; }
    public ICommand UseStarterCommand { get; }
    public ICommand UseLocalWordlistCommand { get; }
    public AttackViewModel(AppServices services, JobsViewModel jobs, Action showJobs)
    {
        _services = services; _jobs = jobs; _showJobs = showJobs; Inspector = new(services);
        services.DefaultDevicesChanged += () => Raise(nameof(RecoveryDeviceSummary));
        _bundledWordlists = new BundledWordlists(services.Store.Paths);
        _rulePresets = new RulePresetCatalog(Path.Combine(services.Store.Paths.Root, "rule-presets"));
        RulePresets = [new(null, "No Rules", "Try the words exactly as they appear in your wordlist.", "One candidate per input word, with no rule transformations."),
            .. _rulePresets.Presets.Select(preset => new RulePresetChoice(preset.Id, preset.DisplayName, preset.Description, $"{preset.CandidateCountLabel} · {preset.Coverage}"))];
        _selectedRulePreset = RulePresets[0];
        Wordlists.CollectionChanged += (_, _) => Raise(nameof(WordlistSummary));
        AddWordlistsCommand = new RelayCommand(_ => SelectWordlists(services.Dialogs.OpenFiles(Expert ? "Add wordlists" : "Choose a wordlist", Expert)));
        DropWordlistsCommand = new RelayCommand(files => { if (files is string[] paths) SelectWordlists(paths); });
        RemoveWordlistCommand = new RelayCommand(_ => { if (SelectedWordlist is { } item) Wordlists.Remove(item); });
        MoveWordlistUpCommand = new RelayCommand(_ => MoveWordlist(-1)); MoveWordlistDownCommand = new RelayCommand(_ => MoveWordlist(1));
        AddRulesCommand = new RelayCommand(_ => AddFiles(Rules, services.Dialogs.OpenFiles("Add rule files", true, "Hashcat rules|*.rule|All files|*.*")));
        BundledRulesCommand = new RelayCommand(_ => AddFiles(Rules, services.Dialogs.OpenFiles("Bundled Hashcat rules", true, "Hashcat rules|*.rule|All files|*.*", Path.Combine(services.Installation?.DirectoryPath ?? "", "rules"))));
        DropRulesCommand = new RelayCommand(files => { if (files is string[] paths) AddFiles(Rules, paths); });
        RemoveRuleCommand = new RelayCommand(_ => { if (SelectedRule is { } item) Rules.Remove(item); });
        BrowseMaskCommand = new RelayCommand(_ => { if (services.Dialogs.OpenFiles("Select a mask file", false, "Hashcat masks|*.hcmask|All files|*.*").FirstOrDefault() is { } path) { MaskFile = path; Raise(nameof(MaskFile)); } });
        BrowseLeftCommand = new RelayCommand(_ => { if (services.Dialogs.OpenFiles("Left wordlist").FirstOrDefault() is { } path) { LeftWordlist = path; Raise(nameof(LeftWordlist)); } });
        BrowseRightCommand = new RelayCommand(_ => { if (services.Dialogs.OpenFiles("Right wordlist").FirstOrDefault() is { } path) { RightWordlist = path; Raise(nameof(RightWordlist)); } });
        BrowseOutputCommand = new RelayCommand(_ => { if (services.Dialogs.SaveFile("Results output", "recovered.txt") is { } path) { OutputPath = path; Raise(nameof(OutputPath)); } });
        PreflightCommand = new AsyncCommand(async _ => { await PrepareAsync(); }, ReportAttackError);
        StartCommand = new AsyncCommand(async _ => { var job = await PrepareAsync(); if (job is null) return; await jobs.StartAsync(job); _showJobs(); _draftId = Guid.NewGuid(); Session = "hashlynx-" + _draftId.ToString("N")[..10]; }, ReportAttackError);
        CopyCommand = new RelayCommand(_ => services.Dialogs.Copy(Preview));
        SaveProfileCommand = new AsyncCommand(_ => SaveProfileAsync(), services.ReportError);
        LoadProfileCommand = new AsyncCommand(_ => { LoadProfile(); return Task.CompletedTask; }, services.ReportError);
        DeleteProfileCommand = new AsyncCommand(async _ => { if (SelectedProfile is not null) { Profiles.Remove(SelectedProfile); await services.Store.SaveProfilesAsync(Profiles.ToList()); } }, services.ReportError);
        ToggleRulesHelpCommand = new RelayCommand(_ => RulesHelpVisible = !RulesHelpVisible);
        UseStarterCommand = new AsyncCommand(_ => UseStarterAsync(), services.ReportError);
        UseLocalWordlistCommand = new RelayCommand(_ => { if (_localFullWordlist is { } path && File.Exists(path)) { Wordlists.Clear(); Wordlists.Add(path); } }, _ => HasLocalWordlist);
    }
    public async Task InitializeAsync()
    {
        Expert = _services.Settings.ExpertMode; Workload = _services.Settings.DefaultWorkloadProfile; Raise(nameof(Workload));
        _localFullWordlist = _bundledWordlists.FindLocalWordlist(); Raise(nameof(HasLocalWordlist));
        if (Wordlists.Count == 0) await UseStarterAsync();
        foreach (var profile in await _services.Store.LoadProfilesAsync()) Profiles.Add(profile);
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
        var charsets = UsesMask ? new[] { Charset1, Charset2, Charset3, Charset4 }.Select((text, index) => (text, index)).Where(item => !string.IsNullOrEmpty(item.text)).ToDictionary(item => item.index + 1, item => item.text) : [];
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
                Mask = UsesMask ? Mask : null,
                MaskFile = UsesMask && !string.IsNullOrWhiteSpace(MaskFile) ? MaskFile : null,
                CustomCharsets = charsets,
                Increment = UsesMask && Increment,
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
    private async Task<HashcatJob?> PrepareAsync()
    {
        Inspector.ValidationMessage = ""; AttackErrors = ""; CommonErrors = "";
        StartFeedback = "Checking the target and attack settings…";
        var backend = _services.RequireBackend();
        var targetRevision = Inspector.TargetRevision;
        string target;
        try { target = await Inspector.PrepareTargetAsync(); }
        catch (Exception exception) when (exception is not OperationCanceledException) { Inspector.ValidationMessage = exception.Message; throw; }
        if (Inspector.SelectedMode is null) await Inspector.AnalyzeAsync();
        if (targetRevision != Inspector.TargetRevision || target != Inspector.PreparedTargetPath) throw new InvalidOperationException("The target changed during the checks. Start again to use your current target.");
        var job = BuildDraft(target);
        var configurationSnapshot = JsonSerializer.Serialize(job);
        var validation = _services.Backend.ValidateJob(backend, job);
        var targetFields = new[] { "Hashcat executable", "Target", "Hash mode" };
        var attackFields = new[] { "Attack", "Wordlists", "Wordlist", "Rule file", "Rules", "Mask", "Increment", "Charset", "Loopback" };
        Inspector.ValidationMessage = string.Join(Environment.NewLine, validation.Errors.Where(item => targetFields.Contains(item.Field)).Select(item => item.Message));
        AttackErrors = string.Join(Environment.NewLine, validation.Errors.Where(item => attackFields.Contains(item.Field)).Select(item => item.Message));
        CommonErrors = string.Join(Environment.NewLine, validation.Errors.Where(item => !targetFields.Contains(item.Field) && !attackFields.Contains(item.Field)).Select(item => item.Message));
        Preflight = string.Join(Environment.NewLine, validation.Errors.Select(item => $"{item.Field}: {item.Message}").Concat(validation.Warnings.Select(item => $"Notice — {item.Message}")));
        if (!validation.IsValid) { Preview = "Resolve the preflight errors before building a command."; StartFeedback = Preflight; return null; }
        await _services.Backend.ProbeAsync(backend.ExecutablePath, _services.LifetimeToken);
        if (job.Options.Devices.Count > 0)
        {
            var available = await _services.Backend.GetDevicesAsync(backend, _services.LifetimeToken);
            if (job.Options.Devices.Any(id => available.All(device => device.Id != id))) { Preflight = "Devices: at least one selected device is unavailable. Refresh Hardware and check IDs."; StartFeedback = Preflight; return null; }
        }
        if (!ReferenceEquals(backend, _services.Installation) || targetRevision != Inspector.TargetRevision || target != Inspector.PreparedTargetPath || configurationSnapshot != JsonSerializer.Serialize(BuildDraft(target))) throw new InvalidOperationException("The target, backend, or attack settings changed during the checks. Start again to use the current configuration.");
        Preview = _services.Backend.BuildCommand(backend, job).Preview;
        Preflight = "Ready to start. Target, attack inputs, session and output paths passed preflight." + (Preflight.Length == 0 ? "" : Environment.NewLine + Preflight);
        StartFeedback = "Ready to start. Target and attack settings passed the automatic checks.";
        return job;
    }
    private async Task SaveProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(ProfileName)) throw new InvalidOperationException("Enter a profile name first.");
        var configuration = BuildDraft("");
        configuration.TargetPath = ""; configuration.Options.OutputPath = null; configuration.Options.RestorePath = null; configuration.Options.SessionName = "";
        Profiles.Add(new AttackProfile { Name = ProfileName.Trim(), Configuration = configuration });
        await _services.Store.SaveProfilesAsync(Profiles.ToList());
        _services.Notice = "Attack profile saved without a target or recovered results.";
    }
    private void LoadProfile()
    {
        if (SelectedProfile is null) return;
        var job = JsonSerializer.Deserialize<HashcatJob>(JsonSerializer.Serialize(SelectedProfile.Configuration))!;
        var attack = job.Attack;
        var supportedFamilies = new[] { AttackFamilies.Dictionary, AttackFamilies.Mask, AttackFamilies.HybridWordlistMask, AttackFamilies.HybridMaskWordlist, AttackFamilies.Combinator };
        if (!supportedFamilies.Contains(attack.Kind)) throw new InvalidOperationException("This profile uses an attack family that this version cannot edit. The saved profile has been preserved.");
        if (attack.Kind != AttackFamilies.Dictionary && (attack.RulePresetId is not null || attack.RuleFiles.Count > 0)) throw new InvalidOperationException("This profile attaches Dictionary rules to another attack family. The saved profile has been preserved; create a Dictionary profile to use those rules.");
        var preset = attack.RulePresetId is null ? null : _rulePresets.GetById(attack.RulePresetId);
        if (preset is not null && attack.RuleFiles.Count > 0) throw new InvalidOperationException("This profile mixes a rule preset with custom rule files. Its saved data has been preserved; use a profile with one rule source.");
        Family = attack.Kind switch { AttackFamilies.Mask => 1, AttackFamilies.HybridWordlistMask or AttackFamilies.HybridMaskWordlist => 2, AttackFamilies.Combinator => 3, _ => 0 };
        UseCustomRules = IsDictionary && attack.RuleFiles.Count > 0;
        SelectedRulePreset = RulePresets.Single(choice => choice.Id == preset?.Id);
        HybridDirection = attack.Kind == AttackFamilies.HybridMaskWordlist ? 1 : 0;
        Wordlists.Clear(); foreach (var path in attack.Wordlists) Wordlists.Add(path); Rules.Clear(); foreach (var path in attack.RuleFiles) Rules.Add(path);
        LeftWordlist = attack.Wordlists.ElementAtOrDefault(0) ?? ""; RightWordlist = attack.Wordlists.ElementAtOrDefault(1) ?? "";
        Mask = attack.Mask ?? ""; MaskFile = attack.MaskFile ?? ""; Charset1 = attack.CustomCharsets.GetValueOrDefault(1) ?? ""; Charset2 = attack.CustomCharsets.GetValueOrDefault(2) ?? ""; Charset3 = attack.CustomCharsets.GetValueOrDefault(3) ?? ""; Charset4 = attack.CustomCharsets.GetValueOrDefault(4) ?? "";
        Increment = attack.Increment; IncrementMinimum = attack.IncrementMinimum; IncrementMaximum = attack.IncrementMaximum; LeftRule = attack.LeftRule ?? ""; RightRule = attack.RightRule ?? ""; Loopback = attack.Loopback;
        OptimizedKernel = job.Options.OptimizedKernel; Workload = job.Options.WorkloadProfile; Devices = string.Join(",", job.Options.Devices); DisablePotfile = job.Options.DisablePotfile; PotfilePath = job.Options.PotfilePath ?? ""; Temperature = job.Options.TemperatureAbort?.ToString() ?? ""; ExtraArguments = string.Join(Environment.NewLine, job.Options.ExtraArguments);
        var managedPotfile = Path.Combine(_services.Store.Paths.ResultsDirectory, "hashlynx.potfile");
        var hasExpertConfiguration = UseCustomRules || attack.RuleFiles.Count > 0 || !string.IsNullOrEmpty(attack.LeftRule) || !string.IsNullOrEmpty(attack.RightRule) || attack.Loopback || job.Options.ExtraArguments.Count > 0 || job.Options.Devices.Count > 0 || job.Options.OptimizedKernel || job.Options.DisablePotfile || job.Options.TemperatureAbort is not null || job.Options.WorkloadProfile != _services.Settings.DefaultWorkloadProfile || (!string.IsNullOrEmpty(job.Options.PotfilePath) && !string.Equals(job.Options.PotfilePath, managedPotfile, StringComparison.OrdinalIgnoreCase));
        if (hasExpertConfiguration) Expert = true;
        if (job.HashMode >= 0) Inspector.SelectedMode = Inspector.FindMode(job.HashMode);
        StartFeedback = UseCustomRules ? "This profile uses custom or legacy rules. Expert mode is open so its original rule settings stay visible." : hasExpertConfiguration ? "This profile includes advanced options. Expert mode is open so you can review them." : "Profile loaded. Choose a target, then Start recovery.";
        Raise(""); _services.Notice = $"Loaded profile: {SelectedProfile.Name}. Select a target before starting.";
    }
}
