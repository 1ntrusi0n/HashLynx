using HashLynx.Core;
using Xunit;

namespace HashLynx.Persistence.Tests;

public sealed class RecoveryQueuePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HashLynx-queue-test-" + Guid.NewGuid().ToString("N"));
    private PersistenceStore Store() => new(new AppPaths(_root));

    [Fact]
    public async Task MissingQueueStartsEmptyWithoutWritingAFile()
    {
        var queue = await Store().LoadQueueAsync();
        Assert.Empty(queue.Plans);
        Assert.False(File.Exists(Path.Combine(_root, "queue.json")));
    }

    [Fact]
    public async Task RestartPreservesOrderStateAndIndependentStepOutputsWithSharedSequencePotfile()
    {
        var store = Store();
        var first = Step("Words", AttackFamilies.Dictionary); var second = Step("Pattern", AttackFamilies.Mask);
        first.State = RecoveryStepState.Running; first.LastJobId = first.Configuration.Id;
        first.Configuration.Attack.Wordlists = [Path.Combine(_root, "remembered-words.txt")];
        second.Configuration.Attack.Mask = "Summer?d?d?d?d!";
        second.Configuration.Attack.CustomCharsets = new() { [1] = "?l?u" };
        second.Configuration.Options.Devices = [3];
        var plan = new RecoveryPlan { Name = "Synthetic sequence", Steps = [first, second] };
        await store.SaveQueueAsync(new() { Plans = [plan] });
        var loaded = Assert.Single((await Store().LoadQueueAsync()).Plans);
        Assert.Equal(plan.Id, loaded.Id);
        Assert.Equal("Synthetic sequence", loaded.Name);
        Assert.Equal(new[] { "Words", "Pattern" }, loaded.Steps.Select(step => step.Name));
        Assert.Equal(RecoveryStepState.Running, loaded.Steps[0].State);
        Assert.Equal(first.Configuration.Id, loaded.Steps[0].LastJobId);
        Assert.Equal(RecoveryStepState.Pending, loaded.Steps[1].State);
        Assert.Equal(first.Configuration.TargetPath, loaded.Steps[0].Configuration.TargetPath);
        Assert.Equal(first.Configuration.Options.PotfilePath, loaded.Steps[1].Configuration.Options.PotfilePath);
        Assert.NotEqual(loaded.Steps[0].Configuration.Options.OutputPath, loaded.Steps[1].Configuration.Options.OutputPath);
        Assert.Equal(first.Configuration.Attack.Wordlists, loaded.Steps[0].Configuration.Attack.Wordlists);
        Assert.Equal("Summer?d?d?d?d!", loaded.Steps[1].Configuration.Attack.Mask);
        Assert.Equal("?l?u", loaded.Steps[1].Configuration.Attack.CustomCharsets[1]);
        Assert.Equal(new[] { 3 }, loaded.Steps[1].Configuration.Options.Devices);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":999,\"Plans\":[]}")]
    [InlineData("{\"Plans\":null}")]
    [InlineData("{\"Plans\":[null]}")]
    [InlineData("{\"Plans\":[{\"Steps\":[]}]}")]
    [InlineData("{\"Plans\":[{\"Steps\":[null]}]}")]
    public async Task CorruptOrUnsupportedQueueIsReportedAndItsBytesArePreserved(string json)
    {
        var store = Store(); var path = Path.Combine(_root, "queue.json");
        await File.WriteAllTextAsync(path, json);
        var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadQueueAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task RejectedSaveCannotOverwriteTheLastValidQueue()
    {
        var store = Store(); var path = Path.Combine(_root, "queue.json");
        await store.SaveQueueAsync(new() { Plans = [new() { Steps = [Step("Original", AttackFamilies.Dictionary)] }] });
        var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveQueueAsync(new() { SchemaVersion = 999 }));
        var duplicate = Step("Duplicate", AttackFamilies.Dictionary);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveQueueAsync(new() { Plans = [new() { Steps = [duplicate, duplicate] }] }));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CancellationPreservesPreviousQueueAndReferencedFiles()
    {
        var store = Store(); var step = Step("Keep", AttackFamilies.Dictionary);
        await File.WriteAllTextAsync(step.Configuration.TargetPath, "synthetic-target");
        await File.WriteAllTextAsync(step.Configuration.Options.PotfilePath!, "synthetic-sequence-cache");
        await store.SaveQueueAsync(new() { Plans = [new() { Steps = [step] }] });
        var before = await File.ReadAllBytesAsync(Path.Combine(_root, "queue.json"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveQueueAsync(new(), new CancellationToken(true)));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(_root, "queue.json")));
        await store.SaveQueueAsync(new());
        Assert.Empty((await store.LoadQueueAsync()).Plans);
        Assert.Equal("synthetic-target", await File.ReadAllTextAsync(step.Configuration.TargetPath));
        Assert.Equal("synthetic-sequence-cache", await File.ReadAllTextAsync(step.Configuration.Options.PotfilePath!));
    }

    private RecoveryStep Step(string name, string kind)
    {
        var job = new HashcatJob
        {
            TargetPath = Path.Combine(_root, "sequence-target.hashes"), HashMode = 0,
            Attack = new() { Kind = kind }
        };
        job.Options.OutputPath = Path.Combine(_root, job.Id.ToString("N") + ".results");
        job.Options.PotfilePath = Path.Combine(_root, "sequence.potfile");
        return new() { Name = name, Configuration = job };
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
