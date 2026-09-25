using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HashLynx.Core;
using HashLynx.Persistence;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Smoke;

internal sealed partial class SmokeApplication
{
    private static HashcatJob BuildDraft(AttackViewModel attack)
        => (HashcatJob)typeof(AttackViewModel).GetMethod("BuildDraft", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(attack, [""])!;

    private async Task CheckUsabilityAsync(ShellViewModel shell, AppServices services, PersistenceStore store, Window window)
    {
        var attack = shell.Attack;
        shell.Selected = shell.Navigation.First(item => item.Page == attack);
        attack.Expert = false;
        attack.Family = 0;
        attack.SelectedRulePreset = attack.RulePresets[0];
        attack.Inspector.InputMode = 0;
        attack.Inspector.HashText = "";
        attack.Inspector.SelectedMode = null;
        await attack.RefreshReadinessAsync();
        Require(!attack.TargetReady && attack.InputsReady && !attack.DeviceReady,
            "An empty target and absent backend must need attention while the starter list remains usable.");
        Require(attack.DeviceReadiness.Contains("Settings", StringComparison.Ordinal), "Missing backend must offer setup.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(Find<Border>(window, "ReadinessCard").IsVisible, "Readiness guidance must remain available in Basic mode.");

        foreach (var theme in new[] { "Light", "Dark" })
        {
            AppServices.ApplyTheme(theme);
            attack.Inspector.HashText = "";
            await attack.RefreshReadinessAsync();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(ReadinessColor(window, "TargetReadinessText") == ResourceColor(window, "ErrorBrush")
                && ReadinessColor(window, "InputsReadinessText") == ResourceColor(window, "SuccessBrush"),
                "Missing targets must appear red and available inputs green in each theme.");

            attack.Inspector.HashText = "synthetic hash for readiness only";
            await attack.RefreshReadinessAsync();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(attack.TargetReadinessState == ReadinessState.Pending
                && ReadinessColor(window, "TargetReadinessText") == ResourceColor(window, "WarningBrush"),
                "A supplied target awaiting analysis must appear amber.");
            attack.Inspector.SelectedMode = new HashMode(1000, "Synthetic NTLM mode", "Test");
            await attack.RefreshReadinessAsync();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(attack.TargetReady && ReadinessColor(window, "TargetReadinessText") == ResourceColor(window, "SuccessBrush"),
                "A supplied target and explicit mode must update to green without starting a backend.");
            await RenderAsync(window, "Readiness-colors-" + theme);
        }

        var targetRevision = attack.Inspector.TargetRevision;
        attack.Inspector.HashText = "";
        await attack.RefreshReadinessAsync();
        Require(attack.Inspector.TargetRevision > targetRevision && !attack.TargetReady && attack.Inspector.SelectedMode is null,
            "Clearing the target must invalidate readiness and the old mode.");

        attack.UseCustomRules = true;
        attack.Rules.Add(Path.Combine(store.Paths.Root, "missing-hidden.rule"));
        attack.Devices = "invalid hidden IDs";
        attack.Temperature = "invalid hidden temperature";
        attack.ExtraArguments = "--limit=1";
        await attack.RefreshReadinessAsync();
        Require(attack.InputsReady, "Basic readiness must ignore hidden Expert settings and rule files.");
        attack.Rules.Clear(); attack.UseCustomRules = false; attack.Devices = ""; attack.Temperature = ""; attack.ExtraArguments = "";

        attack.Family = 1;
        attack.SelectedMaskSource = MaskSourceChoice.File;
        var maskFile = Path.Combine(store.Paths.Root, "usability-masks.hcmask");
        await File.WriteAllTextAsync(maskFile, "?d\n");
        attack.MaskFile = maskFile;
        await attack.RefreshReadinessAsync();
        Require(attack.InputsReady, "An available selected mask file must pass the input readiness check.");
        File.Delete(maskFile);
        await attack.RefreshReadinessAsync();
        Require(!attack.InputsReady && attack.SelectedMaskSource == MaskSourceChoice.File,
            "A removed mask file must need attention without falling back to another source.");

        window.Width = 1040; window.Height = 700;
        var scroll = Find<ScrollViewer>(window, "AttackScrollViewer");
        scroll.ScrollToTop();
        await RenderAsync(window, "Readiness-narrow");
        Require(scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible
            && Find<Border>(window, "ReadinessCard").ActualWidth <= scroll.ViewportWidth,
            "The readiness card must fit the narrow viewport and retain page scrolling.");
        attack.ReviewDeviceCommand.Execute(null);
        Require(shell.Selected.Page == shell.Settings, "The missing-device action must navigate directly to backend setup.");
        shell.Selected = shell.Navigation.First(item => item.Page == attack);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        attack.ReviewInputsCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(Find<ScrollViewer>(window, "AttackScrollViewer").VerticalOffset > 0, "The wordlist/mask action must bring its section into view.");

        attack.Family = 0;
        attack.Mask = "?u?l?l?l?l?l?d?d";
        attack.MaskFile = "";
        await ((AsyncCommand)attack.UseStarterCommand).ExecuteAsync();
        await attack.RefreshReadinessAsync();
        window.Width = 1380; window.Height = 920;
        Find<ScrollViewer>(window, "AttackScrollViewer").ScrollToTop();
        services.Notice = "Readiness colors and Basic guidance checks passed.";
    }

    private static Color ReadinessColor(Window window, string name)
        => ((SolidColorBrush)Find<TextBlock>(window, name).Foreground).Color;

    private static Color ResourceColor(Window window, string name)
        => ((SolidColorBrush)window.FindResource(name)).Color;
}
