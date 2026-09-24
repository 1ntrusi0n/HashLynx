using System.Text;
using Xunit;

namespace HashLynx.Persistence.Tests;

public sealed class WordlistToolsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HashLynx-wordlist-tests-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root);
    private WordlistTools Tools(int memory = 16 * 1024 * 1024) => new(Paths, memory);
    private string FilePath(string name) { Directory.CreateDirectory(root); return Path.Combine(root, name); }

    [Fact]
    public async Task LegacyLibraryLoadsMetadataWithoutRewritingOriginal()
    {
        var store = new PersistenceStore(Paths);
        var words = FilePath("missing.txt");
        var json = System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion = 1, Paths = new[] { words } });
        var path = FilePath("wordlists.json");
        await File.WriteAllTextAsync(path, json);
        var entry = Assert.Single((await store.LoadWordlistLibraryAsync()).Entries);
        Assert.Equal(words, entry.Path);
        Assert.Equal("missing.txt", entry.DisplayName);
        Assert.Null(entry.LineCount);
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FriendlyNamesAndStatisticsRoundTripWithCaseInsensitivePaths()
    {
        var store = new PersistenceStore(Paths);
        var words = FilePath("synthetic.txt");
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await store.SaveWordlistLibraryAsync(new()
        {
            Paths = [words, words.ToUpperInvariant()],
            Entries = [new() { Path = words.ToUpperInvariant(), Name = "Personal examples", FileSizeBytes = 12, LineCount = 3, LastWriteUtc = modified }]
        });
        var entry = Assert.Single((await store.LoadWordlistLibraryAsync()).Entries);
        Assert.Equal(words, entry.Path);
        Assert.Equal("Personal examples", entry.DisplayName);
        Assert.Equal(12, entry.FileSizeBytes);
        Assert.Equal(3, entry.LineCount);
        Assert.Equal(modified, entry.LastWriteUtc);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"Path\":\"relative.txt\"}]")]
    [InlineData("[{\"Path\":\"C:\\\\test.txt\",\"LineCount\":-1}]")]
    public async Task InvalidMetadataIsPreserved(string entries)
    {
        var store = new PersistenceStore(Paths);
        var path = FilePath("wordlists.json");
        var json = "{\"Paths\":[],\"Entries\":" + entries + "}";
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadWordlistLibraryAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("\n", 1)]
    [InlineData("alpha\r\n\nlast", 3)]
    [InlineData("alpha\nbeta\n", 2)]
    public async Task InspectCountsPhysicalLinesIncludingBlanksAndUnterminatedLastLine(string text, int count)
    {
        var input = FilePath("inspect.txt");
        await File.WriteAllTextAsync(input, text, new UTF8Encoding(false));
        var stats = await WordlistTools.InspectAsync(input);
        Assert.Equal(count, stats.LineCount);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), stats.FileSizeBytes);
        Assert.Equal(new FileInfo(input).LastWriteTimeUtc, stats.LastWriteUtc);
    }

    [Fact]
    public async Task CombineFiltersWithoutChangingSourcesOrReorderingWhenDeduplicationIsOff()
    {
        var a = FilePath("a.txt"); var b = FilePath("b.txt"); var output = FilePath("output.txt");
        var originalA = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Beta\r\na\r\nBeta\n\n" )).ToArray();
        var originalB = Encoding.UTF8.GetBytes("Gamma\nlast");
        await File.WriteAllBytesAsync(a, originalA); await File.WriteAllBytesAsync(b, originalB);
        var result = await Tools().TransformAsync(new([a, b], output, false, 4, 4));
        Assert.Equal("Beta\nBeta\nlast\n", await File.ReadAllTextAsync(output));
        Assert.Equal(6, result.LinesRead); Assert.Equal(3, result.LinesWritten);
        Assert.Equal(originalA, await File.ReadAllBytesAsync(a)); Assert.Equal(originalB, await File.ReadAllBytesAsync(b));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task DeduplicatesAcrossMultipleMergePassesWithCaseSensitiveByteOrdering()
    {
        var input = FilePath("large-synthetic.txt"); var output = FilePath("sorted.txt");
        var lines = Enumerable.Range(0, 1600).Reverse().Select(i => "synthetic-" + (i % 750).ToString("D6")).Concat(["Case", "case", "", "Case"]).ToArray();
        await File.WriteAllLinesAsync(input, lines, new UTF8Encoding(false));
        var result = await Tools(1024).TransformAsync(new([input], output));
        var expected = lines.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, await File.ReadAllLinesAsync(output));
        Assert.Equal(lines.Length, result.LinesRead); Assert.Equal(expected.Length, result.LinesWritten);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task FiltersByBytesAndPreservesNonUtf8Candidates()
    {
        var input = FilePath("bytes.txt"); var output = FilePath("filtered.txt");
        byte[] original = [0xc3, 0xa9, 10, 0xff, 0x80, 10, (byte)'a', 10];
        await File.WriteAllBytesAsync(input, original);
        var result = await Tools().TransformAsync(new([input], output, true, 2, 2));
        Assert.Equal(new byte[] { 0xc3, 0xa9, 10, 0xff, 0x80, 10 }, await File.ReadAllBytesAsync(output));
        Assert.Equal(2, result.LinesWritten);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
    }

    [Fact]
    public async Task InternalMergeDoesNotTreatCandidateBomOrCarriageReturnAsRunFormatting()
    {
        var input = FilePath("special.txt"); var output = FilePath("sorted-special.txt");
        byte[] original = [(byte)'a', 13, 13, 10, 0xef, 0xbb, 0xbf, (byte)'b', 10];
        await File.WriteAllBytesAsync(input, original);
        await Tools(1024).TransformAsync(new([input], output));
        Assert.Equal(new byte[] { (byte)'a', 13, 10, 0xef, 0xbb, 0xbf, (byte)'b', 10 }, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task RefusesExistingOutputIncludingInputAndCaseAliases()
    {
        var input = FilePath("source.txt"); var output = FilePath("existing.txt");
        await File.WriteAllTextAsync(input, "source"); await File.WriteAllTextAsync(output, "keep existing");
        await Assert.ThrowsAsync<IOException>(() => Tools().TransformAsync(new([input], output)));
        await Assert.ThrowsAsync<IOException>(() => Tools().TransformAsync(new([input], input.ToUpperInvariant())));
        Assert.Equal("source", await File.ReadAllTextAsync(input));
        Assert.Equal("keep existing", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task CancellingDuringReadPublishesNothingAndCleansScratchFiles()
    {
        var input = FilePath("cancel.txt"); var output = FilePath("not-published.txt");
        await File.WriteAllLinesAsync(input, Enumerable.Repeat("synthetic-word", 110000));
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tools().TransformAsync(new([input], output), progress, cancellation.Token));
        Assert.False(File.Exists(output));
        Assert.Equal(110000, (await WordlistTools.InspectAsync(input)).LineCount);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task UnsupportedEncodingAndExcessiveLineLengthFailWithoutPublishing()
    {
        var input = FilePath("utf16.txt"); var output = FilePath("not-created.txt");
        await File.WriteAllTextAsync(input, "synthetic", Encoding.Unicode);
        await Assert.ThrowsAsync<InvalidDataException>(() => Tools().TransformAsync(new([input], output)));
        Assert.False(File.Exists(output)); AssertNoTemporaryFiles();
        await File.WriteAllBytesAsync(input, Enumerable.Repeat((byte)'a', 1024 * 1024 + 5).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => Tools().TransformAsync(new([input], output)));
        Assert.False(File.Exists(output)); AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task OutputCreatedByAnotherWriterIsPreserved()
    {
        var input = FilePath("race.txt"); var output = FilePath("other-writer.txt");
        await File.WriteAllTextAsync(input, "synthetic\n");
        var progress = new ImmediateProgress(_ => { if (!File.Exists(output)) File.WriteAllText(output, "keep other output"); });
        await Assert.ThrowsAsync<IOException>(() => Tools().TransformAsync(new([input], output), progress));
        Assert.Equal("keep other output", await File.ReadAllTextAsync(output)); AssertNoTemporaryFiles();
    }

    private void AssertNoTemporaryFiles()
    {
        Assert.Empty(Directory.GetFiles(root, ".hashlynx-wordlist-*.tmp"));
        var cache = Path.Combine(root, "cache", "wordlist-tools");
        if (Directory.Exists(cache)) Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
    }
    private sealed class ImmediateProgress(Action<WordlistTransformProgress> action) : IProgress<WordlistTransformProgress>
    { public void Report(WordlistTransformProgress value) => action(value); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
