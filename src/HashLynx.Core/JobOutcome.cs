namespace HashLynx.Core;

public enum JobNextAction { None, ChooseAttempt, CheckHardware, CheckSettings, ReviewInputs, Restore, CheckEarlierResults }

public sealed record JobFailureGuidance(string Detail, JobNextAction Action);

/// <summary>User-facing interpretation of a job; saved backend states and session result ownership are unchanged.</summary>
public sealed record JobOutcome(string StateLabel, string Title, string Detail, bool OfferResults, JobNextAction Action)
{
    public string ActionLabel => Action switch
    {
        JobNextAction.ChooseAttempt => "Choose another attempt",
        JobNextAction.CheckHardware => "Check hardware",
        JobNextAction.CheckSettings => "Check backend settings",
        JobNextAction.ReviewInputs => "Review target and attack",
        JobNextAction.Restore => "Continue saved session",
        JobNextAction.CheckEarlierResults => "Check earlier results",
        _ => ""
    };

    public static JobOutcome For(JobState state, bool? hasSessionResults, bool canRestore = false, JobFailureGuidance? failure = null)
    {
        var found = hasSessionResults == true;
        var results = found ? " Passwords saved by this session are available in its results." : "";
        return state switch
        {
            JobState.Ready => new("Ready", "Ready to start", "This recovery attempt has not started.", false, JobNextAction.None),
            JobState.Running => new("Running", "Checking password guesses", "Recovery is running locally. Results are saved as matching passwords are found.", found, JobNextAction.None),
            JobState.Paused => new("Paused", "Recovery paused", "Resume this session when you are ready to continue." + results, found, JobNextAction.None),
            JobState.Cracked when found => new("Recovered", "Recovery complete", "All targets are known to the backend. View the passwords saved by this session; some targets may have been recovered in earlier attempts.", true, JobNextAction.None),
            JobState.Cracked => new("Targets recovered", "Backend reports recovery complete", "The backend reports all targets recovered, but this session has no confirmed saved passwords. Matches may already be in the password cache, or this session's output may be unavailable. Check earlier sessions in Results; passwords from other sessions are not attributed to this one.", false, JobNextAction.CheckEarlierResults),
            JobState.Exhausted when found => new("Some recovered", "Some passwords recovered", "Finished checking the guesses in this attempt. View the saved passwords, then try another wordlist, rule preset, or pattern for the remaining targets.", true, JobNextAction.ChooseAttempt),
            JobState.Exhausted when hasSessionResults == false => new("Finished — no new match", "No new password saved", "Finished checking the guesses in this attempt; no matching password was saved by this session. A different wordlist, rule preset, or pattern may still work.", false, JobNextAction.ChooseAttempt),
            JobState.Exhausted => new("Finished", "Attempt finished", "Finished checking the guesses in this attempt. The session output could not be checked; open results before deciding whether another wordlist, rule preset, or pattern is needed.", false, JobNextAction.ChooseAttempt),
            JobState.Failed => new("Needs attention", "Recovery needs attention", (failure?.Detail ?? "This attempt could not finish. Review the target and attack settings, and expand Technical details for the recorded error.") + results + " Any queued steps remain paused.", found, failure?.Action ?? JobNextAction.ReviewInputs),
            JobState.Checkpointed when canRestore => new("Progress saved", "Recovery paused with saved progress", "This attempt stopped at a saved checkpoint. Continue the saved session when ready." + results, found, JobNextAction.Restore),
            JobState.Interrupted => new("Interrupted", "Recovery was interrupted", (canRestore ? "HashLynx closed before this attempt finished. A saved checkpoint is available to continue." : "HashLynx closed before this attempt finished. No usable checkpoint was found; start a new attempt to continue searching.") + results, found, canRestore ? JobNextAction.Restore : JobNextAction.ChooseAttempt),
            _ => new("Stopped", "Recovery stopped before finishing", (canRestore ? "This search did not finish. A saved checkpoint is available to continue." : "This search did not finish, and no usable checkpoint was found. Start a new attempt when ready.") + results, found, canRestore ? JobNextAction.Restore : JobNextAction.ChooseAttempt)
        };
    }
}
