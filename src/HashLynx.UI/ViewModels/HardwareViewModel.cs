using HashLynx.Core;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed class HardwareViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _status = "Refresh to ask Hashcat for its available backend devices.";
    public ObservableCollection<BackendDevice> Devices { get; } = [];
    public string Status { get => _status; set => Set(ref _status, value); }
    public ICommand RefreshCommand { get; }
    public ICommand UseDefaultCommand { get; }
    public ICommand AutomaticCommand { get; }
    public string DefaultSummary => $"Recovery default: {_services.DefaultDeviceSummary}. Used in Basic mode and when Expert device IDs are blank.";
    public HardwareViewModel(AppServices services)
    {
        _services = services;
        services.DefaultDevicesChanged += () => Raise(nameof(DefaultSummary));
        UseDefaultCommand = new AsyncCommand(async parameter =>
        {
            if (parameter is not BackendDevice device || !Devices.Contains(device)) return;
            await services.SetDefaultDevicesAsync([device.Id]);
            Status = $"{device.Name} saved for new recovery jobs. Device discovery does not verify that its runtime can run attacks.";
        }, services.ReportError);
        AutomaticCommand = new AsyncCommand(async _ =>
        {
            await services.SetDefaultDevicesAsync([]);
            Status = "Automatic selection restored for new jobs. Explicit Expert device IDs still override it.";
        }, services.ReportError);
        RefreshCommand = new AsyncCommand(async _ =>
        {
            Status = "Querying Hashcat backend information…";
            var devices = await services.Backend.GetDevicesAsync(services.RequireBackend(), services.LifetimeToken, refresh: true);
            Devices.Clear(); foreach (var device in devices) Devices.Add(device);
            Status = $"{Devices.Count} backend device(s) reported. Choose a recovery default below, or override device IDs in Expert run options. Refresh and reselect after driver changes. Live statistics appear in Jobs.";
        }, exception => { Status = "Backend discovery failed. Confirm the Hashcat installation and GPU/runtime drivers."; services.ReportError(exception); });
    }
}
