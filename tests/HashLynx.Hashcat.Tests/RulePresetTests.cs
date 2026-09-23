using System.Text.Json;
using HashLynx.Core;

namespace HashLynx.Hashcat.Tests;

public sealed class RulePresetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HashLynx-presets-test-" + Guid.NewGuid().ToString("N"));
    private readonly RulePresetCatalog _catalog;
    public RulePresetTests() => _catalog = new(Path.Combine(_root, "presets"));

    [Fact]
    public void EmbeddedPresetsMatchReproducibleRecipesAndHaveStrictCumulativeBudgets()
    {
        var generated = RulePresetRecipes.Generate();
        Assert.Equal([64, 512, 4096, 16384], _catalog.Presets.Select(preset => preset.RuleCount));
        IReadOnlyList<string> previous = [];
        foreach (var preset in _catalog.Presets)
        {
            var rules = _catalog.GetRules(preset.Id);
            Assert.Equal(preset.RuleCount, rules.Count);
            Assert.Equal(rules.Count, rules.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(":", rules[0]);
            Assert.Single(rules, rule => rule == ":");
            Assert.Equal(previous, rules.Take(previous.Count));
            Assert.Equal(generated[preset.Id], rules);
            Assert.Equal(generated[preset.Id], RulePresetRecipes.Generate()[preset.Id]);
            Assert.Contains("Up to", preset.CandidateCountLabel);
            previous = rules;
        }
    }

    [Fact]
    public void MetadataDoesNotClaimUnmeasuredRecoveryRates()
    {
        Assert.Equal(["Quick", "Normal", "Heavy", "Super"], _catalog.Presets.Select(preset => preset.DisplayName));
        Assert.All(_catalog.Presets, preset => Assert.False(string.IsNullOrWhiteSpace(preset.Coverage)));
        Assert.Contains("1980–2035", _catalog.GetById(RulePresetCatalog.NormalId).Coverage);
    }

    [Fact]
    public void MaterializationUsesManagedPathsAndRepairsAlteredCacheFiles()
    {
        var path = _catalog.GetRuleFilePath(RulePresetCatalog.NormalId);
        Assert.StartsWith(Path.Combine(_root, "presets"), path, StringComparison.OrdinalIgnoreCase);
        var expected = File.ReadAllBytes(path);
        File.WriteAllText(path, "untrusted altered cache");
        Assert.Equal(path, _catalog.GetRuleFilePath(RulePresetCatalog.NormalId));
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Throws<ArgumentException>(() => _catalog.GetRuleFilePath("../unavailable"));
    }

    [Theory]
    [InlineData(RulePresetCatalog.QuickId)]
    [InlineData(RulePresetCatalog.NormalId)]
    [InlineData(RulePresetCatalog.HeavyId)]
    [InlineData(RulePresetCatalog.SuperId)]
    public void EveryPresetBuildsExactlyOneRuleFileAndSupportsLoopbackPreflight(string id)
    {
        var (builder, installation, job) = Fixture();
        job.Attack.RulePresetId = id;
        job.Attack.Loopback = true;
        var validation = new JobValidator(builder).Validate(installation, job);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(error => error.Message)));
        var command = builder.Build(installation, job);
        Assert.Single(command.Arguments, value => value == "--rules-file");
        var ruleIndex = command.Arguments.ToList().IndexOf("--rules-file");
        Assert.Equal(_catalog.GetRuleFilePath(id), command.Arguments[ruleIndex + 1]);
        Assert.Empty(job.Attack.RuleFiles);
        Assert.Equal(id, job.Attack.RulePresetId);
    }

    [Fact]
    public void PresetsCannotSilentlyMultiplyCustomRuleFilesOrLeakIntoOtherAttacks()
    {
        var (builder, installation, job) = Fixture();
        job.Attack.RulePresetId = RulePresetCatalog.QuickId;
        job.Attack.RuleFiles.Add(_catalog.GetRuleFilePath(RulePresetCatalog.NormalId));
        Assert.Throws<ArgumentException>(() => builder.Build(installation, job));
        Assert.Contains(new JobValidator(builder).Validate(installation, job).Errors, error => error.Message.Contains("Combining them multiplies"));
        job.Attack.RuleFiles.Clear();
        job.Attack.Kind = AttackFamilies.Mask;
        Assert.Throws<ArgumentException>(() => builder.ResolveRuleFiles(job.Attack));
    }

    [Fact]
    public void LegacyProfilesRetainCustomFilesAndNewProfilesKeepStableIds()
    {
        var legacy = JsonSerializer.Deserialize<AttackConfiguration>("{\"Kind\":\"dictionary\",\"RuleFiles\":[\"custom.rule\"]}")!;
        Assert.Null(legacy.RulePresetId);
        Assert.Equal(["custom.rule"], legacy.RuleFiles);
        var builder = new HashcatCommandBuilder(Path.Combine(_root, "jobs"));
        Assert.Equal(legacy.RuleFiles, builder.ResolveRuleFiles(legacy));
        var current = new AttackConfiguration { RulePresetId = RulePresetCatalog.HeavyId };
        var restored = JsonSerializer.Deserialize<AttackConfiguration>(JsonSerializer.Serialize(current))!;
        Assert.Equal(RulePresetCatalog.HeavyId, restored.RulePresetId);
        Assert.Empty(restored.RuleFiles);
    }

    private (HashcatCommandBuilder Builder, HashcatInstallation Installation, HashcatJob Job) Fixture()
    {
        Directory.CreateDirectory(Path.Combine(_root, "backend"));
        var executable = Path.Combine(_root, "backend", "hashcat.exe");
        var target = Path.Combine(_root, "target.hashes");
        var wordlist = Path.Combine(_root, "words.txt");
        File.WriteAllText(executable, "fixture"); File.WriteAllText(target, "synthetic"); File.WriteAllText(wordlist, "synthetic");
        var installation = new HashcatInstallation(executable, "test", new() { StatusJson = true, AttackModes = new() { [AttackFamilies.Dictionary] = 0, [AttackFamilies.Mask] = 3 } });
        var builder = new HashcatCommandBuilder(Path.Combine(_root, "jobs"), rulePresets: _catalog);
        var job = new HashcatJob { TargetPath = target, HashMode = 0, Attack = new() { Wordlists = [wordlist] } };
        return (builder, installation, job);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

public sealed class InstalledRulePresetFactAttribute : FactAttribute
{
    public InstalledRulePresetFactAttribute(string environmentVariable = "HASHLYNX_TEST_HASHCAT")
    {
        if (!File.Exists(Environment.GetEnvironmentVariable(environmentVariable)))
            Skip = $"Set {environmentVariable} to opt into this installed-Hashcat rule check. Candidate-output checks may also set HASHLYNX_TEST_DEVICE to current backend device IDs.";
    }
}

public sealed class RulePresetIntegrationTests
{
    [InstalledRulePresetFact]
    [Trait("Category", "Integration")]
    public async Task InstalledHashcatParsesEveryPresetAndConfirmsCandidateBudgetsWithoutAComputeDevice()
    {
        await WithBackendAsync("HASHLYNX_TEST_HASHCAT", async (facade, installation, wordlist, cancellationToken) =>
        {
            foreach (var preset in facade.RulePresets.Presets)
            {
                var arguments = new[] { "--total-candidates", "--quiet", "--logfile-disable", "--attack-mode", "0", "--rules-file", facade.RulePresets.GetRuleFilePath(preset.Id), "--", wordlist };
                var capture = await new HashcatRunner().CaptureAsync(new(installation.ExecutablePath, installation.WorkingDirectory, arguments), cancellationToken);
                Assert.Equal(0, capture.ExitCode);
                Assert.DoesNotContain("invalid rule", capture.StandardError, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unsupported rule", capture.StandardError, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(preset.RuleCount.ToString(System.Globalization.CultureInfo.InvariantCulture), capture.StandardOutput.Trim());
            }
        });
    }

    [InstalledRulePresetFact("HASHLYNX_TEST_HASHCAT_CANDIDATES")]
    [Trait("Category", "Integration")]
    public async Task InstalledHashcatAcceptsAllRulesAndProducesExpectedSyntheticCandidates()
    {
        await WithBackendAsync("HASHLYNX_TEST_HASHCAT_CANDIDATES", async (facade, installation, wordlist, cancellationToken) =>
        {
            var selectedDevices = Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE");
            string? deviceIds = null;
            if (!string.IsNullOrWhiteSpace(selectedDevices))
            {
                var parts = selectedDevices.Split(',', StringSplitOptions.TrimEntries);
                Assert.All(parts, part => Assert.True(int.TryParse(part, out var id) && id > 0, "HASHLYNX_TEST_DEVICE must contain comma-separated positive backend device IDs."));
                deviceIds = string.Join(',', parts);
            }
            foreach (var preset in facade.RulePresets.Presets)
            {
                // Hashcat's stdout mode can emit no redirected stdout on Windows; its outfile
                // transport exercises the same rule processing without relying on that handle.
                var candidateFile = Path.Combine(Path.GetDirectoryName(wordlist)!, preset.Id + ".candidates.txt");
                var arguments = new List<string> { "--stdout", "--quiet", "--attack-mode", "0", "--rules-file", facade.RulePresets.GetRuleFilePath(preset.Id), "--outfile", candidateFile };
                if (deviceIds is not null) arguments.AddRange(["--backend-devices", deviceIds, "--opencl-device-types", "1,2,3"]);
                arguments.AddRange(["--", wordlist]);
                var capture = await new HashcatRunner().CaptureAsync(new(installation.ExecutablePath, installation.WorkingDirectory, arguments), cancellationToken);
                Assert.Equal(0, capture.ExitCode);
                Assert.DoesNotContain("invalid rule", capture.StandardError, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unsupported rule", capture.StandardError, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(candidateFile), "Hashcat did not produce a candidate output file.");
                var candidates = await File.ReadAllLinesAsync(candidateFile, cancellationToken);
                Assert.Equal(preset.RuleCount, candidates.Length);
                Assert.Contains("Password", candidates);
                Assert.Contains("password", candidates);
                Assert.Contains("Password123", candidates);
                Assert.Contains("P@ssword", candidates);
                if (preset.RuleCount >= 512) Assert.Contains("Password1980", candidates);
                if (preset.RuleCount >= 4096) { Assert.Contains("P0assword", candidates); Assert.Contains("0assword", candidates); }
                if (preset.RuleCount >= 16384) { Assert.Contains("Password9999", candidates); Assert.Contains("999Password", candidates); }
            }
        });
    }

    private static async Task WithBackendAsync(string environmentVariable, Func<HashcatFacade, HashcatInstallation, string, CancellationToken, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "HashLynx-presets-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            var facade = new HashcatFacade(Path.Combine(root, "cache"));
            var installation = await facade.ProbeAsync(Environment.GetEnvironmentVariable(environmentVariable)!, cancellation.Token);
            var wordlist = Path.Combine(root, "synthetic-word.txt");
            await File.WriteAllTextAsync(wordlist, "Password\n", cancellation.Token);
            await action(facade, installation, wordlist, cancellation.Token);
        }
        finally { Directory.Delete(root, true); }
    }
}
