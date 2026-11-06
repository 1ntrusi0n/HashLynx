using HashLynx.Persistence;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;
using HashLynx.Updates;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace HashLynx.UI.Smoke;

internal sealed partial class SmokeApplication
{
    private async Task CheckUpdatesAsync(ShellViewModel originalShell, AppServices originalServices, PersistenceStore originalStore, Window window)
    {
        var store = new PersistenceStore(new AppPaths(Path.Combine(originalStore.Paths.Root, "update-ui")));
        await store.SaveSettingsAsync(new AppSettings { HashcatDirectory = Path.Combine(store.Paths.Root, "missing-backend") });
        var fake = new FakeApplicationUpdates();
        var services = new AppServices(store, updates: fake);
        var shell = new ShellViewModel(services);
        await shell.InitializeAsync();
        Require(fake.Checks == 0, "Startup must not check for updates unless enabled.");
        Require(!shell.Settings.CheckForUpdatesAutomatically && !shell.Attack.Expert, "New users start in Beginner with update checks disabled.");
        Require(shell.Settings.Version == "HashLynx " + ApplicationInfo.Version, "About uses the actual assembly version.");
        Require(shell.Settings.Changelog.Contains(ApplicationInfo.Version, StringComparison.Ordinal), "The offline changelog includes this release.");
        window.DataContext = shell;
        try
        {
            shell.ShowSettings();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(Find<TextBlock>(window, "CreatorCreditText").Text == "Created by 1ntrusi0n", "About shows the creator credit.");
            Require(Equals(Find<CheckBox>(window, "AutoUpdatesCheckBox").Content, "Check for Updates Automatically"), "The automatic check preference is visible.");
            Find<Expander>(window, "ChangelogExpander").IsExpanded = true;
            ScrollToSettingsField(Find<TextBlock>(window, "AboutVersionText"));
            await RenderAsync(window, "About-and-changelog");
            window.Width = 1040; window.Height = 700;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            ScrollToSettingsField(Find<Expander>(window, "ChangelogExpander"));
            await RenderAsync(window, "Changelog-narrow");
            window.Width = 1380; window.Height = 920;
            await ((AsyncCommand)shell.Settings.Updates.CheckCommand).ExecuteAsync();
            Require(fake.Checks == 1 && shell.Settings.Updates.HasRelease, "Manual checks display newer releases.");
            Require(fake.Downloads == 0, "Checking must not download a release.");
            Require(shell.Settings.Updates.ReleaseNotes.Contains("Fixture release", StringComparison.Ordinal), "Release notes are shown.");
            await CheckMarkdownAsync(shell, window);
            shell.Settings.CheckForUpdatesAutomatically = true;
            shell.Settings.Expert = true;
            await ((AsyncCommand)shell.Settings.SaveCommand).ExecuteAsync();
            await shell.Settings.Updates.CheckAutomaticallyAsync();
            Require(fake.Checks == 1, "An automatic check within 24 hours is skipped.");
            Require(!shell.Attack.Expert, "Saving the startup Expert preference does not change the current attack.");
            Require((await store.LoadSettingsAsync()).CheckForUpdatesAutomatically, "The opt-in checkbox persists.");
            var settingsPath = Path.Combine(store.Paths.Root, "settings.json");
            var settingsBackup = Path.Combine(store.Paths.Root, "settings-save-test-backup.json");
            File.Move(settingsPath, settingsBackup);
            Directory.CreateDirectory(settingsPath);
            try
            {
                shell.Settings.Expert = false;
                shell.Settings.CheckForUpdatesAutomatically = false;
                await ((AsyncCommand)shell.Settings.SaveCommand).ExecuteAsync();
                Require(services.Settings.ExpertMode && services.Settings.CheckForUpdatesAutomatically,
                    "A failed settings save must not change the in-memory startup or automatic-update preferences.");
            }
            finally
            {
                Directory.Delete(settingsPath);
                File.Move(settingsBackup, settingsPath);
                shell.Settings.Load();
            }
            var preservedSettings = await store.LoadSettingsAsync();
            Require(preservedSettings.ExpertMode && preservedSettings.CheckForUpdatesAutomatically,
                "A failed save must retain the previously persisted settings.");
            services.Notice = "Settings save failure was handled; saved preferences were preserved.";
            await ((AsyncCommand)shell.Settings.Updates.DownloadCommand).ExecuteAsync();
            Require(fake.Downloads == 1 && shell.Settings.Updates.IsReadyToInstall, "Downloading explicitly stages an update without installing it.");
            Require(!services.IsUpdatePending, "Downloading must not begin application shutdown.");
            using (services.BeginOperation())
            {
                Require(!services.TryBeginUpdate(), "Updates must wait for asynchronous preparation/extraction.");
                Require(!shell.Settings.Updates.RestartCommand.CanExecute(null), "Restart is unavailable during preparation.");
                await Task.Yield();
                Require(!services.TryBeginUpdate(), "The operation lease survives asynchronous continuations.");
            }
            Require(services.TryBeginComputeOperation(), "An idle compute check can start.");
            Require(!services.TryBeginUpdate(), "Updates must wait for compute checks.");
            services.EndComputeOperation();
            services.IsQueueActive = true;
            Require(!services.TryBeginUpdate(), "Updates must wait for queues.");
            services.IsQueueActive = false;
            Require(services.TryBeginUpdate(), "An idle application can begin update preparation.");
            Require(!services.TryBeginComputeOperation(), "New compute work cannot race update preparation.");
            var operationRejected = false;
            try { using var ignored = services.BeginOperation(); }
            catch (OperationCanceledException) { operationRejected = true; }
            Require(operationRejected, "New extraction/preparation cannot race update shutdown.");
            var queueRejected = false;
            try { services.IsQueueActive = true; }
            catch (OperationCanceledException) { queueRejected = true; }
            Require(queueRejected, "A queue cannot begin while update shutdown is pending.");
            services.CancelUpdatePreparation();
            Require(services.CanStartWork, "A cancelled update handoff leaves normal work available.");
            Find<Expander>(VisualTreeHelper.GetParent(Find<TextBlock>(window, "UpdateStatusText")), "").IsExpanded = true;
            ScrollToSettingsField(Find<CheckBox>(window, "AutoUpdatesCheckBox"));
            await RenderAsync(window, "Updates-available");
            window.Width = 1040; window.Height = 700;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            ScrollToSettingsField(Find<CheckBox>(window, "AutoUpdatesCheckBox"));
            await RenderAsync(window, "Updates-available-narrow");
            window.Width = 1380; window.Height = 920;
            fake.Failure = new System.Net.Http.HttpRequestException("fixture offline");
            await ((AsyncCommand)shell.Settings.Updates.CheckCommand).ExecuteAsync();
            Require(shell.Settings.Updates.Status.Contains("connection", StringComparison.OrdinalIgnoreCase), "Offline checks offer actionable status.");
            Require(services.CanStartWork && !services.IsBusyForUpdate, "A failed update check does not block recovery.");
            var restarted = new ShellViewModel(new AppServices(store, updates: fake));
            await restarted.InitializeAsync();
            Require(restarted.Attack.Expert, "The saved Expert preference takes effect on restart.");
            Require(fake.Checks == 2, "Restart within the daily interval does not make another check.");
        }
        finally { window.DataContext = originalShell; window.Width = 1380; window.Height = 920; }

        void ScrollToSettingsField(FrameworkElement field)
        {
            window.UpdateLayout();
            for (DependencyObject? ancestor = field; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            {
                if (ancestor is not ScrollViewer scroll) continue;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + field.TranslatePoint(new Point(0, 0), scroll).Y - 75);
                return;
            }
            throw new InvalidOperationException("Settings content must remain inside a scrolling page.");
        }
    }

    private sealed class FakeApplicationUpdates : IApplicationUpdateService
    {
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public Exception? Failure { get; set; }
        public Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Checks++;
            if (Failure is not null) return Task.FromException<UpdateCheckResult>(Failure);
            return Task.FromResult(new UpdateCheckResult(new("0.3.0", "# Fixture release\n\nNew **features** and fixes.\n\n- Formatted release notes\n- Readable recovery improvements",
                new Uri("https://github.com/1ntrusi0n/HashLynx/releases/tag/v0.3.0"),
                new("HashLynx-win-x64.zip", new Uri("https://github.com/1ntrusi0n/HashLynx/releases/download/v0.3.0/HashLynx-win-x64.zip"), 12345, new string('a', 64))), "A newer release is available."));
        }
        public Task<StagedApplicationUpdate> DownloadAsync(UpdateRelease release, string installationDirectory, string stagingRoot,
            IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Downloads++;
            return Task.FromResult(new StagedApplicationUpdate(stagingRoot, Path.Combine(stagingRoot, "fixture.exe"), Path.Combine(stagingRoot, "plan.json"), release.Version));
        }
    }
}
