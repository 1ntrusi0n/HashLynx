using HashLynx.Hashcat;
using HashLynx.Persistence;
using HashLynx.Extractors;
using HashLynx.UI.Infrastructure;
using System.Windows;
using System.Windows.Media;
using System.IO;
using Microsoft.Win32;

namespace HashLynx.UI.Services;

public sealed class AppServices : ObservableObject
{
    private HashcatInstallation? _installation;
    private string _notice = "Locating your Hashcat installation…";
    private string _errorDetails = "";
    private readonly CancellationTokenSource _lifetime = new();
    public CancellationToken LifetimeToken => _lifetime.Token;
    public void CancelPendingOperations() => _lifetime.Cancel();
    public PersistenceStore Store { get; }
    public HashcatFacade Backend { get; }
    public DialogService Dialogs { get; } = new();
    public StructuredLog Log { get; }
    public AppSettings Settings { get; private set; } = new();
    public HashcatInstallation? Installation { get => _installation; private set { Set(ref _installation, value); Raise(nameof(BackendLabel)); } }
    public string BackendLabel => Installation is null ? "Backend not configured" : $"Hashcat {Installation.Version} · local";
    public string Notice { get => _notice; set => Set(ref _notice, value); }
    public string ErrorDetails { get => _errorDetails; private set => Set(ref _errorDetails, value); }
    public ExtractorRegistry Extractors { get; private set; } = ExtractorRegistry.CreateDefault(null);
    public event Action? BackendChanged;
    public AppServices(PersistenceStore? store = null, HashcatFacade? backend = null)
    {
        Store = store ?? new PersistenceStore();
        Backend = backend ?? new HashcatFacade(Store.Paths.CacheDirectory);
        Log = new StructuredLog(Store.Paths);
        ErrorDetails = $"Sanitized logs: {Store.Paths.LogsDirectory}";
        Backend.Diagnostic += diagnostic => _ = WriteLogSafelyAsync("backend.diagnostic", "The backend reported a parsing or discovery diagnostic.");
    }
    public async Task InitializeAsync()
    {
        Settings = await Store.LoadSettingsAsync();
        ApplyTheme(Settings.Theme);
        var path = Backend.Discover(Settings.HashcatDirectory);
        if (path is not null) await ConnectAsync(path);
        else Notice = "Welcome to HashLynx. Open Settings to locate your Hashcat installation.";
        RebuildExtractors();
    }
    public async Task ConnectAsync(string executable)
    {
        Notice = "Preparing and validating the local backend. The first run may take a moment…";
        Installation = await Backend.ProbeAsync(executable, LifetimeToken);
        Settings.HashcatDirectory = Installation.DirectoryPath;
        RebuildExtractors();
        Notice = $"Connected to Hashcat {Installation.Version}. Your recovery workflow stays on this computer.";
        await Log.WriteAsync("backend.connected", "Hashcat installation validated.");
        BackendChanged?.Invoke();
    }
    public HashcatInstallation RequireBackend() => Installation ?? throw new InvalidOperationException("Configure and validate Hashcat in Settings first.");
    public void RebuildExtractors() => Extractors = ExtractorRegistry.CreateDefault(Installation?.DirectoryPath ?? Settings.HashcatDirectory,
        Settings.ExtractorTools.ToDictionary(pair => pair.Key, pair => new ExtractorConfiguration { ToolPath = pair.Value.ToolPath, InterpreterPath = pair.Value.InterpreterPath }));
    public void ReportError(Exception exception)
    {
        if (exception is OperationCanceledException) return;
        Notice = exception is IOException or UnauthorizedAccessException ? "A file could not be read or written. Check the selected path and permissions." : exception.Message;
        if (Notice.Length > 450) Notice = Notice[..450];
        ErrorDetails = $"{exception.GetType().Name} · code 0x{exception.HResult:X8}{Environment.NewLine}Sanitized logs: {Store.Paths.LogsDirectory}";
        _ = WriteLogSafelyAsync("application.error", "An application operation failed.", exception);
    }
    private async Task WriteLogSafelyAsync(string eventName, string message, Exception? exception = null)
    {
        try { await Log.WriteAsync(eventName, message, exception); }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => ErrorDetails = $"Diagnostic logging is unavailable ({logError.GetType().Name}). Check permissions for {Store.Paths.LogsDirectory}.");
        }
    }
    public static void ApplyTheme(string theme)
    {
        var systemLight = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int value && value != 0;
        var light = theme == "Light" || (theme == "System" && systemLight);
        var colors = light
            ? new[] { "#EDF3F8", "#E3EBF3", "#FFFFFF", "#F4F7FB", "#D7EAEF", "#BDCCD8", "#152C43", "#526A80", "#087D76", "#FFFFFF", "#865800" }
            : new[] { "#080F1D", "#0B1526", "#101D31", "#0B1526", "#1A3048", "#25384C", "#EAF3F9", "#9AACC1", "#3AD8CB", "#042321", "#F0C875" };
        var keys = new[] { "BackgroundBrush", "SidebarBrush", "SurfaceBrush", "InputBrush", "HoverBrush", "BorderBrush", "TextBrush", "MutedBrush", "AccentBrush", "AccentTextBrush", "WarningBrush" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}
