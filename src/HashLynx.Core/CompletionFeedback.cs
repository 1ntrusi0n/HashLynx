namespace HashLynx.Core;

public sealed record CompletionFeedback(string Title, string Detail, bool OfferResults)
{
    public static CompletionFeedback For(JobState state, bool hasSessionResults) => state switch
    {
        JobState.Cracked => new("Recovery complete", "Hashcat reports all targets recovered. View the session results for the passwords found during this attempt.", true),
        JobState.Exhausted when hasSessionResults => new("Some passwords recovered", "All candidates in this attempt were tried. View the recovered passwords, then try another list or pattern for those remaining.", true),
        JobState.Exhausted => new("Attempt finished", "All candidates in this attempt were tried. You can try another wordlist, rule preset, or remembered pattern.", false),
        JobState.Failed => new("Recovery needs attention", "This attempt failed. Open its session for the diagnostic. Any queued steps remain paused.", hasSessionResults),
        _ => new("Recovery stopped", "This attempt stopped before finishing. Open its session to check whether a restore checkpoint is available.", hasSessionResults)
    };
}
