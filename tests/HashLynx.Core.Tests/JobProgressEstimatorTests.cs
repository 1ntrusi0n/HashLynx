using System.Text.Json;
using HashLynx.Core;

namespace HashLynx.Core.Tests;

public sealed class JobProgressEstimatorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LearnsFromActualProgressRatherThanDeviceHashSpeed()
    {
        var estimator = new JobProgressEstimator();
        Assert.Null(Observe(estimator, 0, 0).Remaining);
        Assert.Null(Observe(estimator, 2, 100).Remaining);
        var result = Observe(estimator, 4, 200);
        Assert.Equal(JobProgressEstimateState.Tracking, result.State);
        Assert.Equal(JobProgressEstimateBasis.ObservedProgress, result.Basis);
        Assert.Equal(20, result.ProgressPercent);
        Assert.Equal(TimeSpan.FromSeconds(4), result.ActiveElapsed);
        Assert.Equal(TimeSpan.FromSeconds(16), result.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(20), result.EstimatedTotalDuration);
        Assert.Contains("Other wordlists", result.Note);
    }

    [Fact]
    public void RestoredProgressDoesNotPretendSkippedWorkWasMeasured()
    {
        var estimator = new JobProgressEstimator();
        Observe(estimator, 0, 800);
        Observe(estimator, 2, 810);
        var result = Observe(estimator, 4, 820);
        Assert.Equal(82, result.ProgressPercent);
        Assert.Equal(TimeSpan.FromSeconds(36), result.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(40), result.EstimatedTotalDuration);
        Assert.Contains("earlier checkpoint work", result.Note);
    }

    [Fact]
    public void RecentSpeedChangesInfluenceEstimateWithoutDiscardingEarlierHistory()
    {
        var estimator = new JobProgressEstimator();
        for (var second = 0; second <= 40; second += 2) Observe(estimator, second, second * 10);
        Observe(estimator, 42, 500);
        var result = Observe(estimator, 44, 600);
        // The old rate predicts 40 seconds, the latest burst alone 8 seconds.
        Assert.InRange(result.Remaining!.Value.TotalSeconds, 9, 39);
        // Weighting the recent window reacts faster than averaging the entire pass alone.
        Assert.True(result.Remaining.Value.TotalSeconds < 400.0 / (600.0 / 44));
    }

    [Fact]
    public void ExplicitPauseExcludesIdleTimeAndRequiresFreshRateAfterResume()
    {
        var estimator = WarmEstimator();
        estimator.SetState(JobState.Paused, At(6));
        var paused = estimator.GetEstimate(At(100), JobState.Paused);
        Assert.Equal(JobProgressEstimateState.Paused, paused.State);
        Assert.Null(paused.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(6), paused.ActiveElapsed);
        estimator.SetState(JobState.Running, At(100));
        Assert.Null(Observe(estimator, 101, 200).Remaining);
        Assert.Null(Observe(estimator, 103, 300).Remaining);
        var resumed = Observe(estimator, 105, 400);
        Assert.Equal(TimeSpan.FromSeconds(10), resumed.ActiveElapsed);
        Assert.Equal(TimeSpan.FromSeconds(12), resumed.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(22), resumed.EstimatedTotalDuration);
    }

    [Fact]
    public void BackendPauseIsHonoredBeforeTheJobRecordTransitions()
    {
        var estimator = WarmEstimator();
        var paused = Observe(estimator, 6, 200, backendState: "Paused");
        Assert.Equal(JobProgressEstimateState.Paused, paused.State);
        Assert.Null(paused.Remaining);
        Observe(estimator, 100, 200);
        Observe(estimator, 102, 300);
        var resumed = Observe(estimator, 104, 400);
        Assert.Equal(TimeSpan.FromSeconds(8), resumed.ActiveElapsed);
        Assert.Equal(TimeSpan.FromSeconds(12), resumed.Remaining);
    }

    [Fact]
    public void InitializationAndAutotuningDoNotCountAsCheckingTime()
    {
        var estimator = new JobProgressEstimator();
        Observe(estimator, 0, 0, backendState: "Initializing");
        var tuning = Observe(estimator, 20, 0, backendState: "Autotuning");
        Assert.Equal(TimeSpan.Zero, tuning.ActiveElapsed);
        Assert.Null(tuning.Remaining);
        Observe(estimator, 25, 0);
        Observe(estimator, 27, 100);
        var result = Observe(estimator, 29, 200);
        Assert.Equal(TimeSpan.FromSeconds(4), result.ActiveElapsed);
        Assert.Equal(TimeSpan.FromSeconds(16), result.Remaining);
    }

    [Fact]
    public void ReadingEstimateDoesNotCountDownOrMutateHistory()
    {
        var estimator = WarmEstimator();
        var first = estimator.GetEstimate(At(4), JobState.Running);
        var later = estimator.GetEstimate(At(10), JobState.Running);
        Assert.Equal(first.Remaining, later.Remaining);
        Assert.Equal(first.ActiveElapsed, later.ActiveElapsed);
        Assert.Equal(first.ObservedAt, later.ObservedAt);
        estimator.GetEstimate(At(11), JobState.Paused);
        Assert.Equal(first.Remaining, estimator.GetEstimate(At(12), JobState.Running).Remaining);
    }

    [Fact]
    public void StaleStatusHidesEtaAndAnUnobservedGapDoesNotInflateElapsed()
    {
        var estimator = WarmEstimator();
        var stale = estimator.GetEstimate(At(35), JobState.Running);
        Assert.Equal(JobProgressEstimateState.Stalled, stale.State);
        Assert.Null(stale.Remaining);
        Assert.Null(stale.EstimatedTotalDuration);
        var fresh = Observe(estimator, 100, 400);
        Assert.Equal(JobProgressEstimateState.Learning, fresh.State);
        Assert.Equal(TimeSpan.FromSeconds(4), fresh.ActiveElapsed);
        Observe(estimator, 102, 500);
        var learned = Observe(estimator, 104, 600);
        Assert.Equal(TimeSpan.FromSeconds(8), learned.ActiveElapsed);
        Assert.Equal(TimeSpan.FromSeconds(8), learned.Remaining);
    }

    [Fact]
    public void FreshButUnchangedCountersEventuallySuppressEtaAndRelearn()
    {
        var estimator = WarmEstimator();
        JobProgressEstimate? result = null;
        for (var second = 6; second <= 34; second += 2) result = Observe(estimator, second, 200);
        Assert.Equal(JobProgressEstimateState.Stalled, result!.State);
        Assert.Null(result.Remaining);
        Assert.Null(Observe(estimator, 36, 300).Remaining);
        Assert.Null(Observe(estimator, 38, 400).Remaining);
        Assert.NotNull(Observe(estimator, 40, 500).Remaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnknownOrInvalidTotalsDoNotInventPercentOrEta(long total)
    {
        var estimator = new JobProgressEstimator();
        Observe(estimator, 0, 0, total);
        Observe(estimator, 2, 100, total);
        var result = Observe(estimator, 4, 200, total);
        Assert.Null(result.ProgressPercent);
        Assert.Null(result.Remaining);
        Assert.Null(result.EstimatedTotalDuration);
    }

    [Fact]
    public void NegativeProgressIsUnavailable()
    {
        var result = Observe(new JobProgressEstimator(), 0, -1);
        Assert.Null(result.ProgressPercent);
        Assert.Null(result.Remaining);
        Assert.Equal(JobProgressEstimateState.Unavailable, result.State);
    }

    [Theory]
    [InlineData(JobState.Cracked)]
    [InlineData(JobState.Exhausted)]
    [InlineData(JobState.Cancelled)]
    [InlineData(JobState.Checkpointed)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Interrupted)]
    public void CompletedAndLoadedInactiveJobsNeverShowAFutureEta(JobState state)
    {
        var estimator = WarmEstimator();
        estimator.SetState(state, At(5));
        var result = estimator.GetEstimate(At(100), state);
        Assert.Equal(JobProgressEstimateState.Finished, result.State);
        Assert.Null(result.Remaining);
        Assert.Null(result.EstimatedTotalDuration);
    }

    [Theory]
    [InlineData("Cracked")]
    [InlineData("Exhausted")]
    [InlineData("Aborted")]
    [InlineData("Checkpoint")]
    [InlineData("Runtime limit")]
    [InlineData("Finished")]
    public void BackendCompletionImmediatelyStopsEstimation(string backendState)
    {
        var result = Observe(WarmEstimator(), 6, 300, backendState: backendState);
        Assert.Equal(JobProgressEstimateState.Finished, result.State);
        Assert.Null(result.Remaining);
    }

    [Fact]
    public void CompletedCounterIsClampedAndHasNoFutureEta()
    {
        var result = Observe(WarmEstimator(), 6, 1200);
        Assert.Equal(100, result.ProgressPercent);
        Assert.Equal(JobProgressEstimateState.Finished, result.State);
        Assert.Null(result.Remaining);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("modifier")]
    [InlineData("started")]
    [InlineData("total")]
    [InlineData("regression")]
    public void ANewPassResetsTimingAndDoesNotReusePriorWorkRate(string change)
    {
        var estimator = WarmEstimator();
        var snapshot = Snapshot(300);
        switch (change)
        {
            case "source": snapshot.SourceIndex = 2; break;
            case "modifier": snapshot.ModifierIndex = 2; break;
            case "started": snapshot.StartedAt = At(6); break;
            case "total": snapshot.ProgressTotal = 2000; break;
            case "regression": snapshot.Progress = 100; break;
        }
        var result = estimator.Observe(snapshot, JobState.Running, At(6));
        Assert.Equal(TimeSpan.Zero, result.ActiveElapsed);
        Assert.Equal(JobProgressEstimateState.Learning, result.State);
        Assert.Null(result.Remaining);
    }

    [Fact]
    public void DelayedAndDuplicateSamplesDoNotRewindCountersOrElapsed()
    {
        var estimator = WarmEstimator();
        Observe(estimator, 2, 10);
        Observe(estimator, 4, 20);
        var result = estimator.GetEstimate(At(5), JobState.Running);
        Assert.Equal(20, result.ProgressPercent);
        Assert.Equal(TimeSpan.FromSeconds(4), result.ActiveElapsed);
        Assert.Equal(TimeSpan.FromSeconds(16), result.Remaining);
    }

    [Fact]
    public void LargeCountersKeepSmallDeltasPrecise()
    {
        var estimator = new JobProgressEstimator();
        Observe(estimator, 0, long.MaxValue - 100, long.MaxValue);
        Observe(estimator, 2, long.MaxValue - 90, long.MaxValue);
        var result = Observe(estimator, 4, long.MaxValue - 80, long.MaxValue);
        Assert.Equal(TimeSpan.FromSeconds(16), result.Remaining);
        Assert.InRange(result.ProgressPercent!.Value, 0, 100);
    }

    [Fact]
    public void DurationsTooLargeForTimeSpanAreUnavailable()
    {
        var estimator = new JobProgressEstimator();
        Observe(estimator, 0, 0, long.MaxValue);
        Observe(estimator, 2, 1, long.MaxValue);
        var result = Observe(estimator, 4, 2, long.MaxValue);
        Assert.Equal(JobProgressEstimateState.Unavailable, result.State);
        Assert.Null(result.Remaining);
        Assert.Null(result.EstimatedTotalDuration);
    }

    [Fact]
    public void ValidBackendEtaBridgesWarmupThenObservedRateTakesOver()
    {
        var estimator = new JobProgressEstimator();
        var snapshot = Snapshot(0);
        snapshot.EstimatedCompletion = At(100);
        var initial = estimator.Observe(snapshot, JobState.Running, At(0));
        Assert.Equal(JobProgressEstimateBasis.Backend, initial.Basis);
        Assert.Equal(TimeSpan.FromSeconds(100), initial.Remaining);
        Assert.Equal(initial.Remaining, estimator.GetEstimate(At(10), JobState.Running).Remaining);
        Observe(estimator, 2, 100);
        var learned = Observe(estimator, 4, 200);
        Assert.Equal(JobProgressEstimateBasis.ObservedProgress, learned.Basis);
        Assert.Equal(TimeSpan.FromSeconds(16), learned.Remaining);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void PastOrPresentBackendEtaIsNotUsable(int seconds)
    {
        var snapshot = Snapshot(0);
        snapshot.EstimatedCompletion = At(seconds);
        var result = new JobProgressEstimator().Observe(snapshot, JobState.Running, At(0));
        Assert.Null(result.Remaining);
        Assert.Equal(JobProgressEstimateBasis.None, result.Basis);
    }

    [Fact]
    public void BackendFallbackIsNotReusedWhenMissingPausedOrStale()
    {
        var estimator = new JobProgressEstimator();
        var snapshot = Snapshot(0);
        snapshot.EstimatedCompletion = At(100);
        estimator.Observe(snapshot, JobState.Running, At(0));
        Assert.Null(estimator.GetEstimate(At(30), JobState.Running).Remaining);
        Assert.Null(estimator.GetEstimate(At(1), JobState.Paused).Remaining);
        var missing = Observe(estimator, 2, 100);
        Assert.Null(missing.Remaining);
        Assert.Equal(JobProgressEstimateBasis.None, missing.Basis);
    }

    [Fact]
    public void ALongConfiguredStatusIntervalCanUseALongerFreshnessWindow()
    {
        var estimator = new JobProgressEstimator(staleAfter: TimeSpan.FromSeconds(120));
        Observe(estimator, 0, 0);
        Observe(estimator, 40, 100);
        var result = Observe(estimator, 80, 200);
        Assert.Equal(TimeSpan.FromSeconds(320), result.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(80), result.ActiveElapsed);
    }

    [Fact]
    public void PersistedSummaryContainsOnlyBoundedNumbersAndExplanations()
    {
        var original = WarmEstimator().GetEstimate(At(4), JobState.Running);
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<JobProgressEstimate>(json)!;
        Assert.Equal(original.ObservedAt, restored.ObservedAt);
        Assert.Equal(original.Remaining, restored.Remaining);
        Assert.Equal(original.ActiveElapsed, restored.ActiveElapsed);
        Assert.Equal(original.Basis, restored.Basis);
        Assert.DoesNotContain("Candidates", json);
        Assert.DoesNotContain("Target", json);
    }

    [Fact]
    public void EmptyTrackerAndInvalidConfigurationAreHandled()
    {
        var result = new JobProgressEstimator().GetEstimate(At(0), JobState.Ready);
        Assert.Null(result.ProgressPercent);
        Assert.Null(result.Remaining);
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobProgressEstimator(staleAfter: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobProgressEstimator(minimumObservation: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobProgressEstimator(rollingWindow: TimeSpan.Zero));
    }

    private static JobProgressEstimator WarmEstimator()
    {
        var estimator = new JobProgressEstimator();
        Observe(estimator, 0, 0);
        Observe(estimator, 2, 100);
        Observe(estimator, 4, 200);
        return estimator;
    }

    private static JobProgressEstimate Observe(JobProgressEstimator estimator, int seconds, long progress,
        long total = 1000, string backendState = "Running") =>
        estimator.Observe(Snapshot(progress, total, backendState), JobState.Running, At(seconds));

    private static JobStatusSnapshot Snapshot(long progress, long total = 1000, string backendState = "Running") => new()
    {
        State = backendState,
        Progress = progress,
        ProgressTotal = total,
        SpeedHashesPerSecond = double.MaxValue,
        SourceIndex = 1,
        SourceCount = 1,
        ModifierIndex = 1,
        ModifierCount = 1
    };

    private static DateTimeOffset At(int seconds) => Epoch.AddSeconds(seconds);
}
