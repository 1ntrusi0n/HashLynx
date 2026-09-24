using HashLynx.Drives;
using HashLynx.Hashcat;
using Xunit;
using Xunit.Abstractions;

namespace HashLynx.Integration.Tests;

public sealed class LiveBitLockerFactAttribute : FactAttribute
{
    public LiveBitLockerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HASHLYNX_TEST_BITLOCKER_DRIVE")) ||
            !File.Exists(Environment.GetEnvironmentVariable("HASHLYNX_TEST_DRIVE_READER")) || !File.Exists(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")))
            Skip = "Explicitly select a test volume, drive reader and disposable Hashcat; this test requests UAC and reads that volume's metadata.";
    }
}
public sealed class LiveBitLockerDriveTests(ITestOutputHelper output)
{
    [LiveBitLockerFact]
    [Trait("Category", "LiveDevice")]
    public async Task SelectedTestVolumeExtractsAndHashcatIdentifiesWithoutUnlockingOrWriting()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var service = new DriveReaderClient(Environment.GetEnvironmentVariable("HASHLYNX_TEST_DRIVE_READER"));
        var drives = await service.DiscoverAsync(timeout.Token);
        var selected = Assert.Single(drives, item => item.MountPoint.TrimEnd('\\').Equals(Environment.GetEnvironmentVariable("HASHLYNX_TEST_BITLOCKER_DRIVE")!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        var response = await service.ExtractAsync(selected, timeout.Token);
        Assert.True(response.Extraction?.Success == true, response.Error);
        Assert.InRange(response.BytesRead, 1, 16 * 1024 * 1024);
        var result = response.Extraction!;
        var root = Path.Combine(Path.GetTempPath(), "HashLynx-live-drive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "metadata.hashes"); await File.WriteAllLinesAsync(target, result.Hashes, timeout.Token);
            var facade = new HashcatFacade(Path.Combine(root, "cache"));
            var installation = await facade.ProbeAsync(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")!, timeout.Token);
            Assert.Contains(await facade.IdentifyAsync(installation, target, timeout.Token), mode => mode.Mode == 22100);
            output.WriteLine($"Read-only live volume extraction passed: {result.Hashes.Count} password record(s), {response.BytesRead} metadata bytes; Hashcat identified mode 22100. No password attack was run.");
        }
        finally { Directory.Delete(root, true); }
    }
}
