using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using HashLynx.Extractors;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Smoke;

internal sealed partial class SmokeApplication
{
    private async Task CheckEncryptedFileHashDisplayAsync(InspectorViewModel inspector, AppServices services, Window window)
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var keepassName = "keepass4_keepassxc_2.7.9_aeskdf_defaultsettings";
        var cases = new (string Extension, string Label, byte[] Bytes, string? Expected)[]
        {
            ("pdf", "PDF", await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, "Pdf", "r6.pdf")),
                (await File.ReadAllTextAsync(Path.Combine(fixtureRoot, "Pdf", "r6.expected"))).Trim()),
            ("docx", "Office", await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, "Office", "example_password.docx")),
                (await File.ReadAllTextAsync(Path.Combine(fixtureRoot, "Office", "example_password.docx.expected"))).Trim()),
            ("xlsx", "Office", await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, "Office", "example_password.xlsx")),
                (await File.ReadAllTextAsync(Path.Combine(fixtureRoot, "Office", "example_password.xlsx.expected"))).Trim()),
            ("7z", "7-Zip", HashLynx.Extractors.Tests.NativeArchiveFixtures.Solid, null),
            ("zip", "ZIP", CreateEncryptedZipDisplayFixture(), null),
            ("rar", "RAR", Convert.FromBase64String("UmFyIRoHAM+QcwAADQAAAAAAAABz53QEhCkAEAAAAA4AAAACSbCoRgAAAAAdMAEAIAAAAGHlSnNymIfLUzRiC8yoF2ZCohCxBRkBkh4EsHsAAAcA"), null),
            ("kdbx", "KeePass", await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, "KeePass", keepassName + ".kdbx")),
                (await File.ReadAllTextAsync(Path.Combine(fixtureRoot, "KeePass", keepassName + ".hash"))).Trim())
        };
        var hashes = new List<string>();
        var previousWidth = window.Width;
        try
        {
            window.Width = 1040;
            inspector.InputMode = 2;
            foreach (var item in cases)
            {
                var sourcePath = Path.Combine(services.Store.Paths.Root, "synthetic-hash-display." + item.Extension);
                await File.WriteAllBytesAsync(sourcePath, item.Bytes);
                inspector.TargetPath = sourcePath;
                await RequireNoExtractedHashDisplayAsync(inspector, window);
                var prepared = await inspector.PrepareTargetAsync();
                var saved = await File.ReadAllTextAsync(prepared);
                Require(saved.EndsWith(Environment.NewLine, StringComparison.Ordinal), "Prepared extracted hashes must retain their terminating newline.");
                var expected = item.Expected ?? saved[..^Environment.NewLine.Length];
                Require(saved == expected + Environment.NewLine, "The displayed encrypted-file hash must match the complete prepared target.");
                var sourceAfterExtraction = await File.ReadAllBytesAsync(sourcePath);
                Require(SHA256.HashData(item.Bytes).SequenceEqual(SHA256.HashData(sourceAfterExtraction)),
                    "Displaying a hash must leave the encrypted source untouched.");
                await RequireExtractedHashDisplayAsync(inspector, window, expected, item.Label);
                hashes.Add(expected);
                await CheckExtractedHashAfterIdentifyFailureAsync(inspector, services);
                await RequireExtractedHashDisplayAsync(inspector, window, expected, item.Label);
                Require(!inspector.Summary.Contains(expected, StringComparison.Ordinal)
                    && !inspector.ExtractionNotice.Contains(expected, StringComparison.Ordinal)
                    && !inspector.Problems.Contains(expected, StringComparison.Ordinal),
                    "Extracted records belong in the hash display, not summaries or diagnostic fields.");
                if (item.Extension is "pdf" or "7z")
                    foreach (var theme in new[] { "Light", "Dark" })
                    {
                        AppServices.ApplyTheme(theme);
                        await RequireExtractedHashDisplayAsync(inspector, window, expected, item.Label);
                        await RenderExtractedHashAsync(window, "Encrypted-" + item.Extension + "-hash-" + theme + "-narrow");
                    }
            }
            await CheckEncryptedHashLifecycleAsync(inspector, services, window);
            foreach (var logPath in Directory.EnumerateFiles(services.Store.Paths.LogsDirectory, "*.jsonl"))
            {
                var log = await File.ReadAllTextAsync(logPath);
                Require(hashes.All(hash => !log.Contains(hash, StringComparison.Ordinal)),
                    "Diagnostic logs must not contain extracted encrypted-file records.");
            }
        }
        finally
        {
            window.Width = previousWidth;
            AppServices.ApplyTheme(services.Settings.Theme);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "encrypted-hash-display-result.txt"),
            "PASS: native PDF, Word, Excel, 7-Zip, ZIP, RAR and KeePass extraction; exact selectable display and saved-target parity; type labels; source/context placement; narrow Light/Dark screenshots; complete copy command availability; bounded long-hash preview with full target retention; multiple records; later identification failure preserves extraction; target/input changes, cancellation, failed and late extraction clear or reject stale data; no hashes in diagnostic logs. No backend process, clipboard mutation or live volume was used.");
    }

    private async Task CheckEncryptedHashLifecycleAsync(InspectorViewModel inspector, AppServices services, Window window)
    {
        var registryProperty = typeof(AppServices).GetProperty(nameof(AppServices.Extractors))!;
        var original = services.Extractors;
        var extractor = new HashDisplayFixtureExtractor();
        var path = Path.Combine(services.Store.Paths.Root, "synthetic-lifecycle.displayfixture");
        var replacement = Path.Combine(services.Store.Paths.Root, "synthetic-replacement.displayfixture");
        await File.WriteAllTextAsync(path, "Synthetic extractor lifecycle fixture.");
        await File.WriteAllTextAsync(replacement, "Synthetic replacement fixture.");
        registryProperty.SetValue(services, new ExtractorRegistry([extractor]));
        try
        {
            inspector.InputMode = 2;
            inspector.TargetPath = path;
            var longHash = "$7z$synthetic-large-display$" + new string('a', 80_000);
            extractor.Result = new ExtractionResult
            {
                Success = true, Hashes = [longHash, "$7z$synthetic-second-record"], SourceFileType = "7z",
                ExtractorName = "Synthetic display fixture", Diagnostics = ["Synthetic metadata only."]
            };
            var expected = string.Join(Environment.NewLine, extractor.Result.Hashes);
            var prepared = await inspector.PrepareTargetAsync();
            Require(await File.ReadAllTextAsync(prepared) == expected + Environment.NewLine,
                "A bounded display must never truncate the prepared recovery target.");
            await RequireExtractedHashDisplayAsync(inspector, window, expected, "7-Zip");
            await RenderExtractedHashAsync(window, "Encrypted-large-hash-preview");
            Require(!inspector.ExtractedHashDisplayNotice.Contains(longHash, StringComparison.Ordinal),
                "The preview notice must contain guidance rather than the extracted record.");

            inspector.InputMode = 1;
            inspector.TargetPath = prepared;
            await inspector.PrepareTargetAsync();
            await RequireNoExtractedHashDisplayAsync(inspector, window);
            inspector.InputMode = 2;
            inspector.TargetPath = path;
            extractor.Result = new ExtractionResult { Diagnostics = ["Synthetic encrypted file is unsupported."] };
            try { await inspector.PrepareTargetAsync(); throw new InvalidOperationException("Expected extraction failure."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Synthetic encrypted file is unsupported.", StringComparison.Ordinal)) { }
            Require(inspector.PreparedTargetPath is null, "Failed extraction must leave no prepared target.");
            await RequireNoExtractedHashDisplayAsync(inspector, window);

            extractor.Result = new ExtractionResult { Success = true, SourceFileType = "7z" };
            try { await inspector.PrepareTargetAsync(); throw new InvalidOperationException("Expected empty extraction rejection."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("returned no supported hash records", StringComparison.Ordinal)) { }
            Require(inspector.PreparedTargetPath is null, "An empty successful extractor response must not produce an attack target.");
            await RequireNoExtractedHashDisplayAsync(inspector, window);

            extractor.StartPending();
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = inspector.PrepareTargetAsync(cancellation.Token);
                await extractor.Started.Task;
                cancellation.Cancel();
                try { await pending; throw new InvalidOperationException("Expected cancelled encrypted-file extraction."); }
                catch (OperationCanceledException) { }
            }
            Require(inspector.PreparedTargetPath is null, "Cancelled encrypted-file extraction must leave no prepared target.");
            await RequireNoExtractedHashDisplayAsync(inspector, window);

            extractor.StartPending(ignoreCancellation: true);
            var late = inspector.PrepareTargetAsync();
            await extractor.Started.Task;
            inspector.TargetPath = replacement;
            extractor.Pending!.SetResult(new ExtractionResult
            {
                Success = true, Hashes = ["$pdf$synthetic-stale-record"], SourceFileType = "pdf", ExtractorName = "Synthetic PDF"
            });
            try { await late; throw new InvalidOperationException("Expected stale encrypted-file extraction rejection."); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("The target changed", StringComparison.Ordinal)) { }
            Require(inspector.PreparedTargetPath is null, "Late extraction must not replace the current target.");
            await RequireNoExtractedHashDisplayAsync(inspector, window);
        }
        finally
        {
            inspector.InputMode = 0;
            registryProperty.SetValue(services, original);
        }
    }

    private static byte[] CreateEncryptedZipDisplayFixture()
    {
        // Structural encrypted ZIP only; dummy bytes are never used for recovery.
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        using (var entry = archive.CreateEntry("synthetic.txt", CompressionLevel.NoCompression).Open())
            entry.Write(new byte[16]);
        var bytes = memory.ToArray();
        var directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6, 4));
        bytes[6] |= 1; bytes[directory + 8] |= 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 24, 4), 4);
        return bytes;
    }

    private sealed class HashDisplayFixtureExtractor : IHashExtractor
    {
        public string Id => "display-fixture";
        public string DisplayName => "7-Zip";
        public string Description => "Offline display and lifecycle test only.";
        public string ImplementationType => "Synthetic";
        public bool IsBuiltIn => true;
        public IReadOnlyList<string> SupportedExtensions => [".displayfixture"];
        public ExtractionResult Result { get; set; } = new();
        public TaskCompletionSource<ExtractionResult>? Pending { get; private set; }
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool ignoreCancellation;
        public void StartPending(bool ignoreCancellation = false)
        {
            this.ignoreCancellation = ignoreCancellation;
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public Task<ExtractorAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExtractorAvailability { IsAvailable = true });
        public Task<ExtractorAvailability> ValidateAsync(CancellationToken cancellationToken = default) => GetAvailabilityAsync(cancellationToken);
        public Task<bool> CanHandleAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<ExtractionResult> ExtractAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return Pending is null ? Task.FromResult(Result) : ignoreCancellation ? Pending.Task : Pending.Task.WaitAsync(cancellationToken);
        }
    }
}
