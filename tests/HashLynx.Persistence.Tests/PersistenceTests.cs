using HashLynx.Core;
using Xunit;

namespace HashLynx.Persistence.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HashLynx-tests-" + Guid.NewGuid().ToString("N"));
    private PersistenceStore CreateStore() => new(new AppPaths(root));

    [Fact]
    public async Task MissingSettingsUseLocalDefaults()
    {
        var store = CreateStore();
        var settings = await store.LoadSettingsAsync();
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal(store.Paths.ResultsDirectory, settings.DefaultOutputDirectory);
        Assert.False(File.Exists(Path.Combine(root, "settings.json")));
    }

    [Fact]
    public async Task SettingsRoundTripUnicodePathsAndExtractorConfiguration()
    {
        var store = CreateStore();
        await store.SaveSettingsAsync(new AppSettings
        {
            HashcatDirectory = "C:\\User tools\\hashcat", Theme = "Light", ExpertMode = true,
            ExtractorTools = new() { ["pdf"] = new() { ToolPath = "C:\\Tools\\décode\\pdf2john.py", InterpreterPath = "C:\\Python\\python.exe" } }
        });
        var settings = await CreateStore().LoadSettingsAsync();
        Assert.Equal("C:\\User tools\\hashcat", settings.HashcatDirectory);
        Assert.Equal("Light", settings.Theme);
        Assert.True(settings.ExpertMode);
        Assert.EndsWith("pdf2john.py", settings.ExtractorTools["pdf"].ToolPath);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task CorruptSettingsArePreservedInsteadOfSilentlyReset()
    {
        var store = CreateStore();
        var file = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(file, "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadSettingsAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task NewerSettingsAreNotOverwritten()
    {
        var store = CreateStore();
        var file = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(file, "{\"SchemaVersion\":999}");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadSettingsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveSettingsAsync(new AppSettings { SchemaVersion = 999 }));
        Assert.Contains("999", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task ParallelWritesNeverLeavePartialJson()
    {
        var store = CreateStore();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => store.SaveSettingsAsync(new AppSettings { HashcatDirectory = $"test-{index}" })));
        var reloaded = await store.LoadSettingsAsync();
        Assert.StartsWith("test-", reloaded.HashcatDirectory);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task CancelledWriteKeepsPreviousData()
    {
        var store = CreateStore();
        await store.SaveSettingsAsync(new AppSettings { Theme = "Dark" });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveSettingsAsync(new AppSettings { Theme = "Light" }, cancellation.Token));
        Assert.Equal("Dark", (await store.LoadSettingsAsync()).Theme);
    }

    [Fact]
    public async Task JobAndProfileConfigurationSurviveRoundTrip()
    {
        var store = CreateStore();
        var job = new JobRecord
        {
            Name = "Synthetic lab run", State = JobState.Interrupted, ExitCode = 2,
            Configuration = new HashcatJob { HashMode = 0, Attack = new AttackConfiguration { Kind = AttackFamilies.Mask, Mask = "?d?d", CustomCharsets = new() { [1] = "abc" } } },
            LatestStatus = new JobStatusSnapshot { ProgressPercent = 42.5, RecoveredHashes = 1, TotalHashes = 2 }
        };
        await store.SaveJobsAsync([job]);
        var reloaded = Assert.Single(await store.LoadJobsAsync());
        Assert.Equal(job.Id, reloaded.Id);
        Assert.Equal(JobState.Interrupted, reloaded.State);
        Assert.Equal(42.5, reloaded.LatestStatus!.ProgressPercent);
        Assert.Equal("?d?d", reloaded.Configuration.Attack.Mask);
        await store.SaveProfilesAsync([new AttackProfile { Name = "Numbers", Configuration = job.Configuration }]);
        Assert.Equal("Numbers", Assert.Single(await store.LoadProfilesAsync()).Name);
    }

    [Fact]
    public async Task LoggingDoesNotIncludeExceptionMessageOrStackTrace()
    {
        var paths = new AppPaths(root);
        var logger = new StructuredLog(paths);
        await logger.WriteAsync("process.failed", "The backend could not start.", new IOException("sensitive-target-secret"));
        var content = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(paths.LogsDirectory)));
        Assert.Contains("IOException", content);
        Assert.DoesNotContain("sensitive-target-secret", content);
        Assert.DoesNotContain("StackTrace", content);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    public void JobDirectoryRejectsEscapingDataRoot(string id) => Assert.Throws<ArgumentException>(() => new AppPaths(root).GetJobDirectory(id));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
