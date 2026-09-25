using System.Text.Json;
using HashLynx.Core;

namespace HashLynx.Hashcat;

public sealed record DeviceSelectionResult(BackendDevice? Device, string Message,
    IReadOnlyList<HardwareCheckResult> Checks, bool FromCache = false)
{
    public bool Succeeded => Device is not null;
}

/// <summary>
/// Chooses a device only after a disposable known-answer check. It never receives a user's target,
/// retries a recovery, changes drivers, or overrides an explicitly selected device.
/// </summary>
public sealed class HashcatDeviceSelectionService
{
    private readonly Func<HashcatInstallation, CancellationToken, Task<IReadOnlyList<BackendDevice>>> _getDevices;
    private readonly Func<HashcatInstallation, int, CancellationToken, Task<HardwareCheckResult>> _checkDevice;
    private readonly TimeSpan _discoveryTimeout;
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, VerifiedDevice> _verified = new(StringComparer.OrdinalIgnoreCase);
    private long _cacheGeneration;

    public HashcatDeviceSelectionService(string cacheDirectory,
        Func<HashcatInstallation, CancellationToken, Task<IReadOnlyList<BackendDevice>>>? getDevices = null,
        Func<HashcatInstallation, int, CancellationToken, Task<HardwareCheckResult>>? checkDevice = null,
        TimeSpan? discoveryTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _discoveryTimeout = discoveryTimeout ?? TimeSpan.FromSeconds(60);
        if (_discoveryTimeout <= TimeSpan.Zero || _discoveryTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(discoveryTimeout));
        if (getDevices is null)
        {
            var facade = new HashcatFacade(cacheDirectory);
            var workspace = new HashcatRuntimeWorkspace(cacheDirectory);
            _getDevices = async (installation, token) =>
            {
                var runtime = await workspace.PrepareAsync(installation.ExecutablePath, token).ConfigureAwait(false);
                return await facade.GetDevicesAsync(installation with { RuntimeDirectory = runtime }, token, refresh: true).ConfigureAwait(false);
            };
        }
        else _getDevices = getDevices;
        _checkDevice = checkDevice ?? new HashcatHardwareCheckService(cacheDirectory).CheckAsync;
    }

    /// <summary>Discard verification after a failed recovery or an explicit refresh. Nothing is persisted.</summary>
    public void Invalidate(HashcatInstallation? installation = null)
    {
        lock (_cacheLock)
        {
            _cacheGeneration++;
            if (installation is null) _verified.Clear();
            else _verified.Remove(InstallationKey(installation));
        }
    }

    /// <summary>Earlier sample evidence only; SelectAsync still rechecks the reported topology before reuse.</summary>
    public int? GetPreviouslyVerifiedDeviceId(HashcatInstallation installation)
    {
        lock (_cacheLock) return _verified.TryGetValue(InstallationKey(installation), out var verified) ? verified.DeviceId : null;
    }

    public async Task<DeviceSelectionResult> SelectAsync(HashcatInstallation installation,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        await _selectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await SelectCoreAsync(installation, progress, cancellationToken).ConfigureAwait(false); }
        finally { _selectionGate.Release(); }
    }

    private async Task<DeviceSelectionResult> SelectCoreAsync(HashcatInstallation installation,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        DeviceSelectionResult Failure(string message, IReadOnlyList<HardwareCheckResult>? checks = null)
        {
            // A missing, ambiguous or unreadable topology breaks continuity with an earlier check.
            // Reconnection with the same reported identity must still verify a new sample.
            Invalidate(installation);
            return new(null, message, checks ?? []);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Checking available recovery devices. The first check can take longer while Hashcat prepares its sample.");
        IReadOnlyList<BackendDevice> discovered;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(_discoveryTimeout);
            try
            {
                discovered = await _getDevices(installation, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure("Device discovery did not complete in time. Refresh Hardware, close other compute workloads, and check the vendor driver/runtime before trying again.");
            }
            catch (Exception exception) when (exception is HashcatException or IOException or UnauthorizedAccessException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Failure("Recovery devices could not be discovered. Validate Hashcat in Settings, then refresh Hardware and check the vendor driver/runtime. No recovery was started.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Copy mutable discovery models so a subsequent refresh cannot change the identity mid-check.
        var devices = discovered.Where(device => device.Id > 0).Select(CopyDevice).OrderBy(device => device.Id).ToArray();
        if (devices.Length == 0)
            return Failure("Hashcat did not discover any recovery devices. Refresh Hardware and install or repair the vendor-supported compute driver/runtime for your hardware. No recovery was started.");
        if (devices.Select(device => device.Id).Distinct().Count() != devices.Length)
            return Failure("Hashcat returned ambiguous device IDs. Validate the installation and refresh Hardware before trying again. No recovery was started.");

        var key = InstallationKey(installation);
        var topology = JsonSerializer.Serialize(devices);
        long generation;
        int? verifiedId = null;
        lock (_cacheLock)
        {
            generation = _cacheGeneration;
            if (_verified.TryGetValue(key, out var verified) && verified.Topology == topology)
                verifiedId = verified.DeviceId;
            else _verified.Remove(key);
        }
        if (verifiedId is { } id)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = $"Using device {id}, which passed the sample check earlier in this app session. Support and speed can differ for other hash formats.";
            progress?.Report(message);
            return new(devices.Single(device => device.Id == id), message, [], FromCache: true);
        }

        var checks = new List<HardwareCheckResult>();
        foreach (var device in devices.OrderBy(DevicePriority).ThenBy(device => device.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Verifying device {device.Id} with a built-in sample before recovery ({checks.Count + 1} of {devices.Length}).");
            HardwareCheckResult check;
            try { check = await _checkDevice(installation, device.Id, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is HashcatException or IOException or UnauthorizedAccessException)
            {
                check = new(device.Id, HardwareCheckOutcome.Failed,
                    "The sample could not run. Check the Hashcat installation, application data access, and vendor driver/runtime.", TimeSpan.Zero);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (check.Outcome == HardwareCheckOutcome.Cancelled)
                throw new OperationCanceledException("Automatic device verification was cancelled.", cancellationToken);
            if (check.DeviceId != device.Id)
                check = new(device.Id, HardwareCheckOutcome.Failed, "The sample did not verify the selected device.", check.Elapsed);
            checks.Add(check);
            if (!check.Passed)
            {
                progress?.Report($"Device {device.Id} did not pass the sample check."
                    + (checks.Count < devices.Length ? " Checking the next available device." : ""));
                continue;
            }

            lock (_cacheLock)
                if (generation == _cacheGeneration) _verified[key] = new(topology, device.Id);
            var message = checks.Count == 1
                ? $"Using device {device.Id}, which recovered and verified the built-in sample. Support and speed can differ for other hash formats."
                : $"Using device {device.Id}, which passed the sample check after {checks.Count - 1} other {(checks.Count == 2 ? "device failed" : "devices failed")}. Support and speed can differ for other hash formats.";
            progress?.Report(message);
            return new(device, message, checks.AsReadOnly());
        }

        var summary = string.Join(", ", checks.Select(check => $"device {check.DeviceId}: {(check.Outcome == HardwareCheckOutcome.TimedOut ? "timed out" : "failed")}"));
        return Failure($"No device passed the built-in sample check ({summary}). Open Hardware to test an individual device and inspect its diagnostic. Check the vendor-supported driver/runtime or choose another supported device. No recovery was started.", checks.AsReadOnly());
    }

    private static int DevicePriority(BackendDevice device) =>
        device.Type.Contains("GPU", StringComparison.OrdinalIgnoreCase)
        || device.Backend.Equals("CUDA", StringComparison.OrdinalIgnoreCase)
        || device.Backend.Equals("HIP", StringComparison.OrdinalIgnoreCase)
        || device.Backend.Equals("Metal", StringComparison.OrdinalIgnoreCase) ? 0
        : device.Type.Contains("CPU", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    private static BackendDevice CopyDevice(BackendDevice device) => new()
    {
        Id = device.Id, Name = device.Name, Backend = device.Backend, Type = device.Type, Memory = device.Memory, Driver = device.Driver
    };

    private static string InstallationKey(HashcatInstallation installation) =>
        JsonSerializer.Serialize(new[] { Path.GetFullPath(installation.ExecutablePath), installation.Version });

    private sealed record VerifiedDevice(string Topology, int DeviceId);
}
