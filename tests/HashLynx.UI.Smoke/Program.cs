using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HashLynx.Persistence;
using HashLynx.Core;
using HashLynx.Drives;
using HashLynx.Extractors;
using HashLynx.Hashcat;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Smoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-smoke");
        Directory.CreateDirectory(output);
        var application = new SmokeApplication(output);
        application.Resources = (ResourceDictionary)Application.LoadComponent(new Uri("/HashLynx;component/Themes/AppResources.xaml", UriKind.Relative));
        return application.Run();
    }
}

/// <summary>Loads the real app resources and every real page in an isolated local data directory.</summary>
internal sealed partial class SmokeApplication(string output) : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Intentionally bypass production composition; the tested views and services are unchanged.
        var errors = new StringBuilder();
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            var detail = eventArgs.Exception + Environment.NewLine + errors;
            File.WriteAllText(Path.Combine(output, "failure.txt"), detail);
            Console.Error.WriteLine(detail);
            Shutdown(1);
        };
        using var listener = new TextWriterTraceListener(new StringWriter(errors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            var store = new PersistenceStore(new AppPaths(Path.Combine(output, "data-" + Guid.NewGuid().ToString("N"))));
            await store.SaveSettingsAsync(new AppSettings { HashcatDirectory = Path.Combine(output, "deliberately-missing-backend") });
            var driveService = new FakeDriveService();
            var services = new AppServices(store, bitLockerDrives: driveService);
            var shell = new ShellViewModel(services);
            var window = new MainWindow { DataContext = shell, Width = 1380, Height = 920, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
            MainWindow = window;
            window.Show();
            await shell.InitializeAsync();
            Require(services.Installation is null, "An absent backend must not be treated as connected.");
            Require(shell.Selected.Page == shell.Settings, "Missing backend must open Settings.");
            Require(services.Notice.Contains("Settings", StringComparison.Ordinal), "Missing backend must explain setup.");

            foreach (var page in shell.Navigation)
            {
                shell.Selected = page;
                await RenderAsync(window, page.Name.Replace(" / ", "-"));
            }

            await CheckSimpleWorkflowAsync(shell, store, window);
            await CheckUsabilityAsync(shell, services, store, window);
            await CheckMaskSourcesAsync(shell, services, store, window);
            await CheckWordlistLibraryAsync(shell, services, store, window);
            await CheckHintsAndQueueAsync(shell, store, window);
            await CheckWordlistManagerAsync(shell, store, window);
            await CheckDefaultDeviceAsync(shell, store, window);
            await CheckManualCatalogAsync(shell, window, true);
            await CheckPopulatedJobsAsync(shell, window);
            await CheckSessionHistoryAsync(shell, store, window);
            await CheckSessionResultsIsolationAsync(shell, store, window);
            await CheckZipConfigurationAsync(shell, window);
            await CheckBitLockerDriveAsync(shell, driveService, window);

            shell.Selected = shell.Navigation[0];
            for (var family = 0; family < 4; family++)
            {
                shell.Attack.Family = family;
                shell.Attack.Expert = true;
                await RenderAsync(window, "Attack-family-" + family);
            }
            for (var input = 0; input < 4; input++)
            {
                shell.Attack.Inspector.InputMode = input;
                await RenderAsync(window, "Inspector-input-" + input);
            }

            await ((AsyncCommand)shell.Attack.PreflightCommand).ExecuteAsync(null);
            Require(services.Notice.Contains("Hashcat", StringComparison.Ordinal), "Preflight without backend must explain configuration.");
            var backendPath = Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT");
            if (File.Exists(backendPath)) await CheckConnectedWorkflowAsync(shell, services, window, backendPath);
            if (File.Exists(backendPath)) await CheckAutomaticDeviceLifecycleAsync(services);
            if (File.Exists(backendPath)) await CheckNativeZipInspectorAsync(shell, services, window);
            var recoveryDevice = Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE");
            if (File.Exists(backendPath) && !string.IsNullOrWhiteSpace(recoveryDevice)) await CheckRecoveryWorkflowAsync(shell, services, window, recoveryDevice);
            if (File.Exists(backendPath) && !string.IsNullOrWhiteSpace(recoveryDevice)) await CheckLiveQueueAsync(shell, services, window, recoveryDevice);
            if (File.Exists(backendPath) && Environment.GetEnvironmentVariable("HASHLYNX_TEST_AUTO_DEVICE") == "1") await CheckLiveAutomaticDeviceAsync(services);
            foreach (var theme in new[] { "Light", "Dark", "System" })
            {
                AppServices.ApplyTheme(theme);
                window.Width = 1040; window.Height = 700;
                await RenderAsync(window, "Theme-" + theme);
            }
            listener.Flush();
            Require(errors.Length == 0, "WPF binding failures: " + errors);
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "PASS: missing-backend startup; all navigation pages; four attack families; four target modes; three themes; narrow layout; Basic preset/defaults; Expert custom rules; readiness guidance and red/amber/green states in Light/Dark themes; mask source selection and saved queue preset identity; starter wordlist; saved wordlist import, restart, selection, Combinator/Hybrid, deduplication, missing files, safe removal and failed/corrupt save handling; populated manual catalog expansion, selection and scrolling; populated running/failed Jobs with progress updates; inactive history deletion, Undo, file retention and save-failure rollback; saved Hardware default, Basic selection, Expert overrides, restart persistence; preflight error handling; drive selection, extraction, cancellation, stale-result rejection and removal; native formats hidden from settings, legacy native overrides ignored, including PDF; hints preview and invalid input handling; saved queue pause, failure, retry, recovery skipping and shutdown; completion banners; wordlist manager metadata, names, checkbox binding and failed-save recovery; zero binding errors." + (File.Exists(backendPath) ? " Installed backend: automatic identification blocks ambiguous Start, mode selection, catalog search, preset command preflight, automatic result loading, View recovered passwords navigation/reveal, stale-result clearing, native ZIP/RAR/7z/BitLocker/PDF extraction, confirmed automatic mode selection, and persistent extraction notes passed." : "") + (File.Exists(backendPath) && !string.IsNullOrWhiteSpace(recoveryDevice) ? " Real recovery: Start → Jobs → Results recovered the known NTLM fixture in Basic mode with the starter list, Normal preset, and saved Hardware default." : ""));
            Console.WriteLine("WPF smoke passed. Screenshots and report: " + output);
            Shutdown(0);
        }
        catch (Exception exception)
        {
            var detail = exception.ToString() + Environment.NewLine + errors;
            await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), detail);
            Console.Error.WriteLine(detail);
            Shutdown(1);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    }

    private async Task CheckWordlistLibraryAsync(ShellViewModel shell, AppServices services, PersistenceStore store, Window window)
    {
        var attack = shell.Attack;
        var first = Path.Combine(store.Paths.Root, "first words-é.txt");
        var second = Path.Combine(store.Paths.Root, "second words.txt");
        await File.WriteAllTextAsync(first, "synthetic-one");
        await File.WriteAllTextAsync(second, "synthetic-two");
        attack.Expert = false;
        await ((AsyncCommand)attack.DropWordlistsCommand).ExecuteAsync(new[] { first, second, first.ToUpperInvariant() });
        Require(attack.SavedWordlists.Count == 2 && attack.Wordlists.SequenceEqual(new[] { first }), "Basic import must remember all paths without running every list or creating duplicates.");
        Require((await store.LoadWordlistLibraryAsync()).Paths.SequenceEqual(new[] { first, second }), "Wordlists must be saved immediately.");
        shell.Selected = shell.Navigation[0];
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var picker = Find<ComboBox>(window, "SavedWordlistPicker");
        picker.SelectedItem = attack.SavedWordlistEntries.Single(entry => entry.Path == second);
        Require(attack.Wordlists.SequenceEqual(new[] { second }), "Choosing a saved list in the real dropdown must change the Basic attack.");
        Require(BuildDraft(attack).Attack.Wordlists.SequenceEqual(new[] { second }), "The attack builder must receive the selected saved path, not every library entry.");
        var scroll = Find<ScrollViewer>(window, "AttackScrollViewer");
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + picker.TranslatePoint(new Point(0, 0), scroll).Y - 100);
        await RenderAsync(window, "Saved-wordlists");
        var restarted = new AttackViewModel(new AppServices(store), shell.Jobs, () => { });
        await restarted.InitializeAsync();
        Require(restarted.SavedWordlists.SequenceEqual(attack.SavedWordlists), "The saved library must survive a new view model and store read.");
        restarted.SelectedSavedWordlist = first;
        Require(restarted.Wordlists.SequenceEqual(new[] { first }), "A reloaded entry must select the original path.");
        attack.Family = 3;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Find<ComboBox>(window, "SavedLeftWordlistPicker").SelectedItem = first;
        Find<ComboBox>(window, "SavedRightWordlistPicker").SelectedItem = second;
        Require(attack.LeftWordlist == first && attack.RightWordlist == second, "Combinator must use saved paths independently on each side.");
        attack.Family = 2;
        picker.SelectedItem = attack.SavedWordlistEntries.Single(entry => entry.Path == first);
        Require(attack.Wordlists.SequenceEqual(new[] { first }), "Hybrid must use the same saved library.");
        attack.Family = 0;
        picker.SelectedItem = attack.SavedWordlistEntries.Single(entry => entry.Path == second);
        attack.Expert = true;
        picker.SelectedItem = attack.SavedWordlistEntries.Single(entry => entry.Path == first);
        Require(attack.Wordlists.SequenceEqual(new[] { second, first }), "Expert selection must append to the ordered attack list.");
        attack.SelectedWordlist = second;
        attack.RemoveWordlistCommand.Execute(null);
        Require(attack.SavedWordlists.Count == 2 && attack.Wordlists.SequenceEqual(new[] { first }), "Removing an attack input must keep the saved entry.");

        // Failed saves must not falsely update the library or the attack selection.
        var libraryFile = Path.Combine(store.Paths.Root, "wordlists.json");
        var backup = libraryFile + ".smoke-backup";
        File.Move(libraryFile, backup);
        Directory.CreateDirectory(libraryFile);
        try
        {
            await ((AsyncCommand)attack.ForgetWordlistCommand).ExecuteAsync(null);
            Require(attack.SavedWordlists.Count == 2, "A failed removal save must preserve the library entry.");
            var third = Path.Combine(store.Paths.Root, "third.words");
            await File.WriteAllTextAsync(third, "synthetic-three");
            await ((AsyncCommand)attack.DropWordlistsCommand).ExecuteAsync(new[] { third });
            Require(attack.SavedWordlists.Count == 2 && attack.Wordlists.SequenceEqual(new[] { first }), "A failed import save must preserve library and current attack.");
        }
        finally { Directory.Delete(libraryFile); File.Move(backup, libraryFile); }
        await ((AsyncCommand)attack.ForgetWordlistCommand).ExecuteAsync(null);
        Require(attack.SavedWordlists.SequenceEqual(new[] { second }) && File.Exists(first) && attack.Wordlists.SequenceEqual(new[] { first }), "Forgetting must keep the source file and current attack.");
        File.Move(second, second + ".moved");
        attack.Expert = false;
        picker.SelectedItem = attack.SavedWordlistEntries.Single(entry => entry.Path == second);
        Require(attack.Wordlists.SequenceEqual(new[] { second }) && attack.WordlistSummary.Contains("missing"), "A missing saved file must be identified without silently using a different list.");
        Require((await store.LoadWordlistLibraryAsync()).Paths.SequenceEqual(new[] { second }), "Missing wordlists must remain in the library.");
        window.Width = 1040; window.Height = 700;
        window.UpdateLayout();
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + picker.TranslatePoint(new Point(0, 0), scroll).Y - 50);
        await RenderAsync(window, "Saved-wordlists-missing-narrow");
        window.Width = 1380; window.Height = 920;
        await ((AsyncCommand)attack.ForgetWordlistCommand).ExecuteAsync(null);
        await File.WriteAllTextAsync(libraryFile, "{broken");
        var corrupt = new AttackViewModel(new AppServices(store), shell.Jobs, () => { });
        await corrupt.InitializeAsync();
        await ((AsyncCommand)corrupt.DropWordlistsCommand).ExecuteAsync(new[] { first });
        Require(await File.ReadAllTextAsync(libraryFile) == "{broken" && corrupt.SavedWordlists.Count == 0, "A corrupt library must not be overwritten by an import.");
        await store.SaveWordlistLibraryAsync(new WordlistLibrary());
        await ((AsyncCommand)attack.UseStarterCommand).ExecuteAsync(null);
        scroll.ScrollToTop();
        services.Notice = "Saved wordlist library checks passed.";
    }

    private async Task CheckBitLockerDriveAsync(ShellViewModel shell, FakeDriveService drives, Window window)
    {
        shell.Selected = shell.Navigation[0];
        var inspector = shell.Attack.Inspector;
        inspector.InputMode = 3;
        await inspector.RefreshDrivesAsync();
        Require(inspector.Drives.Count == 2 && inspector.SelectedDrive is null, "Drive selection must be explicit.");
        inspector.SelectedDrive = inspector.Drives[0];
        var target = await inspector.PrepareTargetAsync();
        Require(await File.ReadAllTextAsync(target) == BitLockerFixtureText + Environment.NewLine, "Drive hashes must enter the normal target workflow.");
        Require(inspector.ExtractionNotice.Contains("Synthetic"), "Drive extraction notes must remain visible.");
        Require(await inspector.PrepareTargetAsync() == target && drives.Extractions == 1, "Prepared targets must not trigger repeated elevation.");
        await RenderAsync(window, "BitLocker-drive-extracted");
        inspector.SelectedDrive = inspector.Drives[1];
        Require(inspector.PreparedTargetPath is null && inspector.SelectedMode is null && inspector.ExtractionNotice == "", "Changing volumes must clear the old target.");
        drives.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = inspector.PrepareTargetAsync();
        Require(inspector.ReadingDrive, "The pending read must expose cancellation.");
        inspector.CancelDriveCommand.Execute(null);
        try { await pending; throw new InvalidOperationException("Expected cancelled drive read."); } catch (OperationCanceledException) { }
        Require(inspector.PreparedTargetPath is null && !inspector.ReadingDrive, "Cancellation must not retain a target or busy state.");
        drives.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = inspector.PrepareTargetAsync();
        inspector.InputMode = 0;
        try { await pending; throw new InvalidOperationException("Expected cancellation after target change."); } catch (OperationCanceledException) { }
        Require(inspector.PreparedTargetPath is null, "Late drive results must not replace a new target.");
        drives.Pending = null; drives.Error = "No supported password protector.";
        inspector.InputMode = 3;
        try { await inspector.PrepareTargetAsync(); throw new InvalidOperationException("Expected an unsupported protector error."); }
        catch (InvalidOperationException ex) when (ex.Message == drives.Error) { }
        Require(inspector.DriveStatus == drives.Error && inspector.PreparedTargetPath is null, "Unsupported protectors must leave no attack target.");
        drives.Items = [];
        await inspector.RefreshDrivesAsync();
        Require(inspector.SelectedDrive is null && inspector.Drives.Count == 0, "Removing a device must clear its selection.");
        await RenderAsync(window, "BitLocker-drive-removed");
        inspector.InputMode = 0;
    }
    private static string BitLockerFixtureText => HashLynx.Extractors.Tests.BitLockerFixture.Hash;
    private sealed class FakeDriveService : IBitLockerDriveService
    {
        public IReadOnlyList<DriveCandidate> Items = [new(@"\\?\Volume{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}\", @"E:\", "Synthetic test USB", 16_000_000_000, "Removable"), new(@"\\?\Volume{bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb}\", @"F:\", "Other test volume", 32_000_000_000, "Fixed")];
        public int Extractions;
        public string? Error;
        public TaskCompletionSource<DriveReadResponse>? Pending;
        public Task<IReadOnlyList<DriveCandidate>> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(Items);
        public Task<DriveReadResponse> ExtractAsync(DriveCandidate candidate, CancellationToken ct = default)
        {
            Extractions++;
            if (Pending is not null) return Pending.Task.WaitAsync(ct);
            return Task.FromResult(Error is not null ? new DriveReadResponse(null, Error) : new(new ExtractionResult { Success = true, Hashes = [BitLockerFixtureText], SuggestedHashcatModes = [22100], Diagnostics = ["Synthetic drive metadata only."] }, null, 4096));
        }
    }

    private async Task CheckZipConfigurationAsync(ShellViewModel shell, Window window)
    {
        var page = shell.Extractors;
        Require(!shell.Navigation.Any(item => item.Page is ExtractorsViewModel), "No extractor settings page is needed when all formats are built in.");
        foreach (var id in new[] { "zip", "rar", "7z", "bitlocker", "pdf" })
            shell.Services.Settings.ExtractorTools[id] = new ExtractorToolSettings { ToolPath = Path.Combine(output, "missing-legacy-" + id + ".exe") };
        await shell.Services.Store.SaveSettingsAsync(shell.Services.Settings);
        await page.RefreshAsync();
        Require(page.Extractors.Count == 0, "Built-in formats must not have configuration cards.");
        foreach (var extractor in shell.Services.Extractors.All)
            Require(extractor.IsBuiltIn && (await extractor.ValidateAsync()).IsAvailable, "Hidden legacy paths must not override built-in extraction.");
        Require((await shell.Services.Store.LoadSettingsAsync()).ExtractorTools.ContainsKey("pdf"), "Legacy PDF settings must be preserved on disk.");
        await RenderAsync(window, "Native-extractors-no-configuration");
    }

    private async Task CheckNativeZipInspectorAsync(ShellViewModel shell, AppServices services, Window window)
    {
        // Structural fixture only: dummy encrypted bytes are identified, never attacked.
        // Independently generated real encrypted ZIP recovery is covered by the opt-in integration test.
        using var memory = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
        using (var entry = archive.CreateEntry("synthetic.txt", System.IO.Compression.CompressionLevel.NoCompression).Open())
            entry.Write(new byte[16]);
        var bytes = memory.ToArray();
        var directory = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6, 4));
        bytes[6] |= 1; bytes[directory + 8] |= 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22, 4), 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 24, 4), 4);
        var path = Path.Combine(services.Store.Paths.Root, "synthetic-structure.zip");
        await File.WriteAllBytesAsync(path, bytes);
        var inspector = shell.Attack.Inspector;
        inspector.InputMode = 2; inspector.TargetPath = path;
        await inspector.AnalyzeAsync();
        Require(inspector.SelectedMode?.Mode == 17210, "ZIP metadata plus Hashcat identification must automatically select the single-member stored mode.");
        Require(inspector.ExtractionNotice.Contains("different passwords", StringComparison.Ordinal), "The selected-member limitation must survive target analysis.");
        shell.Selected = shell.Navigation[0];
        await RenderAsync(window, "Native-ZIP-analysis");
        Require(Find<TextBlock>(window, "ExtractionNotice").IsVisible, "Extraction notes must be visible without opening the problem-lines expander.");
        foreach (var (extension, fixture, mode) in new[]
        {
            ("7z", HashLynx.Extractors.Tests.NativeArchiveFixtures.Solid, 11600),
            ("rar", Convert.FromBase64String("UmFyIRoHAM+QcwAADQAAAAAAAABz53QEhCkAEAAAAA4AAAACSbCoRgAAAAAdMAEAIAAAAGHlSnNymIfLUzRiC8yoF2ZCohCxBRkBkh4EsHsAAAcA"), 23700),
            ("raw", HashLynx.Extractors.Tests.BitLockerFixture.Image(toGo: true), 22100),
            ("pdf", await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pdf", "r6.pdf")), 10700)
        })
        {
            var archivePath = Path.Combine(services.Store.Paths.Root, "known-test-archive." + extension);
            await File.WriteAllBytesAsync(archivePath, fixture);
            inspector.TargetPath = archivePath;
            await inspector.AnalyzeAsync();
            Require(inspector.SelectedMode?.Mode == mode, $"Native {extension} must select a Hashcat-confirmed recovery mode.");
            await RenderAsync(window, "Native-" + extension + "-analysis");
            Require(Find<TextBlock>(window, "ExtractionNotice").IsVisible, "The archive selection note must remain visible.");
        }
        inspector.InputMode = 0; inspector.HashText = "new target";
        Require(inspector.ExtractionNotice == "" && inspector.SelectedMode is null, "Changing targets must clear ZIP extraction notes and the selected mode.");
    }

    private async Task CheckSimpleWorkflowAsync(ShellViewModel shell, PersistenceStore store, Window window)
    {
        var attack = shell.Attack;
        shell.Selected = shell.Navigation[0];
        Require(!attack.Expert, "A new workspace must begin in Basic mode.");
        Require(attack.RulePresets.Select(preset => preset.DisplayName).SequenceEqual(new[] { "No Rules", "Quick", "Normal", "Heavy", "Super" }), "The dropdown must offer No Rules followed by the four named presets.");
        Require(attack.SelectedRulePreset.Id is null && attack.SelectedRulePreset.DisplayName == "No Rules", "No Rules must be the default selection.");
        Require(attack.Wordlists.Count == 1 && File.Exists(attack.Wordlists[0]), "A working bundled starter wordlist must be preselected.");
        Require((await File.ReadAllLinesAsync(attack.Wordlists[0])).Length == 848, "The app must ship the authored starter list.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Require(!Find<FrameworkElement>(window, "RunOptionsPanel").IsVisible, "Basic mode must hide run options.");
        Require(!Find<FrameworkElement>(window, "ExpertPreflightPanel").IsVisible, "Basic mode must hide the separate preflight controls.");
        Require(!Find<FrameworkElement>(window, "CustomRulesPanel").IsVisible, "Basic mode must hide custom rule-file controls.");
        Require(Find<FrameworkElement>(window, "StartRecoveryButton").IsVisible, "Basic mode must retain Start recovery.");
        Require(Find<FrameworkElement>(window, "RulesPresetPicker").IsVisible, "Basic mode must show the preset dropdown.");
        var selectionDisplay = Find<ContentPresenter>(Find<ComboBox>(window, "RulesPresetPicker"), "");
        Require(Find<TextBlock>(selectionDisplay, "").Text == "No Rules", "The selected dropdown label must display No Rules, without model details.");
        var scroll = Find<ScrollViewer>(window, "AttackScrollViewer");
        scroll.ScrollToEnd();
        await RenderAsync(window, "Basic-dictionary-presets");
        attack.ToggleRulesHelpCommand.Execute(null);
        Require(attack.RulesHelpVisible, "Question-mark help must open.");
        await RenderAsync(window, "Rule-help");
        window.Width = 1040; window.Height = 700;
        await RenderAsync(window, "Rule-help-narrow");
        window.Width = 1380; window.Height = 920;
        attack.ToggleRulesHelpCommand.Execute(null);

        // Expert leftovers must not secretly affect a later Basic attack.
        attack.Expert = true;
        attack.UseCustomRules = true;
        attack.Rules.Add(Path.Combine(store.Paths.Root, "not-installed.rule"));
        attack.Devices = "not a device";
        attack.Temperature = "not a temperature";
        attack.OptimizedKernel = true;
        attack.DisablePotfile = true;
        attack.LeftRule = "u";
        attack.ExtraArguments = "--force";
        attack.Session = "invalid / expert session";
        attack.Expert = false;
        var saved = BuildDraft(attack);
        Require(saved.Attack.RulePresetId is null && saved.Attack.RuleFiles.Count == 0, "Basic No Rules must use no preset and no hidden custom rule paths.");
        Require(saved.Attack.LeftRule is null && saved.Options.Devices.Count == 0 && saved.Options.TemperatureAbort is null, "Basic mode must use automatic controls.");
        Require(!saved.Options.OptimizedKernel && !saved.Options.DisablePotfile && saved.Options.ExtraArguments.Count == 0, "Hidden Expert options must not apply to Basic mode.");
        attack.SelectedRulePreset = attack.RulePresets.Single(preset => preset.Id == RulePresetCatalog.HeavyId);
        Require(BuildDraft(attack).Attack.RulePresetId == RulePresetCatalog.HeavyId && BuildDraft(attack).Attack.RuleFiles.Count == 0,
            "An explicitly selected Basic preset must replace hidden custom rules in the draft.");
        attack.SelectedRulePreset = attack.RulePresets[0];
        Require(BuildDraft(attack).Attack.RulePresetId is null, "Choosing No Rules must clear the previously selected preset from the draft.");
        attack.Expert = true;
        attack.Devices = ""; attack.Temperature = ""; attack.Session = "hashlynx-smoke";
        Require(BuildDraft(attack).Attack.RuleFiles.SequenceEqual(attack.Rules), "Expert mode must use the selected custom rule files.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(Find<FrameworkElement>(window, "RunOptionsPanel").IsVisible && Find<FrameworkElement>(window, "CustomRulesPanel").IsVisible, "Expert panels must become visible.");
        await RenderAsync(window, "Expert-custom-rules");
        attack.Expert = false;
        attack.UseCustomRules = false;
        attack.Rules.Clear();
        attack.Devices = ""; attack.Temperature = ""; attack.ExtraArguments = ""; attack.LeftRule = "";
        attack.OptimizedKernel = false; attack.DisablePotfile = false;
        attack.Session = "hashlynx-smoke";
        await ((AsyncCommand)attack.UseStarterCommand).ExecuteAsync(null);
        scroll.ScrollToTop();
    }

    private async Task CheckConnectedWorkflowAsync(ShellViewModel shell, AppServices services, Window window, string executable)
    {
        await services.ConnectAsync(executable);
        await shell.Attack.Inspector.LoadCatalogAsync();
        await CheckManualCatalogAsync(shell, window, false);
        var plaintext = "HashLynx-ui-synthetic-" + Guid.NewGuid().ToString("N");
        var digest = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(plaintext)));
        var inspector = shell.Attack.Inspector;
        inspector.InputMode = 0;
        inspector.HashText = digest;
        shell.Attack.Expert = false;
        shell.Attack.Family = 0;
        await ((AsyncCommand)shell.Attack.UseStarterCommand).ExecuteAsync(null);
        var jobsBefore = shell.Jobs.Jobs.Count;
        await ((AsyncCommand)shell.Attack.StartCommand).ExecuteAsync(null);
        Require(inspector.Matches.Count > 1 && inspector.SelectedMode is null, "Ambiguous targets must require an explicit mode choice.");
        Require(shell.Jobs.Jobs.Count == jobsBefore, "Automatic Start identification must not launch an ambiguous target.");
        Require(shell.Attack.StartFeedback.Contains("hash mode", StringComparison.OrdinalIgnoreCase), "Automatic validation errors must remain visible in Basic mode.");
        inspector.SelectedMatch = inspector.Matches.Single(mode => mode.Mode == 0);
        Require(inspector.SelectedMode?.Mode == 0, "Selecting an identification result must set the attack mode.");
        await ((AsyncCommand)shell.Attack.PreflightCommand).ExecuteAsync(null);
        Require(shell.Attack.Preflight.StartsWith("Ready to start", StringComparison.Ordinal), "Basic No Rules must pass automatic validation: " + shell.Attack.Preflight);
        Require(!shell.Attack.Preview.Contains("--rules-file", StringComparison.Ordinal), "No Rules must omit rule-file arguments from the Hashcat command.");
        shell.Attack.SelectedRulePreset = shell.Attack.RulePresets.Single(preset => preset.Id == RulePresetCatalog.NormalId);
        await ((AsyncCommand)shell.Attack.PreflightCommand).ExecuteAsync(null);
        Require(shell.Attack.Preflight.StartsWith("Ready to start", StringComparison.Ordinal), "An explicitly selected preset must pass validation.");
        Require(shell.Attack.Preview.Contains(RulePresetCatalog.NormalId + ".rule", StringComparison.Ordinal), "The built-in preset must be present in the executed command configuration.");
        shell.Attack.SelectedRulePreset = shell.Attack.RulePresets[0];
        await ((AsyncCommand)shell.Attack.PreflightCommand).ExecuteAsync(null);
        Require(!shell.Attack.Preview.Contains("--rules-file", StringComparison.Ordinal), "Switching back to No Rules must remove the selected preset from the command.");
        shell.Selected = shell.Navigation[0];
        shell.Attack.Family = 1;
        shell.Attack.SelectedMaskSource = MaskSourceChoice.Text;
        shell.Attack.Mask = "?d?d?d?d";
        await ((AsyncCommand)shell.Attack.PreflightCommand).ExecuteAsync(null);
        Require(shell.Attack.Preflight.StartsWith("Ready to start", StringComparison.Ordinal), "Synthetic mask configuration must pass preflight: " + shell.Attack.Preflight);
        Require(shell.Attack.Preview.Contains("--hash-type 0", StringComparison.Ordinal), "Command preview must contain selected mode.");
        await RenderAsync(window, "Connected-Attack");

        var potfile = Path.Combine(services.Store.Paths.ResultsDirectory, "synthetic.potfile");
        await File.WriteAllTextAsync(potfile, digest + ":" + plaintext + "\n");
        var record = new JobRecord
        {
            Name = "Synthetic result fixture", State = JobState.Cracked,
            Configuration = new HashcatJob { TargetPath = inspector.PreparedTargetPath!, HashMode = 0, Options = new CommonOptions { PotfilePath = potfile } }
        };
        var row = new JobViewModel(record);
        var sessionOutput = services.Backend.Commands.GetOutputPath(record.Configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(sessionOutput)!);
        await File.WriteAllTextAsync(sessionOutput, digest + ":" + Convert.ToHexString(Encoding.UTF8.GetBytes(plaintext)) + "\n");
        shell.Jobs.Jobs.Add(row);
        var results = (ResultsViewModel)shell.Navigation.Single(page => page.Page is ResultsViewModel).Page;
        results.SelectedJob = row;
        await results.Loading;
        Require(results.Results.Count == 1, "The Results page must load a synthetic recovered result.");
        Require(results.Results[0].Plaintext != plaintext, "Plaintext must initially be hidden.");
        shell.Jobs.Selected = row;
        shell.Selected = shell.Navigation.Single(page => page.Page == shell.Jobs);
        await RenderAsync(window, "Recovered-session-actions");
        var viewResults = Find<Button>(window, "ViewRecoveredPasswordsButton");
        Require(viewResults.Command == shell.Jobs.ViewResultsCommand && viewResults.IsEnabled, "A completed job must offer the View recovered passwords action.");
        await ((AsyncCommand)viewResults.Command!).ExecuteAsync(null);
        Require(shell.Selected.Page == results && results.SelectedJob == row && results.Results.Single().Hash == digest && results.Results[0].Plaintext == plaintext, "View recovered passwords must navigate, load matching hashes, and reveal their passwords in one action.");
        await RenderAsync(window, "Connected-Results");
        var sessionDisplay = Find<ContentPresenter>(Find<ComboBox>(window, "ResultsSessionPicker"), "");
        Require(Find<TextBlock>(sessionDisplay, "").Text == row.Name, "The result session picker must display a session name.");
        results.SelectedResult = results.Results.Single();
        Require(results.CopyCommand.CanExecute(null), "A selected recovered password must be copyable.");
        await ((AsyncCommand)shell.Jobs.DeleteCommand).ExecuteAsync(null);
        Require(results.SelectedJob is null && results.Results.Count == 0 && !results.CopyCommand.CanExecute(null), "Deleting the displayed session must clear recovered rows and disable stale clipboard actions.");
        await ((AsyncCommand)shell.Jobs.UndoDeleteCommand).ExecuteAsync(null);
        results.SelectedJob = row;
        var pending = results.Loading;
        results.SelectedJob = null;
        await pending;
        Require(results.Results.Count == 0 && !results.Reveal, "An in-flight load must not repopulate a cleared selection or preserve reveal state.");
        inspector.HashText = "changed target";
        Require(inspector.SelectedMode is null && inspector.PreparedTargetPath is null, "Editing a target must invalidate identification and prepared input.");
    }

    private async Task CheckSessionResultsIsolationAsync(ShellViewModel shell, PersistenceStore store, Window window)
    {
        var results = (ResultsViewModel)shell.Navigation.Single(page => page.Page is ResultsViewModel).Page;
        var potfile = Path.Combine(store.Paths.Root, "shared-results-fixture.potfile");
        await File.WriteAllTextAsync(potfile, "hash-a:alpha\nhash-b:beta\n");
        JobViewModel Row(string name, string? output = null) => new(new JobRecord { Name = name, State = JobState.Cracked, Configuration = new HashcatJob { TargetPath = "shared-target-fixture", Options = new() { PotfilePath = potfile, OutputPath = output ?? Path.Combine(store.Paths.Root, name + ".txt") } } });
        var first = Row("Session A"); var second = Row("Session B"); var empty = Row("No new recoveries");
        await File.WriteAllTextAsync(first.Output, "hash-a:616c706861\n");
        await File.WriteAllTextAsync(second.Output, "hash-b:62657461\n");
        foreach (var row in new[] { first, second, empty }) shell.Jobs.Jobs.Add(row);
        results.SelectedJob = first; await results.Loading;
        Require(results.Results.Count == 1 && results.Results[0].Hash == "hash-a" && results.Results[0].SessionId == first.Record.Id, "Selected session must read only its own output, even with a shared target and potfile.");
        results.SelectedJob = empty; await results.Loading;
        Require(results.Results.Count == 0, "A session with no output must not inherit passwords from the shared potfile.");
        results.SelectedJob = second; await results.Loading;
        Require(results.Results.Count == 1 && results.Results[0].Hash == "hash-b", "Switching sessions must replace the recovered hash set.");
        shell.Jobs.Selected = second;
        shell.Selected = shell.Navigation.Single(page => page.Page == results);
        await RenderAsync(window, "Session-only-results");
        var allSessions = Find<CheckBox>(window, "AllSessionsResultsCheckBox");
        allSessions.IsChecked = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await results.Loading;
        Require(results.ShowAllSessions && results.Results.Count == 2 && results.Results.Select(row => row.SessionId).Distinct().Count() == 2, "All Sessions must combine attributed rows from history without including potfile-only matches.");
        Require(!Find<ComboBox>(window, "ResultsSessionPicker").IsEnabled && !results.Reveal, "All Sessions must disable single-session selection and start masked.");
        results.Reveal = true;
        Require(results.Results.Any(row => row.Session == "Session A" && row.Plaintext == "alpha") && results.Results.Any(row => row.Session == "Session B" && row.Plaintext == "beta"), "Combined results must retain hash/password/session associations.");
        await RenderAsync(window, "All-session-results");
        allSessions.IsChecked = false;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await results.Loading;
        Require(results.Results.Count == 1 && results.Results[0].SessionId == second.Record.Id && !results.Reveal, "Unchecking All Sessions must restore the selected session and reset reveal.");
        results.ShowAllSessions = true; await results.Loading;
        shell.Jobs.Selected = first;
        await ((AsyncCommand)shell.Jobs.DeleteCommand).ExecuteAsync(null); await results.Loading;
        Require(results.Results.Count == 1 && results.Results[0].SessionId == second.Record.Id, "Deleting a session must remove its rows from All Sessions.");
        await ((AsyncCommand)shell.Jobs.UndoDeleteCommand).ExecuteAsync(null); await results.Loading;
        Require(results.Results.Count == 2, "Undo must restore a session's rows in All Sessions.");
        var ambiguous = Row("Shared output fixture", first.Output);
        shell.Jobs.Jobs.Add(ambiguous); await results.Loading;
        Require(results.Results.Count == 1 && results.Results[0].SessionId == second.Record.Id && results.Status.Contains("excluded", StringComparison.Ordinal), "An output shared by multiple legacy sessions must not be attributed to either session.");
        shell.Jobs.Jobs.Remove(ambiguous); await results.Loading;
        var count = shell.Jobs.Jobs.Count;
        try { await shell.Jobs.StartAsync(new HashcatJob { Options = new() { OutputPath = first.Output } }); throw new InvalidOperationException("Expected shared-output rejection."); }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Another session", StringComparison.Ordinal)) { }
        Require(shell.Jobs.Jobs.Count == count, "Rejecting a shared output must not create a session.");
        shell.Jobs.Selected = first;
        await ((AsyncCommand)shell.Jobs.ViewResultsCommand).ExecuteAsync(null);
        Require(!results.ShowAllSessions && results.Results.Count == 1 && results.Results[0].SessionId == first.Record.Id, "A job's View action must exit All Sessions and show only that job.");
        results.SelectedJob = null;
        foreach (var row in new[] { first, second, empty }) shell.Jobs.Jobs.Remove(row);
        shell.Jobs.Selected = null; await shell.Jobs.SaveAsync();
    }

    private async Task CheckSessionHistoryAsync(ShellViewModel shell, PersistenceStore store, Window window)
    {
        var outputFile = Path.Combine(store.Paths.Root, "retained-recovery.txt");
        await File.WriteAllTextAsync(outputFile, "Synthetic retained recovery file");
        var row = new JobViewModel(new JobRecord { Name = "History deletion fixture", State = JobState.Running, Configuration = new HashcatJob { Options = new CommonOptions { OutputPath = outputFile } } });
        shell.Jobs.Jobs.Add(row); shell.Jobs.Selected = row;
        Require(!shell.Jobs.DeleteCommand.CanExecute(null), "Running sessions must not be deletable.");
        row.Record.State = JobState.Paused;
        Require(!shell.Jobs.DeleteCommand.CanExecute(null), "Paused sessions must not be deletable.");
        row.Record.State = JobState.Failed; row.Refresh();
        shell.Selected = shell.Navigation.Single(page => page.Page == shell.Jobs);
        await RenderAsync(window, "History-delete-controls");
        Require(Find<Button>(window, "DeleteSessionButton").Command == shell.Jobs.DeleteCommand, "History must expose the delete command.");
        await ((AsyncCommand)shell.Jobs.DeleteCommand).ExecuteAsync(null);
        Require(!shell.Jobs.Jobs.Contains(row) && (await store.LoadJobsAsync()).All(job => job.Id != row.Record.Id), "Delete must persist removal from history.");
        Require(File.Exists(outputFile) && shell.Jobs.Selected is null, "Deleting the last session must clear selection and preserve recovery files.");
        await ((AsyncCommand)shell.Jobs.UndoDeleteCommand).ExecuteAsync(null);
        Require(shell.Jobs.Selected == row && (await store.LoadJobsAsync()).Any(job => job.Id == row.Record.Id), "Undo must persist the restored session.");
        var historyFile = Path.Combine(store.Paths.Root, "jobs.json");
        var preservedHistory = Path.Combine(store.Paths.Root, "jobs-save-failure-fixture.json");
        File.Move(historyFile, preservedHistory);
        Directory.CreateDirectory(historyFile);
        try
        {
            await ((AsyncCommand)shell.Jobs.DeleteCommand).ExecuteAsync(null);
            Require(shell.Jobs.Jobs.Contains(row) && shell.Jobs.Selected == row && !shell.Jobs.UndoDeleteCommand.CanExecute(null), "A failed history write must restore the row and must not report a successful deletion.");
        }
        finally { Directory.Delete(historyFile); File.Move(preservedHistory, historyFile); }
        Require((await store.LoadJobsAsync()).Any(job => job.Id == row.Record.Id), "The previous history must survive a failed deletion save.");
        shell.Jobs.Jobs.Remove(row); shell.Jobs.Selected = null;
        await shell.Jobs.SaveAsync();
    }

    private async Task CheckDefaultDeviceAsync(ShellViewModel shell, PersistenceStore store, Window window)
    {
        var hardwarePage = shell.Navigation.Single(page => page.Page is HardwareViewModel);
        var hardware = (HardwareViewModel)hardwarePage.Page;
        var fixture = new BackendDevice { Id = 3, Name = "Synthetic CPU", Type = "CPU", Driver = "Fixture runtime" };
        hardware.Devices.Add(fixture);
        shell.Selected = hardwarePage;
        await RenderAsync(window, "Hardware-default-choice");
        var choose = Find<Button>(window, "UseDefaultDeviceButton");
        Require(choose.Command is not null && ReferenceEquals(choose.CommandParameter, fixture), "Hardware's default button must bind to its device.");
        await ((AsyncCommand)choose.Command!).ExecuteAsync(choose.CommandParameter);
        Require((await store.LoadSettingsAsync()).DefaultDeviceIds.SequenceEqual([3]), "Hardware choice must persist as an application default.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(Find<TextBlock>(window, "DefaultDeviceSummary").Text.Contains("Device 3", StringComparison.Ordinal), "Hardware must show the saved default immediately.");

        var attack = shell.Attack;
        shell.Selected = shell.Navigation[0];
        foreach (var (expert, deviceText, expected) in new[] { (false, "invalid hidden override", 3), (true, "", 3), (true, "4", 4), (false, "4", 3) })
        {
            attack.Expert = expert; attack.Devices = deviceText;
            Require(BuildDraft(attack).Options.Devices.SequenceEqual([expected]), "Basic must use the saved default; only visible Expert IDs may override it.");
        }
        await RenderAsync(window, "Basic-saved-device");
        Require(Find<TextBlock>(window, "RecoveryDeviceSummary").Text.Contains("Device 3", StringComparison.Ordinal), "Basic mode must display its recovery device.");
        var restarted = new AppServices(new PersistenceStore(store.Paths));
        await restarted.InitializeAsync();
        var reopened = new AttackViewModel(restarted, new JobsViewModel(restarted), () => { });
        await reopened.InitializeAsync();
        Require(!reopened.Expert && reopened.RecoveryDeviceSummary.Contains("Device 3", StringComparison.Ordinal), "The device default must survive restart without enabling Expert mode.");
        await ((AsyncCommand)hardware.AutomaticCommand).ExecuteAsync(null);
        Require((await store.LoadSettingsAsync()).DefaultDeviceIds.Count == 0 && attack.RecoveryDeviceSummary.Contains("automatic", StringComparison.Ordinal), "Clearing a default must persist and refresh Basic mode.");
        hardware.Devices.Clear(); attack.Devices = "";
    }

    private async Task CheckRecoveryWorkflowAsync(ShellViewModel shell, AppServices services, Window window, string device)
    {
        var attack = shell.Attack;
        shell.Selected = shell.Navigation[0];
        attack.Family = 0;
        var hardware = (HardwareViewModel)shell.Navigation.Single(page => page.Page is HardwareViewModel).Page;
        await ((AsyncCommand)hardware.RefreshCommand).ExecuteAsync(null);
        Require(int.TryParse(device, out var deviceId), "The Basic recovery smoke requires one current device ID.");
        var selectedDevice = hardware.Devices.Single(entry => entry.Id == deviceId);
        await ((AsyncCommand)hardware.UseDefaultCommand).ExecuteAsync(selectedDevice);
        services.Settings.DefaultWorkloadProfile = 1;
        attack.Expert = false;
        attack.UseCustomRules = false;
        attack.SelectedRulePreset = attack.RulePresets.Single(preset => preset.Id == RulePresetCatalog.NormalId);
        await ((AsyncCommand)attack.UseStarterCommand).ExecuteAsync(null);
        attack.Devices = "";
        attack.ExtraArguments = "";
        attack.Inspector.InputMode = 0;
        // Public known-answer fixture: NTLM of the literal test word "password".
        attack.Inspector.HashText = "8846f7eaee8fb117ad06bdd830b7586c";
        attack.Inspector.SelectedMode = attack.Inspector.FindMode(1000);
        var previousCount = shell.Jobs.Jobs.Count;
        await ((AsyncCommand)attack.StartCommand).ExecuteAsync(null);
        Require(shell.Jobs.Jobs.Count == previousCount + 1 && shell.Selected.Page == shell.Jobs, "Start recovery must create a job and show Jobs.");
        var job = shell.Jobs.Selected!;
        try
        {
            Require(job.Record.Configuration.Options.Devices.SequenceEqual([deviceId]), "Basic Start must use the saved Hardware default with no additional attack setup.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            while (job.Record.State is JobState.Ready or JobState.Running or JobState.Paused) await Task.Delay(100, timeout.Token);
            Require(job.Record.State == JobState.Cracked && job.Record.ExitCode == 0, "Known-answer recovery failed: " + job.Diagnostic);
            await RenderAsync(window, "Real-recovery-Job");
            var resultsPage = shell.Navigation.Single(page => page.Page is ResultsViewModel);
            var results = (ResultsViewModel)resultsPage.Page;
            await ((AsyncCommand)shell.Jobs.ViewResultsCommand).ExecuteAsync(null);
            Require(shell.Selected.Page == results && results.Results.Count == 1 && results.Results[0].Plaintext == "password", "The completed job action must show the actual recovered fixture password.");
            results.Reveal = false;
            Require(results.Results[0].Plaintext != "password", "The recovered password must be hideable again.");
            shell.Selected = resultsPage;
            await RenderAsync(window, "Real-recovery-Result");
        }
        finally { await shell.Jobs.StopAllAsync(); }
    }

    private async Task CheckPopulatedJobsAsync(ShellViewModel shell, Window window)
    {
        var record = new JobRecord
        {
            Name = "Synthetic progress fixture", State = JobState.Running, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            LatestStatus = new JobStatusSnapshot { State = "Running", ProgressPercent = 25, SpeedHashesPerSecond = 1234, TotalHashes = 1 }
        };
        var row = new JobViewModel(record);
        shell.Jobs.Jobs.Add(row);
        shell.Jobs.Selected = row;
        shell.Selected = shell.Navigation.Single(page => page.Page == shell.Jobs);
        await RenderAsync(window, "Jobs-running");
        var progress = Find<ProgressBar>(window, "JobProgressBar");
        Require(progress.Value == 25, "A populated Jobs page must display the selected job's progress.");
        record.LatestStatus.ProgressPercent = 75;
        row.Refresh();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(progress.Value == 75, "The progress display must update when a job status changes.");
        record.State = JobState.Failed;
        record.LatestStatus = null;
        record.ExitCode = -1;
        record.Diagnostic = HashcatRunningJob.SanitizeDiagnostic("clGetDeviceInfo(): CL_INVALID_VALUE");
        row.Refresh();
        await RenderAsync(window, "Jobs-failed");
        Require(progress.Value == 0 && row.Diagnostic.Contains("CL_INVALID_VALUE", StringComparison.Ordinal), "Failed jobs must render their actionable backend diagnostic without binding exceptions.");
        shell.Jobs.Selected = null;
        shell.Jobs.Jobs.Remove(row);
        shell.Selected = shell.Navigation[0];
    }

    private async Task RenderAsync(Window window, string name)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(stream);
    }

    private async Task CheckManualCatalogAsync(ShellViewModel shell, Window window, bool synthetic)
    {
        shell.Selected = shell.Navigation[0];
        var inspector = shell.Attack.Inspector;
        if (synthetic)
            for (var index = 0; index < 600; index++) inspector.Modes.Add(new HashMode(index, "Synthetic long hash mode description with iterations and a large amount of format detail " + index, "Synthetic category"));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var scroll = Find<ScrollViewer>(window, "AttackScrollViewer");
        var expander = Find<Expander>(window, "ManualModeExpander");
        var label = synthetic ? "Synthetic" : "Installed";
        foreach (var width in new[] { 1380, 1040 })
        {
            window.Width = width;
            scroll.ScrollToTop();
            expander.IsExpanded = true;
            await RenderAsync(window, $"Manual-catalog-{label}-{width}");
            var list = Find<ListBox>(window, "ManualModeList");
            Require(scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible && scroll.ScrollableHeight > 0, "Opening the manual catalog must retain the page scrollbar.");
            Require(scroll.ActualWidth <= window.ActualWidth - 220, "Opening the manual catalog must stay within the page viewport.");
            Require(list.Items.Count == inspector.Modes.Count && list.Items.Count > 100, "The expanded catalog must contain the populated mode list.");
            var mode = inspector.Modes[^1];
            list.SelectedItem = mode;
            list.ScrollIntoView(mode);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(inspector.SelectedMode == mode, "Manual mode selection must update the recovery configuration.");
            scroll.ScrollToEnd();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(scroll.VerticalOffset > 0, "The page must remain scrollable while the catalog is open.");
            expander.IsExpanded = false;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        if (!synthetic)
        {
            expander.IsExpanded = true;
            var search = Find<TextBox>(window, "ManualModeSearch");
            search.Text = "NTLM";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(inspector.Search == "NTLM" && inspector.Modes.Any(mode => mode.Mode == 1000), "Typing in manual search must filter the installed catalog and retain NTLM.");
            Require(inspector.Modes.All(mode => mode.DisplayName.Contains("NTLM", StringComparison.OrdinalIgnoreCase) || mode.Category.Contains("NTLM", StringComparison.OrdinalIgnoreCase)), "Manual search must not show nonmatching modes.");
            await RenderAsync(window, "Manual-catalog-search");
            search.Text = "";
            expander.IsExpanded = false;
        }
        inspector.SelectedMode = null;
        if (synthetic) inspector.Modes.Clear();
        window.Width = 1380;
        scroll.ScrollToTop();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && element.Name == name) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindOrNull<T>(VisualTreeHelper.GetChild(root, index), name);
            if (found is not null) return found;
        }
        throw new InvalidOperationException("Expected UI element was not loaded: " + name);
    }

    private static T? FindOrNull<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && element.Name == name) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindOrNull<T>(VisualTreeHelper.GetChild(root, index), name);
            if (found is not null) return found;
        }
        return null;
    }
}
