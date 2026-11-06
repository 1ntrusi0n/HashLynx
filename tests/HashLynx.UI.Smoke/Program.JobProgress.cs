using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HashLynx.Core;
using HashLynx.Persistence;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Smoke;

internal sealed partial class SmokeApplication
{
    private async Task CheckJobProgressAsync(ShellViewModel shell, AppServices services, PersistenceStore store, Window window)
    {
        var path = Path.Combine(store.Paths.Root, "job-progress-synthetic.txt");
        // Include a blank entry, duplicates, and an unterminated last entry: these are source lines, not tested guesses.
        var text = "\n" + string.Concat(Enumerable.Repeat("synthetic-entry\n", 998)) + "synthetic-last";
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        var file = new FileInfo(path);
        var record = new JobRecord
        {
            Name = "Synthetic wordlist progress",
            State = JobState.Ready,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-20),
            Configuration = new() { Attack = new() { Kind = AttackFamilies.Dictionary, Wordlists = [path] } },
            InputSnapshots = [new(path, file.Length, file.LastWriteTimeUtc.Ticks)]
        };
        var row = new JobViewModel(record);
        var added = new List<JobViewModel> { row };
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        shell.Jobs.Jobs.Add(row);
        shell.Jobs.Selected = row;
        shell.Selected = shell.Navigation.Single(page => page.Page == shell.Jobs);
        try
        {
            // Hold only the test store's existing serialization gate so a background count remains pending deterministically.
            // The production count must yield to the dispatcher while waiting for metadata, with update protection active.
            var gate = typeof(PersistenceStore).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(store) as SemaphoreSlim
                ?? throw new InvalidOperationException("The isolated persistence scheduling fixture could not be prepared.");
            await gate.WaitAsync();
            Task count;
            try
            {
                count = shell.Jobs.EnsureWordlistStatisticsAsync(row);
                Require(!count.IsCompleted && row.IsCountingWordlists, "Wordlist counting must be asynchronous and expose its pending state.");
                var dispatched = false;
                await window.Dispatcher.InvokeAsync(() => dispatched = true, DispatcherPriority.Send);
                Require(dispatched && !count.IsCompleted, "The WPF dispatcher must remain responsive while line-count metadata is pending.");
                Require(services.IsBusyForUpdate && !services.TryBeginUpdate(), "An update must wait until background wordlist counting and persistence finish.");
            }
            finally { gate.Release(); }
            await count;
            Require(!row.IsCountingWordlists, "Completed counts must clear their busy state.");
            Require(!services.IsBusyForUpdate, "Completed counting and persistence must release their update-operation guard.");
            Require(record.WordlistStatistics is [{ LineCount: 1000, CountStatus: null }], "The job must receive an exact physical source-line count.");
            var savedCount = (await store.LoadJobsAsync()).Single(job => job.Id == record.Id).WordlistStatistics?.Single();
            Require(savedCount?.LineCount == 1000 && savedCount.FileSizeBytes == file.Length
                && savedCount.LastWriteUtc == file.LastWriteTimeUtc, "Counts and source metadata must survive the normal jobs persistence path.");
            Require(await File.ReadAllTextAsync(path) == text, "Counting a job wordlist must leave its contents unchanged.");

            record.State = JobState.Running;
            row.BeginProgressTracking();
            Require(row.Eta == "Not available" && !row.Remaining.StartsWith("About ", StringComparison.Ordinal), "A new live attempt must learn its checking rate before displaying an ETA.");
            var now = DateTimeOffset.UtcNow;
            row.ObserveStatus(Progress(0, now.AddSeconds(-15)));
            row.ObserveStatus(Progress(100, now.AddSeconds(-10)));
            row.ObserveStatus(Progress(200, now.AddSeconds(-5)));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(record.ProgressEstimate is { Basis: JobProgressEstimateBasis.ObservedProgress, Remaining: not null }, "Progress history must produce a live remaining-time estimate.");
            var firstRemaining = record.ProgressEstimate!.Remaining!.Value;
            Require(row.Progress == 20 && row.ProgressText.Contains(20d.ToString("F1"), StringComparison.Ordinal), "The displayed work percentage must use backend progress totals.");
            Require(row.WordlistLines == $"{1000:N0} lines" && row.WordlistDetail.Contains("blank and duplicate", StringComparison.Ordinal), "Physical wordlist lines must be named separately from generated password guesses.");
            Require(row.Remaining.StartsWith("About ", StringComparison.Ordinal) && row.EstimatedDuration.StartsWith("About ", StringComparison.Ordinal)
                && row.Eta != "Not available", "A measured live attack must show remaining time, active duration, and estimated completion.");
            Require(Find<TextBlock>(window, "JobWordlistLines").Text == row.WordlistLines
                && Find<TextBlock>(window, "JobWordlistDetail").Text == row.WordlistDetail
                && Find<TextBlock>(window, "JobProgressText").Text == row.ProgressText
                && Find<TextBlock>(window, "JobRemaining").Text == row.Remaining
                && Find<TextBlock>(window, "JobEstimatedDuration").Text == row.EstimatedDuration
                && Find<TextBlock>(window, "JobEstimateNote").Text == row.EstimateNote,
                "Real Jobs controls must bind the selected job's line counts, percentage, and time estimates.");

            row.ObserveStatus(Progress(250, now));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(row.Progress == 25 && Find<ProgressBar>(window, "JobProgressBar").Value == 25,
                "Additional status samples must refresh the progress bar.");
            Require(record.ProgressEstimate!.Remaining > firstRemaining, "A slower observed checking rate must revise the estimate using the accumulated history.");
            foreach (var theme in new[] { "Light", "Dark" })
            {
                AppServices.ApplyTheme(theme);
                window.Width = 1040; window.Height = 700;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var scroll = Find<ScrollViewer>(window, "JobsScrollViewer");
                var field = Find<FrameworkElement>(window, "JobWordlistPanel");
                window.UpdateLayout();
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + field.TranslatePoint(new Point(0, 0), scroll).Y - 30);
                await RenderAsync(window, "Jobs-wordlist-progress-" + theme + "-narrow");
                Require(field.IsVisible && scroll.ScrollableHeight > 0, "The narrow Jobs page must keep the count and estimate area visible and scrollable.");
            }

            await shell.Jobs.SaveAsync();
            var restoredRecord = (await store.LoadJobsAsync()).Single(job => job.Id == record.Id);
            Require(restoredRecord.ProgressEstimate?.Remaining is not null, "Saved jobs must retain the observed estimate summary.");
            var restored = new JobViewModel(restoredRecord);
            Require(restored.WordlistLines == row.WordlistLines && restored.Eta == "Not available"
                && !restored.Remaining.StartsWith("About ", StringComparison.Ordinal), "A restored history row must retain line counts without running a saved countdown.");

            record.State = JobState.Paused;
            row.Refresh();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(row.Remaining == "Paused" && row.Eta == "Not available" && row.EstimatedDuration == "Not available",
                "Paused attempts must suppress remaining-time and completion estimates.");
            Require(Find<TextBlock>(window, "JobRemaining").Text == "Paused", "The Jobs panel must show the paused timing state.");
            record.State = JobState.Exhausted;
            record.FinishedAt = DateTimeOffset.UtcNow;
            row.Refresh();
            Require(row.Remaining == "Attempt stopped" && row.Eta == "Not available", "Finished attempts must never display a running ETA.");

            var pattern = new JobViewModel(new()
            {
                Name = "Synthetic pattern without a wordlist",
                Configuration = new() { Attack = new() { Kind = AttackFamilies.Mask, Mask = "?d?d" } }
            });
            added.Add(pattern);
            shell.Jobs.Jobs.Add(pattern);
            shell.Jobs.Selected = pattern;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(!pattern.HasWordlists && Find<FrameworkElement>(window, "JobWordlistPanel").Visibility == Visibility.Collapsed,
                "Attacks without source wordlists must hide the physical-line panel.");
            await CheckJobCountingShutdownAsync(store, window);
            await File.WriteAllTextAsync(Path.Combine(output, "job-progress-result.txt"),
                "PASS: asynchronous physical line counting; responsive dispatcher; updater busy guard; awaited shutdown cancellation and persistence; source preservation; persisted file metadata; observed progress percentages; adaptive remaining/total time; paused and finished ETA suppression; restored summary without countdown; wordlist-free panel collapse; narrow Light/Dark Jobs views.");
        }
        finally
        {
            shell.Jobs.Selected = null;
            foreach (var job in added) shell.Jobs.Jobs.Remove(job);
            await shell.Jobs.SaveAsync();
            window.Width = originalWidth; window.Height = originalHeight;
            AppServices.ApplyTheme(services.Settings.Theme);
            shell.Selected = shell.Navigation[0];
        }

        static JobStatusSnapshot Progress(long value, DateTimeOffset observedAt) => new()
        {
            State = "Running", Progress = value, ProgressTotal = 1000, ProgressPercent = value / 10d,
            SourceIndex = 1, SourceCount = 1, TotalHashes = 1, SpeedHashesPerSecond = 20, ObservedAt = observedAt
        };
    }

    private async Task CheckJobCountingShutdownAsync(PersistenceStore parentStore, Window window)
    {
        var store = new PersistenceStore(new AppPaths(Path.Combine(parentStore.Paths.Root, "job-counting-shutdown")));
        var services = new AppServices(store);
        var jobs = new JobsViewModel(services);
        var path = Path.Combine(store.Paths.Root, "synthetic-shutdown.txt");
        await File.WriteAllTextAsync(path, "synthetic\n");
        var row = new JobViewModel(new() { Configuration = new() { Attack = new() { Wordlists = [path] } } });
        jobs.Jobs.Add(row);
        var gate = typeof(PersistenceStore).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(store) as SemaphoreSlim
            ?? throw new InvalidOperationException("The isolated shutdown scheduling fixture could not be prepared.");
        await gate.WaitAsync();
        Task shutdown;
        try
        {
            var count = jobs.EnsureWordlistStatisticsAsync(row);
            Require(row.IsCountingWordlists && !count.IsCompleted, "The shutdown fixture must have a pending wordlist count.");
            shutdown = jobs.StopAllAsync();
            await count;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Send);
            Require(!row.IsCountingWordlists && row.Record.WordlistStatistics is null && !services.IsBusyForUpdate,
                "Shutdown must cancel pending counts, leave no partial statistics, and release their operation guard.");
            Require(!shutdown.IsCompleted, "Shutdown must still await final persistence after cancelling counts.");
        }
        finally { gate.Release(); }
        await shutdown;
        Require((await store.LoadJobsAsync()).Single().Id == row.Record.Id, "Awaited shutdown must save history after pending counts end.");
        Require(await File.ReadAllTextAsync(path) == "synthetic\n", "Cancelling a count during shutdown must preserve the source file.");
    }
}
