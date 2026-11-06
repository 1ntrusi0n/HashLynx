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
    private async Task RenderExtractedHashAsync(Window window, string name)
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

    private static async Task RequireExtractedHashDisplayAsync(InspectorViewModel inspector, Window window, string expected, string expectedType = "BitLocker")
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Require(inspector.HasExtractedHash && inspector.ExtractedHash == expected,
            "The inspector must retain every extracted record for display and copying.");
        var panel = Find<FrameworkElement>(window, "ExtractedHashPanel");
        var text = Find<TextBox>(window, "ExtractedHashText");
        var source = Find<Grid>(window, "SourceContextControls");
        var preview = expected[..Math.Min(expected.Length, 16384)];
        Require(panel.IsVisible && text.IsVisible && text.Text == preview && inspector.ExtractedHashPreview == preview,
            "The real extracted hash display must be visible and bound to the exact record or bounded prefix.");
        Require(Find<TextBlock>(window, "ExtractedHashLabel").Text == inspector.ExtractedHashLabel
            && inspector.ExtractedHashLabel.Contains(expectedType, StringComparison.OrdinalIgnoreCase),
            "The extracted hash label must identify the selected file type.");
        Require((expected.Length > 16384) == inspector.ExtractedHashDisplayNotice.StartsWith("Showing the first", StringComparison.Ordinal)
            && inspector.ExtractedHashDisplayNotice.Contains("Copy full hash", StringComparison.Ordinal),
            "A truncated display must clearly explain that the complete hash remains available for copying.");
        var copy = Find<Button>(window, "CopyExtractedHashButton");
        Require(copy.IsVisible && copy.IsEnabled && ReferenceEquals(copy.Command, inspector.CopyExtractedHashCommand)
            && inspector.CopyExtractedHashCommand.CanExecute(null),
            "The copy button must make the complete retained hash available without invoking the clipboard in this test.");
        Require(text.IsReadOnly && text.IsEnabled && text.Focusable && text.TextWrapping == TextWrapping.Wrap,
            "The extracted hash must wrap and remain selectable without permitting edits.");
        Require(text.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && text.MaxHeight <= 240,
            "Long extracted hashes must remain scrollable within a bounded panel.");
        Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(text)),
            "The extracted hash display must provide accessible help.");
        text.SelectAll();
        Require(text.SelectedText == preview, "Selecting the displayed hash must preserve the exact displayed text.");
        text.Select(0, 0);
        Require(panel.TranslatePoint(new Point(0, 0), window).Y >= source.TranslatePoint(new Point(0, source.ActualHeight), window).Y,
            "The extracted hash must appear below the source/context controls and analysis button.");
        Require(text.ActualWidth > 0 && text.ActualWidth <= source.ActualWidth + 1,
            "The hash must stay within the inspector width at narrow window sizes.");
    }

    private static async Task RequireNoExtractedHashDisplayAsync(InspectorViewModel inspector, Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(!inspector.HasExtractedHash && inspector.ExtractedHash.Length == 0,
            "Invalidated, failed, cancelled, or imported targets must not retain an extracted hash.");
        Require(inspector.ExtractedHashPreview.Length == 0
            && !inspector.ExtractedHashDisplayNotice.StartsWith("Showing the first", StringComparison.Ordinal)
            && !inspector.CopyExtractedHashCommand.CanExecute(null),
            "Clearing a target must clear its preview, notice, and copy availability.");
        Require(Find<FrameworkElement>(window, "ExtractedHashPanel").Visibility == Visibility.Collapsed
            && Find<TextBox>(window, "ExtractedHashText").Text.Length == 0,
            "The real display must collapse and clear stale extracted hash text.");
    }

    private static async Task CheckExtractedHashAfterIdentifyFailureAsync(InspectorViewModel inspector, AppServices services)
    {
        // A capability failure occurs before any process can launch, keeping this check completely offline.
        var installationProperty = typeof(AppServices).GetProperty(nameof(AppServices.Installation))!;
        var originalInstallation = services.Installation;
        var expected = inspector.ExtractedHash;
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
            Require(inspector.HasExtractedHash && inspector.ExtractedHash == expected
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
        await RequireNoExtractedHashDisplayAsync(inspector, window);
        var prepared = await inspector.PrepareTargetAsync();
        Require(await File.ReadAllTextAsync(prepared) == BitLockerFixtureText + Environment.NewLine,
            "The built-in raw-image extractor must prepare the same BitLocker record shown in the inspector.");
        await RequireExtractedHashDisplayAsync(inspector, window, BitLockerFixtureText);
        await RenderExtractedHashAsync(window, "BitLocker-image-hash");

        var archivePath = Path.Combine(services.Store.Paths.Root, "synthetic-display-non-bitlocker.7z");
        await File.WriteAllBytesAsync(archivePath, HashLynx.Extractors.Tests.NativeArchiveFixtures.Solid);
        inspector.TargetPath = archivePath;
        await RequireNoExtractedHashDisplayAsync(inspector, window);
        var archiveTarget = await inspector.PrepareTargetAsync();
        var archiveHash = (await File.ReadAllTextAsync(archiveTarget)).TrimEnd('\r', '\n');
        Require(archiveHash.StartsWith("$7z$", StringComparison.Ordinal), "The archive fixture must produce a 7-Zip recovery record.");
        await RequireExtractedHashDisplayAsync(inspector, window, archiveHash, "7-Zip");

        inspector.TargetPath = imagePath;
        await inspector.PrepareTargetAsync();
        await RequireExtractedHashDisplayAsync(inspector, window, BitLockerFixtureText);
        inspector.TargetPath = Path.Combine(services.Store.Paths.Root, "missing-bitlocker-image.raw");
        await RequireNoExtractedHashDisplayAsync(inspector, window);
        try { await inspector.PrepareTargetAsync(); throw new InvalidOperationException("Expected a missing image error."); }
        catch (InvalidOperationException ex) when (ex.Message == "Choose an existing encrypted file.") { }
        await RequireNoExtractedHashDisplayAsync(inspector, window);

        inspector.InputMode = 0;
        inspector.HashText = BitLockerFixtureText;
        await inspector.PrepareTargetAsync();
        await RequireNoExtractedHashDisplayAsync(inspector, window);
        inspector.HashText = "synthetic changed target";
        await RequireNoExtractedHashDisplayAsync(inspector, window);
        await File.WriteAllTextAsync(Path.Combine(output, "bitlocker-display-result.txt"),
            "PASS: synthetic drive and raw-image BitLocker extraction; exact selectable read-only text; multiple records; source/context placement; narrow Light/Dark layouts; later identification failure retains extraction; drive, file and input changes clear display; cancellation and late-result rejection; extraction failure; changing to an archive replaces the display; pasted hashes remain separate. No backend process or live volume was used.");
    }
}
