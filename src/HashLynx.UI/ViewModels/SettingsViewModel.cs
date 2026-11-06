using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.IO;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _hashcatDirectory = "", _outputDirectory = "", _theme = "Dark", _status = "";
    private int _workload = 2, _interval = 2;
    private bool _expert;
    private bool _completionNotifications;
    private bool _checkForUpdatesAutomatically;
    public ApplicationUpdatesViewModel Updates { get; }
    public bool CheckForUpdatesAutomatically { get => _checkForUpdatesAutomatically; set => Set(ref _checkForUpdatesAutomatically, value); }
    public bool CompletionNotifications { get => _completionNotifications; set => Set(ref _completionNotifications, value); }
    public string HashcatDirectory { get => _hashcatDirectory; set => Set(ref _hashcatDirectory, value); }
    public string OutputDirectory { get => _outputDirectory; set => Set(ref _outputDirectory, value); }
    public string Theme { get => _theme; set { if (Set(ref _theme, value)) AppServices.ApplyTheme(value); } }
    public int Workload { get => _workload; set => Set(ref _workload, value); }
    public int Interval { get => _interval; set => Set(ref _interval, value); }
    public bool Expert { get => _expert; set => Set(ref _expert, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string DataLocation => _services.Store.Paths.Root;
    public string LogLocation => _services.Store.Paths.LogsDirectory;
    public string Version => $"HashLynx {ApplicationInfo.Version}";
    public string Changelog => ApplicationInfo.Changelog;
    public bool WorkloadEffective => _services.Installation?.Capabilities.WorkloadProfilesEffective != false;
    public string WorkloadHelp => WorkloadEffective ? "Lower profiles can improve desktop responsiveness with Hashcat 7." : "Hashcat 8 tunes workload automatically and ignores workload profiles. Recovery can still make the desktop less responsive.";
    public IReadOnlyList<string> Themes { get; } = ["Dark", "Light", "System"];
    public IReadOnlyList<int> Workloads { get; } = [1, 2, 3, 4];
    public ICommand BrowseBackendCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand DetectCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand SaveCommand { get; }
    public SettingsViewModel(AppServices services)
    {
        _services = services;
        Updates = new(services);
        services.BackendChanged += () => { Raise(nameof(WorkloadEffective)); Raise(nameof(WorkloadHelp)); };
        BrowseBackendCommand = new RelayCommand(_ => { if (services.Dialogs.OpenFolder("Select the Hashcat release folder") is { } path) HashcatDirectory = path; });
        BrowseOutputCommand = new RelayCommand(_ => { if (services.Dialogs.OpenFolder("Default results folder") is { } path) OutputDirectory = path; });
        DetectCommand = new AsyncCommand(async _ => { var found = services.Backend.Discover(); if (found is null) { Status = "No Hashcat release found. Browse to the extracted release folder."; return; } HashcatDirectory = Path.GetDirectoryName(found)!; await ValidateAsync(); }, services.ReportError);
        ValidateCommand = new AsyncCommand(_ => ValidateAsync(), services.ReportError);
        SaveCommand = new AsyncCommand(_ => SaveAsync(), services.ReportError, _ => services.SettingsLoaded && services.CanStartWork);
    }
    public void Load()
    {
        HashcatDirectory = _services.Settings.HashcatDirectory ?? "";
        OutputDirectory = _services.Settings.DefaultOutputDirectory;
        Theme = _services.Settings.Theme; Workload = _services.Settings.DefaultWorkloadProfile;
        Expert = _services.Settings.ExpertMode; Interval = _services.Settings.StatusIntervalSeconds;
        CompletionNotifications = _services.Settings.CompletionNotifications;
        CheckForUpdatesAutomatically = _services.Settings.CheckForUpdatesAutomatically;
        Raise(nameof(WorkloadEffective)); Raise(nameof(WorkloadHelp));
        Status = _services.BackendLabel;
    }
    private async Task ValidateAsync()
    {
        var executable = Path.Combine(HashcatDirectory, "hashcat.exe");
        await _services.ConnectAsync(executable);
        Status = $"Validated · {_services.BackendLabel}";
    }
    private async Task SaveAsync()
    {
        using var operation = _services.BeginOperation();
        if (Interval is < 1 or > 60) throw new InvalidOperationException("Status refresh must be between 1 and 60 seconds.");
        if (!string.IsNullOrWhiteSpace(OutputDirectory)) Directory.CreateDirectory(OutputDirectory);
        await _services.UpdateSettingsAsync(settings =>
        {
            settings.HashcatDirectory = HashcatDirectory; settings.DefaultOutputDirectory = OutputDirectory;
            settings.Theme = Theme; settings.DefaultWorkloadProfile = Workload; settings.ExpertMode = Expert; settings.StatusIntervalSeconds = Interval;
            settings.CompletionNotifications = CompletionNotifications;
            settings.CheckForUpdatesAutomatically = CheckForUpdatesAutomatically;
        });
        Status = "Preferences saved. Attack defaults apply to the next application launch.";
        _services.Notice = "Settings saved locally.";
        _ = Updates.CheckAutomaticallyAsync();
    }
}
