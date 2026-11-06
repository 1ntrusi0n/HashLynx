namespace HashLynx.Core;

public enum JobProgressEstimateState { Unavailable, Learning, Tracking, Paused, Stalled, Finished }
public enum JobProgressEstimateBasis { None, Backend, ObservedProgress }

/// <summary>A sanitized, serializable summary. Durations describe the observed current pass only.</summary>
public sealed class JobProgressEstimate
{
    public DateTimeOffset ObservedAt { get; set; }
    public double? ProgressPercent { get; set; }
    public TimeSpan ActiveElapsed { get; set; }
    public TimeSpan? Remaining { get; set; }
    public TimeSpan? EstimatedTotalDuration { get; set; }
    public JobProgressEstimateState State { get; set; }
    public JobProgressEstimateBasis Basis { get; set; }
    public string Scope { get; set; } = "Current pass";
    public string Note { get; set; } = "";
}

/// <summary>
/// Estimates work from changes in backend progress counters, never from hash speed or wordlist size.
/// Keep one instance per running invocation; saved summaries must not be used as live timers.
/// </summary>
public sealed class JobProgressEstimator
{
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _minimumObservation;
    private readonly TimeSpan _rollingWindow;
    private readonly Queue<Sample> _samples = new();
    private DateTimeOffset? _observedAt;
    private DateTimeOffset _timingAt;
    private DateTimeOffset _lastAdvanceAt;
    private JobState _state = JobState.Ready;
    private bool _measuring;
    private bool _backendPaused;
    private bool _backendFinished;
    private bool _resumeNeedsBaseline;
    private bool _countersValid;
    private TimeSpan? _backendRemaining;
    private string? _segmentKey;
    private long _progress;
    private long _total;
    private long _baselineProgress;
    private double _activeSeconds;
    private double _baselineSeconds;
    private int _positiveIntervals;

    public JobProgressEstimator(TimeSpan? staleAfter = null, TimeSpan? minimumObservation = null,
        TimeSpan? rollingWindow = null)
    {
        _staleAfter = Positive(staleAfter ?? TimeSpan.FromSeconds(30), nameof(staleAfter));
        _minimumObservation = Positive(minimumObservation ?? TimeSpan.FromSeconds(4), nameof(minimumObservation));
        _rollingWindow = Positive(rollingWindow ?? TimeSpan.FromSeconds(30), nameof(rollingWindow));
    }

    /// <summary>
    /// Notify explicit pause, resume, and completion transitions even when no backend status arrives.
    /// A resume requires fresh running samples; the paused interval is never included in the rate.
    /// </summary>
    public void SetState(JobState state, DateTimeOffset now)
    {
        if (_state == state) return;
        if (_observedAt.HasValue && now < _timingAt) return;
        if (_measuring) AddActiveInterval(now);
        _state = state;
        _measuring = false;
        _timingAt = now;
        _resumeNeedsBaseline = true;
        ResetRate(_progress);
    }

    public JobProgressEstimate Observe(JobStatusSnapshot snapshot, JobState state, DateTimeOffset observedAt,
        string? segmentKey = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Delayed status delivery must not rewind counters, elapsed time, or a newer estimate.
        if (_observedAt.HasValue && observedAt <= _observedAt.Value)
            return GetEstimate(_observedAt.Value, state);

        SetState(state, observedAt);
        var progress = Math.Max(0, snapshot.Progress);
        var total = Math.Max(0, snapshot.ProgressTotal);
        _countersValid = snapshot.Progress >= 0 && snapshot.ProgressTotal >= 0;
        var key = segmentKey ?? SegmentKey(snapshot);
        var measuring = _countersValid && state == JobState.Running &&
            string.Equals(snapshot.State, "Running", StringComparison.OrdinalIgnoreCase);
        _backendPaused = string.Equals(snapshot.State, "Paused", StringComparison.OrdinalIgnoreCase);
        _backendFinished = IsBackendFinished(snapshot.State);
        _backendRemaining = snapshot.EstimatedCompletion is { } completion && completion > observedAt
            ? completion - observedAt : null;

        var newPass = !_observedAt.HasValue || !string.Equals(_segmentKey, key, StringComparison.Ordinal) ||
            total != _total || progress < _progress;
        var previousProgress = _progress;
        if (newPass)
        {
            _activeSeconds = 0;
            ResetRate(progress);
            _lastAdvanceAt = observedAt;
        }
        else if (_measuring && measuring && !_resumeNeedsBaseline)
        {
            var intervalValid = AddActiveInterval(observedAt);
            var stalled = observedAt - _lastAdvanceAt >= _staleAfter;
            if (!intervalValid || (stalled && progress > _progress))
            {
                ResetRate(progress);
                _lastAdvanceAt = observedAt;
            }
            else if (progress > _progress)
            {
                _positiveIntervals = Math.Min(2, _positiveIntervals + 1);
                _lastAdvanceAt = observedAt;
            }
        }
        else
        {
            // Initialization, tuning, pauses, and restored startup do not supply useful rate samples.
            ResetRate(progress);
            _lastAdvanceAt = observedAt;
        }

        _progress = progress;
        _total = total;
        _segmentKey = key;
        _observedAt = observedAt;
        _timingAt = observedAt;
        _measuring = measuring;
        _resumeNeedsBaseline = false;
        if (measuring)
        {
            if (newPass || previousProgress != progress || _samples.Count == 0 ||
                _samples.Last().Seconds != _activeSeconds)
                _samples.Enqueue(new(_activeSeconds, progress));
            TrimSamples();
        }
        return GetEstimate(observedAt, state);
    }

    /// <summary>
    /// Reads the last observed estimate without advancing counters or subtracting wall time from an ETA.
    /// A timer may call this to mark stale data unavailable. It does not record pause/resume transitions.
    /// </summary>
    public JobProgressEstimate GetEstimate(DateTimeOffset now, JobState state)
    {
        var result = new JobProgressEstimate
        {
            ObservedAt = _observedAt ?? now,
            ProgressPercent = _countersValid && _total > 0 ? Math.Clamp((double)_progress / _total * 100, 0, 100) : null,
            ActiveElapsed = Duration(_activeSeconds) ?? TimeSpan.MaxValue,
            State = JobProgressEstimateState.Unavailable,
            Note = "Waiting for measurable progress. Estimates cover this pass from the first observed running sample; earlier checkpoint work is excluded."
        };
        if (IsFinished(state) || _backendFinished || (_countersValid && _total > 0 && _progress >= _total))
        {
            result.State = JobProgressEstimateState.Finished;
            result.Note = "This attempt has stopped. No remaining-time estimate is running.";
            return result;
        }
        if (state == JobState.Paused || _backendPaused)
        {
            result.State = JobProgressEstimateState.Paused;
            result.Note = "Paused. Timing resumes after fresh running samples; paused time is excluded.";
            return result;
        }
        if (!_observedAt.HasValue || state != JobState.Running) return result;
        if (now < _observedAt.Value || now - _observedAt.Value >= _staleAfter ||
            (_measuring && now - _lastAdvanceAt >= _staleAfter))
        {
            result.State = JobProgressEstimateState.Stalled;
            result.Note = "Waiting for fresh progress. Remaining time is unavailable while progress is stale or stopped.";
            return result;
        }
        if (!_measuring)
        {
            result.Note = _countersValid
                ? "Preparing the attack. Setup and tuning time are excluded from the estimate."
                : "Waiting for valid progress counters from the backend.";
            return result;
        }
        if (_total <= 0)
        {
            result.Note = "The backend has not reported a total amount of work, so a percentage and duration cannot be estimated.";
            return BackendFallback(result);
        }

        result.State = JobProgressEstimateState.Learning;
        result.Note = "Learning the checking rate from recent progress. Estimates cover the current pass and exclude pauses and earlier checkpoint work.";
        var measuredSeconds = _activeSeconds - _baselineSeconds;
        if (_positiveIntervals < 2 || measuredSeconds < _minimumObservation.TotalSeconds || _samples.Count < 2)
            return BackendFallback(result);

        var first = _samples.Peek();
        var recentSeconds = _activeSeconds - first.Seconds;
        if (recentSeconds <= 0 || measuredSeconds <= 0) return result;
        var recentRate = (double)(_progress - first.Progress) / recentSeconds;
        var historicalRate = (double)(_progress - _baselineProgress) / measuredSeconds;
        var rate = 0.65 * recentRate + 0.35 * historicalRate;
        if (!double.IsFinite(rate) || rate <= 0) return result;
        var remainingSeconds = (double)(_total - _progress) / rate;
        var remaining = Duration(remainingSeconds);
        var totalDuration = Duration(_activeSeconds + remainingSeconds);
        if (remaining is null || totalDuration is null)
        {
            result.State = JobProgressEstimateState.Unavailable;
            result.Note = "The measured rate implies a duration too large to estimate reliably.";
            return result;
        }

        result.State = JobProgressEstimateState.Tracking;
        result.Basis = JobProgressEstimateBasis.ObservedProgress;
        result.Remaining = remaining;
        result.EstimatedTotalDuration = totalDuration;
        result.Note = "Based on recent and earlier progress in this pass. Total duration is observed active time plus estimated remaining time; pauses and earlier checkpoint work are excluded. Other wordlists or pattern passes are not included.";
        return result;
    }

    private JobProgressEstimate BackendFallback(JobProgressEstimate result)
    {
        if (_backendRemaining is not { } remaining || Duration(_activeSeconds + remaining.TotalSeconds) is not { } total)
            return result;
        result.State = JobProgressEstimateState.Tracking;
        result.Basis = JobProgressEstimateBasis.Backend;
        result.Remaining = remaining;
        result.EstimatedTotalDuration = total;
        result.Note = "Initial estimate supplied by the backend while local progress history builds. Estimates cover this pass; total duration includes observed active time and excludes pauses and earlier checkpoint work.";
        return result;
    }

    private bool AddActiveInterval(DateTimeOffset now)
    {
        var elapsed = now - _timingAt;
        if (elapsed <= TimeSpan.Zero || elapsed >= _staleAfter) return false;
        _activeSeconds = Math.Min(TimeSpan.MaxValue.TotalSeconds, _activeSeconds + elapsed.TotalSeconds);
        return true;
    }

    private void ResetRate(long progress)
    {
        _baselineProgress = progress;
        _baselineSeconds = _activeSeconds;
        _positiveIntervals = 0;
        _samples.Clear();
    }

    private void TrimSamples()
    {
        var cutoff = _activeSeconds - _rollingWindow.TotalSeconds;
        // Retain the preceding sample to avoid an empty window at low status frequencies.
        while (_samples.Count > 2 && (_samples.ElementAt(1).Seconds <= cutoff || _samples.Count > 256))
            _samples.Dequeue();
    }

    private static string SegmentKey(JobStatusSnapshot snapshot) =>
        $"{snapshot.SourceIndex}:{snapshot.SourceCount}:{snapshot.ModifierIndex}:{snapshot.ModifierCount}:{snapshot.StartedAt?.UtcTicks}";

    private static bool IsBackendFinished(string state) => string.Equals(state, "Cracked", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, "Exhausted", StringComparison.OrdinalIgnoreCase) || string.Equals(state, "Aborted", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, "Quit", StringComparison.OrdinalIgnoreCase) || string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, "Checkpoint", StringComparison.OrdinalIgnoreCase) || string.Equals(state, "Runtime limit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, "Finished", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinished(JobState state) => state is JobState.Cracked or JobState.Exhausted or JobState.Cancelled or
        JobState.Checkpointed or JobState.Failed or JobState.Interrupted;

    private static TimeSpan? Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds) return null;
        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan Positive(TimeSpan value, string name) => value > TimeSpan.Zero ? value :
        throw new ArgumentOutOfRangeException(name, "The observation interval must be positive.");

    private readonly record struct Sample(double Seconds, long Progress);
}
