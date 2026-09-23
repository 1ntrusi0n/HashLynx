using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HashLynx.Core;

namespace HashLynx.Hashcat.Tests;

public sealed class RunnerTests
{
    private static HashcatCommand Fixture(params string[] arguments)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "ProcessFixture");
        var assembly = Path.Combine(directory, "HashLynx.ProcessFixture.dll");
        Assert.True(File.Exists(assembly), "The process fixture must be built and copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return new(host, directory, new[] { assembly }.Concat(arguments).ToArray());
    }

    [Fact]
    public async Task CapturePreservesLiteralArgumentsThroughAnActualChildProcess()
    {
        string[] values = ["space in value", "embedded \"quotes\"", "--leading-dash", "& | > < $() ;", @"C:\some path\trailing\", "", "?1?d?d"];
        var result = await new HashcatRunner().CaptureAsync(Fixture(new[] { "echo" }.Concat(values).ToArray()));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(values, JsonSerializer.Deserialize<string[]>(result.StandardOutput));
        Assert.Equal("", result.StandardError);
    }

    [Fact]
    public async Task StreamingParsesStatusWhileExcludingSensitiveOutputAndSanitizingErrors()
    {
        var events = new ConcurrentQueue<HashcatEvent>();
        var running = new HashcatRunner().Start(Fixture("emit", "0"), new InlineProgress(events.Enqueue));
        var result = await running.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(JobState.Cracked, result.State);
        var status = Assert.Single(events, item => item.Kind == "status").Status;
        Assert.Equal("Running", status!.State);
        Assert.Equal(40, status.ProgressPercent);
        Assert.Equal(9876, status.SpeedHashesPerSecond);
        Assert.Contains(events, item => item.Kind == "error" && item.Message!.Contains("token length"));
        Assert.DoesNotContain("synthetic-private", JsonSerializer.Serialize(events));
        Assert.DoesNotContain("synthetic-private", result.Diagnostic);
        Assert.DoesNotContain(events, item => item.Kind == "output");
    }

    [Theory]
    [InlineData(0, JobState.Cracked)]
    [InlineData(1, JobState.Exhausted)]
    [InlineData(2, JobState.Cancelled)]
    [InlineData(3, JobState.Checkpointed)]
    [InlineData(4, JobState.Interrupted)]
    [InlineData(17, JobState.Failed)]
    public async Task ExitCodesBecomeTypedJobOutcomes(int exitCode, JobState expected)
    {
        var running = new HashcatRunner().Start(Fixture("emit", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var result = await running.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(expected, result.State);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task CaptureCancellationKillsChildAndDrainsReadersBeforeReturning()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "HashLynx-process-test-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        var task = new HashcatRunner().CaptureAsync(Fixture("wait", pidFile), cancellation.Token);
        try
        {
            var pid = await ReadPidAsync(pidFile);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(15)));
            AssertExited(pid);
        }
        finally { await cancellation.CancelAsync(); File.Delete(pidFile); }
    }

    [Fact]
    public async Task ConcurrentStopIsSafeBeforeAndAfterCompletionAndKillsTheChild()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "HashLynx-process-test-" + Guid.NewGuid().ToString("N"));
        var running = new HashcatRunner().Start(Fixture("wait", pidFile));
        try
        {
            var pid = await ReadPidAsync(pidFile);
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => running.StopAsync())).WaitAsync(TimeSpan.FromSeconds(15));
            var result = await running.Completion;
            Assert.True(result.Cancelled);
            Assert.Equal(JobState.Cancelled, result.State);
            await running.StopAsync();
            AssertExited(pid);
        }
        finally { await running.StopAsync(); File.Delete(pidFile); }
    }

    [Fact]
    public async Task ExternalTokenCancellationTerminatesAnActiveJob()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "HashLynx-process-test-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        var running = new HashcatRunner().Start(Fixture("wait", pidFile), cancellationToken: cancellation.Token);
        try
        {
            var pid = await ReadPidAsync(pidFile);
            await cancellation.CancelAsync();
            var result = await running.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(result.Cancelled);
            AssertExited(pid);
        }
        finally { await running.StopAsync(); File.Delete(pidFile); }
    }

    [Fact]
    public async Task UnverifiedInteractiveControlsAreRejectedWithoutSendingInput()
    {
        var running = new HashcatRunner().Start(Fixture("emit", "1"));
        Assert.False(running.CanSendInteractiveControls);
        await Assert.ThrowsAsync<HashcatException>(() => running.SendControlAsync(HashcatControl.Pause));
        await running.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task<int> ReadPidAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try { if (int.TryParse(await File.ReadAllTextAsync(path, timeout.Token), out var pid)) return pid; }
                catch (IOException) { /* The child may still be completing its small readiness file. */ }
            }
            await Task.Delay(20, timeout.Token);
        }
    }

    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { /* The operating system has already removed the exited process. */ }
    }

    private sealed class InlineProgress(Action<HashcatEvent> action) : IProgress<HashcatEvent>
    {
        public void Report(HashcatEvent value) => action(value);
    }
}
