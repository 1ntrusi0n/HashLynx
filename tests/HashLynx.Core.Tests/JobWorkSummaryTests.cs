namespace HashLynx.Core.Tests;

public sealed class JobWorkSummaryTests
{
    [Fact]
    public void SingleWordlistShowsPhysicalLinesSeparatelyFromAmplifiedWork()
    {
        var record = DictionaryJob(["first.txt"], [100]);
        record.LatestStatus = new()
        {
            Progress = 2500, ProgressTotal = 10000, ProgressPercent = 99, RestorePoint = 80,
            RejectedCandidates = 500, SourceCount = 1, SourceIndex = 1, ModifierCount = 1, ModifierIndex = 1
        };
        record.Configuration.Attack.RuleFiles = ["variations.rule"];

        var summary = JobWorkSummary.For(record);

        Assert.True(summary.UsesWordlists);
        Assert.Equal(100m, summary.TotalLines);
        Assert.Equal(100, summary.CurrentLines);
        Assert.Equal(0, summary.CurrentWordlistIndex);
        Assert.Equal(25, summary.PassPercent);
        Assert.Equal(25, summary.OverallPercent);
        Assert.True(summary.WholeAttempt);
        Assert.Contains("rule variations", summary.ScopeNote);
    }

    [Fact]
    public void MultipleDictionaryFilesUseLineWeightedOverallAndCurrentPassTimeScope()
    {
        var record = DictionaryJob(["first.txt", "second.txt", "third.txt"], [100, 300, 600]);
        record.LatestStatus = new()
        {
            Progress = 25000, ProgressTotal = 100000, SourceCount = 3, SourceIndex = 2,
            ModifierCount = 1, ModifierIndex = 1
        };

        var summary = JobWorkSummary.For(record);

        Assert.Equal(1000m, summary.TotalLines);
        Assert.Equal(300, summary.CurrentLines);
        Assert.Equal(1, summary.CurrentWordlistIndex);
        Assert.Equal(25, summary.PassPercent);
        Assert.Equal(17.5, summary.OverallPercent);
        Assert.False(summary.WholeAttempt);
        Assert.Contains("current pass only", summary.ScopeNote);
        Assert.Contains("approximate", summary.ScopeNote);
    }

    [Fact]
    public void CombinedBackendSourceDoesNotMapToOnlyTheFirstConfiguredWordlist()
    {
        var record = DictionaryJob(["first.txt", "second.txt"], [100, 900]);
        record.BackendVersion = "8.0.0";
        record.LatestStatus = new()
        {
            Progress = 500, ProgressTotal = 1000, SourceCount = 1, SourceIndex = 1,
            ModifierCount = 1, ModifierIndex = 1
        };

        var summary = JobWorkSummary.For(record);

        Assert.Equal(1000m, summary.TotalLines);
        Assert.Null(summary.CurrentLines);
        Assert.Null(summary.CurrentWordlistIndex);
        Assert.Equal(50, summary.PassPercent);
        Assert.Null(summary.OverallPercent);
        Assert.False(summary.WholeAttempt);
    }

    [Theory]
    [InlineData("mask-file")]
    [InlineData("mask-preset")]
    [InlineData("increment")]
    [InlineData("loopback")]
    [InlineData("extra-arguments")]
    [InlineData("multiple-sources")]
    [InlineData("multiple-modifiers")]
    public void MultipleOrUnboundedPassesDoNotClaimWholeAttemptTime(string scenario)
    {
        var record = DictionaryJob(["first.txt"], [100]);
        record.LatestStatus = new() { Progress = 25, ProgressTotal = 100, SourceCount = 1, SourceIndex = 1, ModifierCount = 1, ModifierIndex = 1 };
        switch (scenario)
        {
            case "mask-file": record.Configuration.Attack.Kind = AttackFamilies.HybridWordlistMask; record.Configuration.Attack.MaskFile = "patterns.hcmask"; break;
            case "mask-preset": record.Configuration.Attack.Kind = AttackFamilies.Mask; record.Configuration.Attack.MaskPresetId = "synthetic-preset"; break;
            case "increment": record.Configuration.Attack.Kind = AttackFamilies.Mask; record.Configuration.Attack.Increment = true; break;
            case "loopback": record.Configuration.Attack.Loopback = true; break;
            case "extra-arguments": record.Configuration.Options.ExtraArguments = ["--skip", "20"]; break;
            case "multiple-sources": record.LatestStatus.SourceCount = 2; break;
            case "multiple-modifiers": record.LatestStatus.ModifierCount = 2; break;
        }

        var summary = JobWorkSummary.For(record);

        Assert.False(summary.WholeAttempt);
        Assert.Contains("current pass only", summary.ScopeNote);
    }

    [Fact]
    public void OneInlineMaskHasNoWordlistLinesButCanDescribeOneWholeAttempt()
    {
        var record = new JobRecord
        {
            Configuration = new() { Attack = new() { Kind = AttackFamilies.Mask, Mask = "?d?d?d?d" } },
            LatestStatus = new() { Progress = 2500, ProgressTotal = 10000, SourceCount = 1, SourceIndex = 1, ModifierCount = 1, ModifierIndex = 1 }
        };

        var summary = JobWorkSummary.For(record);

        Assert.False(summary.UsesWordlists);
        Assert.Null(summary.TotalLines);
        Assert.Null(summary.CurrentLines);
        Assert.Null(summary.CurrentWordlistIndex);
        Assert.Equal(25, summary.PassPercent);
        Assert.True(summary.WholeAttempt);
    }

    [Theory]
    [InlineData(AttackFamilies.Combinator)]
    [InlineData(AttackFamilies.HybridWordlistMask)]
    [InlineData(AttackFamilies.HybridMaskWordlist)]
    [InlineData(AttackFamilies.WordlistPattern)]
    public void OtherAttackFamiliesDoNotTreatSourceIndexAsASequentialDictionaryQueue(string kind)
    {
        var record = DictionaryJob(["first.txt", "second.txt"], [100, 900]);
        record.Configuration.Attack.Kind = kind;
        record.LatestStatus = new() { Progress = 25, ProgressTotal = 100, SourceCount = 2, SourceIndex = 2, ModifierCount = 1, ModifierIndex = 1 };

        var summary = JobWorkSummary.For(record);

        Assert.Equal(1000m, summary.TotalLines);
        Assert.Null(summary.CurrentLines);
        Assert.Null(summary.CurrentWordlistIndex);
        Assert.Null(summary.OverallPercent);
        Assert.False(summary.WholeAttempt);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-count")]
    [InlineData("wrong-order")]
    [InlineData("unknown-count")]
    [InlineData("negative-count")]
    public void UnavailableOrMismatchedStatisticsDoNotInventLineTotals(string scenario)
    {
        var record = DictionaryJob(["first.txt", "second.txt"], [100, 900]);
        record.LatestStatus = new() { Progress = 25, ProgressTotal = 100, SourceCount = 2, SourceIndex = 1 };
        switch (scenario)
        {
            case "missing": record.WordlistStatistics = null; break;
            case "wrong-count": record.WordlistStatistics!.RemoveAt(1); break;
            case "wrong-order": record.WordlistStatistics!.Reverse(); break;
            case "unknown-count": record.WordlistStatistics![0] = new("first.txt", null, null, null, "Unavailable"); break;
            case "negative-count": record.WordlistStatistics![0] = new("first.txt", -1, null, null); break;
        }

        var summary = JobWorkSummary.For(record);

        Assert.Null(summary.TotalLines);
        Assert.Null(summary.CurrentLines);
        Assert.Null(summary.OverallPercent);
        Assert.Equal(25, summary.PassPercent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void InvalidSourceQueueMetadataCannotClaimAWholeAttempt(long count, long index)
    {
        var record = DictionaryJob(["first.txt"], [100]);
        record.LatestStatus = new() { Progress = 25, ProgressTotal = 100, SourceCount = count, SourceIndex = index };

        Assert.False(JobWorkSummary.For(record).WholeAttempt);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-10)]
    [InlineData(101)]
    [InlineData(0)]
    public void UnknownProgressNeverProducesAnOverallPercentage(double storedPercent)
    {
        var record = DictionaryJob(["first.txt"], [100]);
        record.LatestStatus = new() { ProgressPercent = storedPercent };

        var summary = JobWorkSummary.For(record);

        Assert.Null(summary.PassPercent);
        Assert.Null(summary.OverallPercent);
        Assert.Equal(100m, summary.TotalLines);
    }

    [Fact]
    public void MissingStatusAndLegacyStatisticsRemainSafe()
    {
        var record = DictionaryJob(["first.txt"], [100]);
        record.LatestStatus = null;
        record.WordlistStatistics = null;

        var summary = JobWorkSummary.For(record);

        Assert.True(summary.UsesWordlists);
        Assert.Null(summary.TotalLines);
        Assert.Null(summary.CurrentLines);
        Assert.Null(summary.PassPercent);
        Assert.Null(summary.OverallPercent);
    }

    [Fact]
    public void LargePhysicalTotalsDoNotOverflowSignedLong()
    {
        var record = DictionaryJob(["first.txt", "second.txt"], [long.MaxValue, long.MaxValue]);
        record.LatestStatus = new() { Progress = 1, ProgressTotal = 2, SourceCount = 2, SourceIndex = 2 };

        var summary = JobWorkSummary.For(record);

        Assert.Equal(2m * long.MaxValue, summary.TotalLines);
        Assert.Equal(75, summary.OverallPercent);
    }

    private static JobRecord DictionaryJob(string[] paths, long[] counts) => new()
    {
        Configuration = new() { Attack = new() { Kind = AttackFamilies.Dictionary, Wordlists = [.. paths] } },
        WordlistStatistics = paths.Select((path, index) => new JobWordlistInfo(path, counts[index], null, null)).ToList()
    };
}
