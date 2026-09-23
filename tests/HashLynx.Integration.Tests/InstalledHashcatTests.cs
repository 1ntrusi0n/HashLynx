using System.Security.Cryptography;
using System.Text;
using HashLynx.Core;
using HashLynx.Hashcat;
using Xunit;

namespace HashLynx.Integration.Tests;

public sealed class InstalledHashcatFactAttribute : FactAttribute
{
    public InstalledHashcatFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")))
            Skip = "Set HASHLYNX_TEST_HASHCAT to an explicitly selected hashcat.exe to run installed-backend integration checks.";
    }
}

/// <summary>Explicit opt-in backend checks. Inputs are generated synthetic data; no cracking device is required.</summary>
public sealed class InstalledHashcatTests
{
    [InstalledHashcatFact]
    [Trait("Category", "Integration")]
    public async Task InstalledBackendSupportsCatalogAmbiguousIdentificationAndShow()
    {
        var root = Path.Combine(Path.GetTempPath(), "HashLynx-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var facade = new HashcatFacade(Path.Combine(root, "cache"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            var installation = await facade.ProbeAsync(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")!, cancellation.Token);
            Assert.NotEmpty(installation.Version);
            Assert.True(installation.Capabilities.Identify);
            Assert.True(installation.Capabilities.StatusJson);
            Assert.Contains(AttackFamilies.Dictionary, installation.Capabilities.AttackModes.Keys);
            var catalog = await facade.GetHashModesAsync(installation, cancellation.Token);
            Assert.True(catalog.Count > 100);
            Assert.Contains(catalog, mode => mode.Mode == 0);
            Assert.Contains(catalog, mode => mode.Mode == 1000);

            // Only synthetic test text is used, generated at runtime rather than checked-in credentials.
            var syntheticPlaintext = "HashLynx-integration-only-" + Guid.NewGuid().ToString("N");
            var digest = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(syntheticPlaintext)));
            var target = Path.Combine(root, "synthetic input.hashes");
            await File.WriteAllTextAsync(target, digest + "\n", cancellation.Token);
            var candidates = await facade.IdentifyAsync(installation, target, cancellation.Token);
            Assert.True(candidates.Count > 1);
            Assert.Contains(candidates, mode => mode.Mode == 0);
            Assert.Contains(candidates, mode => mode.Mode == 1000);

            var potfile = Path.Combine(root, "synthetic test.potfile");
            await File.WriteAllTextAsync(potfile, digest + ":" + syntheticPlaintext + "\n", cancellation.Token);
            var job = new HashcatJob { TargetPath = target, HashMode = 0, Options = new CommonOptions { PotfilePath = potfile } };
            var results = await facade.ShowAsync(installation, job, cancellation.Token);
            var result = Assert.Single(results);
            Assert.Equal(digest, result.Hash);
            Assert.Equal(syntheticPlaintext, result.Plaintext);

            // The catalog cache must also work through a fresh facade.
            var secondFacade = new HashcatFacade(Path.Combine(root, "cache"));
            Assert.Equal(catalog.Count, (await secondFacade.GetHashModesAsync(installation, cancellation.Token)).Count);
        }
        finally
        {
            // This uniquely created temporary directory is the complete extent of test-owned data.
            Directory.Delete(root, recursive: true);
        }
    }
}
