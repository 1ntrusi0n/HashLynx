using System.Text;
using HashLynx.Core;

namespace HashLynx.Hashcat.Tests;

public sealed class SessionResultsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HashLynx-results-" + Guid.NewGuid().ToString("N"));
    private HashcatFacade Facade => new(Path.Combine(_root, "cache"));

    [Fact]
    public async Task SharedTargetsAndPotfileDoNotLeakAcrossSessionOutputs()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "shared.hashes");
        var potfile = Path.Combine(_root, "shared.potfile");
        await File.WriteAllTextAsync(target, "first\nsecond\n");
        await File.WriteAllTextAsync(potfile, "first:alpha\nsecond:beta\n");
        var first = new HashcatJob { TargetPath = target, Options = new() { PotfilePath = potfile, OutputPath = Path.Combine(_root, "first.txt") } };
        var second = new HashcatJob { TargetPath = target, Options = new() { PotfilePath = potfile, OutputPath = Path.Combine(_root, "second.txt") } };
        await File.WriteAllTextAsync(first.Options.OutputPath, "first:" + Convert.ToHexString(Encoding.UTF8.GetBytes("alpha")) + "\n");
        await File.WriteAllTextAsync(second.Options.OutputPath, "second:" + Convert.ToHexString(Encoding.UTF8.GetBytes("beta")) + "\n");
        Assert.Equal("alpha", Assert.Single(await Facade.ReadSessionResultsAsync(first)).Plaintext);
        Assert.Equal("beta", Assert.Single(await Facade.ReadSessionResultsAsync(second)).Plaintext);
        File.Delete(second.Options.OutputPath);
        Assert.Empty(await Facade.ReadSessionResultsAsync(second));
    }

    [Fact]
    public async Task ResultsRemainReadableWithoutBackendTargetOrPotfile()
    {
        var job = new HashcatJob();
        var facade = Facade;
        Directory.CreateDirectory(facade.Commands.GetJobDirectory(job));
        await File.WriteAllTextAsync(facade.Commands.GetOutputPath(job), "salted:hash:613a62\n");
        var result = Assert.Single(await facade.ReadSessionResultsAsync(job));
        Assert.Equal("salted:hash", result.Hash);
        Assert.Equal("a:b", result.Plaintext);
    }

    [Fact]
    public async Task LiveOutputExcludesIncompleteRecordsAndDeduplicatesWithinSession()
    {
        var job = new HashcatJob();
        var facade = Facade;
        Directory.CreateDirectory(facade.Commands.GetJobDirectory(job));
        var output = facade.Commands.GetOutputPath(job);
        await File.WriteAllTextAsync(output, "one:61\none:61\ntwo:62");
        Assert.Equal("one", Assert.Single(await facade.ReadSessionResultsAsync(job)).Hash);
        await File.AppendAllTextAsync(output, "\n");
        Assert.Equal(2, (await facade.ReadSessionResultsAsync(job)).Count);
    }

    [Theory]
    [InlineData("target:61\n", true)]
    [InlineData("salted:target:613a62\r\n", true)]
    [InlineData("target:\n", true)]
    [InlineData("target:61", false)]
    [InlineData("target:6\n", false)]
    [InlineData("target:invalid\n", false)]
    [InlineData("diagnostic\ntarget:61\n", true)]
    [InlineData("target:6\r1\n", false)]
    public async Task PresenceCheckRequiresACompleteValidSessionRecord(string contents, bool expected)
    {
        var facade = Facade;
        var job = new HashcatJob();
        Directory.CreateDirectory(facade.Commands.GetJobDirectory(job));
        await File.WriteAllTextAsync(facade.Commands.GetOutputPath(job), contents);
        Assert.Equal(expected, await facade.HasSessionResultsAsync(job));
    }

    [Fact]
    public async Task PresenceCheckDoesNotReadSharedPasswordCache()
    {
        Directory.CreateDirectory(_root);
        var job = new HashcatJob { Options = new() { PotfilePath = Path.Combine(_root, "cache.potfile"), OutputPath = Path.Combine(_root, "missing.txt") } };
        await File.WriteAllTextAsync(job.Options.PotfilePath, "target:61\n");
        Assert.False(await Facade.HasSessionResultsAsync(job));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
