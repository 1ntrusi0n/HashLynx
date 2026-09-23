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

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
