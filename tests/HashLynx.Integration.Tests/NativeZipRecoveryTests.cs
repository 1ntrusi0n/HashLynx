using System.Diagnostics;
using System.Security.Cryptography;
using HashLynx.Core;
using HashLynx.Extractors;
using HashLynx.Hashcat;
using Xunit;

namespace HashLynx.Integration.Tests;

public sealed class InstalledZipRecoveryFactAttribute : FactAttribute
{
    public InstalledZipRecoveryFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")) ||
            !File.Exists(Environment.GetEnvironmentVariable("HASHLYNX_TEST_7ZIP")) ||
            !int.TryParse(Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE"), out _))
            Skip = "Set HASHLYNX_TEST_HASHCAT (disposable backend), HASHLYNX_TEST_7ZIP and HASHLYNX_TEST_DEVICE to run real ZIP recovery.";
    }
}

public sealed class NativeZipRecoveryTests
{
    [InstalledZipRecoveryFact]
    [Trait("Category", "Integration")]
    public async Task ExtractIdentifyRecoverAndReadResultsForIndependentlyCreatedArchives()
    {
        var root = Path.Combine(Path.GetTempPath(), "HashLynx-zip-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cancellation.Token;
        try
        {
            var facade = new HashcatFacade(Path.Combine(root, "cache"));
            var installation = await facade.ProbeAsync(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")!, ct);
            var device = int.Parse(Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE")!);
            var source = Path.Combine(root, "synthetic.txt");
            await File.WriteAllTextAsync(source, string.Concat(Enumerable.Repeat("Synthetic HashLynx ZIP recovery test data.\n", 64)), ct);
            foreach (var (encryption, compression, expectedMode) in new[]
            {
                ("ZipCrypto", "Copy", 17210), ("ZipCrypto", "Deflate", 17200),
                ("AES128", "Deflate", 13600), ("AES192", "Deflate", 13600), ("AES256", "Deflate", 13600)
            })
            {
                var name = encryption + "-" + compression;
                var password = "zip-fixture-" + Guid.NewGuid().ToString("N")[..12];
                var archive = Path.Combine(root, name + ".zip");
                var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("HASHLYNX_TEST_7ZIP")!)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
                foreach (var argument in new[] { "a", "-tzip", "-mem=" + encryption, "-mm=" + compression, "-p" + password, archive, source }) info.ArgumentList.Add(argument);
                using (var process = Process.Start(info)!)
                {
                    var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
                    using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
                    await process.WaitForExitAsync(ct); await Task.WhenAll(stdout, stderr);
                    Assert.True(process.ExitCode == 0, $"7-Zip failed to generate {name}: exit {process.ExitCode}.");
                }
                var before = SHA256.HashData(await File.ReadAllBytesAsync(archive, ct));
                var extraction = await new ZipHashExtractor().ExtractAsync(archive, ct);
                Assert.True(extraction.Success, string.Join("; ", extraction.Diagnostics));
                Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(archive, ct)));
                Assert.Equal(expectedMode, Assert.Single(extraction.SuggestedHashcatModes));
                var target = Path.Combine(root, name + ".hashes");
                await File.WriteAllLinesAsync(target, extraction.Hashes, ct);
                var identified = await facade.IdentifyAsync(installation, target, ct);
                Assert.Contains(identified, mode => mode.Mode == expectedMode);
                Assert.Single(identified, mode => extraction.SuggestedHashcatModes.Contains(mode.Mode));
                Assert.Equal(0, (await new HashFileAnalyzer().AnalyzeAsync(target, ct)).ProblemLines);
                var words = Path.Combine(root, name + ".words");
                await File.WriteAllLinesAsync(words, ["wrong-test-candidate", password], ct);
                var job = new HashcatJob
                {
                    TargetPath = target, HashMode = expectedMode, Attack = new() { Wordlists = [words] },
                    Options = new() { Devices = [device], WorkloadProfile = 1, DisablePotfile = true }
                };
                var completed = await facade.StartJob(installation, job, cancellationToken: ct).Completion;
                Assert.True(completed.ExitCode == 0, $"Hashcat recovery failed for {name}: exit {completed.ExitCode}.");
                var recovered = Assert.Single(await facade.ReadSessionResultsAsync(job, ct));
                Assert.Equal(password, recovered.Plaintext);
                Assert.StartsWith(expectedMode == 13600 ? "$zip2$" : "$pkzip$", recovered.Hash);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
