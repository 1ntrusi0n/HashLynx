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
    public string HashcatDirectory { get => _hashcatDirectory; set => Set(ref _hashcatDirectory, value); }
    public string OutputDirectory { get => _outputDirectory; set => Set(ref _outputDirectory, value); }
    public string Theme { get => _theme; set { if (Set(ref _theme, value)) AppServices.ApplyTheme(value); } }
    public int Workload { get => _workload; set => Set(ref _workload, value); }
    public int Interval { get => _interval; set => Set(ref _interval, value); }
    public bool Expert { get => _expert; set => Set(ref _expert, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string DataLocation => _services.Store.Paths.Root;
    public string LogLocation => _services.Store.Paths.LogsDirectory;
    public string Version => "HashLynx 0.1.0";
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
        BrowseBackendCommand = new RelayCommand(_ => { if (services.Dialogs.OpenFolder("Select the Hashcat release folder") is { } path) HashcatDirectory = path; });
        BrowseOutputCommand = new RelayCommand(_ => { if (services.Dialogs.OpenFolder("Default results folder") is { } path) OutputDirectory = path; });
        DetectCommand = new AsyncCommand(async _ => { var found = services.Backend.Discover(); if (found is null) { Status = "No Hashcat release found. Browse to the extracted release folder."; return; } HashcatDirectory = Path.GetDirectoryName(found)!; await ValidateAsync(); }, services.ReportError);
        ValidateCommand = new AsyncCommand(_ => ValidateAsync(), services.ReportError);
        SaveCommand = new AsyncCommand(_ => SaveAsync(), services.ReportError);
    }
    public void Load()
    {
        HashcatDirectory = _services.Settings.HashcatDirectory ?? "";
        OutputDirectory = _services.Settings.DefaultOutputDirectory;
        Theme = _services.Settings.Theme; Workload = _services.Settings.DefaultWorkloadProfile;
        Expert = _services.Settings.ExpertMode; Interval = _services.Settings.StatusIntervalSeconds;
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
        if (Interval is < 1 or > 60) throw new InvalidOperationException("Status refresh must be between 1 and 60 seconds.");
        if (!string.IsNullOrWhiteSpace(OutputDirectory)) Directory.CreateDirectory(OutputDirectory);
        var settings = _services.Settings;
        settings.HashcatDirectory = HashcatDirectory; settings.DefaultOutputDirectory = OutputDirectory;
        settings.Theme = Theme; settings.DefaultWorkloadProfile = Workload; settings.ExpertMode = Expert; settings.StatusIntervalSeconds = Interval;
        await _services.Store.SaveSettingsAsync(settings);
        Status = "Preferences saved. Attack defaults apply to the next application launch.";
        _services.Notice = "Settings saved locally.";
    }
}
