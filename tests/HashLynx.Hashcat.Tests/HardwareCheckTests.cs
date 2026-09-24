using System.Security.Cryptography;
using System.Text;

namespace HashLynx.Hashcat.Tests;

public sealed class HardwareCheckTests
{
    [Fact]
    public async Task PassRequiresRecoveryOfFreshSampleOnExplicitDeviceAndUsesOnlyPrivateFiles()
    {
        using var fixture = new CheckFixture();
        var captureCount = 0;
        var service = fixture.Service(async (command, token) =>
        {
            captureCount++;
            Assert.Equal("3", Value(command, "--backend-devices"));
            Assert.Equal("1,2,3", Value(command, "--opencl-device-types"));
            Assert.Equal("0", Value(command, "--hash-type"));
            Assert.Equal("1,3", Value(command, "--outfile-format"));
            Assert.Equal("1", Value(command, "--workload-profile"));
            Assert.Equal("30", Value(command, "--runtime"));
            Assert.Contains("--potfile-disable", command.Arguments);
            Assert.Contains("--restore-disable", command.Arguments);
            Assert.Contains("--logfile-disable", command.Arguments);
            Assert.DoesNotContain("--force", command.Arguments);
            Assert.NotEqual(fixture.Source, command.WorkingDirectory);
            Assert.StartsWith(fixture.Cache, command.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(command.WorkingDirectory, "hashcat.potfile")));
            Assert.StartsWith(fixture.CheckRoot, command.Arguments[^2], StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(fixture.CheckRoot, command.Arguments[^1], StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(fixture.CheckRoot, Value(command, "--outfile"), StringComparison.OrdinalIgnoreCase);
            var start = HashcatRunner.CreateStartInfo(command);
            Assert.False(start.UseShellExecute);
            await WriteAnswerAsync(command, token);
            return new ProcessCapture(0, "", "");
        });
        var result = await service.CheckAsync(fixture.Installation, 3);
        Assert.True(result.Passed);
        Assert.Equal(3, result.DeviceId);
        Assert.Equal(1, captureCount);
        fixture.AssertClean();
        Assert.Equal("private-source-potfile", await File.ReadAllTextAsync(Path.Combine(fixture.Source, "hashcat.potfile")));
        Assert.Equal(new[] { "hashcat.exe", "hashcat.potfile" }, Directory.GetFiles(fixture.Source).Select(Path.GetFileName).Order().ToArray());
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(0, "wrong:answer\n")]
    [InlineData(1, "")]
    public async Task ExitCodeAloneOrUnrelatedOutputNeverPasses(int exitCode, string output)
    {
        using var fixture = new CheckFixture();
        var service = fixture.Service(async (command, token) =>
        {
            if (output.Length > 0) await File.WriteAllTextAsync(Value(command, "--outfile"), output, token);
            return new(exitCode, "", "");
        });
        var result = await service.CheckAsync(fixture.Installation, 2);
        Assert.Equal(HardwareCheckOutcome.Failed, result.Outcome);
        Assert.Contains("expected sample", result.Message);
        fixture.AssertClean();
    }

    [Fact]
    public async Task MatchingAnswerDoesNotOverrideFailureExitCode()
    {
        using var fixture = new CheckFixture();
        var service = fixture.Service(async (command, token) =>
        {
            await WriteAnswerAsync(command, token);
            return new(-1, "", "clGetDeviceInfo(): CL_INVALID_VALUE");
        });
        var result = await service.CheckAsync(fixture.Installation, 3);
        Assert.False(result.Passed);
        Assert.Contains("driver", result.Message);
        fixture.AssertClean();
    }

    [Fact]
    public async Task RuntimeDiagnosticsAreActionableWithoutEchoingRawOutputOrPaths()
    {
        using var fixture = new CheckFixture();
        var service = fixture.Service((_, _) => Task.FromResult(new ProcessCapture(-1,
            "synthetic-private-plaintext", "Outdated or broken OpenCL runtime: synthetic-private-driver\nNo devices found\nC:\\synthetic-private-path")));
        var result = await service.CheckAsync(fixture.Installation, 3);
        Assert.Equal(HardwareCheckOutcome.Failed, result.Outcome);
        Assert.Contains("outdated or broken", result.Message);
        Assert.Contains("supported driver/runtime", result.Message);
        Assert.DoesNotContain("synthetic-private", result.Message);
        fixture.AssertClean();
    }

    [Fact]
    public async Task DeadlineCancelsCaptureAndCleansUpSampleFiles()
    {
        using var fixture = new CheckFixture();
        var entered = false;
        var service = fixture.Service(async (_, token) =>
        {
            entered = true;
            await Task.Delay(Timeout.Infinite, token);
            return new(0, "", "");
        }, TimeSpan.FromSeconds(1));
        var result = await service.CheckAsync(fixture.Installation, 1);
        Assert.True(entered);
        Assert.Equal(HardwareCheckOutcome.TimedOut, result.Outcome);
        fixture.AssertClean();
    }

    [Fact]
    public async Task UserCancellationIsDistinctFromTimeout()
    {
        using var fixture = new CheckFixture();
        using var cancellation = new CancellationTokenSource();
        var service = fixture.Service((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new ProcessCapture(0, "", ""));
        });
        var result = await service.CheckAsync(fixture.Installation, 1, cancellation.Token);
        Assert.Equal(HardwareCheckOutcome.Cancelled, result.Outcome);
        fixture.AssertClean();
    }

    [Fact]
    public async Task AlreadyCancelledCheckDoesNotLaunchAnything()
    {
        using var fixture = new CheckFixture();
        var service = fixture.Service((_, _) => throw new InvalidOperationException("Must not launch."));
        var result = await service.CheckAsync(fixture.Installation, 1, new CancellationToken(true));
        Assert.Equal(HardwareCheckOutcome.Cancelled, result.Outcome);
        fixture.AssertClean();
    }

    [Fact]
    public async Task MissingExecutableProducesActionableFailure()
    {
        using var fixture = new CheckFixture();
        File.Delete(fixture.Installation.ExecutablePath);
        var service = fixture.Service((_, _) => throw new InvalidOperationException("Must not launch."));
        var result = await service.CheckAsync(fixture.Installation, 1);
        Assert.Equal(HardwareCheckOutcome.Failed, result.Outcome);
        Assert.Contains("installation", result.Message);
        fixture.AssertClean();
    }

    [Fact]
    public async Task InvalidDeviceIsRejectedBeforePreparingFiles()
    {
        using var fixture = new CheckFixture();
        var service = fixture.Service((_, _) => throw new InvalidOperationException("Must not launch."));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CheckAsync(fixture.Installation, 0));
        fixture.AssertClean();
    }

    private static string Value(HashcatCommand command, string option) => command.Arguments[command.Arguments.ToList().IndexOf(option) + 1];

    private static async Task WriteAnswerAsync(HashcatCommand command, CancellationToken token)
    {
        var hash = (await File.ReadAllTextAsync(command.Arguments[^2], token)).Trim();
        var words = await File.ReadAllLinesAsync(command.Arguments[^1], token);
        var answerBytes = Encoding.ASCII.GetBytes(words[^1]);
        Assert.Equal(hash, Convert.ToHexString(MD5.HashData(answerBytes)).ToLowerInvariant());
        await File.WriteAllTextAsync(Value(command, "--outfile"), hash + ":" + Convert.ToHexString(answerBytes) + "\n", token);
    }

    private sealed class CheckFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "HashLynx-hardware-test-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source");
        public string Cache => Path.Combine(Root, "cache");
        public string CheckRoot => Path.Combine(Cache, "hardware-checks");
        public HashcatInstallation Installation { get; }

        public CheckFixture()
        {
            Directory.CreateDirectory(Source);
            File.WriteAllText(Path.Combine(Source, "hashcat.exe"), "test executable placeholder");
            File.WriteAllText(Path.Combine(Source, "hashcat.potfile"), "private-source-potfile");
            Installation = new(Path.Combine(Source, "hashcat.exe"), "7.1.2", new());
        }

        public HashcatHardwareCheckService Service(Func<HashcatCommand, CancellationToken, Task<ProcessCapture>> capture, TimeSpan? timeout = null) => new(Cache, capture, timeout);
        public void AssertClean() { if (Directory.Exists(CheckRoot)) Assert.Empty(Directory.EnumerateFileSystemEntries(CheckRoot)); }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

public sealed class InstalledHardwareCheckFactAttribute : FactAttribute
{
    public InstalledHardwareCheckFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT"))
            || !int.TryParse(Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE"), out var id) || id <= 0)
            Skip = "Set HASHLYNX_TEST_HASHCAT to a disposable backend and HASHLYNX_TEST_DEVICE to a selected working device for known-answer hardware validation.";
    }
}

public sealed class HardwareCheckIntegrationTests
{
    [InstalledHardwareCheckFact]
    [Trait("Category", "Integration")]
    public async Task SelectedDeviceRecoversTheIsolatedKnownAnswer()
    {
        var root = Path.Combine(Path.GetTempPath(), "HashLynx-hardware-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var facade = new HashcatFacade(root);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var installation = await facade.ProbeAsync(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")!, cancellation.Token);
            var device = int.Parse(Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE")!, System.Globalization.CultureInfo.InvariantCulture);
            var result = await new HashcatHardwareCheckService(root).CheckAsync(installation, device, cancellation.Token);
            Assert.True(result.Passed, result.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "hardware-checks")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
