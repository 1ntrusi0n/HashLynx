using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace HashLynx.Updates;

/// <summary>Replaces only manifest-owned application files; user files and LocalApplicationData are never migrated.</summary>
public static class ApplicationUpdateInstaller
{
    public const string ManifestFileName = "hashlynx-update.json";

    public static void ValidateInstallation(string installationDirectory)
    {
        if (string.IsNullOrWhiteSpace(installationDirectory) || !Path.IsPathFullyQualified(installationDirectory))
            throw new InvalidOperationException("An installed application folder is required.");
        var root = Path.GetFullPath(installationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        UpdateFiles.EnsureNoReparsePoints(root);
        if (root.Length == 2 || !Directory.Exists(root) || Directory.Exists(Path.Combine(root, ".git")) || Directory.Exists(Path.Combine(root, "src")) ||
            File.Exists(Path.Combine(root, "HashLynx.sln")) || File.Exists(Path.Combine(root, "HashLynx.UI.csproj")))
            throw new InvalidOperationException("In-app updates require a published application folder. Source and development builds cannot be replaced.");
        var state = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HashLynx"));
        if (root.Equals(state, StringComparison.OrdinalIgnoreCase) || root.StartsWith(state + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The application cannot be updated inside its local data directory. Use a separate application folder.");
        _ = UpdateFiles.ReadManifest(root);
        var probe = Path.Combine(root, ".hashlynx-write-check-" + Guid.NewGuid().ToString("N"));
        try { using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("The application folder is not writable. Download the release and extract it into a folder you can write to; administrator access is not requested.", ex); }
    }

    public static async Task<UpdateInstallResult> ApplyAsync(string planPath, CancellationToken cancellationToken = default)
    {
        var (stage, plan) = ReadPlan(planPath);
        var installation = plan.InstallationDirectory;
        ValidateInstallation(installation);
        var lockPath = Path.Combine(installation, ".hashlynx-update.lock");
        UpdateFiles.EnsureNoReparsePoints(lockPath);
        using var updateLock = AcquireInstallationLock(lockPath);
        var backup = Path.Combine(stage, "backup");
        var journalPath = Path.Combine(stage, "transaction.json");
        if (File.Exists(journalPath))
        {
            var previous = ReadJournal(journalPath, installation);
            if (previous.State is "applying" or "recovery-needed")
            {
                var recovered = RollBack(previous, installation, backup);
                previous.State = recovered ? "rolled-back" : "recovery-needed";
                await WriteDurableAsync(journalPath, previous, CancellationToken.None).ConfigureAwait(false);
                return new(false, recovered, recovered ? "An interrupted update was rolled back. Open HashLynx and download the update again." :
                    "The interrupted update needs manual recovery. Keep the backup directory and reinstall the published application files; your local data was not changed.");
            }
            throw new InvalidOperationException("This update stage has already been used. Download the release again to retry.");
        }
        if (!string.Equals(await UpdateFiles.HashAsync(Path.Combine(installation, ManifestFileName), cancellationToken).ConfigureAwait(false),
            plan.PreviousManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installed application changed after the update was prepared. Download the update again.");
        var original = UpdateFiles.ReadManifest(installation);
        var package = Path.Combine(stage, "package");
        var replacement = UpdateFiles.ReadManifest(package);
        if (!ApplicationVersion.TryParse(plan.Version, out var desired) || !ApplicationVersion.TryParse(replacement.Version, out var next) ||
            !ApplicationVersion.TryParse(original.Version, out var current) || desired.CompareTo(next) != 0 || next.CompareTo(current) <= 0)
            throw new InvalidDataException("The update version is not newer than the installed application.");
        await UpdateFiles.VerifyFilesAsync(package, replacement, cancellationToken).ConfigureAwait(false);
        var oldPaths = original.Files.Select(file => file.Path).Append(ManifestFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newPaths = replacement.Files.Select(file => file.Path).Append(ManifestFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = oldPaths.Union(newPaths, StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var journal = new UpdateJournal { InstallationDirectory = installation };
        foreach (var relative in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = UpdateFiles.Child(installation, relative);
            if (Directory.Exists(destination) || (!oldPaths.Contains(relative) && File.Exists(destination)))
                throw new InvalidOperationException("An update file conflicts with an existing user file. Move that file aside yourself before updating.");
            // Validate all parent paths before creating a backup or changing any application file.
            for (var parent = Path.GetDirectoryName(destination); parent is not null && !parent.Equals(installation, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
                if (File.Exists(parent)) throw new InvalidOperationException("An update directory conflicts with an existing user file.");
            journal.Files.Add(new(relative, File.Exists(destination)));
        }
        if (Directory.Exists(backup)) throw new InvalidOperationException("The update backup directory already exists. Prepare a fresh update.");
        Directory.CreateDirectory(backup);
        foreach (var entry in journal.Files.Where(file => file.Existed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = UpdateFiles.Child(installation, entry.Path);
            var target = UpdateFiles.Child(backup, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, false);
            using var durable = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            durable.Flush(true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        journal.State = "applying";
        await WriteDurableAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
        try
        {
            // Once replacement starts, complete or roll back; cancellation must not leave a partial application.
            foreach (var relative in newPaths.Where(path => path != ManifestFileName))
                ReplaceFile(UpdateFiles.Child(package, relative), UpdateFiles.Child(installation, relative));
            foreach (var relative in oldPaths.Except(newPaths, StringComparer.OrdinalIgnoreCase))
            {
                var destination = UpdateFiles.Child(installation, relative);
                if (File.Exists(destination)) File.Delete(destination);
            }
            ReplaceFile(UpdateFiles.Child(package, ManifestFileName), UpdateFiles.Child(installation, ManifestFileName));
            journal.State = "complete";
            await WriteDurableAsync(journalPath, journal, CancellationToken.None).ConfigureAwait(false);
            return new(true, false, "HashLynx was updated. Your settings, recovery history and separate Hashcat installation were preserved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var restored = RollBack(journal, installation, backup);
            journal.State = restored ? "rolled-back" : "recovery-needed";
            try { await WriteDurableAsync(journalPath, journal, CancellationToken.None).ConfigureAwait(false); } catch (IOException) { }
            return new(false, restored, restored ? "The update could not replace all application files. The previous application was restored; close other HashLynx instances and try again." :
                "The update could not finish or fully restore application files. Keep the update backup and reinstall the published application. Your local settings and recovery data were not changed.");
        }
    }

    public static string GetInstallationDirectory(string planPath) => ReadPlan(planPath).Plan.InstallationDirectory;

    private static FileStream AcquireInstallationLock(string path)
    {
        try { return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException ex) { throw new InvalidOperationException("Another update owns this application folder, or an update lock file already exists. No application files were changed.", ex); }
    }

    public static async Task<bool> WaitForParentExitAsync(int processId, long startTimeUtcTicks, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (processId <= 0 || processId == Environment.ProcessId || startTimeUtcTicks <= 0 || timeout <= TimeSpan.Zero)
            throw new ArgumentException("A valid parent process identity and timeout are required.");
        Process parent;
        try { parent = Process.GetProcessById(processId); }
        catch (ArgumentException) { return true; }
        using (parent)
        {
            try
            {
                if (parent.StartTime.ToUniversalTime().Ticks != startTimeUtcTicks) return true;
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(timeout);
                try { await parent.WaitForExitAsync(limit.Token).ConfigureAwait(false); return true; }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
            }
            catch (InvalidOperationException) { return true; }
        }
    }

    private static (string Stage, ApplicationUpdatePlan Plan) ReadPlan(string path)
    {
        path = Path.GetFullPath(path);
        UpdateFiles.EnsureNoReparsePoints(path);
        if (!Path.GetFileName(path).Equals("plan.json", StringComparison.Ordinal) || !File.Exists(path) || new FileInfo(path).Length > 16384)
            throw new InvalidDataException("The update plan is missing or invalid.");
        ApplicationUpdatePlan plan;
        try { plan = JsonSerializer.Deserialize<ApplicationUpdatePlan>(File.ReadAllText(path), UpdateFiles.JsonOptions) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("The update plan could not be read.", ex); }
        var stage = Path.GetDirectoryName(path)!;
        if (plan.SchemaVersion != 1 || !Path.IsPathFullyQualified(plan.InstallationDirectory) ||
            !UpdateFiles.IsSha256(plan.PreviousManifestSha256) || !ApplicationVersion.TryParse(plan.Version, out var version) || version.IsPrerelease)
            throw new InvalidDataException("The update plan contains unsupported values.");
        plan.InstallationDirectory = Path.GetFullPath(plan.InstallationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (stage.Equals(plan.InstallationDirectory, StringComparison.OrdinalIgnoreCase) ||
            stage.StartsWith(plan.InstallationDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            plan.InstallationDirectory.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update staging and application directories must be separate.");
        UpdateFiles.EnsureNoReparsePoints(plan.InstallationDirectory);
        UpdateFiles.EnsureNoReparsePoints(Path.Combine(stage, "package"));
        UpdateFiles.EnsureNoReparsePoints(Path.Combine(stage, "backup"));
        return (stage, plan);
    }

    private static UpdateJournal ReadJournal(string path, string installation)
    {
        UpdateFiles.EnsureNoReparsePoints(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The update journal exceeds its supported size.");
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(path), UpdateFiles.JsonOptions) ?? throw new InvalidDataException("The update journal is empty.");
        if (journal.InstallationDirectory != installation || journal.Files is null || journal.Files.Count is 0 or > UpdateFiles.MaximumEntries)
            throw new InvalidDataException("The update journal does not belong to this application.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Files)
            if (!paths.Add(UpdateFiles.Relative(entry.Path))) throw new InvalidDataException("The update journal has duplicate paths.");
        return journal;
    }

    private static bool RollBack(UpdateJournal journal, string installation, string backup)
    {
        var succeeded = true;
        foreach (var entry in journal.Files)
        {
            try
            {
                var destination = UpdateFiles.Child(installation, entry.Path);
                if (entry.Existed)
                {
                    var source = UpdateFiles.Child(backup, entry.Path);
                    // An unchanged file can still be locked by another process. It already is the desired rollback state.
                    if (!SameContent(source, destination)) ReplaceFile(source, destination);
                }
                else if (File.Exists(destination)) File.Delete(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { succeeded = false; }
        }
        return succeeded;
    }

    private static bool SameContent(string source, string destination)
    {
        if (!File.Exists(source) || !File.Exists(destination) || new FileInfo(source).Length != new FileInfo(destination).Length) return false;
        using var left = File.OpenRead(source);
        using var right = File.OpenRead(destination);
        return SHA256.HashData(left).AsSpan().SequenceEqual(SHA256.HashData(right));
    }

    private static void ReplaceFile(string source, string destination)
    {
        UpdateFiles.EnsureNoReparsePoints(source);
        UpdateFiles.EnsureNoReparsePoints(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".hashlynx-update-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, false);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough)) stream.Flush(true);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteDurableAsync<T>(string path, T value, CancellationToken ct)
    {
        UpdateFiles.EnsureNoReparsePoints(path);
        var temporary = path + ".tmp";
        UpdateFiles.EnsureNoReparsePoints(temporary);
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, value, UpdateFiles.JsonOptions, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    private sealed class UpdateJournal
    {
        public string InstallationDirectory { get; set; } = "";
        public string State { get; set; } = "";
        public List<UpdateJournalFile> Files { get; set; } = [];
    }
    private sealed record UpdateJournalFile(string Path, bool Existed);
}
