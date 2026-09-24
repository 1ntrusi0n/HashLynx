using System.IO;
using System.Security.Cryptography;
using System.Text;
using HashLynx.Core;
using HashLynx.Hashcat;
using HashLynx.Persistence;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Smoke;

internal sealed partial class SmokeApplication
{
    private async Task CheckAutomaticDeviceLifecycleAsync(AppServices connected)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitForCancellation = false;
        var store = new PersistenceStore(new AppPaths(Path.Combine(output, "device-lifecycle-" + Guid.NewGuid().ToString("N"))));
        var selector = new HashcatDeviceSelectionService(store.Paths.CacheDirectory,
            (_, _) => Task.FromResult<IReadOnlyList<BackendDevice>>([new() { Id = 3, Name = "Synthetic CPU", Type = "CPU" }]),
            async (_, id, token) =>
            {
                entered.TrySetResult();
                if (waitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(id, HardwareCheckOutcome.Failed, "Synthetic failure", TimeSpan.Zero);
            });
        var services = new AppServices(store, backend: connected.Backend, deviceSelection: selector);
        await services.ConnectAsync(connected.RequireBackend().ExecutablePath);
        var jobs = new JobsViewModel(services);
        var target = Path.Combine(store.Paths.Root, "fixture.hashes");
        var words = Path.Combine(store.Paths.Root, "fixture.words");
        Directory.CreateDirectory(store.Paths.Root);
        await File.WriteAllTextAsync(target, "8846f7eaee8fb117ad06bdd830b7586c");
        await File.WriteAllTextAsync(words, "password\n");
        HashcatJob NewJob() => new() { TargetPath = target, HashMode = 1000, Attack = new() { Wordlists = [words] }, Options = new() { DisablePotfile = true } };
        try
        {
            try { await jobs.StartAsync(NewJob()); throw new InvalidOperationException("No working device must block launch."); }
            catch (HashcatException) { }
            Require(jobs.Selected!.Record.State == JobState.Failed && jobs.Selected.Diagnostic.Contains("No device passed", StringComparison.Ordinal)
                && jobs.Selected.Record.StartedAt is null && !services.IsComputeBusy, "Failed verification must preserve its useful diagnostic and release the compute gate without launching recovery.");

            var attack = new AttackViewModel(services, jobs, () => { });
            attack.Wordlists.Add(words); attack.Inspector.InputMode = 1; attack.Inspector.TargetPath = target; attack.Inspector.SelectedMode = new(1000, "NTLM");
            var beforeRetry = jobs.Jobs.Count;
            await ((AsyncCommand)attack.StartCommand).ExecuteAsync();
            var firstAttempt = jobs.Selected!.Record.Id;
            await ((AsyncCommand)attack.StartCommand).ExecuteAsync();
            Require(jobs.Jobs.Count == beforeRetry + 2 && jobs.Selected!.Record.Id != firstAttempt,
                "Retry after failed automatic verification must get fresh session paths rather than colliding with its previous attempt.");

            waitForCancellation = true;
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var launch = jobs.StartAsync(NewJob());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(jobs.HasRunning && jobs.Selected!.IsPreparing && jobs.StopCommand.CanExecute(null) && !jobs.DeleteCommand.CanExecute(null),
                "Pending verification must be stoppable and protected from session deletion.");
            await ((AsyncCommand)jobs.StopCommand).ExecuteAsync();
            try { await launch; throw new InvalidOperationException("Stop must cancel verification."); } catch (OperationCanceledException) { }
            Require(jobs.Selected!.Record.State == JobState.Cancelled && jobs.Selected.Record.StartedAt is null && !jobs.HasRunning && !services.IsComputeBusy,
                "Stop during verification must cancel before the attack starts and release the compute gate.");

            var queue = new RecoveryQueueViewModel(services, jobs);
            await queue.InitializeAsync();
            var template = NewJob();
            await queue.AddAsync(template, [("Sample", template.Attack)]);
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await queue.StartAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.RequestPause(); await queue.WaitForIdleAsync();
            Require(queue.Rows.Single().Step.State == RecoveryStepState.Interrupted && jobs.Selected!.Record.StartedAt is null && !services.IsComputeBusy,
                "Pausing the queue during device verification must prevent recovery launch and leave a retryable step.");

            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            launch = jobs.StartAsync(NewJob());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await jobs.StopAllAsync();
            try { await launch; throw new InvalidOperationException("Shutdown must cancel pending launch."); } catch (OperationCanceledException) { }
            Require(!jobs.HasRunning && !services.IsComputeBusy && jobs.Selected!.Record.StartedAt is null, "Shutdown must await device checks without a late attack process.");
        }
        finally { await jobs.StopAllAsync(); }
    }

    private async Task CheckLiveAutomaticDeviceAsync(AppServices connected)
    {
        var store = new PersistenceStore(new AppPaths(Path.Combine(output, "auto-recovery-" + Guid.NewGuid().ToString("N"))));
        var services = new AppServices(store, backend: connected.Backend);
        await services.ConnectAsync(connected.RequireBackend().ExecutablePath);
        var jobs = new JobsViewModel(services);
        var attack = new AttackViewModel(services, jobs, () => { });
        services.Settings.DefaultWorkloadProfile = 1;
        var words = Path.Combine(store.Paths.Root, "sample.words");
        Directory.CreateDirectory(store.Paths.Root);
        await File.WriteAllTextAsync(words, "synthetic-unused\npassword\n");
        var verifiedDevice = 0;
        try
        {
            foreach (var useMasks in new[] { false, true })
            {
                var plaintext = useMasks ? "42" : "password";
                var target = Path.Combine(store.Paths.Root, useMasks ? "mask.hashes" : "dictionary.hashes");
                await File.WriteAllTextAsync(target, Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(plaintext))).ToLowerInvariant());
                attack.Expert = false; attack.Family = useMasks ? 1 : 0;
                attack.Wordlists.Clear(); attack.Wordlists.Add(words);
                attack.Inspector.InputMode = 1; attack.Inspector.TargetPath = target; attack.Inspector.SelectedMode = new(0, "MD5");
                Require(services.Settings.DefaultDeviceIds.Count == 0 && attack.SelectedRulePreset.Id is null, "The live regression must start with empty device preferences and No Rules.");
                await ((AsyncCommand)attack.StartCommand).ExecuteAsync().WaitAsync(TimeSpan.FromMinutes(6));
                var record = jobs.Selected?.Record ?? throw new InvalidOperationException("Basic Start did not create a session: " + attack.StartFeedback);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                while (services.IsComputeBusy || record.State is JobState.Ready or JobState.Running or JobState.Paused) await Task.Delay(100, deadline.Token);
                Require(record.State == JobState.Cracked && record.ExitCode == 0 && record.Configuration.Options.Devices.Count == 1,
                    "Automatic device selection must recover the generated fixture: " + record.Diagnostic);
                if (!useMasks) verifiedDevice = record.Configuration.Options.Devices.Single();
                else Require(record.Configuration.Options.Devices.SequenceEqual([verifiedDevice]), "The unchanged device topology must reuse the verified device for the built-in mask attack.");
                var results = await services.Backend.ReadSessionResultsAsync(record.Configuration);
                Require(results.Count == 1 && results[0].Plaintext == plaintext, "Each automatic recovery must report its own verified password.");
            }
            await File.WriteAllTextAsync(Path.Combine(output, "automatic-device-result.txt"),
                $"PASS: initially empty device preferences selected device {verifiedDevice}; dictionary without rules and built-in common-1000-v1 mask recovery both verified; separate session outputs; no driver bypass flags.");
        }
        finally { await jobs.StopAllAsync(); }
    }
}
