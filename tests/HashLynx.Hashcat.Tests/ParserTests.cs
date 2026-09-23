using HashLynx.Core;

namespace HashLynx.Hashcat.Tests;

public sealed class ParserTests
{
    [Fact]
    public void IdentificationPreservesAmbiguityAndCategories()
    {
        const string text = "The following modes match:\n # | Name | Category\n ===+====\n 0 | MD5 | Raw Hash\n1000 | NTLM | Operating System\n900 | MD4 | Raw Hash\n";
        var modes = HashModeParser.ParseIdentification(text);
        Assert.Equal(3, modes.Count);
        Assert.Equal("Operating System", modes.Single(m => m.Mode == 1000).Category);
        Assert.Equal("MD5", modes[0].Name);
        Assert.Empty(HashModeParser.ParseIdentification("No hash modes match."));
    }

    [Fact]
    public void CatalogParserDoesNotMistakeOtherNumberedHelpSectionsForHashModes()
    {
        const string text = "- [ Hash Modes ] -\n 0 | MD5 | Raw Hash\n1000 | NTLM | Operating System\n- [ Brain Client Features ] -\n1 | Send hashed passwords | Other\n";
        Assert.Equal([0, 1000], HashModeParser.ParseHelpCatalog(text).Select(m => m.Mode));
    }

    [Fact]
    public void CapabilitiesUseInstalledAttackIdsAndKeepUnknownModesExtensible()
    {
        const string help = "--identify\n--status |\n--status-json\n--restore |\n--custom-charset8\n- [ Attack Modes ] -\n 12 | Straight\n1 | Combination\n3 | Brute-force\n6 | Hybrid Wordlist + Mask\n7 | Hybrid Mask + Wordlist\n9 | Association\n- [ Built-in Charsets ] -";
        var capabilities = HashcatHelpParser.ParseCapabilities(help);
        Assert.True(capabilities.StatusJson);
        Assert.True(capabilities.Restore);
        Assert.Equal(12, capabilities.AttackModes[AttackFamilies.Dictionary]);
        Assert.Equal(9, capabilities.AttackModes["hashcat:9"]);
        Assert.Equal(8, capabilities.CustomCharsetCount);
    }

    [Fact]
    public void HashInfoFallbackReadsNameAndMode()
    {
        var modes = HashModeParser.ParseHashInfo("Hash mode #0\n Name................: MD5\n Category............: Raw Hash\nHash mode #1000\n Name................: NTLM\n");
        Assert.Equal(2, modes.Count);
        Assert.Equal("MD5", modes[0].Name);
    }

    [Fact]
    public void JsonStatusPreservesUsefulMetricsAndExcludesHashAndCandidateSecrets()
    {
        const string json = "{\"status\":3,\"target\":\"sensitive hash\",\"progress\":[25,100],\"recovered_hashes\":[2,5],\"rejected\":4,\"restore_point\":10,\"time_start\":1700000000,\"estimated_stop\":1700000120,\"devices\":[{\"device_id\":1,\"device_name\":\"GPU\",\"speed\":12345,\"temp\":61,\"util\":98,\"guess_candidates\":\"private candidate\"}]}";
        Assert.True(HashcatStatusParser.TryParse(json, out var result));
        Assert.NotNull(result);
        Assert.Equal("Running", result.State);
        Assert.Equal(25, result.ProgressPercent);
        Assert.Equal(12345, result.SpeedHashesPerSecond);
        Assert.Equal(2, result.RecoveredHashes);
        Assert.Equal(61, result.Devices.Single().Temperature);
        var persisted = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("sensitive", persisted);
        Assert.DoesNotContain("private candidate", persisted);
    }

    [Theory]
    [InlineData(2, "Self-test")]
    [InlineData(3, "Running")]
    [InlineData(4, "Paused")]
    [InlineData(5, "Exhausted")]
    [InlineData(6, "Cracked")]
    [InlineData(10, "Checkpoint")]
    [InlineData(11, "Runtime limit")]
    public void StatusNumbersMatchHashcatSeven(int number, string expected)
    {
        Assert.True(HashcatStatusParser.TryParse($"{{\"status\":{number}}}", out var status));
        Assert.Equal(expected, status!.State);
    }

    [Theory]
    [InlineData("{bad json}")]
    [InlineData("regular output")]
    [InlineData("{\"status\":3,\"devices\":[1]}")]
    public void InvalidStatusDoesNotCrashOutputReader(string value) => Assert.False(HashcatStatusParser.TryParse(value, out _));

    [Fact]
    public void ZeroTotalAndUnavailableDeviceMetricsAreHandled()
    {
        Assert.True(HashcatStatusParser.TryParse("{\"status\":4,\"progress\":[0,0],\"devices\":[{\"device_id\":2,\"speed\":0}]}", out var result));
        Assert.Equal(0, result!.ProgressPercent);
        Assert.Null(result.Devices.Single().Temperature);
    }

    [Fact]
    public void BackendParserAssociatesDetailsWithCorrectDevice()
    {
        const string text = "OpenCL Info:\nOpenCL Platform ID #1\n Name....: Vendor platform\nBackend Device ID #01\n Type...........: GPU\n Name...........: Example GPU\n Memory.Total...: 4096 MB\n Driver.Version.: 1.2\nBackend Device ID #02\n Type...........: CPU\n Name...........: Example CPU\nOpenCL Platform ID #2\n Name....: Another platform\n";
        var devices = BackendInfoParser.Parse(text);
        Assert.Equal(2, devices.Count);
        Assert.Equal("Example GPU", devices[0].Name);
        Assert.Equal("Example CPU", devices[1].Name);
        Assert.Equal("OpenCL", devices[0].Backend);
        Assert.Equal("4096 MB", devices[0].Memory);
    }

    [Fact]
    public void HexResultsKeepSaltAndPasswordSeparatorsUnambiguous()
    {
        var results = RecoveredResultParser.Parse("synthetic:salt:613a62\nempty:\nnoise:invalidhex\n");
        Assert.Equal(2, results.Count);
        Assert.Equal("synthetic:salt", results[0].Hash);
        Assert.Equal("a:b", results[0].Plaintext);
        Assert.Equal("", results[1].Plaintext);
    }

    [Theory]
    [InlineData("secret-hash:secret-password")]
    [InlineData("Hashfile C:\\private\\target.txt: Token length exception: private-secret")]
    public void DiagnosticSanitizationDoesNotExposePrivateValues(string line)
    {
        var sanitized = HashcatRunningJob.SanitizeDiagnostic(line);
        Assert.DoesNotContain("secret", sanitized);
        Assert.DoesNotContain("C:\\private", sanitized);
    }

    [Fact]
    public void RejectedLegacyDriverProducesAnActionableSanitizedDiagnostic()
    {
        var message = HashcatRunningJob.SanitizeDiagnostic("* Device #2: Outdated or broken Intel OpenCL runtime 5.2.0.10094 detected!");
        Assert.Contains("Install a supported driver/runtime", message);
        Assert.DoesNotContain("Device #2", message);
    }
}
