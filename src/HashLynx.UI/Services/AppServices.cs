using HashLynx.Hashcat;
using HashLynx.Persistence;
using HashLynx.Extractors;
using HashLynx.Drives;
using HashLynx.UI.Infrastructure;
using System.Windows;
using System.Windows.Media;
using System.IO;
using Microsoft.Win32;
using HashLynx.Updates;

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
    public HashcatDeviceSelectionService DeviceSelection { get; }
    public DialogService Dialogs { get; } = new();
    public IBitLockerDriveService BitLockerDrives { get; }
    public StructuredLog Log { get; }
    public AppSettings Settings { get; private set; } = new();
    public bool SettingsLoaded { get; private set; }
    public IApplicationUpdateService Updates { get; }
    private readonly object _operationGate = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private int _workspaceOperations;
    private bool _updatePending;
    private bool _queueActive;
    public bool IsUpdatePending { get { lock (_operationGate) return _updatePending; } }
    public bool CanStartWork => !IsUpdatePending && !LifetimeToken.IsCancellationRequested;
    public bool IsBusyForUpdate { get { lock (_operationGate) return _computeBusy || _queueActive || _workspaceOperations != 0; } }
    private bool _computeBusy;
    public bool IsComputeBusy { get { lock (_operationGate) return _computeBusy; } }
    public bool IsQueueActive
    {
        get { lock (_operationGate) return _queueActive; }
        set
        {
            lock (_operationGate)
            {
                if (value && (_updatePending || LifetimeToken.IsCancellationRequested)) throw new OperationCanceledException("HashLynx is preparing to close.");
                _queueActive = value;
            }
            RaiseOperationState();
        }
    }
    public IDisposable BeginOperation()
    {
        lock (_operationGate)
        {
            if (_updatePending || LifetimeToken.IsCancellationRequested) throw new OperationCanceledException("HashLynx is preparing to close.");
            _workspaceOperations++;
        }
        RaiseOperationState();
        return new OperationLease(this);
    }
    private sealed class OperationLease(AppServices owner) : IDisposable
    {
        private AppServices? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            lock (current._operationGate) current._workspaceOperations--;
            current.RaiseOperationState();
        }
    }
    public bool TryBeginUpdate()
    {
        lock (_operationGate)
        {
            if (_updatePending || _computeBusy || _queueActive || _workspaceOperations != 0 || LifetimeToken.IsCancellationRequested) return false;
            _updatePending = true;
        }
        RaiseOperationState();
        return true;
    }
    public void CancelUpdatePreparation()
    {
        lock (_operationGate) _updatePending = false;
        RaiseOperationState();
    }
    private void RaiseOperationState()
    {
        void Refresh()
        {
            Raise(nameof(IsComputeBusy)); Raise(nameof(IsQueueActive)); Raise(nameof(IsBusyForUpdate));
            Raise(nameof(IsUpdatePending)); Raise(nameof(CanStartWork));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Refresh);
        else Refresh();
    }
    public bool TryBeginComputeOperation()
    {
        lock (_operationGate)
        {
            if (_computeBusy || _updatePending || LifetimeToken.IsCancellationRequested) return false;
            _computeBusy = true;
        }
        RaiseOperationState();
        return true;
    }
    public void EndComputeOperation() { lock (_operationGate) _computeBusy = false; RaiseOperationState(); }
    public HashcatInstallation? Installation { get => _installation; private set { Set(ref _installation, value); Raise(nameof(BackendLabel)); } }
    public string BackendLabel => Installation is null ? "Backend not configured" : $"Hashcat {Installation.Version} · local";
    public string Notice { get => _notice; set => Set(ref _notice, value); }
    public string ErrorDetails { get => _errorDetails; private set => Set(ref _errorDetails, value); }
    public ExtractorRegistry Extractors { get; private set; } = ExtractorRegistry.CreateDefault(null);
    public event Action? BackendChanged;
    public event Action? DefaultDevicesChanged;
    public string DefaultDeviceSummary => Settings.DefaultDeviceIds.Count == 0
        ? "Checked automatic selection (finds a working device before recovery)"
        : "Device " + string.Join(", ", Settings.DefaultDeviceIds);
    public async Task UpdateSettingsAsync(Action<AppSettings> update)
    {
        using var operation = BeginOperation();
        await _settingsGate.WaitAsync(LifetimeToken);
        try
        {
            var copy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Settings))!;
            update(copy);
            await Store.SaveSettingsAsync(copy, LifetimeToken);
            Settings = copy;
        }
        finally { _settingsGate.Release(); }
    }
    public async Task SetDefaultDevicesAsync(IEnumerable<int> deviceIds)
    {
        using var operation = BeginOperation();
        var selected = deviceIds.Distinct().ToList();
        if (selected.Any(id => id <= 0)) throw new InvalidOperationException("Choose a device reported in Hardware.");
        await UpdateSettingsAsync(settings => settings.DefaultDeviceIds = selected);
        DeviceSelection.Invalidate();
        DefaultDevicesChanged?.Invoke();
        Notice = $"Recovery default saved: {DefaultDeviceSummary}. Applies to new attacks, including Basic mode.";
    }
    public AppServices(PersistenceStore? store = null, HashcatFacade? backend = null, IBitLockerDriveService? bitLockerDrives = null, HashcatDeviceSelectionService? deviceSelection = null, IApplicationUpdateService? updates = null)
    {
        Store = store ?? new PersistenceStore();
        Updates = updates ?? new GitHubApplicationUpdateService();
        BitLockerDrives = bitLockerDrives ?? new DriveReaderClient();
        Backend = backend ?? new HashcatFacade(Store.Paths.CacheDirectory);
        DeviceSelection = deviceSelection ?? new HashcatDeviceSelectionService(Store.Paths.CacheDirectory,
            (installation, token) => Backend.GetDevicesAsync(installation, token, refresh: true));
        Log = new StructuredLog(Store.Paths);
        ErrorDetails = $"Sanitized logs: {Store.Paths.LogsDirectory}";
        Backend.Diagnostic += diagnostic => _ = WriteLogSafelyAsync("backend.diagnostic", "The backend reported a parsing or discovery diagnostic.");
    }
    public async Task InitializeAsync()
    {
        Settings = await Store.LoadSettingsAsync();
        SettingsLoaded = true;
        DefaultDevicesChanged?.Invoke();
        ApplyTheme(Settings.Theme);
        var path = Backend.Discover(Settings.HashcatDirectory);
        if (path is not null) await ConnectAsync(path);
        else Notice = "Welcome to HashLynx. Open Settings to locate your Hashcat installation.";
        RebuildExtractors();
    }
    public async Task ConnectAsync(string executable)
    {
        using var operation = BeginOperation();
        Notice = "Preparing and validating the local backend. The first run may take a moment…";
        Installation = await Backend.ProbeAsync(executable, LifetimeToken);
        DeviceSelection.Invalidate();
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
            ? new[] { "#EDF3F8", "#E3EBF3", "#FFFFFF", "#F4F7FB", "#D7EAEF", "#BDCCD8", "#152C43", "#526A80", "#087D76", "#FFFFFF", "#865800", "#146C43", "#B42318" }
            : new[] { "#080F1D", "#0B1526", "#101D31", "#0B1526", "#1A3048", "#25384C", "#EAF3F9", "#9AACC1", "#3AD8CB", "#042321", "#F0C875", "#70DBA1", "#FF9191" };
        var keys = new[] { "BackgroundBrush", "SidebarBrush", "SurfaceBrush", "InputBrush", "HoverBrush", "BorderBrush", "TextBrush", "MutedBrush", "AccentBrush", "AccentTextBrush", "WarningBrush", "SuccessBrush", "ErrorBrush" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}
