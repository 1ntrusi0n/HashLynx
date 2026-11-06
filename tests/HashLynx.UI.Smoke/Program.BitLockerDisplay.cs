using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using HashLynx.Hashcat;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Smoke;

internal sealed partial class SmokeApplication
{
    private async Task RenderBitLockerHashAsync(Window window, string name)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var scroll = Find<ScrollViewer>(window, "AttackScrollViewer");
        var source = Find<Grid>(window, "SourceContextControls");
        var previousOffset = scroll.VerticalOffset;
        try
        {
            scroll.ScrollToVerticalOffset(previousOffset + source.TranslatePoint(new Point(0, 0), scroll).Y - 70);
            await RenderAsync(window, name);
        }
        finally
        {
            scroll.ScrollToVerticalOffset(previousOffset);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    private static async Task RequireBitLockerDisplayAsync(InspectorViewModel inspector, Window window, string expected)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Require(inspector.HasExtractedBitLockerHash && inspector.ExtractedBitLockerHash == expected,
            "The inspector must retain every extracted BitLocker record for display.");
        var panel = Find<FrameworkElement>(window, "ExtractedBitLockerHashPanel");
        var text = Find<TextBox>(window, "ExtractedBitLockerHashText");
        var source = Find<Grid>(window, "SourceContextControls");
        Require(panel.IsVisible && text.IsVisible && text.Text == expected,
            "The real BitLocker hash display must be visible and bound to the extracted records.");
        Require(text.IsReadOnly && text.IsEnabled && text.Focusable && text.TextWrapping == TextWrapping.Wrap,
            "The extracted hash must wrap and remain selectable without permitting edits.");
        Require(text.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && text.MaxHeight <= 240,
            "Long extracted hashes must remain scrollable within a bounded panel.");
        Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(text)),
            "The extracted hash display must provide accessible help.");
        text.SelectAll();
        Require(text.SelectedText == expected, "Selecting the hash must retain its exact records for copying.");
        text.Select(0, 0);
        Require(panel.TranslatePoint(new Point(0, 0), window).Y >= source.TranslatePoint(new Point(0, source.ActualHeight), window).Y,
            "The extracted hash must appear below the source/context controls and analysis button.");
        Require(text.ActualWidth > 0 && text.ActualWidth <= source.ActualWidth + 1,
            "The hash must stay within the inspector width at narrow window sizes.");
    }

    private static async Task RequireNoBitLockerDisplayAsync(InspectorViewModel inspector, Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(!inspector.HasExtractedBitLockerHash && inspector.ExtractedBitLockerHash.Length == 0,
            "Invalidated, failed, cancelled, or unrelated targets must not retain an extracted BitLocker hash.");
        Require(Find<FrameworkElement>(window, "ExtractedBitLockerHashPanel").Visibility == Visibility.Collapsed
            && Find<TextBox>(window, "ExtractedBitLockerHashText").Text.Length == 0,
            "The real display must collapse and clear stale extracted hash text.");
    }

    private static async Task CheckBitLockerDisplayAfterIdentifyFailureAsync(InspectorViewModel inspector, AppServices services)
    {
        // A capability failure occurs before any process can launch, keeping this check completely offline.
        var installationProperty = typeof(AppServices).GetProperty(nameof(AppServices.Installation))!;
        var originalInstallation = services.Installation;
        installationProperty.SetValue(services, new HashcatInstallation(
            Path.Combine(services.Store.Paths.Root, "never-executed-synthetic-backend.exe"), "7.1.2", new() { Identify = false }));
        try
        {
            try
            {
                await inspector.AnalyzeAsync();
                throw new InvalidOperationException("Expected unsupported identification to fail.");
            }
            catch (HashcatException ex) when (ex.Message.Contains("does not support identification", StringComparison.Ordinal)) { }
            Require(inspector.HasExtractedBitLockerHash && inspector.ExtractedBitLockerHash == BitLockerFixtureText
                && inspector.PreparedTargetPath is not null,
                "Successful extraction must remain visible when later Hashcat identification fails.");
        }
        finally { installationProperty.SetValue(services, originalInstallation); }
    }

    private async Task CheckBitLockerImageDisplayAsync(InspectorViewModel inspector, AppServices services, Window window)
    {
        var imagePath = Path.Combine(services.Store.Paths.Root, "synthetic-display-bitlocker.raw");
        await File.WriteAllBytesAsync(imagePath, HashLynx.Extractors.Tests.BitLockerFixture.Image(toGo: true));
        inspector.InputMode = 2;
        inspector.TargetPath = imagePath;
        await RequireNoBitLockerDisplayAsync(inspector, window);
        var prepared = await inspector.PrepareTargetAsync();
        Require(await File.ReadAllTextAsync(prepared) == BitLockerFixtureText + Environment.NewLine,
            "The built-in raw-image extractor must prepare the same BitLocker record shown in the inspector.");
        await RequireBitLockerDisplayAsync(inspector, window, BitLockerFixtureText);
        await RenderBitLockerHashAsync(window, "BitLocker-image-hash");

        var archivePath = Path.Combine(services.Store.Paths.Root, "synthetic-display-non-bitlocker.7z");
        await File.WriteAllBytesAsync(archivePath, HashLynx.Extractors.Tests.NativeArchiveFixtures.Solid);
        inspector.TargetPath = archivePath;
        await RequireNoBitLockerDisplayAsync(inspector, window);
        await inspector.PrepareTargetAsync();
        await RequireNoBitLockerDisplayAsync(inspector, window);

        inspector.TargetPath = imagePath;
        await inspector.PrepareTargetAsync();
        await RequireBitLockerDisplayAsync(inspector, window, BitLockerFixtureText);
        inspector.TargetPath = Path.Combine(services.Store.Paths.Root, "missing-bitlocker-image.raw");
        await RequireNoBitLockerDisplayAsync(inspector, window);
        try { await inspector.PrepareTargetAsync(); throw new InvalidOperationException("Expected a missing image error."); }
        catch (InvalidOperationException ex) when (ex.Message == "Choose an existing encrypted file.") { }
        await RequireNoBitLockerDisplayAsync(inspector, window);

        inspector.InputMode = 0;
        inspector.HashText = BitLockerFixtureText;
        await inspector.PrepareTargetAsync();
        await RequireNoBitLockerDisplayAsync(inspector, window);
        inspector.HashText = "synthetic changed target";
        await RequireNoBitLockerDisplayAsync(inspector, window);
        await File.WriteAllTextAsync(Path.Combine(output, "bitlocker-display-result.txt"),
            "PASS: synthetic drive and raw-image BitLocker extraction; exact selectable read-only text; multiple records; source/context placement; narrow Light/Dark layouts; later identification failure retains extraction; drive, file and input changes clear display; cancellation and late-result rejection; extraction failure; unrelated archive and pasted-hash exclusion. No backend process or live volume was used.");
    }
}
