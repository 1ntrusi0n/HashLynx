using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using HashLynx.Core;

namespace HashLynx.Hashcat.Tests;

public sealed class Version8Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HashLynx-v8-" + Guid.NewGuid().ToString("N"));
    private readonly HashcatInstallation _installation;
    private readonly HashcatFacade _facade;
    private HashcatCommandBuilder Builder => _facade.Commands;
    private const string Help = "--restore |\n--restore-position |\n--rules-concat |\n--status |\n--status-json |\n--custom-charset8 |\n- [ Attack Modes ] -\n0 | Straight\n1 | Combination\n3 | Brute-force\n4 | PCFG, a trained grammar makes the candidates\n6 | Hybrid Wordlist + Mask\n7 | Hybrid Mask + Wordlist\n12 | Hybrid, mask says where the word goes\n- [ Built-in Charsets ] -";

    public Version8Tests()
    {
        Directory.CreateDirectory(_root);
        var runtime = Path.Combine(_root, "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, "pcfg"));
        _installation = new(File("source/hashcat.exe"), "v8.0.0", HashcatHelpParser.ParseCapabilities(Help, "v8.0.0")) { RuntimeDirectory = runtime };
        System.IO.File.WriteAllText(Path.Combine(runtime, "pcfg", "default-passwords.tar.xz"), "grammar fixture");
        System.IO.File.WriteAllText(Path.Combine(runtime, "pcfg", "hints.tar.xz"), "hint grammar fixture");
        _facade = new(Path.Combine(_root, "cache"));
    }

    private string File(string relative, string contents = "fixture")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, contents);
        return path;
    }

    private HashcatJob Job(string family = AttackFamilies.Dictionary) => new()
    {
        TargetPath = File("target.hashes"), HashMode = 0,
        Attack = new() { Kind = family, Wordlists = family == AttackFamilies.Dictionary ? [File("words.txt")] : [] },
        Options = new() { OutputPath = Path.Combine(_root, "out.txt"), RestorePath = Path.Combine(_root, "session.restore"), Devices = [1] }
    };

    [Fact]
    public void CapabilitiesSeparateV8FeaturesFromLegacyModes()
    {
        Assert.Equal(4, _installation.Capabilities.AttackModes[AttackFamilies.Pcfg]);
        Assert.Equal(12, _installation.Capabilities.AttackModes[AttackFamilies.WordlistPattern]);
        Assert.True(_installation.Capabilities.RestorePosition);
        Assert.True(_installation.Capabilities.RulesConcatenation);
        Assert.True(_installation.Capabilities.ExpandedRules);
        Assert.False(_installation.Capabilities.WorkloadProfilesEffective);
        var legacy = HashcatHelpParser.ParseCapabilities("--restore |\n- [ Attack Modes ] -\n0 | Straight", "v7.1.2");
        Assert.False(legacy.RestorePosition);
        Assert.False(legacy.ExpandedRules);
        Assert.True(legacy.WorkloadProfilesEffective);
    }

    [Fact]
    public void DefaultPcfgNeedsNoWordlistAndUsesStagedGrammar()
    {
        var job = Job(AttackFamilies.Pcfg);
        Assert.True(_facade.ValidateJob(_installation, job).IsValid);
        var command = Builder.Build(_installation, job);
        Assert.Equal([job.TargetPath, Path.Combine(_installation.WorkingDirectory, "pcfg", "default-passwords.tar.xz")], Positionals(command));
        Assert.Equal("4", Value(command, "--attack-mode"));
        Assert.Equal("1,2", Value(command, "--opencl-device-types"));
        Assert.DoesNotContain("--workload-profile", command.Arguments);
        Assert.Empty(AttackReadiness.CheckInputs(job.Attack, Builder.RulePresets, Builder.MaskPresets, _installation.Capabilities));
    }

    [Fact]
    public async Task HintsAreImmutableUtf8FilesAndNeverInlineArguments()
    {
        var path = await HashcatPcfgHints.WriteAsync(_root, "Alice\nété & $(literal)\nAlice");
        Assert.Equal("Alice\nété & $(literal)\n", (await System.IO.File.ReadAllTextAsync(path)).Replace("\r\n", "\n"));
        Assert.False((await System.IO.File.ReadAllBytesAsync(path)).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }));
        var job = Job(AttackFamilies.Pcfg);
        job.Attack.Pcfg = new() { UseHints = true, HintFile = path, MinimumLength = 4, MaximumLength = 12 };
        var command = Builder.Build(_installation, job);
        Assert.Contains("hintfile=" + path, command.Arguments);
        Assert.Contains("pwmin=4", command.Arguments);
        Assert.Contains("pwmax=12", command.Arguments);
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains("Alice") || argument.Contains("été"));
        Assert.False(HashcatRunner.CreateStartInfo(command).UseShellExecute);
        var another = await HashcatPcfgHints.WriteAsync(_root, "Other");
        Assert.NotEqual(path, another);
        Assert.Contains("Alice", await System.IO.File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("word\t0.5")]
    [InlineData("word\0")]
    public void HintFilesRejectProbabilitySyntaxAndEmptyInput(string words) => Assert.Throws<ArgumentException>(() => HashcatPcfgHints.ValidateWords(words));

    [Fact]
    public void PcfgRejectsConflictingShapesLengthsAndIncrement()
    {
        var attack = new AttackConfiguration { Kind = AttackFamilies.Pcfg, Increment = true, Pcfg = new() { ShapeMask = "?l?d", MinimumLength = 2 } };
        Assert.Contains(HashcatAttackValidation.CheckSpecialAttack(attack), error => error.Contains("increment"));
        Assert.Contains(HashcatAttackValidation.CheckSpecialAttack(attack), error => error.Contains("shape or length"));
        attack.Increment = false; attack.Pcfg.MinimumLength = null;
        Assert.Empty(HashcatAttackValidation.CheckSpecialAttack(attack));
        attack.CustomCharsets[1] = "?l"; attack.Pcfg.ShapeMask = null;
        Assert.Contains(HashcatAttackValidation.CheckSpecialAttack(attack), error => error.Contains("Custom character"));
    }

    [Fact]
    public void WordlistPatternOrdersMaskThenWordsAndPreservesLiteralCharacters()
    {
        var job = Job(AttackFamilies.WordlistPattern);
        job.Attack.Mask = "?d?w-?q! &";
        job.Attack.Wordlists = [File("first words.txt"), File("second & words.txt")];
        Assert.True(_facade.ValidateJob(_installation, job).IsValid);
        Assert.Equal([job.TargetPath, job.Attack.Mask, .. job.Attack.Wordlists], Positionals(Builder.Build(_installation, job)));
    }

    [Theory]
    [InlineData("?w", 1, true)]
    [InlineData("??w?w", 1, true)]
    [InlineData("?w??q", 1, true)]
    [InlineData("?w-?q", 2, true)]
    [InlineData("??w", 1, false)]
    [InlineData("?w?w", 1, false)]
    [InlineData("?q?w", 2, false)]
    [InlineData("?w?q?q", 2, false)]
    [InlineData("?w?q", 1, false)]
    [InlineData("?w", 2, false)]
    public void WordMarkersAreValidatedAsTokens(string mask, int wordlists, bool valid)
    {
        var attack = new AttackConfiguration { Kind = AttackFamilies.WordlistPattern, Mask = mask, Wordlists = Enumerable.Repeat("fixture", wordlists).ToList() };
        Assert.Equal(valid, HashcatAttackValidation.CheckSpecialAttack(attack).Count == 0);
        Assert.False(MaskValidator.Analyze("?w").IsValid);
    }

    [Fact]
    public void RulesAreVersionGatedAndConcatenationHasOnePersistentCompatibilityMarker()
    {
        var job = Job(AttackFamilies.Mask); job.Attack.Mask = "?d?d";
        job.Attack.RuleFiles = [File("first.rule"), File("second.rule")]; job.Attack.RulesConcatenated = true;
        HashcatCommandBuilder.SynchronizeCompatibilityOptions(job);
        HashcatCommandBuilder.SynchronizeCompatibilityOptions(job);
        Assert.Single(job.Options.ExtraArguments, value => value == "--rules-concat");
        Assert.Single(Builder.Build(_installation, job).Arguments, value => value == "--rules-concat");
        Assert.True(_facade.ValidateJob(_installation, job).IsValid);
        var legacy = _installation with { Version = "v7.1.2", Capabilities = HashcatHelpParser.ParseCapabilities(Help.Replace("--rules-concat |\n", ""), "v7.1.2") };
        Assert.False(new JobValidator(Builder).Validate(legacy, job).IsValid);
        Assert.NotEmpty(ExpertArguments.Validate(job.Options.ExtraArguments)); // older application rejects marker
        var roundtrip = JsonSerializer.Deserialize<HashcatJob>(JsonSerializer.Serialize(job))!;
        Assert.True(roundtrip.Attack.RulesConcatenated);
        Assert.Contains("--rules-concat", roundtrip.Options.ExtraArguments);
    }

    [Fact]
    public void RemovedInputsAndArgumentsHaveActionableValidation()
    {
        var job = Job(); job.Attack.Wordlists = [File("words.zip")]; job.Options.ExtraArguments = ["--segment-size", "32"];
        var result = _facade.ValidateJob(_installation, job);
        Assert.Contains(result.Errors, error => error.Message.Contains("ZIP"));
        Assert.Contains(result.Errors, error => error.Message.Contains("removed --segment-size"));
    }

    [Theory]
    [InlineData(1310, 40009)]
    [InlineData(10900, 40024)]
    [InlineData(12000, 40032)]
    [InlineData(35000, 40078)]
    public void LegacyRecipeModesRemainUsableWithoutChangingSavedIds(int oldMode, int currentMode)
    {
        Assert.True(HashcatModeAliases.IsSupported(_installation, oldMode, [new(currentMode, "fixture")]));
        Assert.False(HashcatModeAliases.IsSupported(_installation with { Version = "v7.1.2" }, oldMode, [new(currentMode, "fixture")]));
        var job = Job(); job.HashMode = oldMode;
        Assert.Equal(oldMode.ToString(), Value(Builder.Build(_installation, job), "--hash-type"));
    }

    [Fact]
    public void RestoreRebuildsManagedArgumentsAndDisregardsStoredCommands()
    {
        var job = Job(); var record = new JobRecord { Configuration = job };
        _facade.RecordProvenance(_installation, record);
        WriteRestore(job, 720, _installation.WorkingDirectory, "malicious stored argv --outfile unrelated.txt");
        System.IO.File.WriteAllText(job.Options.OutputPath!, "saved result");
        Assert.True(_facade.GetRestoreAvailability(_installation, record).CanRestore);
        Assert.True(new JobValidator(Builder).Validate(_installation, job, restoring: true).IsValid);
        var command = Builder.BuildRestore(_installation, job);
        Assert.Contains("--restore-position", command.Arguments);
        Assert.DoesNotContain("--restore", command.Arguments);
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains("malicious") || argument.Contains("unrelated.txt"));
        Assert.Equal(job.Options.OutputPath, Value(command, "--outfile"));
    }

    [Fact]
    public void RestorePreservesLegacyCheckpointsAndRejectsChangedInputsOrSettings()
    {
        var job = Job(); var record = new JobRecord { Configuration = job };
        _facade.RecordProvenance(_installation, record);
        WriteRestore(job, 611, _installation.WorkingDirectory);
        var original = System.IO.File.ReadAllBytes(job.Options.RestorePath!);
        var unavailable = _facade.GetRestoreAvailability(_installation, record);
        Assert.False(unavailable.CanRestore); Assert.Contains("older Hashcat", unavailable.Message);
        Assert.Equal(original, System.IO.File.ReadAllBytes(job.Options.RestorePath!));
        WriteRestore(job, 720, _installation.WorkingDirectory);
        job.Attack.Wordlists.Add(File("new.txt"));
        Assert.False(_facade.GetRestoreAvailability(_installation, record).CanRestore);
        job.Attack.Wordlists.RemoveAt(1);
        System.IO.File.AppendAllText(job.Attack.Wordlists[0], "changed");
        Assert.Contains("changed or is unavailable", _facade.GetRestoreAvailability(_installation, record).Message);
    }

    [Fact]
    public void RestoreRejectsUnexpectedDirectoryUnknownProvenanceAndUnsupportedHeaders()
    {
        var job = Job(); var record = new JobRecord { Configuration = job };
        WriteRestore(job, 720, _installation.WorkingDirectory);
        Assert.False(_facade.GetRestoreAvailability(_installation, record).CanRestore);
        _facade.RecordProvenance(_installation, record);
        WriteRestore(job, 720, _installation.DirectoryPath);
        Assert.False(_facade.GetRestoreAvailability(_installation, record).CanRestore);
        WriteRestore(job, 720, ".");
        Assert.False(_facade.GetRestoreAvailability(_installation, record).CanRestore);
        WriteRestore(job, 9999, _installation.WorkingDirectory);
        Assert.False(_facade.GetRestoreAvailability(_installation, record).CanRestore);
        System.IO.File.WriteAllBytes(job.Options.RestorePath!, [1, 2]);
        Assert.False(_facade.GetRestoreAvailability(_installation, record).CanRestore);
    }

    [Theory]
    [InlineData(-2, "attack command")]
    [InlineData(-12, "contents")]
    [InlineData(-13, "bridge or plugin")]
    [InlineData(-11, "self-test")]
    public void NewExitCodesGiveSanitizedActionableOutcomes(int exit, string expected)
    {
        var outcome = HashcatExitCodes.Describe(exit, "v8.0.0");
        Assert.Equal(JobState.Failed, outcome.State);
        Assert.Contains(expected, outcome.Diagnostic);
        Assert.Equal(JobState.Interrupted, HashcatExitCodes.Describe(5, "v8.0.0").State);
    }

    [Theory]
    [InlineData("Cannot open liblzma.dll at private-path", "compression runtime")]
    [InlineData("Cannot load feed_pcfg.dll: private-target", "candidate-generator")]
    public void NewDependencyGuidanceNeverEchoesPrivateBackendOutput(string input, string expected)
    {
        var message = HashcatRunningJob.SanitizeDiagnostic(input);
        Assert.Contains(expected, message);
        Assert.DoesNotContain("private", message);
    }

    private static void WriteRestore(HashcatJob job, int version, string cwd, string arguments = "")
    {
        var bytes = new byte[296];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, version);
        Encoding.UTF8.GetBytes(cwd).CopyTo(bytes, 4);
        System.IO.File.WriteAllBytes(job.Options.RestorePath!, bytes.Concat(Encoding.UTF8.GetBytes(arguments)).ToArray());
    }

    private static string[] Positionals(HashcatCommand command) => command.Arguments.SkipWhile(value => value != "--").Skip(1).ToArray();
    private static string Value(HashcatCommand command, string name) => command.Arguments[command.Arguments.ToList().IndexOf(name) + 1];
    public void Dispose() => Directory.Delete(_root, true);
}
