using HashLynx.Core;
using HashLynx.Hashcat;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed class HardwareViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly HashcatHardwareCheckService _hardwareCheck;
    private CancellationTokenSource? _checkCancellation;
    private bool _isTesting;
    private string _testStatus = "Test a device below to verify it can recover a built-in sample. The check runs locally and takes up to 90 seconds.";
    private string _status = "Refresh to ask Hashcat for its available backend devices.";
    public ObservableCollection<BackendDevice> Devices { get; } = [];
    public string Status { get => _status; set => Set(ref _status, value); }
    public ICommand RefreshCommand { get; }
    public ICommand UseDefaultCommand { get; }
    public ICommand AutomaticCommand { get; }
    public ICommand TestDeviceCommand { get; }
    public ICommand CancelTestCommand { get; }
    public bool IsTesting { get => _isTesting; private set { Set(ref _isTesting, value); CommandManager.InvalidateRequerySuggested(); } }
    public string TestStatus { get => _testStatus; private set => Set(ref _testStatus, value); }
    public string DefaultSummary => $"Recovery default: {_services.DefaultDeviceSummary}. Used in Basic mode and when Expert device IDs are blank.";
    public HardwareViewModel(AppServices services)
    {
        _services = services;
        _hardwareCheck = new(services.Store.Paths.CacheDirectory);
        services.DefaultDevicesChanged += () => Raise(nameof(DefaultSummary));
        services.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AppServices.IsComputeBusy)) CommandManager.InvalidateRequerySuggested(); };
        services.BackendChanged += () =>
        {
            Devices.Clear();
            Status = "Backend changed. Refresh devices to see the current installation.";
            TestStatus = "Refresh devices and test the device you want to use.";
        };
        TestDeviceCommand = new AsyncCommand(TestDeviceAsync, services.ReportError,
            parameter => !services.IsComputeBusy && !services.IsQueueActive && !IsTesting && parameter is BackendDevice device && Devices.Contains(device));
        CancelTestCommand = new RelayCommand(_ => _checkCancellation?.Cancel(), _ => IsTesting);
        UseDefaultCommand = new AsyncCommand(async parameter =>
        {
            if (parameter is not BackendDevice device || !Devices.Contains(device)) return;
            await services.SetDefaultDevicesAsync([device.Id]);
            Status = $"{device.Name} saved for new recovery jobs. Device discovery does not verify that its runtime can run attacks.";
        }, services.ReportError);
        AutomaticCommand = new AsyncCommand(async _ =>
        {
            await services.SetDefaultDevicesAsync([]);
            Status = "Automatic selection will verify a sample and choose a working device before recovery. Explicit Expert device IDs still override it.";
        }, services.ReportError);
        RefreshCommand = new AsyncCommand(async _ =>
        {
            Status = "Querying Hashcat backend information…";
            services.DeviceSelection.Invalidate();
            var devices = await services.Backend.GetDevicesAsync(services.RequireBackend(), services.LifetimeToken, refresh: true);
            Devices.Clear(); foreach (var device in devices) Devices.Add(device);
            Status = $"{Devices.Count} backend device(s) reported. Choose a recovery default below, or override device IDs in Expert run options. Refresh and reselect after driver changes. Live statistics appear in Jobs.";
        }, exception => { Status = "Backend discovery failed. Confirm the Hashcat installation and GPU/runtime drivers."; services.ReportError(exception); }, _ => !IsTesting);
    }

    private async Task TestDeviceAsync(object? parameter)
    {
        if (parameter is not BackendDevice device || !Devices.Contains(device)) return;
        var installation = _services.RequireBackend();
        if (_services.IsQueueActive || !_services.TryBeginComputeOperation())
        {
            TestStatus = "A recovery or hardware check is already running. Wait for it to finish, then test this device.";
            return;
        }
        try
        {
            IsTesting = true;
            _checkCancellation = CancellationTokenSource.CreateLinkedTokenSource(_services.LifetimeToken);
            TestStatus = $"Testing device {device.Id}: {device.Name}. Preparing a sample and checking the recovered answer; allow up to 90 seconds.";
            var result = await _hardwareCheck.CheckAsync(installation, device.Id, _checkCancellation.Token);
            if (!result.Passed) _services.DeviceSelection.Invalidate(installation);
            TestStatus = ReferenceEquals(installation, _services.Installation)
                ? $"Device {device.Id}: {device.Name} - {result.Message} ({result.Elapsed.TotalSeconds:0.0} seconds)"
                : "The backend changed during this check. Refresh devices and test the current installation.";
        }
        finally
        {
            _checkCancellation?.Dispose();
            _checkCancellation = null;
            IsTesting = false;
            _services.EndComputeOperation();
        }
    }
}
