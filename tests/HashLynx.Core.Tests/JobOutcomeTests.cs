using HashLynx.Core;

namespace HashLynx.Core.Tests;

public sealed class JobOutcomeTests
{
    [Fact]
    public void ExhaustedDistinguishesNoMatchFromSavedPartialRecovery()
    {
        var noMatch = JobOutcome.For(JobState.Exhausted, false);
        Assert.Contains("no new match", noMatch.StateLabel);
        Assert.Contains("this attempt", noMatch.Detail);
        Assert.Equal(JobNextAction.ChooseAttempt, noMatch.Action);
        Assert.False(noMatch.OfferResults);
        var partial = JobOutcome.For(JobState.Exhausted, true);
        Assert.True(partial.OfferResults);
        Assert.Contains("remaining", partial.Detail);
    }

    [Fact]
    public void CachedMatchesDoNotClaimSessionResultOwnership()
    {
        var cached = JobOutcome.For(JobState.Cracked, false);
        Assert.False(cached.OfferResults);
        Assert.Contains("password cache", cached.Detail);
        Assert.Contains("earlier sessions", cached.Detail);
        Assert.Equal(JobNextAction.CheckEarlierResults, cached.Action);
        Assert.True(JobOutcome.For(JobState.Cracked, true).OfferResults);
    }

    [Fact]
    public void UnreadableOutputDoesNotClaimNoMatch()
    {
        var unknown = JobOutcome.For(JobState.Exhausted, null);
        Assert.DoesNotContain("no new match", unknown.StateLabel);
        Assert.Contains("could not be checked", unknown.Detail);
        Assert.False(unknown.OfferResults);
    }

    [Theory]
    [InlineData(JobState.Cancelled)]
    [InlineData(JobState.Checkpointed)]
    [InlineData(JobState.Interrupted)]
    public void ContinueIsOnlyOfferedWhenCheckpointExists(JobState state)
    {
        Assert.Equal(JobNextAction.Restore, JobOutcome.For(state, false, true).Action);
        var noCheckpoint = JobOutcome.For(state, true, false);
        Assert.Equal(JobNextAction.ChooseAttempt, noCheckpoint.Action);
        Assert.True(noCheckpoint.OfferResults);
        Assert.Contains("no usable checkpoint", noCheckpoint.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailuresKeepPartialResultsAndGiveTheSuggestedRemedy()
    {
        var outcome = JobOutcome.For(JobState.Failed, true, failure: new("Device could not run.", JobNextAction.CheckHardware));
        Assert.True(outcome.OfferResults);
        Assert.Equal("Check hardware", outcome.ActionLabel);
        Assert.Contains("queued steps remain paused", outcome.Detail);
    }
}
