using HashLynx.Core;

namespace HashLynx.Hashcat.Tests;

public sealed class AttackUsabilityChecksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HashLynx-usability-checks-" + Guid.NewGuid().ToString("N"));
    private RulePresetCatalog Rules => new(Path.Combine(_root, "uncreated-rules"));
    private MaskPresetCatalog Masks => new(Path.Combine(_root, "uncreated-masks"));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("example\n", true)]
    public void DictionaryReadinessRequiresANonemptyReadableWordlist(string? contents, bool expectedReady)
    {
        var path = Path.Combine(_root, "words.txt");
        if (contents is not null) Write("words.txt", contents);
        var errors = AttackReadiness.CheckInputs(new() { Wordlists = [path] }, Rules, Masks);
        Assert.Equal(expectedReady, errors.Count == 0);
        Assert.Equal(expectedReady, AttackReadiness.IsReadableFile(path));
        if (!expectedReady) Assert.Contains(errors, error => error.Contains("empty, missing or unreadable"));
    }

    [Fact]
    public void MissingDictionarySelectionSuggestsTheStarterList()
        => Assert.Contains(AttackReadiness.CheckInputs(new(), Rules, Masks), error => error.Contains("starter list"));

    [Theory]
    [InlineData("?u?l?l?d", true)]
    [InlineData("fixed??text?d", true)]
    [InlineData("?Q", false)]
    [InlineData("?1", false)]
    [InlineData("?", false)]
    [InlineData("", false)]
    public void MaskReadinessChecksSyntaxAndRequiredCharsets(string mask, bool expectedReady)
    {
        var errors = AttackReadiness.CheckInputs(new() { Kind = AttackFamilies.Mask, Mask = mask }, Rules, Masks);
        Assert.Equal(expectedReady, errors.Count == 0);
    }

    [Fact]
    public void UnsupportedAndMixedPresetsHaveUsefulRemedies()
    {
        var words = Write("words.txt", "example\n");
        var rule = Write("custom.rule", ":\n");
        Assert.Contains(AttackReadiness.CheckInputs(new() { Wordlists = [words], RulePresetId = "missing-version" }, Rules, Masks), error => error.Contains("available rule preset"));
        Assert.Contains(AttackReadiness.CheckInputs(new() { Kind = AttackFamilies.Mask, MaskPresetId = "missing-version" }, Rules, Masks), error => error.Contains("available mask preset"));
        Assert.Contains(AttackReadiness.CheckInputs(new() { Wordlists = [words], RulePresetId = RulePresetCatalog.QuickId, RuleFiles = [rule] }, Rules, Masks), error => error.Contains("one Dictionary rule source"));
        Assert.Contains(AttackReadiness.CheckInputs(new() { Kind = AttackFamilies.Mask, MaskPresetId = MaskPresetCatalog.Common1000Id, Mask = "?d" }, Rules, Masks), error => error.Contains("one mask source"));
        Assert.Contains(AttackReadiness.CheckInputs(new() { Kind = "unsupported-family" }, Rules, Masks), error => error.Contains("supported attack type"));
    }

    [Fact]
    public void ReadOnlyChecksDoNotMaterializeAssetsOrCreateOutputFiles()
    {
        var attack = new AttackConfiguration
        {
            Wordlists = [Write("words.txt", "example\n")], RulePresetId = RulePresetCatalog.QuickId
        };
        var before = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).Order().ToArray();
        Assert.Empty(AttackReadiness.CheckInputs(attack, Rules, Masks));
        Assert.Empty(AttackReadiness.CheckInputs(new() { Kind = AttackFamilies.Mask, MaskPresetId = MaskPresetCatalog.Common1000Id }, Rules, Masks));
        Assert.Equal(before, Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.False(Directory.Exists(Path.Combine(_root, "uncreated-rules")));
        Assert.False(Directory.Exists(Path.Combine(_root, "uncreated-masks")));
        Assert.False(Directory.Exists(Path.Combine(_root, "uncreated-output")));
    }

    [Fact]
    public void ADirectoryCannotBeUsedAsAWordlist()
    {
        Directory.CreateDirectory(_root);
        Assert.False(AttackReadiness.IsReadableFile(_root));
        Assert.NotEmpty(AttackReadiness.CheckInputs(new() { Wordlists = [_root] }, Rules, Masks));
    }

    private string Write(string name, string contents)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
