using System.IO;
using System.Buffers.Binary;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HashLynx.Core;
using HashLynx.Hashcat;
using HashLynx.Persistence;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Smoke;

internal sealed partial class SmokeApplication
{
    private async Task CheckModernAttackChoicesAsync(ShellViewModel shell, AppServices services, PersistenceStore store, Window window)
    {
        var attack = shell.Attack;
        shell.Selected = shell.Navigation.First(item => item.Page == attack);
        attack.Expert = false;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var tabs = Find<TabControl>(window, "RecoveryMethodTabs");
        Require(tabs.Items.Cast<TabItem>().Count(item => item.IsVisible) == 3,
            "Beginner mode must expose exactly Wordlist, Known pattern and Likely passwords.");
        Require(shell.Navigation.Any(item => item.Page == shell.Queue), "Saved queues must remain accessible in Beginner mode.");
        services.Settings.ExpertMode = true;
        var expertStartup = new AttackViewModel(services, shell.Jobs, () => { });
        await expertStartup.InitializeAsync();
        Require(expertStartup.Expert, "An explicitly saved Expert preference must control startup.");
        expertStartup.Expert = false;
        Require(services.Settings.ExpertMode, "The temporary Attack switch must not rewrite the startup preference.");
        services.Settings.ExpertMode = false;
        Require(!attack.AddToQueueCommand.CanExecute(null) && !attack.AddProgressiveSequenceCommand.CanExecute(null),
            "Creating sequences must require Expert mode even if a hidden command is invoked.");
        attack.Family = 5;
        Require(attack.Family == 0, "An unavailable Expert family must not become a hidden Beginner draft.");
        attack.Family = 4;
        attack.PcfgUseHints = true; attack.PcfgHintWords = "synthetic-memory";
        attack.PcfgConstraint = 1; attack.PcfgMinimumLength = "invalid hidden value";
        attack.RulesConcatenated = true;
        var beginnerPcfg = BuildDraft(attack).Attack;
        Require(beginnerPcfg.Kind == AttackFamilies.Pcfg && beginnerPcfg.Wordlists.Count == 0 && beginnerPcfg.Mask is null
            && beginnerPcfg.Pcfg is { UseHints: false, HintFile: null, MinimumLength: null, MaximumLength: null, ShapeMask: null }
            && !beginnerPcfg.RulesConcatenated,
            "Beginner PCFG must use only the default model, without stale Expert constraints or files.");
        Require(attack.MethodAvailability.Contains("Hashcat 8"), "An older or missing backend must explain why PCFG is unavailable.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(Find<StackPanel>(window, "PcfgMethodPanel").IsVisible && !Find<Expander>(window, "PcfgExpertOptions").IsVisible,
            "Beginner PCFG needs visible guidance and no advanced hints controls.");

        var originalInstallation = services.Installation;
        var installationProperty = typeof(AppServices).GetProperty(nameof(AppServices.Installation), BindingFlags.Public | BindingFlags.Instance)!;
        var synthetic = new HashcatInstallation(Path.Combine(store.Paths.Root, "synthetic-hashcat.exe"), "8.0.0", new()
        {
            ExpandedRules = true, RulesConcatenation = true, WorkloadProfilesEffective = false, Restore = true, RestorePosition = true,
            AttackModes = new() { [AttackFamilies.Dictionary] = 0, [AttackFamilies.Mask] = 3, [AttackFamilies.Pcfg] = 9, [AttackFamilies.WordlistPattern] = 12 }
        });
        installationProperty.SetValue(services, synthetic);
        try
        {
            attack.Expert = true;
            attack.PcfgUseHints = false; attack.PcfgMinimumLength = "8"; attack.PcfgMaximumLength = "12";
            var bounded = BuildDraft(attack).Attack.Pcfg!;
            Require(bounded.MinimumLength == 8 && bounded.MaximumLength == 12 && bounded.ShapeMask is null,
                "PCFG length restrictions must use the visible range.");
            attack.PcfgConstraint = 2; attack.PcfgShape = "?u?l?l?l?d?d";
            var shaped = BuildDraft(attack).Attack.Pcfg!;
            Require(shaped.MinimumLength is null && shaped.MaximumLength is null && shaped.ShapeMask == attack.PcfgShape,
                "Choosing a PCFG shape must omit hidden length restrictions.");
            Require(!attack.WorkloadSupported, "Hashcat 8 must explain its automatic workload management.");
            attack.Hints.Words = "synthetic-memory";
            attack.Hints.PreviewCommand.Execute(null);
            Require(!attack.Hints.Steps.Contains("PCFG", StringComparison.Ordinal), "Hints must not add probability-based attempts implicitly.");
            attack.Hints.IncludePcfgAttempt = true;
            Require(!attack.Hints.QueueCommand.CanExecute(null), "Adding the optional PCFG step must require a fresh preview.");
            attack.Hints.PreviewCommand.Execute(null);
            Require(attack.Hints.Steps.Contains("2. Likely passwords (PCFG) using remembered words", StringComparison.Ordinal)
                && attack.Hints.Summary.Contains("plus the separate PCFG attempt", StringComparison.Ordinal),
                "The explicit PCFG step must follow focused attempts and keep its unknown candidate count separate.");
            attack.Hints.IncludePcfgAttempt = false; attack.Hints.Words = "";
            var checkpointPath = Path.Combine(store.Paths.Root, "old-version.restore");
            var checkpoint = new byte[296]; BinaryPrimitives.WriteInt32LittleEndian(checkpoint, 611);
            await File.WriteAllBytesAsync(checkpointPath, checkpoint);
            var oldSession = new JobViewModel(new() { State = JobState.Interrupted, Configuration = new() { Options = new() { RestorePath = checkpointPath } } });
            var previouslySelected = shell.Jobs.Selected;
            shell.Jobs.Jobs.Add(oldSession); shell.Jobs.Selected = oldSession;
            await WaitUntilAsync(() => oldSession.RestoreGuidance.Contains("older Hashcat", StringComparison.Ordinal));
            Require(!oldSession.CanRestore && !shell.Jobs.RestoreCommand.CanExecute(null) && File.Exists(checkpointPath),
                "Jobs must explain incompatible old checkpoints, disable Restore and preserve the file.");
            shell.Jobs.Jobs.Remove(oldSession); shell.Jobs.Selected = previouslySelected;
            attack.Family = 5; attack.WordlistPattern = "?w-?q?d";
            var originalWords = attack.Wordlists.ToArray();
            attack.Wordlists.Clear(); attack.Wordlists.Add("synthetic-left.txt"); attack.Wordlists.Add("synthetic-right.txt");
            attack.UseCustomRules = true; attack.Rules.Add("synthetic-first.rule"); attack.Rules.Add("synthetic-second.rule");
            var pattern = BuildDraft(attack);
            Require(pattern.Attack.Kind == AttackFamilies.WordlistPattern && pattern.Attack.Mask == "?w-?q?d"
                && pattern.Attack.Wordlists.SequenceEqual(new[] { "synthetic-left.txt", "synthetic-right.txt" })
                && pattern.Attack.MaskFile is null && pattern.Attack.MaskPresetId is null,
                "Wordlist-pattern drafts must preserve ordered whole-word sources and only the inline pattern.");
            Require(pattern.Attack.RulesConcatenated && pattern.Options.ExtraArguments.Contains("--rules-concat"),
                "Separate rule lists must carry their compatibility marker through saved drafts.");
            var isolated = new PersistenceStore(new AppPaths(Path.Combine(store.Paths.Root, "modern-queue")));
            pattern.TargetPath = Path.Combine(isolated.Paths.Root, "synthetic-target.hashes");
            pattern.HashMode = 0;
            await isolated.SaveQueueAsync(new RecoveryQueueDocument { Plans = [new() { Steps = [new() { Configuration = pattern }] }] });
            attack.Expert = false;
            Require(attack.Family == 0 && attack.WordlistPattern == "?w-?q?d" && !services.Settings.ExpertMode,
                "Leaving Expert must select Wordlist, retain advanced input text, and keep the startup preference unchanged.");
            var loaded = await isolated.LoadQueueAsync();
            Require(loaded.Plans[0].Steps[0].Configuration.Attack.Kind == AttackFamilies.WordlistPattern
                && loaded.Plans[0].Steps[0].Configuration.Attack.RulesConcatenated,
                "Saved advanced queues must keep their exact settings while the UI is in Beginner mode.");
            attack.Wordlists.Clear(); foreach (var word in originalWords) attack.Wordlists.Add(word);
            attack.Rules.Clear(); attack.UseCustomRules = false; attack.RulesConcatenated = false;
            attack.Family = 4;
            foreach (var theme in new[] { "Light", "Dark" })
            {
                AppServices.ApplyTheme(theme);
                window.Width = 1040; window.Height = 700;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var scroll = Find<ScrollViewer>(window, "AttackScrollViewer");
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + tabs.TranslatePoint(new Point(0, 0), scroll).Y - 70);
                await RenderAsync(window, "Beginner-PCFG-" + theme);
                Require(tabs.ActualWidth <= scroll.ViewportWidth, "Beginner recovery choices must fit a narrow window.");
            }
            installationProperty.SetValue(services, new HashcatInstallation(synthetic.ExecutablePath, "7.1.2", new()
            {
                AttackModes = new() { [AttackFamilies.Dictionary] = 0, [AttackFamilies.Mask] = 3 }
            }));
            attack.Family = 0; attack.Expert = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(!Find<TabItem>(window, "PcfgMethodTab").IsVisible && !Find<TabItem>(window, "WordlistPatternMethodTab").IsVisible,
                "A configured older backend must hide attack methods it cannot run.");
            attack.Family = 4;
            Require(attack.Family == 0, "A missing backend capability must also prevent a hidden draft selection.");
        }
        finally
        {
            installationProperty.SetValue(services, originalInstallation);
            attack.Expert = false; attack.Family = 0;
            attack.PcfgConstraint = 0; attack.PcfgUseHints = false; attack.PcfgHintWords = "";
            attack.PcfgMinimumLength = ""; attack.PcfgMaximumLength = ""; attack.PcfgShape = "";
            window.Width = 1380; window.Height = 920;
        }
    }
}
