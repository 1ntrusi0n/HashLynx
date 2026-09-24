using HashLynx.Core;
namespace HashLynx.Core.Tests;

public sealed class CompletionFeedbackTests
{
    [Fact] public void PartialExhaustionOffersResultsAndAnotherAttempt()
    {
        var feedback = CompletionFeedback.For(JobState.Exhausted, true);
        Assert.True(feedback.OfferResults); Assert.Contains("remaining", feedback.Detail);
    }
    [Fact] public void ExhaustionDoesNotImplyThatPasswordCannotBeRecovered()
    {
        var feedback = CompletionFeedback.For(JobState.Exhausted, false);
        Assert.False(feedback.OfferResults); Assert.Contains("this attempt", feedback.Detail);
    }
    [Theory] [InlineData(JobState.Failed)] [InlineData(JobState.Cancelled)] [InlineData(JobState.Interrupted)]
    public void FailureAndStopStillOfferPartialResults(JobState state)
        => Assert.True(CompletionFeedback.For(state, true).OfferResults);
}
