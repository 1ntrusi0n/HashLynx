using System.Text.Json;

namespace HashLynx.Hashcat.Tests;

public sealed class StatusProgressTests
{
    // Synthetic, non-secret fixtures follow the JSON emitters in the official v7.1.2 and v8.0.0
    // src/terminal.c. Queue indices identify a subattack; they never report physical wordlist lines.
    public static TheoryData<string, string> BackendStatusFixtures => new()
    {
        { "7.1.2", """
            {"session":"synthetic","guess":{"guess_base":"private-pattern","guess_base_count":3,"guess_base_offset":2,"guess_base_percent":66.67,"guess_mask_length":0,"guess_mod":"private-rule","guess_mod_count":1,"guess_mod_offset":1,"guess_mod_percent":100.00,"guess_mode":1},"status":3,"target":"private-target","progress":[2500,10000],"restore_point":100,"recovered_hashes":[0,1],"recovered_salts":[0,1],"rejected":100,"devices":[{"device_id":1,"device_name":"Synthetic GPU","speed":100,"temp":50,"util":90}],"time_start":1700000000,"estimated_stop":1700000120}
            """ },
        { "8.0.0", """
            {"session":"synthetic","guess":{"guess_base":"private-pattern","guess_base_count":3,"guess_base_offset":2,"guess_base_percent":66.67,"guess_mask_length":0,"guess_mod":"private-rule","guess_mod_count":1,"guess_mod_offset":1,"guess_mod_percent":100.00,"guess_mode":1},"status":3,"target":"private-target","progress":[2500,10000],"restore_point":100,"recovered_hashes":[0,1],"recovered_salts":[0,1],"rejected":100,"brain_rejected_position":0,"brain_rejected_candidate":0,"devices":[{"device_id":1,"device_name":"Synthetic GPU","speed":100,"temp":50,"util":90}],"time_start":1700000000,"estimated_stop":1700000120}
            """ }
    };

    [Theory]
    [MemberData(nameof(BackendStatusFixtures))]
    public void BackendFixturesPreserveSubattackIndicesWithoutRetainingInputContent(string version, string json)
    {
        Assert.False(string.IsNullOrWhiteSpace(version));
        var before = DateTimeOffset.UtcNow;
        Assert.True(HashcatStatusParser.TryParse(json, out var status));
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(status);
        Assert.Equal(3, status.SourceCount);
        Assert.Equal(2, status.SourceIndex);
        Assert.Equal(1, status.ModifierCount);
        Assert.Equal(1, status.ModifierIndex);
        Assert.Equal(2500, status.Progress);
        Assert.Equal(10000, status.ProgressTotal);
        Assert.Equal(25, status.ProgressPercent);
        Assert.Equal(100, status.RestorePoint);
        Assert.InRange(status.ObservedAt!.Value, before, after);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), status.StartedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000120), status.EstimatedCompletion);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(status));
    }

    [Fact]
    public void QueueMetadataIsOptionalAndDoesNotInventAWordCount()
    {
        Assert.True(HashcatStatusParser.TryParse("""{"status":3,"progress":[1,4]}""", out var status));
        Assert.Null(status!.SourceCount);
        Assert.Null(status.SourceIndex);
        Assert.Null(status.ModifierCount);
        Assert.Null(status.ModifierIndex);
    }

    [Fact]
    public void HybridMetadataPreservesSeparateSourceAndModifierPositions()
    {
        Assert.True(HashcatStatusParser.TryParse("""{"status":3,"guess":{"guess_base_count":2,"guess_base_offset":1,"guess_mod_count":6,"guess_mod_offset":4}}""", out var status));
        Assert.Equal(2, status!.SourceCount);
        Assert.Equal(1, status.SourceIndex);
        Assert.Equal(6, status.ModifierCount);
        Assert.Equal(4, status.ModifierIndex);
    }

    [Fact]
    public void LargeCountersRemainExactAboveDoublePrecision()
    {
        const long value = 9007199254740993;
        Assert.True(HashcatStatusParser.TryParse("""{"status":3,"progress":[9007199254740993,9007199254741993],"restore_point":9007199254740993,"rejected":9007199254740993,"recovered_hashes":[9007199254740993,9223372036854775807],"guess":{"guess_base_count":"9007199254740993","guess_base_offset":1}}""", out var status));
        Assert.Equal(value, status!.Progress);
        Assert.Equal(value + 1000, status.ProgressTotal);
        Assert.Equal(value, status.RestorePoint);
        Assert.Equal(value, status.RejectedCandidates);
        Assert.Equal(value, status.RecoveredHashes);
        Assert.Equal(long.MaxValue, status.TotalHashes);
        Assert.Equal(value, status.SourceCount);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    [InlineData("18446744073709551615")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("null")]
    [InlineData("{}")]
    public void InvalidCountersDoNotWrapRoundOrProducePlausibleProgress(string value)
    {
        Assert.True(HashcatStatusParser.TryParse($$$"""{"status":3,"progress":[1,{{{value}}}],"restore_point":{{{value}}},"rejected":{{{value}}},"guess":{"guess_base_count":{{{value}}},"guess_base_offset":{{{value}}}}}""", out var status));
        Assert.Equal(0, status!.Progress);
        Assert.Equal(0, status.ProgressTotal);
        Assert.Equal(0, status.ProgressPercent);
        Assert.Equal(0, status.RestorePoint);
        Assert.Equal(0, status.RejectedCandidates);
        Assert.Null(status.SourceCount);
        Assert.Null(status.SourceIndex);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("253402300800")]
    [InlineData("18446744073709551615")]
    [InlineData("\"NaN\"")]
    public void UnavailableOrOutOfRangeTimestampsAreNotShownAsEstimates(string value)
    {
        Assert.True(HashcatStatusParser.TryParse($$"""{"status":3,"time_start":{{value}},"estimated_stop":{{value}}}""", out var status));
        Assert.Null(status!.StartedAt);
        Assert.Null(status.EstimatedCompletion);
        Assert.NotNull(status.ObservedAt);
    }

    [Fact]
    public void MalformedOptionalMetadataDoesNotDiscardOtherwiseUsefulStatus()
    {
        Assert.True(HashcatStatusParser.TryParse("""{"status":3,"guess":[],"progress":[25,100],"devices":[{"speed":"Infinity","temp":9223372036854775807,"util":"NaN"},{"speed":-10}]}""", out var status));
        Assert.Equal(25, status!.ProgressPercent);
        Assert.Null(status.SourceIndex);
        Assert.Equal(0, status.SpeedHashesPerSecond);
        Assert.Null(status.Devices[0].Temperature);
        Assert.Null(status.Devices[0].Utilization);
        Assert.DoesNotContain("Infinity", JsonSerializer.Serialize(status));
    }

    [Fact]
    public void SummedDeviceSpeedCannotProduceAnUnpersistableInfinity()
    {
        Assert.True(HashcatStatusParser.TryParse("""{"status":3,"devices":[{"speed":1e308},{"speed":1e308}]}""", out var status));
        Assert.Equal(0, status!.SpeedHashesPerSecond);
        Assert.NotEmpty(JsonSerializer.Serialize(status));
    }
}
