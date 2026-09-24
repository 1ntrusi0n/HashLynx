using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HashLynx.Core;
using HashLynx.Hashcat;
using HashLynx.Persistence;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Smoke;

internal sealed partial class SmokeApplication
{
    private async Task CheckMaskSourcesAsync(ShellViewModel shell, AppServices services, PersistenceStore store, Window window)
    {
        var attack = shell.Attack;
        var fresh = new AttackViewModel(services, shell.Jobs, () => { }) { Family = 1 };
        Require(fresh.IsBuiltInMask, "A new Mask attack must offer the bundled list without requiring a file.");
        shell.Selected = shell.Navigation.First(item => item.Page == attack);
        attack.Family = 1; attack.Expert = false;
        attack.SelectedMaskSource = MaskSourceChoice.BuiltIn;
        attack.Mask = "stale?d"; attack.MaskFile = Path.Combine(store.Paths.Root, "stale-missing.hcmask");
        attack.Increment = true; attack.Charset1 = "stale";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(Find<StackPanel>(window, "BuiltInMasksPanel").IsVisible && !Find<StackPanel>(window, "CustomMaskTextPanel").IsVisible
            && !Find<StackPanel>(window, "CustomMaskFilePanel").IsVisible && !Find<StackPanel>(window, "CustomMaskOptionsPanel").IsVisible,
            "Built-in masks must have one visible source and no misleading custom options.");
        Require(attack.BuiltInMaskSummary.Contains("1,000") && attack.BuiltInMaskEffort.Contains("impractically"), "Built-in masks must expose the count and practical effort limitation.");

        async Task<AttackProfile> SaveAsync(string name)
        {
            attack.ProfileName = name;
            await ((AsyncCommand)attack.SaveProfileCommand).ExecuteAsync();
            return attack.Profiles.Last();
        }
        async Task RemoveAsync(AttackProfile profile)
        {
            attack.SelectedProfile = profile;
            await ((AsyncCommand)attack.DeleteProfileCommand).ExecuteAsync();
        }
        async Task LoadAsync(AttackProfile profile)
        {
            attack.SelectedProfile = profile;
            await ((AsyncCommand)attack.LoadProfileCommand).ExecuteAsync();
        }

        var builtin = await SaveAsync("Built-in masks smoke");
        var builtAttack = builtin.Configuration.Attack;
        Require(builtAttack.MaskPresetId == MaskPresetCatalog.Common1000Id && builtAttack.Mask is null && builtAttack.MaskFile is null
            && !builtAttack.Increment && builtAttack.CustomCharsets.Count == 0 && builtAttack.RulePresetId is null && builtAttack.RuleFiles.Count == 0,
            "A built-in mask profile must persist only the stable preset ID, with no stale text, files, charsets or dictionary rules.");
        attack.SelectedMaskSource = MaskSourceChoice.File;
        await LoadAsync(builtin);
        Require(attack.IsBuiltInMask, "Loading a built-in profile must restore its explicit source.");

        var queueStore = new PersistenceStore(new AppPaths(Path.Combine(store.Paths.Root, "mask-queue-roundtrip")));
        var queueConfiguration = JsonSerializer.Deserialize<HashcatJob>(JsonSerializer.Serialize(builtin.Configuration))!;
        queueConfiguration.TargetPath = Path.Combine(queueStore.Paths.Root, "synthetic-target.hashes");
        await queueStore.SaveQueueAsync(new RecoveryQueueDocument { Plans = [new() { Steps = [new() { Configuration = queueConfiguration }] }] });
        Require((await queueStore.LoadQueueAsync()).Plans[0].Steps[0].Configuration.Attack.MaskPresetId == MaskPresetCatalog.Common1000Id,
            "A queued bundled list must keep its versioned preset ID across restart.");

        window.Width = 1040; window.Height = 700;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var scroll = Find<ScrollViewer>(window, "AttackScrollViewer");
        var picker = Find<ComboBox>(window, "MaskSourcePicker");
        window.UpdateLayout();
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + picker.TranslatePoint(new Point(0, 0), scroll).Y - 45);
        await RenderAsync(window, "Built-in-masks-narrow");
        Require(scroll.ScrollableHeight > 0 && picker.ActualWidth <= scroll.ViewportWidth, "The mask source must remain reachable within the narrow scrolling layout.");

        picker.SelectedItem = MaskSourceChoice.Text;
        attack.Mask = "?d?d"; attack.MaskFile = "ignored-missing.hcmask";
        attack.Increment = false; attack.Charset1 = "";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(Find<StackPanel>(window, "CustomMaskTextPanel").IsVisible && !Find<StackPanel>(window, "BuiltInMasksPanel").IsVisible
            && !Find<StackPanel>(window, "CustomMaskFilePanel").IsVisible, "Choosing typed masks in the dropdown must show only that source.");
        var custom = await SaveAsync("Custom text mask smoke");
        Require(custom.Configuration.Attack.Mask == "?d?d" && custom.Configuration.Attack.MaskFile is null && custom.Configuration.Attack.MaskPresetId is null,
            "A custom mask profile must not inherit the bundled list or an inactive file path.");
        attack.SelectedMaskSource = MaskSourceChoice.BuiltIn;
        await LoadAsync(custom);
        Require(attack.SelectedMaskSource == MaskSourceChoice.Text && attack.Mask == "?d?d", "A custom mask must survive profile reload without switching to the default list.");

        var customFile = Path.Combine(store.Paths.Root, "custom masks.hcmask");
        await File.WriteAllTextAsync(customFile, "?d?d\n");
        picker.SelectedItem = MaskSourceChoice.File; attack.MaskFile = customFile; attack.Mask = "inactive?u";
        var fileProfile = await SaveAsync("Custom mask file smoke");
        Require(fileProfile.Configuration.Attack.MaskFile == customFile && fileProfile.Configuration.Attack.Mask is null && fileProfile.Configuration.Attack.MaskPresetId is null,
            "A file source must use only the selected file.");
        // Legacy profiles could store both fields; their documented file precedence must be retained.
        fileProfile.Configuration.Attack.Mask = "legacy-inactive?d";
        attack.SelectedMaskSource = MaskSourceChoice.BuiltIn;
        await LoadAsync(fileProfile);
        Require(attack.SelectedMaskSource == MaskSourceChoice.File && attack.EffectiveMask is null && attack.EffectiveMaskFile == customFile,
            "Legacy file profiles must retain file precedence after loading.");
        File.Delete(customFile);
        Require(attack.EffectiveMaskFile == customFile && attack.EffectiveMaskPresetId is null, "A missing external file must never silently fall back to the built-in list.");
        attack.MaskFile = "";
        Require(attack.EffectiveMask is null && attack.EffectiveMaskPresetId is null, "An empty external file choice must remain invalid instead of selecting another mask source.");

        var invalid = new AttackProfile { Name = "Unsupported mask preset", Configuration = new() { Attack = new() { Kind = AttackFamilies.Mask, MaskPresetId = "future-preset-v999" } } };
        var invalidSnapshot = JsonSerializer.Serialize(invalid);
        await LoadAsync(invalid);
        Require(attack.SelectedMaskSource == MaskSourceChoice.File && JsonSerializer.Serialize(invalid) == invalidSnapshot
            && services.Notice.Contains("preset", StringComparison.OrdinalIgnoreCase), "An unknown saved preset must produce an error while preserving the profile and current selection.");

        attack.Family = 1; attack.SelectedMaskSource = MaskSourceChoice.BuiltIn;
        attack.Family = 2; attack.HybridDirection = 0;
        Require(attack.SelectedMaskSource == MaskSourceChoice.Text && attack.MaskSources.All(source => source.Kind != MaskInputKind.BuiltIn)
            && attack.EffectiveMaskPresetId is null, "Hybrid must use its own custom source and exclude the complete-password preset.");
        attack.Mask = "?d?d"; attack.Wordlists.Clear(); attack.Wordlists.Add("synthetic-wordlist.txt");
        var hybrid = await SaveAsync("Hybrid mask smoke");
        Require(hybrid.Configuration.Attack.Kind == AttackFamilies.HybridWordlistMask && hybrid.Configuration.Attack.Mask == "?d?d"
            && hybrid.Configuration.Attack.MaskPresetId is null, "Hybrid profiles must preserve their original mask semantics.");
        attack.Family = 1;
        Require(attack.IsBuiltInMask, "Returning from Hybrid must preserve the pure Mask source choice.");

        foreach (var profile in new[] { builtin, custom, fileProfile, hybrid }) await RemoveAsync(profile);
        attack.SelectedProfile = null; attack.ProfileName = ""; attack.Family = 0;
        attack.Mask = "?u?l?l?l?l?l?d?d"; attack.MaskFile = "";
        await ((AsyncCommand)attack.UseStarterCommand).ExecuteAsync();
        window.Width = 1380; window.Height = 920; scroll.ScrollToTop();
        services.Notice = "Mask source and profile checks passed.";
    }
}
