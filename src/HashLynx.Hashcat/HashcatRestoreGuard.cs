using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HashLynx.Core;

namespace HashLynx.Hashcat;

/// <summary>Restore positions are meaningful only for the original backend, configuration and inputs.</summary>
internal static class HashcatRestoreGuard
{
    public static void Record(HashcatInstallation installation, JobRecord record, HashcatCommandBuilder builder)
    {
        HashcatCommandBuilder.SynchronizeCompatibilityOptions(record.Configuration);
        record.BackendVersion = installation.Version;
        record.BackendRuntimeDirectory = Path.GetFullPath(installation.WorkingDirectory);
        record.ConfigurationFingerprint = Fingerprint(record.Configuration);
        record.InputSnapshots = Inputs(installation, record.Configuration, builder).Select(path =>
        {
            var file = new FileInfo(Path.GetFullPath(path));
            return new SessionInputSnapshot(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
        }).ToList();
    }

    public static RestoreAvailability Check(HashcatInstallation installation, JobRecord record, HashcatCommandBuilder builder)
    {
        try
        {
            var path = builder.GetRestorePath(record.Configuration);
            if (!installation.Capabilities.Restore || !File.Exists(path)) return new(false, "No saved checkpoint is available for this session.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[296]; // official Windows x64 restore_data_t, never its stored argv
            if (input.ReadAtLeast(header, header.Length, false) != header.Length) return new(false, "The checkpoint is incomplete. Its data was preserved; retry as a new session.");
            var version = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (installation.IsVersion8OrLater && version < 720)
                return new(false, "This checkpoint was created by an older Hashcat and cannot resume in Hashcat 8. Select the matching older backend or retry as a new session. Existing results and the checkpoint are preserved.");
            if (installation.IsVersion8OrLater && (version != 720 || !installation.Capabilities.RestorePosition))
                return new(false, "This backend or checkpoint uses an unsupported restore format. Select the original backend or retry as a new session.");
            if (!installation.IsVersion8OrLater && version is < 600 or > 611)
                return new(false, "This checkpoint requires its original Hashcat version. Select that backend or retry as a new session.");
            var cwdBytes = header.Slice(4, 256);
            var terminator = cwdBytes.IndexOf((byte)0);
            if (terminator < 1) return new(false, "The checkpoint directory is invalid. Its data was preserved.");
            var cwd = new UTF8Encoding(false, true).GetString(cwdBytes[..terminator]);
            var expectedRuntime = record.BackendRuntimeDirectory ?? installation.WorkingDirectory;
            if (!Path.IsPathFullyQualified(cwd) || !SamePath(cwd, expectedRuntime) || !SamePath(expectedRuntime, installation.WorkingDirectory))
                return new(false, "The checkpoint belongs to a different backend workspace. Select the original backend or retry as a new session.");
            if (record.BackendVersion is { } backendVersion && backendVersion != installation.Version)
                return new(false, "The backend version changed since this session. Select the original version or retry as a new session.");
            if (installation.IsVersion8OrLater && (record.ConfigurationFingerprint is null || record.InputSnapshots is null))
                return new(false, "This older session has no verified input snapshot. Retry it as a new session; existing results and its checkpoint are preserved.");
            if (record.ConfigurationFingerprint is { } fingerprint && fingerprint != Fingerprint(record.Configuration))
                return new(false, "The saved attack settings changed after this session started. Retry as a new session instead of using its checkpoint.");
            if (record.InputSnapshots is { } snapshots)
            {
                if (snapshots.Any(snapshot => snapshot is null || string.IsNullOrWhiteSpace(snapshot.Path)))
                    return new(false, "The saved input snapshot is unsupported or incomplete. Retry as a new session; its data was preserved.");
                var expectedPaths = Inputs(installation, record.Configuration, builder).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!expectedPaths.SetEquals(snapshots.Select(snapshot => snapshot.Path))) return new(false, "The checkpoint input list changed. Retry as a new session.");
                foreach (var snapshot in snapshots)
                {
                    var file = new FileInfo(snapshot.Path);
                    if (!file.Exists || file.Length != snapshot.Length || file.LastWriteTimeUtc.Ticks != snapshot.LastWriteTimeUtcTicks)
                        return new(false, "A target, wordlist, rule, grammar or hints file changed or is unavailable. Restore needs the original inputs; retry as a new session after reviewing them.");
                }
            }
            return new(true, "The checkpoint can resume with the saved inputs and backend.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(false, "The checkpoint or an input could not be checked. Restore needs the original accessible files; existing data was preserved.");
        }
    }

    private static IEnumerable<string> Inputs(HashcatInstallation installation, HashcatJob job, HashcatCommandBuilder builder)
    {
        var paths = new[] { job.TargetPath }.Concat(job.Attack.Wordlists)
            .Concat(builder.ResolveRuleFiles(job.Attack, installation.Capabilities.ExpandedRules))
            .Append(builder.ResolveMaskFile(job.Attack) ?? "").Append(job.Attack.Pcfg?.HintFile ?? "");
        if (job.Attack.Kind == AttackFamilies.Pcfg) paths = paths.Append(builder.GetPcfgRulesetPath(installation, job.Attack));
        return paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string Fingerprint(HashcatJob job) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(job)));
    private static bool SamePath(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
        .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
