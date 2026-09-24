namespace HashLynx.Extractors.Tests;

public sealed class InstalledDocumentRecoveryTheoryAttribute : TheoryAttribute
{
    public InstalledDocumentRecoveryTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")) ||
            !int.TryParse(Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE"), out _))
            Skip = "Select HASHLYNX_TEST_HASHCAT (disposable backend) and HASHLYNX_TEST_DEVICE for real Office/KeePass recovery.";
    }
}

public sealed class DocumentRecoveryTests(Xunit.Abstractions.ITestOutputHelper testOutput)
{
    [InstalledDocumentRecoveryTheory]
    [Trait("Category", "Integration")]
    [InlineData("ecma376standard_password.docx")]
    [InlineData("example_password.docx")]
    [InlineData("example_password.xlsx")]
    public Task PublicOfficeFixtureRecoversItsKnownPassword(string name) => RecoverAsync(new OfficeHashExtractor(),
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Office", name), "Password1234_");

    [InstalledDocumentRecoveryTheory]
    [Trait("Category", "Integration")]
    [InlineData("generated")]
    [InlineData("keepass4_keepass.info_2.59_argon2d_defaultsettings")]
    [InlineData("keepass4_keepass.info_2.59_argon2id")]
    public Task KeePassFixtureRecoversItsKnownPassword(string name) => RecoverAsync(new KeePassHashExtractor(),
        name == "generated" ? null : Path.Combine(AppContext.BaseDirectory, "Fixtures", "KeePass", name + ".kdbx"),
        name == "generated" ? "HashLynxFixture!" : "hashcat");

    private async Task RecoverAsync(IHashExtractor extractor, string? source, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "HashLynx-document-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        // Bound each independent case while allowing first-use OpenCL compilation on older CPUs.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); var ct = timeout.Token;
        var backend = Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT")!;
        var device = Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE")!;
        try
        {
            if (source is null)
            {
                source = Path.Combine(root, "generated.kdbx"); await File.WriteAllBytesAsync(source, NativeDocumentTests.Kdbx3(), ct);
            }
            var extraction = await extractor.ExtractAsync(source, ct); Assert.True(extraction.Success, string.Join("; ", extraction.Diagnostics));
            var mode = Assert.Single(extraction.SuggestedHashcatModes);
            var target = Path.Combine(root, "target.hash"); var words = Path.Combine(root, "words.txt"); var output = Path.Combine(root, "recovered.txt");
            await File.WriteAllLinesAsync(target, extraction.Hashes, ct); await File.WriteAllLinesAsync(words, ["wrong-fixture-candidate", expected], ct);
            File.Delete(output);
            var runner = new ExtractorProcessRunner();
            var identify = await runner.RunAsync(new(backend, ["--identify", target]), ct);
            Assert.True(identify.ExitCode == 0 && identify.StandardOutput.Contains(mode.ToString(System.Globalization.CultureInfo.InvariantCulture)), $"Mode {mode} was not identified.");
            var result = await runner.RunAsync(new(backend,
                ["-m", mode.ToString(System.Globalization.CultureInfo.InvariantCulture), "-a", "0", "-d", device,
                    "--opencl-device-types", "1,2,3", "-w", "1", "--potfile-disable", "--restore-disable", "--logfile-disable", "--quiet", "--outfile", output,
                    "--outfile-format", "2", target, words]), ct);
            Assert.True(result.ExitCode == 0, $"Known-answer {extractor.DisplayName} recovery in mode {mode} exited {result.ExitCode}.");
            Assert.Equal(expected, (await File.ReadAllTextAsync(output, ct)).TrimEnd('\r', '\n'));
            testOutput.WriteLine($"Recovered a public/generated {extractor.DisplayName} fixture using mode {mode}.");
        }
        finally { Directory.Delete(root, true); }
    }
}
