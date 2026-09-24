namespace HashLynx.Core.Tests;

public sealed class RecoveryQueueTests
{
    [Fact]
    public void ExhaustedAttemptAdvancesAndCrackedAttemptSkipsOnlyItsOwnSequence()
    {
        var first = Plan(3); var second = Plan(2);
        var document = new RecoveryQueueDocument { Plans = [first, second] };
        Assert.Same(first.Steps[0], RecoveryQueueTransitions.Next(document)!.Value.Step);
        RecoveryQueueTransitions.Complete(first, first.Steps[0], JobState.Exhausted);
        Assert.Same(first.Steps[1], RecoveryQueueTransitions.Next(document)!.Value.Step);
        RecoveryQueueTransitions.Complete(first, first.Steps[1], JobState.Cracked);
        Assert.Equal(RecoveryStepState.Completed, first.Steps[0].State);
        Assert.Equal(RecoveryStepState.Skipped, first.Steps[2].State);
        Assert.All(second.Steps, step => Assert.Equal(RecoveryStepState.Pending, step.State));
        Assert.Same(second.Steps[0], RecoveryQueueTransitions.Next(document)!.Value.Step);
    }

    [Theory]
    [InlineData(JobState.Failed, RecoveryStepState.Failed)]
    [InlineData(JobState.Cancelled, RecoveryStepState.Interrupted)]
    [InlineData(JobState.Checkpointed, RecoveryStepState.Interrupted)]
    [InlineData(JobState.Interrupted, RecoveryStepState.Interrupted)]
    public void FailedOrStoppedAttemptBlocksLaterStepsUntilExplicitlyResolved(JobState outcome, RecoveryStepState expected)
    {
        var plan = Plan(2); var other = Plan(1);
        var document = new RecoveryQueueDocument { Plans = [plan, other] };
        RecoveryQueueTransitions.Complete(plan, plan.Steps[0], outcome);
        Assert.Equal(expected, plan.Steps[0].State);
        Assert.Equal(RecoveryStepState.Pending, plan.Steps[1].State);
        Assert.Equal(RecoveryStepState.Pending, other.Steps[0].State);
        Assert.Null(RecoveryQueueTransitions.Next(document));
        plan.Steps[0].State = RecoveryStepState.Skipped;
        Assert.Same(plan.Steps[1], RecoveryQueueTransitions.Next(document)!.Value.Step);
    }

    [Fact]
    public void RestartNeverAutomaticallyRestartsAnUnaccountedRunningStep()
    {
        var plan = Plan(2); var step = plan.Steps[0];
        step.State = RecoveryStepState.Running; step.LastJobId = step.Configuration.Id;
        var document = new RecoveryQueueDocument { Plans = [plan] };
        RecoveryQueueTransitions.Reconcile(document, []);
        Assert.Equal(RecoveryStepState.Interrupted, step.State);
        Assert.Equal(RecoveryStepState.Pending, plan.Steps[1].State);
        Assert.Null(RecoveryQueueTransitions.Next(document));
    }

    [Theory]
    [InlineData(RecoveryStepState.Running, JobState.Exhausted)]
    [InlineData(RecoveryStepState.Interrupted, JobState.Exhausted)]
    [InlineData(RecoveryStepState.Failed, JobState.Exhausted)]
    [InlineData(RecoveryStepState.Running, JobState.Cracked)]
    [InlineData(RecoveryStepState.Interrupted, JobState.Cracked)]
    [InlineData(RecoveryStepState.Failed, JobState.Cracked)]
    public void SavedCompletionOrSuccessfulRestoreReconcilesWithItsOriginalQueueStep(RecoveryStepState previous, JobState restored)
    {
        var plan = Plan(2); var step = plan.Steps[0];
        step.State = previous; step.LastJobId = step.Configuration.Id;
        var document = new RecoveryQueueDocument { Plans = [plan] };
        RecoveryQueueTransitions.Reconcile(document, [new() { Id = step.Configuration.Id, Configuration = step.Configuration, State = restored }]);
        Assert.Equal(RecoveryStepState.Completed, step.State);
        if (restored == JobState.Cracked)
        {
            Assert.Equal(RecoveryStepState.Skipped, plan.Steps[1].State);
            Assert.Null(RecoveryQueueTransitions.Next(document));
        }
        else Assert.Same(plan.Steps[1], RecoveryQueueTransitions.Next(document)!.Value.Step);
    }

    [Fact]
    public void UnrelatedSuccessfulSessionDoesNotCompleteAnInterruptedSequence()
    {
        var plan = Plan(2); var step = plan.Steps[0];
        step.State = RecoveryStepState.Interrupted; step.LastJobId = step.Configuration.Id;
        var document = new RecoveryQueueDocument { Plans = [plan] };
        RecoveryQueueTransitions.Reconcile(document, [new() { Id = Guid.NewGuid(), Configuration = step.Configuration, State = JobState.Cracked }]);
        Assert.Equal(RecoveryStepState.Interrupted, step.State);
        Assert.Equal(RecoveryStepState.Pending, plan.Steps[1].State);
        Assert.Null(RecoveryQueueTransitions.Next(document));
    }

    [Fact]
    public void SkippedStepIsNotRevivedByLaterSessionChanges()
    {
        var plan = Plan(2); var step = plan.Steps[0];
        step.State = RecoveryStepState.Skipped; step.LastJobId = step.Configuration.Id;
        var document = new RecoveryQueueDocument { Plans = [plan] };
        RecoveryQueueTransitions.Reconcile(document, [new() { Id = step.Configuration.Id, State = JobState.Cracked }]);
        Assert.Equal(RecoveryStepState.Skipped, step.State);
        Assert.Same(plan.Steps[1], RecoveryQueueTransitions.Next(document)!.Value.Step);
    }

    [Fact]
    public void DuplicateStepJobIdsAcrossSequencesAreRejected()
    {
        var first = Plan(1); var second = Plan(1);
        second.Steps[0].Configuration.Id = first.Steps[0].Configuration.Id;
        Assert.Throws<InvalidDataException>(() => new RecoveryQueueDocument { Plans = [first, second] }.Validate());
    }

    [Fact]
    public void InvalidStateCannotBeLoadedAsAnImplicitPendingAttempt()
    {
        var plan = Plan(1); plan.Steps[0].State = (RecoveryStepState)999;
        Assert.Throws<InvalidDataException>(() => new RecoveryQueueDocument { Plans = [plan] }.Validate());
    }

    private static RecoveryPlan Plan(int count) => new()
    {
        Steps = Enumerable.Range(0, count).Select(index => new RecoveryStep
        {
            Name = $"Attempt {index + 1}",
            Configuration = new() { TargetPath = @"C:\synthetic\target.hashes", HashMode = 0 }
        }).ToList()
    };
}
