using Xunit;

namespace HashLynx.Persistence.Tests;

public sealed class BundledWordlistsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HashLynx-wordlists-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StarterIsSmallUniquePlainTextAndIncludedWithoutSourceCollections()
    {
        var lists = new BundledWordlists(new AppPaths(root));
        var path = await lists.GetStarterPathAsync();
        var words = await File.ReadAllLinesAsync(path);
        Assert.True(words.Length >= 500);
        Assert.True(new FileInfo(path).Length < 32 * 1024);
        Assert.Equal(words.Length, words.Distinct(StringComparer.Ordinal).Count());
        Assert.All(words, word => Assert.True(word.Length > 0 && word.All(character => character is >= 'a' and <= 'z')));
        Assert.Contains("river", words);
        Assert.Contains("hashlynx", words);
        Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentReadsReturnSameCompleteListAndRepairAppOwnedCache()
    {
        var lists = new BundledWordlists(new AppPaths(root));
        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => lists.GetStarterPathAsync()));
        Assert.Single(paths.Distinct());
        var expected = await File.ReadAllTextAsync(paths[0]);
        await File.WriteAllTextAsync(paths[0], "incomplete");
        Assert.Equal(paths[0], await lists.GetStarterPathAsync());
        Assert.Equal(expected, await File.ReadAllTextAsync(paths[0]));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(paths[0])!, "*.tmp"));
    }

    [Fact]
    public async Task CancellationDoesNotCreatePartialStarter()
    {
        var paths = new AppPaths(root);
        var lists = new BundledWordlists(paths);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lists.GetStarterPathAsync(cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(root, "wordlists")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
