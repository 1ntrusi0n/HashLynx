using HashLynx.Core;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed class AttackViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly JobsViewModel _jobs;
    private readonly Action _showJobs;
    private Guid _draftId = Guid.NewGuid();
    private int _family, _hybridDirection;
    private string _preview = "Prepare a target and attack, then run Preflight to generate the exact command.", _preflight = "Preflight has not run.", _profileName = "", _session = "hashlynx-" + Guid.NewGuid().ToString("N")[..10];
    private bool _expert;
    private string _attackErrors = "", _commonErrors = "";
    private AttackProfile? _selectedProfile;
    public InspectorViewModel Inspector { get; }
    public ObservableCollection<string> Wordlists { get; } = [];
    public ObservableCollection<string> Rules { get; } = [];
    public ObservableCollection<AttackProfile> Profiles { get; } = [];
    public AttackProfile? SelectedProfile { get => _selectedProfile; set => Set(ref _selectedProfile, value); }
    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
    public int Family { get => _family; set { if (Set(ref _family, value)) { Raise(nameof(UsesWordlists)); Raise(nameof(UsesMask)); Raise(nameof(IsHybrid)); Raise(nameof(IsCombinator)); Raise(nameof(IsDictionary)); Raise(nameof(UsesSingleRules)); } } }
    public bool IsDictionary => Family == 0;
    public bool UsesSingleRules => Family != 1;
    public bool UsesWordlists => Family is 0 or 2;
    public bool UsesMask => Family is 1 or 2;
    public bool IsHybrid => Family == 2;
    public bool IsCombinator => Family == 3;
    public int HybridDirection { get => _hybridDirection; set => Set(ref _hybridDirection, value); }
    public bool Expert { get => _expert; set => Set(ref _expert, value); }
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
    public string Devices { get; set; } = "";
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
    public AttackViewModel(AppServices services, JobsViewModel jobs, Action showJobs)
    {
        _services = services; _jobs = jobs; _showJobs = showJobs; Inspector = new(services);
        AddWordlistsCommand = new RelayCommand(_ => AddFiles(Wordlists, services.Dialogs.OpenFiles("Add wordlists", true)));
        DropWordlistsCommand = new RelayCommand(files => { if (files is string[] paths) AddFiles(Wordlists, paths); });
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
        PreflightCommand = new AsyncCommand(async _ => { await PrepareAsync(); }, services.ReportError);
        StartCommand = new AsyncCommand(async _ => { var job = await PrepareAsync(); if (job is null) return; await jobs.StartAsync(job); _showJobs(); _draftId = Guid.NewGuid(); Session = "hashlynx-" + _draftId.ToString("N")[..10]; }, services.ReportError);
        CopyCommand = new RelayCommand(_ => services.Dialogs.Copy(Preview));
        SaveProfileCommand = new AsyncCommand(_ => SaveProfileAsync(), services.ReportError);
        LoadProfileCommand = new RelayCommand(_ => LoadProfile());
        DeleteProfileCommand = new AsyncCommand(async _ => { if (SelectedProfile is not null) { Profiles.Remove(SelectedProfile); await services.Store.SaveProfilesAsync(Profiles.ToList()); } }, services.ReportError);
    }
    public async Task InitializeAsync()
    {
        Expert = _services.Settings.ExpertMode; Workload = _services.Settings.DefaultWorkloadProfile; Raise(nameof(Workload));
        foreach (var profile in await _services.Store.LoadProfilesAsync()) Profiles.Add(profile);
        await Inspector.LoadCatalogAsync();
    }
    private static void AddFiles(ObservableCollection<string> collection, IEnumerable<string> paths) { foreach (var path in paths.Where(File.Exists)) if (!collection.Contains(path)) collection.Add(path); }
    private void MoveWordlist(int delta) { var index = SelectedWordlist is null ? -1 : Wordlists.IndexOf(SelectedWordlist); if (index >= 0 && index + delta >= 0 && index + delta < Wordlists.Count) Wordlists.Move(index, index + delta); }
    private HashcatJob BuildDraft(string targetPath)
    {
        var kinds = new[] { AttackFamilies.Dictionary, AttackFamilies.Mask, HybridDirection == 0 ? AttackFamilies.HybridWordlistMask : AttackFamilies.HybridMaskWordlist, AttackFamilies.Combinator };
        var charsets = UsesMask ? new[] { Charset1, Charset2, Charset3, Charset4 }.Select((text, index) => (text, index)).Where(item => !string.IsNullOrEmpty(item.text)).ToDictionary(item => item.index + 1, item => item.text) : [];
        var devices = new List<int>();
        foreach (var token in Devices.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) { if (!int.TryParse(token, out var id)) throw new InvalidOperationException("Device selection must contain comma-separated numeric IDs from Hardware."); devices.Add(id); }
        int? temperature = null;
        if (!string.IsNullOrWhiteSpace(Temperature)) { if (!int.TryParse(Temperature, out var value)) throw new InvalidOperationException("Temperature threshold must be a number in degrees Celsius."); temperature = value; }
        var directory = _services.Store.Paths.GetJobDirectory(_draftId.ToString("N"));
        return new HashcatJob
        {
            Id = _draftId, Name = Session, TargetPath = targetPath, HashMode = Inspector.SelectedMode?.Mode ?? -1,
            Attack = new() { Kind = kinds[Family], Wordlists = IsCombinator ? [LeftWordlist, RightWordlist] : UsesWordlists ? Wordlists.ToList() : [], RuleFiles = IsDictionary ? Rules.ToList() : [], Mask = UsesMask ? Mask : null, MaskFile = UsesMask && !string.IsNullOrWhiteSpace(MaskFile) ? MaskFile : null, CustomCharsets = charsets, Increment = UsesMask && Increment, IncrementMinimum = IncrementMinimum, IncrementMaximum = IncrementMaximum, LeftRule = UsesSingleRules ? LeftRule : null, RightRule = UsesSingleRules ? RightRule : null, Loopback = Family == 0 && Loopback },
            Options = new() { Devices = devices, WorkloadProfile = Workload, OptimizedKernel = OptimizedKernel, SessionName = Session, OutputPath = string.IsNullOrWhiteSpace(OutputPath) ? Path.Combine(string.IsNullOrWhiteSpace(_services.Settings.DefaultOutputDirectory) ? directory : _services.Settings.DefaultOutputDirectory, $"{_draftId:N}.txt") : OutputPath, PotfilePath = string.IsNullOrWhiteSpace(PotfilePath) ? Path.Combine(_services.Store.Paths.ResultsDirectory, "hashlynx.potfile") : PotfilePath, DisablePotfile = DisablePotfile, RestorePath = Path.Combine(directory, "session.restore"), TemperatureAbort = temperature, StatusIntervalSeconds = _services.Settings.StatusIntervalSeconds, ExtraArguments = Expert ? ExtraArguments.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList() : [] }
        };
    }
    private async Task<HashcatJob?> PrepareAsync()
    {
        Inspector.ValidationMessage = ""; AttackErrors = ""; CommonErrors = "";
        var backend = _services.RequireBackend();
        string target;
        try { target = await Inspector.PrepareTargetAsync(); }
        catch (Exception exception) when (exception is not OperationCanceledException) { Inspector.ValidationMessage = exception.Message; throw; }
        var job = BuildDraft(target);
        var validation = _services.Backend.ValidateJob(backend, job);
        var targetFields = new[] { "Hashcat executable", "Target", "Hash mode" };
        var attackFields = new[] { "Attack", "Wordlists", "Wordlist", "Rule file", "Rules", "Mask", "Increment", "Charset", "Loopback" };
        Inspector.ValidationMessage = string.Join(Environment.NewLine, validation.Errors.Where(item => targetFields.Contains(item.Field)).Select(item => item.Message));
        AttackErrors = string.Join(Environment.NewLine, validation.Errors.Where(item => attackFields.Contains(item.Field)).Select(item => item.Message));
        CommonErrors = string.Join(Environment.NewLine, validation.Errors.Where(item => !targetFields.Contains(item.Field) && !attackFields.Contains(item.Field)).Select(item => item.Message));
        Preflight = string.Join(Environment.NewLine, validation.Errors.Select(item => $"{item.Field}: {item.Message}").Concat(validation.Warnings.Select(item => $"Notice — {item.Message}")));
        if (!validation.IsValid) { Preview = "Resolve the preflight errors before building a command."; return null; }
        await _services.Backend.ProbeAsync(backend.ExecutablePath, _services.LifetimeToken);
        if (job.Options.Devices.Count > 0)
        {
            var available = await _services.Backend.GetDevicesAsync(backend, _services.LifetimeToken);
            if (job.Options.Devices.Any(id => available.All(device => device.Id != id))) { Preflight = "Devices: at least one selected device is unavailable. Refresh Hardware and check IDs."; return null; }
        }
        Preview = _services.Backend.BuildCommand(backend, job).Preview;
        Preflight = "Ready to start. Target, attack inputs, session and output paths passed preflight." + (Preflight.Length == 0 ? "" : Environment.NewLine + Preflight);
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
        Family = attack.Kind switch { AttackFamilies.Mask => 1, AttackFamilies.HybridWordlistMask or AttackFamilies.HybridMaskWordlist => 2, AttackFamilies.Combinator => 3, _ => 0 };
        HybridDirection = attack.Kind == AttackFamilies.HybridMaskWordlist ? 1 : 0;
        Wordlists.Clear(); foreach (var path in attack.Wordlists) Wordlists.Add(path); Rules.Clear(); foreach (var path in attack.RuleFiles) Rules.Add(path);
        LeftWordlist = attack.Wordlists.ElementAtOrDefault(0) ?? ""; RightWordlist = attack.Wordlists.ElementAtOrDefault(1) ?? "";
        Mask = attack.Mask ?? ""; MaskFile = attack.MaskFile ?? ""; Charset1 = attack.CustomCharsets.GetValueOrDefault(1) ?? ""; Charset2 = attack.CustomCharsets.GetValueOrDefault(2) ?? ""; Charset3 = attack.CustomCharsets.GetValueOrDefault(3) ?? ""; Charset4 = attack.CustomCharsets.GetValueOrDefault(4) ?? "";
        Increment = attack.Increment; IncrementMinimum = attack.IncrementMinimum; IncrementMaximum = attack.IncrementMaximum; LeftRule = attack.LeftRule ?? ""; RightRule = attack.RightRule ?? ""; Loopback = attack.Loopback;
        OptimizedKernel = job.Options.OptimizedKernel; Workload = job.Options.WorkloadProfile; Devices = string.Join(",", job.Options.Devices); DisablePotfile = job.Options.DisablePotfile; PotfilePath = job.Options.PotfilePath ?? ""; Temperature = job.Options.TemperatureAbort?.ToString() ?? ""; ExtraArguments = string.Join(Environment.NewLine, job.Options.ExtraArguments);
        if (job.Options.ExtraArguments.Count > 0) Expert = true;
        if (job.HashMode >= 0) Inspector.SelectedMode = Inspector.FindMode(job.HashMode);
        Raise(""); _services.Notice = $"Loaded profile: {SelectedProfile.Name}. Select a target before starting.";
    }
}
