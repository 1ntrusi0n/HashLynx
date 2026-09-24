using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace HashLynx.Hashcat.Tests;

public sealed class MaskPresetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HashLynx-masks-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void VersionedCatalogueContainsOneThousandDisjointValidStructuresWithExactEffort()
    {
        var catalog = new MaskPresetCatalog(_root);
        var preset = Assert.Single(catalog.Presets);
        Assert.Equal(MaskPresetCatalog.Common1000Id, preset.Id);
        var masks = catalog.GetMasks(preset.Id);
        Assert.Equal(1000, masks.Count);
        Assert.Equal(1000, masks.Distinct(StringComparer.Ordinal).Count());
        Assert.All(masks, mask => Assert.Matches(@"^(\?[luds]){1,11}$", mask));
        Assert.Equal(1, preset.MinLength);
        Assert.Equal(11, preset.MaxLength);
        // Each token is a disjoint ASCII class and the masks are distinct, so summing
        // their products counts the union without overlap or unsigned-integer overflow.
        var independentlyCounted = masks.Aggregate(BigInteger.Zero, (total, mask) => total +
            mask.Where(character => character != '?').Aggregate(BigInteger.One,
                (product, token) => product * (token == 'd' ? 10 : token == 's' ? 33 : 26)));
        Assert.Equal(BigInteger.Parse("971288570427", CultureInfo.InvariantCulture), preset.CandidateCount);
        Assert.Equal(independentlyCounted, preset.CandidateCount);
        Assert.Contains("historical", preset.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top", preset.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("?d", masks[0]);
        Assert.Equal("?u?s?u?d?d?d?d", masks[^1]);
        Assert.Contains("?l?l?l?l?l?l?d?d", masks);
        Assert.Contains("?u?l?l?l?l?l?l", masks);
    }

    [Fact]
    public void EffortUsesBigIntegersAndRejectsUnsupportedTokensRatherThanGuessing()
    {
        Assert.Equal(26 * 26 * 10 * 33, MaskPresetCatalog.CountCandidates("?u?l?d?s"));
        Assert.Equal(BigInteger.Pow(26, 32), MaskPresetCatalog.CountCandidates(string.Concat(Enumerable.Repeat("?l", 32))));
        foreach (var invalid in new[] { "", "?", "?x", "?a", "?1", "literal", "?l,?d", "?d\n?l" })
            Assert.Throws<ArgumentException>(() => MaskPresetCatalog.CountCandidates(invalid));
    }

    [Fact]
    public async Task ManagedMaterializationIsVersionPinnedAndRepairsOnlyItsCache()
    {
        var catalog = new MaskPresetCatalog(_root);
        var path = await catalog.GetMaskFilePathAsync(MaskPresetCatalog.Common1000Id);
        Assert.Equal(Path.Combine(_root, MaskPresetCatalog.Common1000Id + ".hcmask"), path);
        var expected = await File.ReadAllBytesAsync(path);
        Assert.Equal(MaskPresetCatalog.Common1000Sha256, Convert.ToHexString(SHA256.HashData(expected)));
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("The MIT License (MIT)", text);
        Assert.Contains("Copyright (c) 2015-2025 Jens Steube", text);
        Assert.Contains("THE SOFTWARE IS PROVIDED", text);
        Assert.DoesNotContain('\r', text);
        var unrelated = Path.Combine(_root, "my-masks.hcmask");
        await File.WriteAllTextAsync(unrelated, "?l?l?l\n");
        await File.WriteAllTextAsync(path, "tampered cache");
        Assert.Equal(path, catalog.GetMaskFilePath(MaskPresetCatalog.Common1000Id));
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        Assert.Equal("?l?l?l\n", await File.ReadAllTextAsync(unrelated));
        // Exercise the asynchronous integrity comparison, including equal-length damage.
        var altered = expected.ToArray(); altered[^3] = (byte)'x';
        await File.WriteAllBytesAsync(path, altered);
        Assert.Equal(path, await catalog.GetMaskFilePathAsync(MaskPresetCatalog.Common1000Id));
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.Throws<ArgumentException>(() => catalog.GetMaskFilePath("../external"));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.GetMaskFilePathAsync("unavailable-v2"));
    }

    [Fact]
    public async Task CancelledMaterializationDoesNotCreateDirectoryOrChangeAnExistingFile()
    {
        var catalog = new MaskPresetCatalog(_root);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetMaskFilePathAsync(MaskPresetCatalog.Common1000Id, cancellation.Token));
        Assert.False(Directory.Exists(_root));
        var path = catalog.GetMaskFilePath(MaskPresetCatalog.Common1000Id);
        await File.WriteAllTextAsync(path, "preserved until repair requested");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetMaskFilePathAsync(MaskPresetCatalog.Common1000Id, cancellation.Token));
        Assert.Equal("preserved until repair requested", await File.ReadAllTextAsync(path));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

public sealed class MaskPresetIntegrationTests
{
    [InstalledRulePresetFact]
    [Trait("Category", "Integration")]
    public async Task InstalledHashcatConfirmsRepresentativeCharacterClassesAndLengthsWithoutAComputeDevice()
    {
        var root = Path.Combine(Path.GetTempPath(), "HashLynx-masks-integration-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var catalog = new MaskPresetCatalog(root);
            _ = await catalog.GetMaskFilePathAsync(MaskPresetCatalog.Common1000Id, cancellation.Token);
            var executable = Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")!;
            var masks = catalog.GetMasks(MaskPresetCatalog.Common1000Id);
            // Hashcat 7.1.2 does not accept mask files with --total-candidates.
            // Check boundaries/classes individually; normal tests validate the complete asset.
            var representatives = new[] { masks[0], masks[^1], masks.MaxBy(mask => mask.Length)!, masks.MaxBy(MaskPresetCatalog.CountCandidates)! };
            foreach (var mask in representatives)
            {
                var arguments = new[] { "--total-candidates", "--quiet", "--logfile-disable", "--session", "mask-check-" + Guid.NewGuid().ToString("N"), "--attack-mode", "3", "--", mask };
                var capture = await new HashcatRunner().CaptureAsync(new(executable, Path.GetDirectoryName(executable)!, arguments), cancellation.Token);
                Assert.Equal(0, capture.ExitCode);
                Assert.DoesNotContain("invalid", capture.StandardError, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(MaskPresetCatalog.CountCandidates(mask), BigInteger.Parse(capture.StandardOutput.Trim(), CultureInfo.InvariantCulture));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
