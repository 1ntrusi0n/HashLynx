namespace HashLynx.Core;

public enum RecoveryStepState { Pending, Running, Completed, Failed, Interrupted, Skipped }

public sealed class RecoveryQueueDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<RecoveryPlan> Plans { get; set; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1 || Plans is null || Plans.Count > 1000)
            throw new InvalidDataException("Unsupported recovery queue. Its saved data has been preserved.");
        var planIds = new HashSet<Guid>(); var jobIds = new HashSet<Guid>();
        foreach (var plan in Plans)
        {
            if (plan is null || plan.Id == Guid.Empty || !planIds.Add(plan.Id) || plan.Steps is not { Count: > 0 and <= 64 })
                throw new InvalidDataException("Invalid recovery plan. Its saved data has been preserved.");
            foreach (var step in plan.Steps)
                if (step is null || !Enum.IsDefined(step.State) || step.Configuration is not { } job || job.Id == Guid.Empty || !jobIds.Add(job.Id)
                    || string.IsNullOrWhiteSpace(job.TargetPath) || job.Attack is null || job.Options is null)
                    throw new InvalidDataException("Invalid recovery step. Its saved data has been preserved.");
        }
    }
}

public sealed class RecoveryPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Recovery sequence";
    public List<RecoveryStep> Steps { get; set; } = [];
}

public sealed class RecoveryStep
{
    public string Name { get; set; } = "Attack";
    public HashcatJob Configuration { get; set; } = new();
    public RecoveryStepState State { get; set; }
    public Guid? LastJobId { get; set; }
    public string Message { get; set; } = "Waiting to run.";
}

/// <summary>Pure queue transitions; the UI controls starting, persistence, and process lifetime.</summary>
public static class RecoveryQueueTransitions
{
    public static void Reconcile(RecoveryQueueDocument document, IReadOnlyList<JobRecord> jobs)
    {
        foreach (var plan in document.Plans)
            foreach (var step in plan.Steps.Where(step => step.State is RecoveryStepState.Running or RecoveryStepState.Interrupted or RecoveryStepState.Failed))
            {
                var record = jobs.FirstOrDefault(job => job.Id == step.LastJobId);
                if (record?.State is JobState.Cracked or JobState.Exhausted) Complete(plan, step, record.State);
                else if (step.State == RecoveryStepState.Running) { step.State = RecoveryStepState.Interrupted; step.Message = "Interrupted. Review the session checkpoint or retry this step."; }
            }
    }

    public static (RecoveryPlan Plan, RecoveryStep Step)? Next(RecoveryQueueDocument document)
    {
        foreach (var plan in document.Plans)
        {
            if (plan.Steps.Any(step => step.State is RecoveryStepState.Running or RecoveryStepState.Failed or RecoveryStepState.Interrupted)) return null;
            if (plan.Steps.FirstOrDefault(step => step.State == RecoveryStepState.Pending) is { } next) return (plan, next);
        }
        return null;
    }

    public static void Complete(RecoveryPlan plan, RecoveryStep step, JobState state)
    {
        step.State = state switch
        {
            JobState.Cracked or JobState.Exhausted => RecoveryStepState.Completed,
            JobState.Cancelled or JobState.Checkpointed or JobState.Interrupted => RecoveryStepState.Interrupted,
            _ => RecoveryStepState.Failed
        };
        step.Message = state switch
        {
            JobState.Cracked => "All targets are recovered. View results in the sequence's sessions.",
            JobState.Exhausted => "This attempt finished; its candidates have been tried.",
            JobState.Failed => "The attempt failed. Review its session diagnostic before retrying.",
            _ => "Stopped before finishing. Restore the session or retry this step."
        };
        if (state == JobState.Cracked)
            foreach (var remaining in plan.Steps.Where(item => item.State == RecoveryStepState.Pending))
            { remaining.State = RecoveryStepState.Skipped; remaining.Message = "Skipped: all targets in this sequence are recovered."; }
    }
}
