using HashLynx.Core;

namespace HashLynx.Hashcat.Tests;

public sealed class DeviceSelectionTests
{
    private static readonly HashcatInstallation Installation = new(Path.Combine(Path.GetTempPath(), "synthetic-hashcat", "hashcat.exe"), "7.1.2", new());
    private static readonly string Cache = Path.Combine(Path.GetTempPath(), "HashLynx-unused-device-selector-tests");

    [Fact]
    public async Task BrokenGpuFallsBackToVerifiedCpuBeforeAnyUserRecovery()
    {
        var checkedIds = new List<int>();
        var service = Service([Device(3, "CPU"), Device(1, "GPU"), Device(4, "ACCELERATOR")], (_, id, _) =>
        {
            checkedIds.Add(id);
            return Task.FromResult(Check(id, id == 3 ? HardwareCheckOutcome.Passed : HardwareCheckOutcome.Failed));
        });
        var messages = new List<string>();
        var selection = await service.SelectAsync(Installation, new InlineProgress(messages.Add));
        Assert.True(selection.Succeeded);
        Assert.Equal(3, selection.Device!.Id);
        Assert.Equal(new[] { 1, 3 }, checkedIds);
        Assert.Equal(new[] { HardwareCheckOutcome.Failed, HardwareCheckOutcome.Passed }, selection.Checks.Select(check => check.Outcome));
        Assert.Contains("1 other device failed", selection.Message);
        Assert.Contains(messages, message => message.Contains("Verifying device 1", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("Verifying device 3", StringComparison.Ordinal));
        Assert.False(selection.FromCache);
    }

    [Fact]
    public async Task GpuPriorityIsStableAndRecognizesNativeBackendsWithoutType()
    {
        var ids = new List<int>();
        var nativeGpu = Device(8, "");
        nativeGpu.Backend = "CUDA";
        var service = Service([Device(1, "CPU"), Device(5, "GPU"), nativeGpu, Device(9, "GPU")], (_, id, _) =>
        {
            ids.Add(id);
            return Task.FromResult(Check(id, id == 8 ? HardwareCheckOutcome.Passed : HardwareCheckOutcome.Failed));
        });
        var result = await service.SelectAsync(Installation);
        Assert.Equal(new[] { 5, 8 }, ids);
        Assert.Equal(8, result.Device!.Id);
    }

    [Fact]
    public async Task UnchangedTopologyRefreshesDiscoveryButReusesSuccessfulSample()
    {
        var discoveries = 0;
        var checks = 0;
        var devices = new List<BackendDevice> { Device(3, "CPU"), Device(1, "GPU") };
        var service = new HashcatDeviceSelectionService(Cache, (_, _) =>
        {
            discoveries++;
            return Task.FromResult<IReadOnlyList<BackendDevice>>(devices.ToArray());
        }, (_, id, _) =>
        {
            checks++;
            return Task.FromResult(Check(id, id == 3 ? HardwareCheckOutcome.Passed : HardwareCheckOutcome.Failed));
        });
        var first = await service.SelectAsync(Installation);
        devices.Reverse();
        first.Device!.Driver = "caller cannot mutate the stored identity";
        var second = await service.SelectAsync(Installation);
        Assert.Equal(2, discoveries);
        Assert.Equal(2, checks);
        Assert.True(second.FromCache);
        Assert.Empty(second.Checks);
        Assert.Equal("driver-1", second.Device!.Driver);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("backend")]
    [InlineData("driver")]
    [InlineData("memory")]
    [InlineData("other-device")]
    public async Task AnyTopologyIdentityChangeRequiresANewSample(string field)
    {
        var devices = new List<BackendDevice> { Device(3, "CPU") };
        var calls = 0;
        var service = Service(devices, (_, id, _) => { calls++; return Task.FromResult(Check(id, HardwareCheckOutcome.Passed)); });
        await service.SelectAsync(Installation);
        switch (field)
        {
            case "id": devices[0].Id = 4; break;
            case "name": devices[0].Name = "replacement CPU"; break;
            case "type": devices[0].Type = "GPU"; break;
            case "backend": devices[0].Backend = "CUDA"; break;
            case "driver": devices[0].Driver = "new driver"; break;
            case "memory": devices[0].Memory = "new memory"; break;
            case "other-device": devices.Add(Device(8, "ACCELERATOR")); break;
        }
        var result = await service.SelectAsync(Installation);
        Assert.False(result.FromCache);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task BackendPathAndVersionArePartOfVerificationIdentity()
    {
        var calls = 0;
        var service = Service([Device(3, "CPU")], (_, id, _) => { calls++; return Task.FromResult(Check(id, HardwareCheckOutcome.Passed)); });
        await service.SelectAsync(Installation);
        await service.SelectAsync(Installation with { Version = "7.1.3" });
        await service.SelectAsync(Installation with { ExecutablePath = Path.Combine(Path.GetTempPath(), "other-backend", "hashcat.exe") });
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task InvalidateForFailedRecoveryRequiresAnotherKnownAnswerCheck()
    {
        var calls = 0;
        var service = Service([Device(3, "CPU")], (_, id, _) => { calls++; return Task.FromResult(Check(id, HardwareCheckOutcome.Passed)); });
        await service.SelectAsync(Installation);
        service.Invalidate(Installation);
        Assert.False((await service.SelectAsync(Installation)).FromCache);
        service.Invalidate();
        Assert.False((await service.SelectAsync(Installation)).FromCache);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task InvalidationDuringCheckPreventsCachingStalePass()
    {
        HashcatDeviceSelectionService? service = null;
        var calls = 0;
        service = Service([Device(3, "CPU")], (_, id, _) =>
        {
            calls++;
            if (calls == 1) service!.Invalidate(Installation);
            return Task.FromResult(Check(id, HardwareCheckOutcome.Passed));
        });
        await service.SelectAsync(Installation);
        Assert.False((await service.SelectAsync(Installation)).FromCache);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AllFailuresAreActionableAndNeverReuseAFailedSelection()
    {
        var calls = 0;
        var service = Service([Device(3, "CPU"), Device(1, "GPU")], (_, id, _) =>
        {
            calls++;
            return Task.FromResult(new HardwareCheckResult(id, id == 3 ? HardwareCheckOutcome.TimedOut : HardwareCheckOutcome.Failed,
                "synthetic-private-raw-diagnostic", TimeSpan.Zero));
        });
        var result = await service.SelectAsync(Installation);
        Assert.False(result.Succeeded);
        Assert.Null(result.Device);
        Assert.Equal(2, result.Checks.Count);
        Assert.Contains("device 1: failed", result.Message);
        Assert.Contains("device 3: timed out", result.Message);
        Assert.Contains("Open Hardware", result.Message);
        Assert.DoesNotContain("synthetic-private", result.Message);
        await service.SelectAsync(Installation);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task NoDevicesNeverStartsSample()
    {
        var service = Service([], (_, _, _) => throw new InvalidOperationException("Should not run."));
        var result = await service.SelectAsync(Installation);
        Assert.False(result.Succeeded);
        Assert.Empty(result.Checks);
        Assert.Contains("did not discover", result.Message);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("unreadable")]
    [InlineData("timeout")]
    public async Task FailedDiscoveryInvalidatesPreviousPassEvenIfTheSameTopologyReturns(string failure)
    {
        var discoveries = 0;
        var checks = 0;
        var service = new HashcatDeviceSelectionService(Cache, async (_, token) =>
        {
            discoveries++;
            if (discoveries == 2)
            {
                switch (failure)
                {
                    case "missing": return [];
                    case "ambiguous": return new[] { Device(3, "CPU"), Device(3, "GPU") };
                    case "unreadable": throw new HashcatException("synthetic discovery failure");
                    case "timeout": await Task.Delay(Timeout.InfiniteTimeSpan, token); break;
                }
            }
            return new[] { Device(3, "CPU") };
        }, (_, id, _) =>
        {
            checks++;
            return Task.FromResult(Check(id, HardwareCheckOutcome.Passed));
        }, TimeSpan.FromMilliseconds(100));

        Assert.True((await service.SelectAsync(Installation)).Succeeded);
        var failureResult = await service.SelectAsync(Installation);
        Assert.False(failureResult.Succeeded);
        Assert.Empty(failureResult.Checks);
        var reconnected = await service.SelectAsync(Installation);
        Assert.True(reconnected.Succeeded);
        Assert.False(reconnected.FromCache);
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task AmbiguousDeviceIdsNeverGuessWhichDeviceWasVerified()
    {
        var service = Service([Device(1, "GPU"), Device(1, "CPU")], (_, _, _) => throw new InvalidOperationException("Should not run."));
        var result = await service.SelectAsync(Installation);
        Assert.False(result.Succeeded);
        Assert.Contains("ambiguous", result.Message);
    }

    [Fact]
    public async Task DiscoveryFailureDoesNotLeakBackendExceptionText()
    {
        var service = new HashcatDeviceSelectionService(Cache,
            (_, _) => throw new HashcatException("synthetic-private-installation-path"),
            (_, _, _) => throw new InvalidOperationException("Should not run."));
        var result = await service.SelectAsync(Installation);
        Assert.False(result.Succeeded);
        Assert.Contains("Settings", result.Message);
        Assert.DoesNotContain("synthetic-private", result.Message);
    }

    [Fact]
    public async Task DiscoveryDeadlineCancelsDiscoveryWithoutStartingSample()
    {
        var entered = false;
        var service = new HashcatDeviceSelectionService(Cache, async (_, token) =>
        {
            entered = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Array.Empty<BackendDevice>();
        }, (_, _, _) => throw new InvalidOperationException("Should not run."), TimeSpan.FromMilliseconds(100));
        var result = await service.SelectAsync(Installation);
        Assert.True(entered);
        Assert.False(result.Succeeded);
        Assert.Contains("did not complete in time", result.Message);
    }

    [Fact]
    public async Task AlreadyCancelledSelectionNeverDiscoversOrChecksDevices()
    {
        var service = new HashcatDeviceSelectionService(Cache,
            (_, _) => throw new InvalidOperationException("Should not discover."),
            (_, _, _) => throw new InvalidOperationException("Should not run."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SelectAsync(Installation, cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public async Task UserCancellationDuringDiscoveryIsNotReportedAsTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new HashcatDeviceSelectionService(Cache, (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<BackendDevice>>([]);
        }, (_, _, _) => throw new InvalidOperationException("Should not run."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SelectAsync(Installation, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ConcurrentSelectorsShareOneVerificationAndWaitingCancellationDoesNotRunAnotherSample()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        var service = Service([Device(3, "CPU")], async (_, id, _) =>
        {
            checks++;
            entered.SetResult();
            await release.Task;
            return Check(id, HardwareCheckOutcome.Passed);
        });
        var first = service.SelectAsync(Installation);
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var cancelled = service.SelectAsync(Installation, cancellationToken: cancellation.Token);
        var second = service.SelectAsync(Installation);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult();
        Assert.False((await first).FromCache);
        Assert.True((await second).FromCache);
        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task CancellationDuringSampleNeverTriesNextDeviceOrCachesPassedResult()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var service = Service([Device(1, "GPU"), Device(3, "CPU")], (_, id, _) =>
        {
            calls++;
            if (calls == 1) cancellation.Cancel();
            return Task.FromResult(Check(id, HardwareCheckOutcome.Passed));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SelectAsync(Installation, cancellationToken: cancellation.Token));
        Assert.Equal(1, calls);
        Assert.False((await service.SelectAsync(Installation)).FromCache);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancelledHardwareResultStopsSelectionWithoutTryingAnotherDevice()
    {
        var calls = 0;
        var service = Service([Device(1, "GPU"), Device(3, "CPU")], (_, id, _) =>
        {
            calls++;
            return Task.FromResult(Check(id, HardwareCheckOutcome.Cancelled));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SelectAsync(Installation));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PassForDifferentDeviceIsNotAccepted()
    {
        var service = Service([Device(3, "CPU")], (_, _, _) => Task.FromResult(Check(1, HardwareCheckOutcome.Passed)));
        var result = await service.SelectAsync(Installation);
        Assert.False(result.Succeeded);
        Assert.Equal(HardwareCheckOutcome.Failed, Assert.Single(result.Checks).Outcome);
    }

    private static HashcatDeviceSelectionService Service(IReadOnlyList<BackendDevice> devices,
        Func<HashcatInstallation, int, CancellationToken, Task<HardwareCheckResult>> check) =>
        new(Cache, (_, _) => Task.FromResult(devices), check);

    private static BackendDevice Device(int id, string type) => new()
    {
        Id = id, Name = "synthetic device", Type = type, Backend = "OpenCL", Driver = "driver-1", Memory = "1024 MB"
    };

    private static HardwareCheckResult Check(int id, HardwareCheckOutcome outcome) => new(id, outcome, outcome.ToString(), TimeSpan.Zero);
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
