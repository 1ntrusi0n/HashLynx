using HashLynx.Core;

namespace HashLynx.Hashcat.Tests;

public sealed class CommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HashLynx-test-" + Guid.NewGuid().ToString("N"));
    private readonly HashcatInstallation _installation;
    private readonly HashcatCommandBuilder _builder;
    public CommandTests()
    {
        Directory.CreateDirectory(_root);
        var backend = Path.Combine(_root, "backend");
        Directory.CreateDirectory(backend);
        System.IO.File.WriteAllText(Path.Combine(backend, "hashcat.exe"), "fixture");
        _installation = new(Path.Combine(backend, "hashcat.exe"), "v7.1.2", new()
        {
            Identify = true, Restore = true, Status = true, StatusJson = true,
            AttackModes = new() { [AttackFamilies.Dictionary] = 0, [AttackFamilies.Combinator] = 1, [AttackFamilies.Mask] = 3, [AttackFamilies.HybridWordlistMask] = 6, [AttackFamilies.HybridMaskWordlist] = 7 }
        });
        _builder = new(Path.Combine(_root, "state"));
    }

    private HashcatJob Job(string family = AttackFamilies.Dictionary) => new()
    {
        TargetPath = File("target with spaces & symbols.hash"), HashMode = 0,
        Attack = new() { Kind = family, Wordlists = [File("word list & data.txt")], Mask = "?u?l?d?d" },
        Options = new() { SessionName = "safe-session_01", OutputPath = Path.Combine(_root, "results output.txt") }
    };

    private string File(string name)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, "synthetic fixture");
        return path;
    }

    [Fact]
    public void DictionaryPreservesPathsAndMultipleRulesAsSeparateArguments()
    {
        var job = Job();
        job.Attack.RuleFiles = [File("left rules.rule"), File("right & rules.rule")];
        job.Options.Devices = [1, 3];
        var command = _builder.Build(_installation, job);
        Assert.Equal("0", Value(command, "--attack-mode"));
        Assert.Equal("1,3", Value(command, "--backend-devices"));
        Assert.Equal("safe-session_01", Value(command, "--session"));
        Assert.Equal(job.Options.OutputPath, Value(command, "--outfile"));
        Assert.Equal(2, command.Arguments.Count(a => a == "--rules-file"));
        Assert.Contains(job.Attack.RuleFiles[1], command.Arguments);
        Assert.Equal([job.TargetPath, job.Attack.Wordlists[0]], Positionals(command));
        Assert.Contains("\"" + job.TargetPath + "\"", command.Preview);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("1")]
    [InlineData("1,3")]
    public void ExplicitDeviceIdsRemainEligibleRegardlessOfOpenClDeviceType(string deviceIds)
    {
        var job = Job();
        job.Options.Devices = deviceIds.Split(',').Select(int.Parse).ToList();
        var command = _builder.Build(_installation, job);
        Assert.Equal(deviceIds, Value(command, "--backend-devices"));
        Assert.Equal("1,2,3", Value(command, "--opencl-device-types"));
        Assert.Single(command.Arguments, value => value == "--backend-devices");
        Assert.Single(command.Arguments, value => value == "--opencl-device-types");
        Assert.DoesNotContain("--force", command.Arguments);
    }

    [Fact]
    public void AutomaticDeviceSelectionPreservesHashcatDefaults()
    {
        var command = _builder.Build(_installation, Job());
        Assert.DoesNotContain("--backend-devices", command.Arguments);
        Assert.DoesNotContain("--opencl-device-types", command.Arguments);
    }

    [Theory]
    [InlineData(AttackFamilies.Mask, "3")]
    [InlineData(AttackFamilies.HybridWordlistMask, "6")]
    [InlineData(AttackFamilies.HybridMaskWordlist, "7")]
    public void MaskAndHybridOrderingReflectDirection(string kind, string expectedId)
    {
        var job = Job(kind);
        job.Attack.Increment = true;
        job.Attack.IncrementMaximum = 4;
        job.Attack.CustomCharsets = new() { [1] = "?l?u" };
        var command = _builder.Build(_installation, job);
        Assert.Equal(expectedId, Value(command, "--attack-mode"));
        Assert.Equal("?l?u", Value(command, "--custom-charset1"));
        Assert.Equal("4", Value(command, "--increment-max"));
        Assert.Equal(job.TargetPath, Positionals(command)[0]);
        if (kind == AttackFamilies.HybridWordlistMask) Assert.Equal(job.Attack.Mask, Positionals(command)[2]);
        else Assert.Equal(job.Attack.Mask, Positionals(command)[1]);
    }

    [Fact]
    public void CombinatorPreservesLeftRightWordlistsAndRules()
    {
        var job = Job(AttackFamilies.Combinator);
        job.Attack.Wordlists.Add(File("right.txt"));
        job.Attack.LeftRule = "c"; job.Attack.RightRule = "$!";
        var command = _builder.Build(_installation, job);
        Assert.Equal("1", Value(command, "--attack-mode"));
        Assert.Equal("c", Value(command, "--rule-left"));
        Assert.Equal("$!", Value(command, "--rule-right"));
        Assert.Equal(job.Attack.Wordlists, Positionals(command).Skip(1));
    }

    [Fact]
    public void MaskSpecialCharactersRemainOneLiteralArgumentAndNeverInvokeShell()
    {
        var job = Job(AttackFamilies.Mask);
        job.Attack.Mask = "-literal \"quoted\" & | $(echo injected) ?d";
        var command = _builder.Build(_installation, job);
        var start = HashcatRunner.CreateStartInfo(command);
        Assert.False(start.UseShellExecute);
        Assert.Equal("", start.Arguments);
        Assert.Equal(_installation.ExecutablePath, start.FileName);
        Assert.Equal(job.Attack.Mask, start.ArgumentList[^1]);
        Assert.Contains("--", start.ArgumentList);
        Assert.Equal(command.Arguments, start.ArgumentList);
    }

    [Theory]
    [InlineData("--session=hijack")]
    [InlineData("-m1000")]
    [InlineData("--remove")]
    [InlineData("--force")]
    [InlineData("--brain-client")]
    [InlineData("--outfile=private.txt")]
    [InlineData("--status-json=false")]
    [InlineData("--opencl-device-types=1")]
    [InlineData("-D1")]
    [InlineData("--")]
    public void ExpertArgumentsCannotOverrideManagedValuesOrChangeExecutionMode(string value)
    {
        var job = Job(); job.Options.ExtraArguments = [value];
        Assert.Throws<ArgumentException>(() => _builder.Build(_installation, job));
    }

    [Fact]
    public void ExpertArgumentParserPreservesBackslashesAndQuotedValues()
    {
        Assert.Equal(["--encoding-from", "ISO-8859-1", "--runtime=5"], ExpertArguments.Parse("--encoding-from \"ISO-8859-1\" --runtime=5"));
        Assert.Empty(ExpertArguments.Validate(ExpertArguments.Parse("--runtime 5 --username")));
        Assert.Throws<ArgumentException>(() => ExpertArguments.Parse("--runtime \"5"));
    }

    [Fact]
    public void AttackModeExtensionNeedsNoChangesToCommandGeneration()
    {
        _installation.Capabilities.AttackModes["hashcat:9"] = 9;
        var builder = new HashcatCommandBuilder(Path.Combine(_root, "state"), [new("hashcat:9", attack => attack.Wordlists)]);
        var job = Job("hashcat:9");
        Assert.Equal("9", Value(builder.Build(_installation, job), "--attack-mode"));
    }

    [Fact]
    public void PreflightRejectsSourceOverwriteAndMissingWordlists()
    {
        var job = Job(); job.Options.OutputPath = job.TargetPath;
        job.Attack.Wordlists = [Path.Combine(_root, "missing.txt")];
        var result = new JobValidator(_builder).Validate(_installation, job);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "Wordlist");
        Assert.Contains(result.Errors, error => error.Message.Contains("overwrite"));
    }

    [Fact]
    public void PreflightRejectsInstallationOutputAndInvalidSession()
    {
        var job = Job(); job.Options.OutputPath = Path.Combine(_installation.DirectoryPath, "recovered.txt");
        job.Options.SessionName = "../../escape";
        var result = new JobValidator(_builder).Validate(_installation, job);
        Assert.Contains(result.Errors, error => error.Field == "Session");
        Assert.Contains(result.Errors, error => error.Message.Contains("outside the Hashcat"));
    }

    [Fact]
    public void ValidDictionaryPreflightSucceedsWithoutRunningABackend()
    {
        var result = new JobValidator(_builder).Validate(_installation, Job());
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Theory]
    [InlineData(AttackFamilies.HybridWordlistMask)]
    [InlineData(AttackFamilies.HybridMaskWordlist)]
    public void HybridAcceptsInlineRulesButRejectsRuleFiles(string family)
    {
        var job = Job(family);
        job.Attack.LeftRule = "c";
        Assert.True(new JobValidator(_builder).Validate(_installation, job).IsValid);
        job.Attack.RuleFiles.Add(File("hybrid.rule"));
        Assert.Contains(new JobValidator(_builder).Validate(_installation, job).Errors, error => error.Field == "Rules");
    }

    [Fact]
    public void LoopbackRequiresDictionaryRulesAndRejectsCandidateLimits()
    {
        var job = Job();
        job.Attack.Loopback = true;
        Assert.Contains(new JobValidator(_builder).Validate(_installation, job).Errors, error => error.Field == "Loopback");
        job.Attack.RuleFiles.Add(File("loopback.rule"));
        Assert.True(new JobValidator(_builder).Validate(_installation, job).IsValid);
        job.Options.ExtraArguments = ["--limit=10"];
        Assert.Contains(new JobValidator(_builder).Validate(_installation, job).Errors, error => error.Field == "Loopback");
    }

    [Fact]
    public void ShowPreservesInputInterpretationWithoutForwardingAttackOrRuntimeOptions()
    {
        var job = Job();
        job.Options.ExtraArguments = ["--username", "--dynamic-x", "--hex-salt", "--runtime", "60", "--slow-candidates"];
        var command = _builder.BuildShow(_installation, job);
        Assert.Contains("--username", command.Arguments);
        Assert.Contains("--dynamic-x", command.Arguments);
        Assert.Contains("--hex-salt", command.Arguments);
        Assert.DoesNotContain("--runtime", command.Arguments);
        Assert.DoesNotContain("60", command.Arguments);
        Assert.DoesNotContain("--slow-candidates", command.Arguments);
        Assert.Equal(job.TargetPath, command.Arguments[^1]);
    }

    [Fact]
    public void DiscoveryHonorsManualOverrideAndHandlesMissingBackend()
    {
        var facade = new HashcatFacade(Path.Combine(_root, "cache"));
        Assert.Equal(_installation.ExecutablePath, facade.Discover(_installation.DirectoryPath));
        Assert.Null(facade.Discover(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void PreviewQuotesEmbeddedQuotesAndTrailingSlashes()
    {
        Assert.Equal("\"a\\\"b\"", CommandPreview.Quote("a\"b"));
        Assert.Equal("\"C:\\space path\\\\\"", CommandPreview.Quote("C:\\space path\\"));
        Assert.Equal("\"\"", CommandPreview.Quote(""));
    }

    private static string Value(HashcatCommand command, string option) => command.Arguments[command.Arguments.ToList().IndexOf(option) + 1];
    private static string[] Positionals(HashcatCommand command) => command.Arguments.SkipWhile(a => a != "--").Skip(1).ToArray();
    public void Dispose() => Directory.Delete(_root, true);
}
