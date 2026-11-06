using HashLynx.Core;
using System.IO;

namespace HashLynx.UI.ViewModels;

public sealed partial class JobViewModel
{
    private JobProgressEstimator? _progressEstimator;
    private bool _isCountingWordlists;
    public bool IsCountingWordlists { get => _isCountingWordlists; internal set { if (Set(ref _isCountingWordlists, value)) Refresh(); } }
    private JobWorkSummary WorkSummary => JobWorkSummary.For(Record);
    public bool HasWordlists => WorkSummary.UsesWordlists;
    public string WordlistLines => WorkSummary.TotalLines is { } total ? $"{total:N0} lines"
        : IsCountingWordlists ? "Counting lines…" : "Line count unavailable";
    public string WordlistDetail
    {
        get
        {
            if (!HasWordlists) return "";
            var lists = Record.Configuration.Attack.Wordlists;
            var summary = WorkSummary;
            var source = lists.Count == 1 ? Path.GetFileName(lists[0])
                : summary.CurrentWordlistIndex is { } index
                    ? $"Wordlist {index + 1:N0} of {lists.Count:N0}: {Path.GetFileName(lists[index])} ({summary.CurrentLines?.ToString("N0") ?? "unknown"} lines)"
                    : $"{lists.Count:N0} source wordlists";
            return source + ". Physical lines include blank and duplicate entries; rules can create additional guesses.";
        }
    }
    public string WordlistCountNotice => string.Join(" ", (Record.WordlistStatistics ?? [])
        .Select(item => item.CountStatus).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
        + (HasWordlists && Record.InputSnapshots is null && Record.WordlistStatistics is not null
            ? " Counts describe the current source files; this older session has no saved file metadata." : "");
    public string ProgressText => WorkSummary.OverallPercent is { } overall && !WorkSummary.WholeAttempt
        ? $"Overall wordlists: approximately {overall:F1}% · current pass: {WorkSummary.PassPercent:F1}%"
        : WorkSummary.PassPercent is { } percent ? $"{percent:F1}% of {(WorkSummary.WholeAttempt ? "attempt" : "current pass")} checked"
        : "Waiting for the backend to report total work";

    private JobProgressEstimate CurrentEstimate => _progressEstimator?.GetEstimate(DateTimeOffset.UtcNow, Record.State)
        ?? new JobProgressEstimate
        {
            State = Record.State is JobState.Running ? JobProgressEstimateState.Learning
                : Record.State is JobState.Paused ? JobProgressEstimateState.Paused : JobProgressEstimateState.Unavailable,
            Note = Record.State is JobState.Running ? "Learning from live progress updates."
                : "Time estimates become available while the attempt is running."
        };
    public string Remaining => CurrentEstimate.Remaining is { } time ? "About " + FormatDuration(time)
        : CurrentEstimate.State switch
        {
            JobProgressEstimateState.Learning => "Learning checking rate…",
            JobProgressEstimateState.Paused => "Paused",
            JobProgressEstimateState.Stalled => "Waiting for fresh progress",
            JobProgressEstimateState.Finished => "Attempt stopped",
            _ => "Not available"
        };
    public string EstimatedDuration => CurrentEstimate.EstimatedTotalDuration is { } time ? "About " + FormatDuration(time) : "Not available";
    public string Eta
    {
        get
        {
            var estimate = CurrentEstimate;
            if (estimate.Remaining is not { } remaining) return "Not available";
            return remaining >= TimeSpan.Zero && remaining <= DateTimeOffset.MaxValue - estimate.ObservedAt
                ? (estimate.ObservedAt + remaining).LocalDateTime.ToString("g") : "Not available";
        }
    }
    public string EstimateNote => CurrentEstimate.Note;
    public string EstimateScope => WorkSummary.WholeAttempt ? "ESTIMATED TIME REMAINING" : "CURRENT PASS · TIME REMAINING";

    public void BeginProgressTracking()
    {
        _progressEstimator = new(staleAfter: TimeSpan.FromSeconds(Math.Max(30, 3 * Math.Clamp(Record.Configuration.Options.StatusIntervalSeconds, 1, 60))));
        Record.ProgressEstimate = null;
        Record.LatestStatus = null;
        _progressEstimator.SetState(JobState.Running, DateTimeOffset.UtcNow);
        Refresh();
    }

    public void ObserveStatus(JobStatusSnapshot snapshot)
    {
        _progressEstimator ??= new(staleAfter: TimeSpan.FromSeconds(Math.Max(30, 3 * Math.Clamp(Record.Configuration.Options.StatusIntervalSeconds, 1, 60))));
        if (Record.State is JobState.Running or JobState.Paused)
        {
            if (snapshot.State.Equals("Paused", StringComparison.OrdinalIgnoreCase)) Record.State = JobState.Paused;
            else if (snapshot.State.Equals("Running", StringComparison.OrdinalIgnoreCase)) Record.State = JobState.Running;
        }
        Record.LatestStatus = snapshot;
        Record.ProgressEstimate = _progressEstimator.Observe(snapshot, Record.State, snapshot.ObservedAt ?? DateTimeOffset.UtcNow);
        Refresh();
    }

    internal void FinishProgressTracking()
    {
        if (_progressEstimator is null) return;
        var now = Record.FinishedAt ?? DateTimeOffset.UtcNow;
        _progressEstimator.SetState(Record.State, now);
        Record.ProgressEstimate = _progressEstimator.GetEstimate(now, Record.State);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) return "0s";
        if (duration.TotalDays >= 1) return $"{Math.Floor(duration.TotalDays):N0}d {duration.Hours}h {duration.Minutes}m";
        if (duration.TotalHours >= 1) return $"{duration.Hours}h {duration.Minutes}m {duration.Seconds}s";
        if (duration.TotalMinutes >= 1) return $"{duration.Minutes}m {duration.Seconds}s";
        return $"{Math.Ceiling(duration.TotalSeconds):N0}s";
    }
}
