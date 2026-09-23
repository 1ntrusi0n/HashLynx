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
internal sealed class SmokeApplication(string output) : Application
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
            var services = new AppServices(store);
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
            await CheckDefaultDeviceAsync(shell, store, window);
            await CheckManualCatalogAsync(shell, window, true);
            await CheckPopulatedJobsAsync(shell, window);

            shell.Selected = shell.Navigation[0];
            for (var family = 0; family < 4; family++)
            {
                shell.Attack.Family = family;
                shell.Attack.Expert = true;
                await RenderAsync(window, "Attack-family-" + family);
            }
            for (var input = 0; input < 3; input++)
            {
                shell.Attack.Inspector.InputMode = input;
                await RenderAsync(window, "Inspector-input-" + input);
            }

            shell.Attack.Family = 1;
            shell.Attack.Mask = "?d?d?d?d";
            shell.Attack.ProfileName = "Synthetic smoke profile";
            await ((AsyncCommand)shell.Attack.SaveProfileCommand).ExecuteAsync(null);
            Require(shell.Attack.Profiles.Count == 1, "Saving a profile must populate the profile list.");
            Require((await store.LoadProfilesAsync()).Single().Configuration.TargetPath == "", "Profiles must not save target contents.");
            shell.Attack.SelectedProfile = shell.Attack.Profiles[0];
            shell.Attack.Family = 0;
            await ((AsyncCommand)shell.Attack.LoadProfileCommand).ExecuteAsync(null);
            Require(shell.Attack.Family == 1 && shell.Attack.Mask == "?d?d?d?d", "Loading a profile must restore attack settings.");
            await ((AsyncCommand)shell.Attack.DeleteProfileCommand).ExecuteAsync(null);
            Require((await store.LoadProfilesAsync()).Count == 0, "Deleting a profile must persist its removal.");

            await ((AsyncCommand)shell.Attack.PreflightCommand).ExecuteAsync(null);
            Require(services.Notice.Contains("Hashcat", StringComparison.Ordinal), "Preflight without backend must explain configuration.");
            var backendPath = Environment.GetEnvironmentVariable("HASHLYNX_TEST_HASHCAT");
            if (File.Exists(backendPath)) await CheckConnectedWorkflowAsync(shell, services, window, backendPath);
            var recoveryDevice = Environment.GetEnvironmentVariable("HASHLYNX_TEST_DEVICE");
            if (File.Exists(backendPath) && !string.IsNullOrWhiteSpace(recoveryDevice)) await CheckRecoveryWorkflowAsync(shell, services, window, recoveryDevice);
            foreach (var theme in new[] { "Light", "Dark", "System" })
            {
                AppServices.ApplyTheme(theme);
                window.Width = 1040; window.Height = 700;
                await RenderAsync(window, "Theme-" + theme);
            }
            listener.Flush();
            Require(errors.Length == 0, "WPF binding failures: " + errors);
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "PASS: missing-backend startup; all six pages; four attack families; three target modes; three themes; narrow layout; profile save/load/delete; Basic preset/defaults; Expert custom rules and legacy profiles; starter wordlist; populated manual catalog expansion, selection and scrolling; populated running/failed Jobs with progress updates; saved Hardware default, Basic selection, Expert overrides, restart persistence; preflight error handling; zero binding errors." + (File.Exists(backendPath) ? " Installed backend: automatic identification blocks ambiguous Start, mode selection, catalog search, preset command preflight, and masked/revealed results passed." : "") + (File.Exists(backendPath) && !string.IsNullOrWhiteSpace(recoveryDevice) ? " Real recovery: Start → Jobs → Results recovered the known NTLM fixture in Basic mode with the starter list, Normal preset, and saved Hardware default." : ""));
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

        // Expert leftovers must not secretly affect a later Basic attack or profile.
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
        attack.ProfileName = "Basic preset regression";
        await ((AsyncCommand)attack.SaveProfileCommand).ExecuteAsync(null);
        var saved = (await store.LoadProfilesAsync()).Single().Configuration;
        Require(saved.Attack.RulePresetId is null && saved.Attack.RuleFiles.Count == 0, "Basic No Rules must save no preset and no hidden custom rule paths.");
        Require(saved.Attack.LeftRule is null && saved.Options.Devices.Count == 0 && saved.Options.TemperatureAbort is null, "Basic mode must use automatic controls.");
        Require(!saved.Options.OptimizedKernel && !saved.Options.DisablePotfile && saved.Options.ExtraArguments.Count == 0, "Hidden Expert options must not apply to Basic mode.");
        attack.SelectedProfile = attack.Profiles.Single();
        attack.SelectedRulePreset = attack.RulePresets.Single(preset => preset.Id == RulePresetCatalog.NormalId);
        await ((AsyncCommand)attack.LoadProfileCommand).ExecuteAsync(null);
        Require(!attack.Expert && !attack.UseCustomRules && attack.SelectedRulePreset.Id is null, "Loading a No Rules profile must restore No Rules without requiring Expert mode.");
        await ((AsyncCommand)attack.DeleteProfileCommand).ExecuteAsync(null);

        attack.SelectedRulePreset = attack.RulePresets.Single(preset => preset.Id == RulePresetCatalog.HeavyId);
        attack.ProfileName = "Explicit preset regression";
        await ((AsyncCommand)attack.SaveProfileCommand).ExecuteAsync(null);
        attack.SelectedProfile = attack.Profiles.Single();
        attack.SelectedRulePreset = attack.RulePresets[0];
        await ((AsyncCommand)attack.LoadProfileCommand).ExecuteAsync(null);
        Require(attack.SelectedRulePreset.Id == RulePresetCatalog.HeavyId && !attack.UseCustomRules, "A saved explicit preset must retain its selection.");
        await ((AsyncCommand)attack.DeleteProfileCommand).ExecuteAsync(null);

        // A pre-preset dictionary profile must preserve its original custom-rule semantics.
        var legacy = new AttackProfile { Name = "Legacy custom rules", Configuration = new HashcatJob { Attack = new AttackConfiguration { RuleFiles = [Path.Combine(store.Paths.Root, "legacy.rule")] } } };
        attack.Profiles.Add(legacy);
        attack.SelectedProfile = legacy;
        await ((AsyncCommand)attack.LoadProfileCommand).ExecuteAsync(null);
        Require(attack.Expert && attack.UseCustomRules && attack.Rules.Single().EndsWith("legacy.rule", StringComparison.Ordinal), "Legacy custom profiles must visibly open Expert mode without replacing rules.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(Find<FrameworkElement>(window, "RunOptionsPanel").IsVisible && Find<FrameworkElement>(window, "CustomRulesPanel").IsVisible, "Expert panels must become visible.");
        await RenderAsync(window, "Expert-custom-rules");
        foreach (var incompatible in new[]
        {
            new AttackConfiguration { Kind = "future-attack" },
            new AttackConfiguration { Kind = AttackFamilies.Mask, RuleFiles = ["custom.rule"] },
            new AttackConfiguration { Kind = AttackFamilies.Mask, RulePresetId = RulePresetCatalog.QuickId }
        })
        {
            attack.SelectedProfile = new AttackProfile { Name = "Unsupported fixture", Configuration = new HashcatJob { Attack = incompatible } };
            await ((AsyncCommand)attack.LoadProfileCommand).ExecuteAsync(null);
            Require(attack.Family == 0 && attack.UseCustomRules && attack.Rules.Single().EndsWith("legacy.rule", StringComparison.Ordinal), "Unsupported profile combinations must be rejected before changing the editor.");
        }
        attack.SelectedProfile = legacy;
        await ((AsyncCommand)attack.DeleteProfileCommand).ExecuteAsync(null);
        attack.SelectedProfile = new AttackProfile { Name = "Legacy no-rule fixture", Configuration = new HashcatJob() };
        await ((AsyncCommand)attack.LoadProfileCommand).ExecuteAsync(null);
        Require(!attack.UseCustomRules && attack.SelectedRulePreset.Id is null && attack.Rules.Count == 0, "Legacy no-rule dictionary profiles must load as No Rules.");
        attack.SelectedProfile = null;
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
        shell.Jobs.Jobs.Add(row);
        var results = (ResultsViewModel)shell.Navigation.Single(page => page.Page is ResultsViewModel).Page;
        results.SelectedJob = row;
        await ((AsyncCommand)results.LoadCommand).ExecuteAsync(null);
        Require(results.Results.Count == 1, "The Results page must load a synthetic recovered result.");
        Require(results.Results[0].Plaintext != plaintext, "Plaintext must initially be hidden.");
        results.Reveal = true;
        Require(results.Results[0].Plaintext == plaintext, "Reveal must display the decoded plaintext.");
        results.Reveal = false;
        shell.Selected = shell.Navigation.Single(page => page.Page == results);
        await RenderAsync(window, "Connected-Results");
        results.SelectedJob = null;
        Require(results.Results.Count == 0, "Switching jobs must clear previous results.");
        inspector.HashText = "changed target";
        Require(inspector.SelectedMode is null && inspector.PreparedTargetPath is null, "Editing a target must invalidate identification and prepared input.");
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
            attack.ProfileName = "Device default fixture";
            await ((AsyncCommand)attack.SaveProfileCommand).ExecuteAsync(null);
            var profile = attack.Profiles.Last();
            Require(profile.Configuration.Options.Devices.SequenceEqual([expected]), "Basic must use the saved default; only visible Expert IDs may override it.");
            attack.SelectedProfile = profile;
            await ((AsyncCommand)attack.DeleteProfileCommand).ExecuteAsync(null);
        }
        await RenderAsync(window, "Basic-saved-device");
        Require(Find<TextBlock>(window, "RecoveryDeviceSummary").Text.Contains("Device 3", StringComparison.Ordinal), "Basic mode must display its recovery device.");
        var restarted = new AppServices(new PersistenceStore(store.Paths));
        await restarted.InitializeAsync();
        var reopened = new AttackViewModel(restarted, new JobsViewModel(restarted), () => { });
        await reopened.InitializeAsync();
        Require(!reopened.Expert && reopened.RecoveryDeviceSummary.Contains("Device 3", StringComparison.Ordinal), "The device default must survive restart without loading a profile or enabling Expert mode.");
        await ((AsyncCommand)hardware.AutomaticCommand).ExecuteAsync(null);
        Require((await store.LoadSettingsAsync()).DefaultDeviceIds.Count == 0 && attack.RecoveryDeviceSummary.Contains("automatic", StringComparison.Ordinal), "Clearing a default must persist and refresh Basic mode.");
        hardware.Devices.Clear(); attack.Devices = ""; attack.SelectedProfile = null;
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
            Require(job.Record.Configuration.Options.Devices.SequenceEqual([deviceId]), "Basic Start must use the saved Hardware default without a profile.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            while (job.Record.State is JobState.Ready or JobState.Running or JobState.Paused) await Task.Delay(100, timeout.Token);
            Require(job.Record.State == JobState.Cracked && job.Record.ExitCode == 0, "Known-answer recovery failed: " + job.Diagnostic);
            await RenderAsync(window, "Real-recovery-Job");
            var resultsPage = shell.Navigation.Single(page => page.Page is ResultsViewModel);
            var results = (ResultsViewModel)resultsPage.Page;
            results.SelectedJob = job;
            await ((AsyncCommand)results.LoadCommand).ExecuteAsync(null);
            Require(results.Results.Count == 1 && results.Results[0].Plaintext != "password", "Actual recovered results must load masked by default.");
            results.Reveal = true;
            Require(results.Results[0].Plaintext == "password", "Actual recovery must return the expected fixture plaintext.");
            results.Reveal = false;
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
