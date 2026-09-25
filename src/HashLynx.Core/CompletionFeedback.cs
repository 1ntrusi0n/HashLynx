namespace HashLynx.Core;

public sealed record CompletionFeedback(string Title, string Detail, bool OfferResults)
{
    public static CompletionFeedback For(JobState state, bool hasSessionResults) => From(JobOutcome.For(state, hasSessionResults));
    public static CompletionFeedback From(JobOutcome outcome) => new(outcome.Title, outcome.Detail, outcome.OfferResults);
}
