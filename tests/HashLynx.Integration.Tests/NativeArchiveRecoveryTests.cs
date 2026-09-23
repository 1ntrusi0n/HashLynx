using System.Diagnostics;
using System.Security.Cryptography;
using HashLynx.Core;
using HashLynx.Extractors;
using HashLynx.Hashcat;
using Xunit;
using Xunit.Abstractions;

namespace HashLynx.Integration.Tests;

public sealed class InstalledSevenZipFactAttribute : FactAttribute
{
    public InstalledSevenZipFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")) ||
            !File.Exists(Environment.GetEnvironmentVariable("HASHLYNX_TEST_7ZIP")) ||
            !int.TryParse(Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE"), out _))
            Skip = "Select HASHLYNX_TEST_HASHCAT, HASHLYNX_TEST_7ZIP and HASHLYNX_TEST_DEVICE for real 7z recovery.";
    }
}
public sealed class InstalledRarFactAttribute : FactAttribute
{
    public InstalledRarFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")) ||
            !Directory.Exists(Environment.GetEnvironmentVariable("HASHLYNX_TEST_RAR_FIXTURES")) ||
            !int.TryParse(Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE"), out _))
            Skip = "Select HASHLYNX_TEST_HASHCAT, HASHLYNX_TEST_RAR_FIXTURES and HASHLYNX_TEST_DEVICE for public RAR fixture recovery.";
    }
}

public sealed class NativeArchiveRecoveryTests(ITestOutputHelper output)
{
    [InstalledSevenZipFact]
    [Trait("Category", "Integration")]
    public async Task SevenZipCreatedArchivesExtractIdentifyRecoverAndReturnSessionPassword()
    {
        await WithBackendAsync(async (root, facade, installation, ct) =>
        {
            var source = Path.Combine(root, "synthetic.txt");
            await File.WriteAllTextAsync(source, string.Concat(Enumerable.Repeat("Synthetic 7-Zip recovery validation.\n", 128)), ct);
            var secondSource = Path.Combine(root, "second.txt");
            await File.WriteAllTextAsync(secondSource, "Second synthetic file in a solid 7z stream.", ct);
            foreach (var (codec, headers, solid) in new[] { ("LZMA2", true, false), ("LZMA2", false, false), ("Copy", false, false), ("LZMA", false, false), ("Deflate", false, false), ("LZMA2", false, true) })
            {
                var archive = Path.Combine(root, codec + "-" + headers + "-" + solid + ".7z");
                var password = "7z-test-" + Guid.NewGuid().ToString("N")[..10];
                var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("HASHLYNX_TEST_7ZIP")!)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "a", "-t7z", "-m0=" + codec, "-mhe=" + (headers ? "on" : "off"), "-p" + password, archive, source }) info.ArgumentList.Add(arg);
                if (solid) info.ArgumentList.Add(secondSource);
                using (var process = Process.Start(info)!)
                {
                    var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
                    using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
                    await process.WaitForExitAsync(ct); await Task.WhenAll(stdout, stderr); Assert.Equal(0, process.ExitCode);
                }
                await RecoverAsync(new SevenZipHashExtractor(), archive, password, root, facade, installation, ct);
                output.WriteLine($"Recovered 7z {codec}, encrypted headers={headers}, solid={solid}.");
            }
        });
    }

    [InstalledRarFact]
    [Trait("Category", "Integration")]
    public async Task PublicRar3AndRar5FixturesExtractIdentifyRecoverAndReturnSessionPassword()
    {
        // Public rarfile test suite archives; provenance and download instructions are in docs/extractors.md.
        // The fixtures and their known test password are unrelated to user targets.
        await WithBackendAsync(async (root, facade, installation, ct) =>
        {
            // Synthetic RAR3 container around Hashcat's public mode-23700 self-test vector.
            // Source: https://github.com/hashcat/hashcat/blob/master/src/modules/module_23700.c
            var stored = Path.Combine(root, "stored.rar");
            await File.WriteAllBytesAsync(stored, Convert.FromBase64String("UmFyIRoHAM+QcwAADQAAAAAAAABz53QEhCkAEAAAAA4AAAACSbCoRgAAAAAdMAEAIAAAAGHlSnNymIfLUzRiC8yoF2ZCohCxBRkBkh4EsHsAAAcA"), ct);
            await RecoverAsync(new RarHashExtractor(), stored, "hashcat", root, facade, installation, ct);
            output.WriteLine("Recovered stored RAR3 self-test fixture.");
            foreach (var name in new[] { "rar3-comment-hpsw.rar", "test_read_format_rar4_encrypted.rar", "rar5-hpsw.rar", "rar5-psw.rar", "rar5-psw-blake.rar" })
            {
                await RecoverAsync(new RarHashExtractor(), Path.Combine(Environment.GetEnvironmentVariable("HASHLYNX_TEST_RAR_FIXTURES")!, name),
                    "password", root, facade, installation, ct);
                output.WriteLine($"Recovered public fixture {name}.");
            }
        });
    }

    private static async Task WithBackendAsync(Func<string, HashcatFacade, HashcatInstallation, CancellationToken, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "HashLynx-native-archive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            var facade = new HashcatFacade(Path.Combine(root, "cache"));
            var installation = await facade.ProbeAsync(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")!, cancellation.Token);
            await action(root, facade, installation, cancellation.Token);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task RecoverAsync(IHashExtractor extractor, string archive, string password, string root,
        HashcatFacade facade, HashcatInstallation installation, CancellationToken ct)
    {
        var before = SHA256.HashData(await File.ReadAllBytesAsync(archive, ct));
        var extraction = await extractor.ExtractAsync(archive, ct);
        Assert.True(extraction.Success, string.Join("; ", extraction.Diagnostics));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(archive, ct)));
        var mode = Assert.Single(extraction.SuggestedHashcatModes);
        var target = Path.Combine(root, Guid.NewGuid() + ".hashes");
        await File.WriteAllLinesAsync(target, extraction.Hashes, ct);
        Assert.Contains(await facade.IdentifyAsync(installation, target, ct), match => match.Mode == mode);
        Assert.Equal(0, (await new HashFileAnalyzer().AnalyzeAsync(target, ct)).ProblemLines);
        var words = Path.Combine(root, "fixture.words"); await File.WriteAllLinesAsync(words, ["wrong-fixture-candidate", password], ct);
        var job = new HashcatJob
        {
            TargetPath = target, HashMode = mode, Attack = new() { Wordlists = [words] },
            Options = new() { Devices = [int.Parse(Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE")!)], WorkloadProfile = 1, DisablePotfile = true }
        };
        var completed = await facade.StartJob(installation, job, cancellationToken: ct).Completion;
        Assert.True(completed.ExitCode == 0, $"Native {extractor.DisplayName} recovery (mode {mode}) exited with code {completed.ExitCode}.");
        Assert.Equal(password, Assert.Single(await facade.ReadSessionResultsAsync(job, ct)).Plaintext);
    }
}
