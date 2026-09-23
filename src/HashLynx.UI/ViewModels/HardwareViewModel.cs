using HashLynx.Core;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed class HardwareViewModel : ObservableObject
{
    private string _status = "Refresh to ask Hashcat for its available backend devices.";
    public ObservableCollection<BackendDevice> Devices { get; } = [];
    public string Status { get => _status; set => Set(ref _status, value); }
    public ICommand RefreshCommand { get; }
    public HardwareViewModel(AppServices services)
    {
        RefreshCommand = new AsyncCommand(async _ =>
        {
            Status = "Querying Hashcat backend information…";
            var devices = await services.Backend.GetDevicesAsync(services.RequireBackend(), services.LifetimeToken, refresh: true);
            Devices.Clear(); foreach (var device in devices) Devices.Add(device);
            Status = $"{Devices.Count} backend device(s) reported. Use these IDs in Attack → Advanced → Devices. Live statistics appear in Jobs.";
        }, exception => { Status = "Backend discovery failed. Confirm the Hashcat installation and GPU/runtime drivers."; services.ReportError(exception); });
    }
}
