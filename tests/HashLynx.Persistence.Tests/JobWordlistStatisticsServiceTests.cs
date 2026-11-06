using System.Text;
using HashLynx.Core;
using Xunit;

namespace HashLynx.Persistence.Tests;

public sealed class JobWordlistStatisticsServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HashLynx-job-wordlist-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PersistenceStore store;
    private readonly JobWordlistStatisticsService service;

    public JobWordlistStatisticsServiceTests()
    {
        store = new(new AppPaths(root));
        service = new(store);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("\n", 1)]
    [InlineData("alpha\r\n\nlast", 3)]
    [InlineData("alpha\nbeta\n", 2)]
    public async Task CountsPhysicalLinesIncludingBlankAndUnterminatedLines(string content, long expected)
    {
        var path = await WriteAsync("physical.txt", content);
        var original = await File.ReadAllBytesAsync(path);
        var result = Assert.Single(await service.InspectAsync(Job(path)));
        Assert.Equal(expected, result.LineCount);
        Assert.Equal(original.LongLength, result.FileSizeBytes);
        Assert.Equal(File.GetLastWriteTimeUtc(path), result.LastWriteUtc);
        Assert.Null(result.CountStatus);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(Path.Combine(root, "wordlists.json")));
    }

    [Theory]
    [InlineData(AttackFamilies.Dictionary)]
    [InlineData(AttackFamilies.Combinator)]
    [InlineData(AttackFamilies.HybridWordlistMask)]
    [InlineData(AttackFamilies.HybridMaskWordlist)]
    [InlineData(AttackFamilies.WordlistPattern)]
    public async Task IncludesEachConfiguredWordlistOccurrenceInOrder(string family)
    {
        var first = await WriteAsync("first.txt", "one\ntwo\n");
        var second = await WriteAsync("second.txt", "three");
        var job = Job(first, second, first);
        job.Attack.Kind = family;
        var results = await service.InspectAsync(job);
        Assert.Equal(new[] { first, second, first }, results.Select(result => result.Path));
        Assert.Equal(new long?[] { 2, 1, 2 }, results.Select(result => result.LineCount));
    }

    [Theory]
    [InlineData(AttackFamilies.Pcfg)]
    [InlineData(AttackFamilies.Mask)]
    [InlineData("future-family")]
    public async Task DoesNotTreatUnrelatedDraftInputsOrPcfgHintsAsWordlists(string family)
    {
        var path = await WriteAsync("unused.txt", "synthetic\n");
        var job = Job(path);
        job.Attack.Kind = family;
        job.Attack.Pcfg = new() { UseHints = true, HintFile = path };
        Assert.Empty(await service.InspectAsync(job));
    }

    [Fact]
    public async Task ReusesFreshLibraryMetadataWithoutReadingLockedContentOrRewritingLibrary()
    {
        var path = await WriteAsync("library.txt", "one\ntwo\n");
        await SaveMetadataAsync(path, 2);
        var libraryPath = Path.Combine(root, "wordlists.json");
        var original = await File.ReadAllBytesAsync(libraryPath);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = Assert.Single(await service.InspectAsync(Job(path.ToUpperInvariant())));
        Assert.Equal(2, result.LineCount);
        Assert.Null(result.CountStatus);
        Assert.Equal(original, await File.ReadAllBytesAsync(libraryPath));
    }

    [Fact]
    public async Task ReusesFreshServiceCacheButRecountsAfterSizeChange()
    {
        var path = await WriteAsync("cache.txt", "one\ntwo\n");
        Assert.Equal(2, Assert.Single(await service.InspectAsync(Job(path))).LineCount);
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(2, Assert.Single(await service.InspectAsync(Job(path))).LineCount);
        await File.AppendAllTextAsync(path, "three\n");
        Assert.Equal(3, Assert.Single(await service.InspectAsync(Job(path))).LineCount);
    }

    [Theory]
    [InlineData("one\ntwo\n", 0, 2)]
    [InlineData("one\ntwo\n", 999, 2)]
    [InlineData("", 1, 0)]
    public async Task ImplausibleCachedLineCountsAreRecountedWithoutOverwritingMetadata(string content, long saved, long expected)
    {
        var path = await WriteAsync("implausible.txt", content);
        await SaveMetadataAsync(path, saved);
        Assert.Equal(expected, Assert.Single(await service.InspectAsync(Job(path))).LineCount);
        Assert.Equal(saved, Assert.Single((await store.LoadWordlistLibraryAsync()).Entries).LineCount);
    }

    [Fact]
    public async Task RecountsLibraryAndServiceCachesWhenTimestampChangesEvenAtSameSize()
    {
        var path = await WriteAsync("modified.txt", "ab\nc");
        await SaveMetadataAsync(path, 2);
        Assert.Equal(2, Assert.Single(await service.InspectAsync(Job(path))).LineCount);
        var previous = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "a\n\nb", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, previous.AddMinutes(1));
        Assert.Equal(3, Assert.Single(await service.InspectAsync(Job(path))).LineCount);
        Assert.Equal(3, Assert.Single(await new JobWordlistStatisticsService(store).InspectAsync(Job(path))).LineCount);
        Assert.Equal(2, Assert.Single((await store.LoadWordlistLibraryAsync()).Entries).LineCount);
    }

    [Fact]
    public async Task MatchingRecordedInputAllowsCountAndChangedFileDoesNotReuseCount()
    {
        var path = await WriteAsync("recorded.txt", "one\ntwo\n");
        var snapshot = Snapshot(path);
        Assert.Equal(2, Assert.Single(await service.InspectAsync(Job(path), [snapshot])).LineCount);
        await File.AppendAllTextAsync(path, "three\n");
        var changed = Assert.Single(await service.InspectAsync(Job(path), [snapshot]));
        Assert.Null(changed.LineCount);
        Assert.Contains("changed", changed.CountStatus);
        Assert.Contains("new attempt", changed.CountStatus);
        Assert.Equal(new FileInfo(path).Length, changed.FileSizeBytes);
    }

    [Fact]
    public async Task RecordedInputTimestampMismatchIsRejectedEvenWithSameSize()
    {
        var path = await WriteAsync("same-size.txt", "one\n");
        var snapshot = Snapshot(path);
        File.SetLastWriteTimeUtc(path, new DateTime(snapshot.LastWriteTimeUtcTicks, DateTimeKind.Utc).AddMinutes(1));
        var result = Assert.Single(await service.InspectAsync(Job(path), [snapshot]));
        Assert.Null(result.LineCount);
        Assert.Contains("changed", result.CountStatus);
    }

    [Fact]
    public async Task MissingRecordedInputCannotBeClaimedAsTheOriginalJobFile()
    {
        var path = await WriteAsync("not-recorded.txt", "one\n");
        var result = Assert.Single(await service.InspectAsync(Job(path), []));
        Assert.Null(result.LineCount);
        Assert.Contains("no recorded metadata", result.CountStatus);
    }

    [Fact]
    public async Task MissingAndUnreadableListsLeaveOtherCountsAvailable()
    {
        var good = await WriteAsync("good.txt", "one\n");
        var locked = await WriteAsync("locked.txt", "two\n");
        var missing = Path.Combine(root, "missing.txt");
        using var exclusive = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var results = await service.InspectAsync(Job(missing, good, locked));
        Assert.Null(results[0].LineCount);
        Assert.Contains("not found", results[0].CountStatus);
        Assert.Equal(1, results[1].LineCount);
        Assert.Null(results[2].LineCount);
        Assert.Contains("could not be counted", results[2].CountStatus);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"SchemaVersion\":999,\"Paths\":[],\"Entries\":[]}")]
    public async Task UnreadableOrUnsupportedLibraryIsPreservedAndDoesNotPreventCounting(string content)
    {
        var libraryPath = await WriteAsync("wordlists.json", content);
        var path = await WriteAsync("fallback.txt", "one\ntwo\n");
        Assert.Equal(2, Assert.Single(await service.InspectAsync(Job(path))).LineCount);
        Assert.Equal(content, await File.ReadAllTextAsync(libraryPath));
    }

    [Fact]
    public async Task CancellationPropagatesIncludingEmptyInputs()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InspectAsync(Job(Path.Combine(root, "missing.txt")), ct: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InspectAsync(Job(), ct: cancellation.Token));
        var path = await WriteAsync("after-cancel.txt", "one\n");
        Assert.Equal(1, Assert.Single(await service.InspectAsync(Job(path))).LineCount);
    }

    [Fact]
    public async Task ConcurrentInspectionsKeepPerJobResultsIndependent()
    {
        var first = await WriteAsync("concurrent-first.txt", "one\ntwo\n");
        var second = await WriteAsync("concurrent-second.txt", "three\n");
        var tasks = Enumerable.Range(0, 12).Select(index => service.InspectAsync(Job(index % 2 == 0 ? first : second))).ToArray();
        var results = await Task.WhenAll(tasks);
        for (var i = 0; i < results.Length; i++)
        {
            var result = Assert.Single(results[i]);
            Assert.Equal(i % 2 == 0 ? first : second, result.Path);
            Assert.Equal(i % 2 == 0 ? 2 : 1, result.LineCount);
            Assert.Null(result.CountStatus);
        }
    }

    [Fact]
    public async Task InvalidPathReturnsGuidanceWithoutResolvingAgainstTheWorkingDirectory()
    {
        var result = Assert.Single(await service.InspectAsync(Job("relative.txt")));
        Assert.Null(result.LineCount);
        Assert.Contains("absolute wordlist file path", result.CountStatus);
    }

    private async Task SaveMetadataAsync(string path, long lines)
    {
        var file = new FileInfo(path);
        await store.SaveWordlistLibraryAsync(new()
        {
            Paths = [path],
            Entries = [new() { Path = path, FileSizeBytes = file.Length, LineCount = lines, LastWriteUtc = file.LastWriteTimeUtc }]
        });
    }

    private async Task<string> WriteAsync(string name, string content)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        return path;
    }

    private static SessionInputSnapshot Snapshot(string path)
    {
        var file = new FileInfo(path);
        return new(path, file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static HashcatJob Job(params string[] paths) => new() { Attack = new() { Wordlists = [.. paths] } };

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
