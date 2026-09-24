using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HashLynx.Core;
using HashLynx.Hashcat;
using System.Security.Cryptography;
using System.Text;
using HashLynx.Persistence;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Smoke;

internal sealed partial class SmokeApplication
{
    private async Task CheckHintsAndQueueAsync(ShellViewModel shell, PersistenceStore store, Window window)
    {
        shell.Selected = shell.Navigation.First(item => item.Page == shell.Attack);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Find<Expander>(window, "HintsExpander").IsExpanded = true;
        var hints = shell.Attack.Hints;
        hints.UsePattern = true; hints.Prefix = "Summer"; hints.Suffix = "!"; hints.MinimumLength = hints.MaximumLength = "11";
        hints.PreviewCommand.Execute(null);
        Require(hints.Examples.Contains("Summer0000!") && hints.Steps.Contains("10,000"), "Hints must preserve the fixed prefix and suffix and show the right candidate count.");
        Require(hints.QueueCommand.CanExecute(null), "Valid preview may be queued.");
        hints.MinimumLength = "bad";
        Require(!hints.QueueCommand.CanExecute(null), "Invalid numeric text must invalidate a previous preview immediately.");
        hints.PreviewCommand.Execute(null);
        Require(hints.Summary.Contains("whole numbers"), "Invalid numeric hint fields need actionable feedback.");
        hints.MinimumLength = "11"; hints.PreviewCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var hintsScroll = Find<ScrollViewer>(window, "AttackScrollViewer");
        var hintsPanel = Find<Expander>(window, "HintsExpander");
        window.UpdateLayout();
        hintsScroll.ScrollToVerticalOffset(hintsScroll.VerticalOffset + hintsPanel.TranslatePoint(new Point(0, 0), hintsScroll).Y - 25);
        await RenderAsync(window, "Hints-preview");
        Find<Expander>(window, "HintsExpander").IsExpanded = false;

        Find<Expander>(window, "WordlistManagerExpander").IsExpanded = true;
        await RenderAsync(window, "Wordlist-library-tools");
        Find<Expander>(window, "WordlistManagerExpander").IsExpanded = false;

        shell.ShowCompletion(new(new JobRecord { State = JobState.Exhausted, LatestStatus = new() { RecoveredHashes = 1, TotalHashes = 2 } }));
        Require(shell.CompletionVisible && shell.CompletionHasResults, "Partial recoveries need a visible results action.");
        await RenderAsync(window, "Completion-partial-results");
        shell.DismissCompletionCommand.Execute(null);
        Require(!shell.CompletionVisible, "Completion banners must dismiss.");

        var queueStore = new PersistenceStore(new AppPaths(Path.Combine(store.Paths.Root, "queue-tests")));
        var document = new RecoveryQueueDocument { Plans = [new() { Steps = Enumerable.Range(1, 3).Select(i => new RecoveryStep
        { Name = "Test " + i, Configuration = new() { TargetPath = "test.hashes", HashMode = 0 } }).ToList() }] };
        await queueStore.SaveQueueAsync(document);
        var services = new AppServices(queueStore); var jobs = new JobsViewModel(services);
        var firstFinished = new TaskCompletionSource<JobRecord>(); var calls = 0;
        var queue = new RecoveryQueueViewModel(services, jobs, async job =>
        {
            calls++;
            if (calls == 1) return await firstFinished.Task;
            return new() { Id = job.Id, Configuration = job, State = JobState.Cracked };
        });
        await queue.InitializeAsync(); Require(calls == 0 && !queue.IsRunning, "Loading a queue must never execute it.");
        await queue.StartAsync();
        await WaitUntilAsync(() => calls == 1);
        queue.RequestPause();
        firstFinished.SetResult(new() { State = JobState.Exhausted });
        await queue.WaitForIdleAsync();
        Require(calls == 1 && queue.Rows[1].Step.State == RecoveryStepState.Pending, "Pause must preserve subsequent steps.");
        await queue.StartAsync(); await queue.WaitForIdleAsync();
        Require(calls == 2 && queue.Rows[2].Step.State == RecoveryStepState.Skipped, "Full recovery must skip later steps in the sequence.");
        Require((await queueStore.LoadQueueAsync()).Plans[0].Steps[2].State == RecoveryStepState.Skipped, "Completion and skipped state must survive restart.");

        await queueStore.SaveQueueAsync(document);
        var failQueue = new RecoveryQueueViewModel(services, jobs, job => Task.FromResult(new JobRecord { Id = job.Id, State = JobState.Failed }));
        await failQueue.InitializeAsync(); await failQueue.StartAsync(); await failQueue.WaitForIdleAsync();
        Require(failQueue.Rows[0].Step.State == RecoveryStepState.Failed && failQueue.Rows[1].Step.State == RecoveryStepState.Pending, "Failed attempts must pause the remaining queue.");
        var oldId = failQueue.Rows[0].Step.Configuration.Id;
        await ((AsyncCommand)failQueue.RetryCommand).ExecuteAsync();
        Require(failQueue.Rows[0].Step.Configuration.Id != oldId && failQueue.Rows[0].Step.State == RecoveryStepState.Pending, "Retry must preserve the previous session and allocate a new attempt.");

        await queueStore.SaveQueueAsync(document);
        var prematureLaunches = 0;
        var stoppedQueue = new RecoveryQueueViewModel(services, jobs, _ => { prematureLaunches++; return Task.FromResult(new JobRecord { State = JobState.Exhausted }); });
        await stoppedQueue.InitializeAsync(); await stoppedQueue.StartAsync(); stoppedQueue.RequestPause(); services.CancelPendingOperations(); await stoppedQueue.WaitForIdleAsync();
        Require(prematureLaunches == 0 && stoppedQueue.Rows[0].Step.State == RecoveryStepState.Pending, "Closing while saving a launch must not start a late recovery process.");
        await jobs.StopAllAsync();

        shell.Selected = shell.Navigation.First(item => item.Page == shell.Queue);
        await RenderAsync(window, "Recovery-queue");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition()) { if (DateTime.UtcNow >= until) throw new InvalidOperationException("Timed out waiting for a workflow transition."); await Task.Delay(20); }
    }

    private async Task CheckLiveQueueAsync(ShellViewModel shell, AppServices services, Window window, string device)
    {
        if (!int.TryParse(device, out var deviceId)) throw new InvalidOperationException("Choose a numeric test device ID.");
        var folder = Path.Combine(services.Store.Paths.Root, "queue-live"); Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "target.txt"); var wrong = Path.Combine(folder, "first.txt"); var right = Path.Combine(folder, "second.txt");
        const string expected = "Queue1!";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(expected))).ToLowerInvariant();
        await File.WriteAllTextAsync(target, hash + "\n"); await File.WriteAllTextAsync(wrong, "not-the-answer\n"); await File.WriteAllTextAsync(right, expected + "\n");
        var config = new HashcatJob { HashMode = 0, TargetPath = target, Options = new() { Devices = [deviceId], WorkloadProfile = 1, ExtraArguments = ["--runtime=60"] } };
        await shell.Queue.AddAsync(config,
        [
            ("Known exhaustion", new() { Wordlists = [wrong] }),
            ("Known recovery", new() { Wordlists = [right] }),
            ("Must be skipped", new() { Wordlists = [right] })
        ]);
        var queuedRows = shell.Queue.Rows.TakeLast(3).ToArray();
        Require(queuedRows.Select(row => row.Step.Configuration.Options.OutputPath).Distinct().Count() == 3, "Queue attempts need separate output files.");
        Require(queuedRows.Select(row => row.Step.Configuration.Options.PotfilePath).Distinct().Count() == 1, "A sequence needs one private shared cache.");
        // Editing the original must not change the queued target snapshot.
        await File.WriteAllTextAsync(target, "source-changed-after-queue\n");
        Require((await File.ReadAllTextAsync(queuedRows[0].Step.Configuration.TargetPath)).Trim() == hash, "Queued targets must be snapshots.");
        shell.Selected = shell.Navigation.First(item => item.Page == shell.Queue);
        await shell.Queue.StartAsync();
        var done = shell.Queue.WaitForIdleAsync();
        if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromMinutes(4))) != done)
        {
            shell.Queue.RequestPause(); await shell.Jobs.StopAllAsync(); throw new InvalidOperationException("Live queue exceeded its bounded test time.");
        }
        await done;
        Require(queuedRows[0].Step.State == RecoveryStepState.Completed && queuedRows[1].Step.State == RecoveryStepState.Completed && queuedRows[2].Step.State == RecoveryStepState.Skipped,
            "Queue must advance after exhaustion, finish on recovery, then skip redundant attempts. " + shell.Queue.Status);
        var first = shell.Jobs.Jobs.Single(job => job.Record.Id == queuedRows[0].Step.LastJobId);
        var second = shell.Jobs.Jobs.Single(job => job.Record.Id == queuedRows[1].Step.LastJobId);
        Require(first.Record.State == JobState.Exhausted && second.Record.State == JobState.Cracked, "Real backend states must drive queue transitions.");
        Require((await services.Backend.ReadSessionResultsAsync(first.Record.Configuration)).Count == 0, "Earlier exhausted queue sessions must not inherit later recoveries.");
        var result = await services.Backend.ReadSessionResultsAsync(second.Record.Configuration);
        Require(result.Any(item => item.Hash == hash && item.Plaintext == expected), "The recovering queue step must own the actual result.");
        Require(shell.CompletionVisible && shell.CompletionHasResults, "Actual queue completion must expose a results banner.");
        await RenderAsync(window, "Queue-live-completed");
        await ((AsyncCommand)shell.OpenCompletionCommand).ExecuteAsync();
        Require(shell.CurrentPage is ResultsViewModel, "Completion action must open actual results.");
        await RenderAsync(window, "Queue-live-results");
        shell.DismissCompletionCommand.Execute(null);

        // Real hint file creation and queue staging, without automatically running the broader pattern.
        shell.Attack.Expert = false; shell.Attack.Inspector.InputMode = 0; shell.Attack.Inspector.HashText = hash;
        await shell.Attack.Inspector.PrepareTargetAsync(); shell.Attack.Inspector.SelectedMode = shell.Attack.Inspector.FindMode(0);
        shell.Attack.Hints.UsePattern = false; shell.Attack.Hints.Words = "$HEX[61]\nQueue1!"; shell.Attack.Hints.TryVariations = false;
        shell.Attack.Hints.PreviewCommand.Execute(null);
        await ((AsyncCommand)shell.Attack.Hints.QueueCommand).ExecuteAsync();
        var hinted = shell.Queue.Rows.Last().Step;
        Require(hinted.State == RecoveryStepState.Pending && !shell.Queue.IsRunning, "Hint queueing must not start recovery automatically.");
        Require(hinted.Configuration.Options.ExtraArguments.Contains("--wordlist-autohex-disable"), "Generated hint words must remain literal.");
        Require((await File.ReadAllLinesAsync(hinted.Configuration.Attack.Wordlists.Single())).SequenceEqual(new[] { "$HEX[61]", "Queue1!" }), "Hint candidate file must preserve entered words.");
    }
}
