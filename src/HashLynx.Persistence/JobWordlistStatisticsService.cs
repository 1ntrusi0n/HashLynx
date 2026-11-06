using HashLynx.Core;

namespace HashLynx.Persistence;

/// <summary>Counts job wordlists without loading candidates or changing the source files or saved library.</summary>
public sealed class JobWordlistStatisticsService(PersistenceStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, WordlistStatistics> cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<JobWordlistInfo>> InspectAsync(HashcatJob configuration,
        IReadOnlyList<SessionInputSnapshot>? recordedInputs = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // Snapshot mutable configuration before the background operation. Keep duplicates: a list can be used on both sides of an attack.
        var paths = UsesWordlists(configuration.Attack.Kind) ? configuration.Attack.Wordlists.ToArray() : [];
        var snapshots = recordedInputs?.ToArray();
        return Task.Run(() => InspectCoreAsync(paths, snapshots, ct), ct);
    }

    private async Task<IReadOnlyList<JobWordlistInfo>> InspectCoreAsync(string[] paths,
        SessionInputSnapshot[]? recordedInputs, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (paths.Length == 0) return [];
            WordlistLibrary? library = null;
            try { library = await store.LoadWordlistLibraryAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // The library is optional metadata. Preserve unreadable/unsupported data and count the source directly.
            }

            var entries = (library?.Entries ?? []).ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
            var results = new List<JobWordlistInfo>(paths.Length);
            foreach (var configuredPath in paths)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await InspectOneAsync(configuredPath, recordedInputs, entries, ct).ConfigureAwait(false));
            }
            ct.ThrowIfCancellationRequested();
            return results;
        }
        finally { gate.Release(); }
    }

    private async Task<JobWordlistInfo> InspectOneAsync(string configuredPath, SessionInputSnapshot[]? recordedInputs,
        IReadOnlyDictionary<string, WordlistEntry> entries, CancellationToken ct)
    {
        string path = configuredPath;
        WordlistStatistics? before = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
                return Unavailable(path, null, "Choose an absolute wordlist file path to count its lines.");
            path = System.IO.Path.GetFullPath(path);
            before = Snapshot(path);
            if (recordedInputs is not null)
            {
                var recorded = recordedInputs.Where(input => input is not null && SamePath(path, input.Path)).ToArray();
                if (recorded.Length == 0)
                    return Unavailable(path, before, "This job has no recorded metadata for this wordlist. Start a new attempt to count its current lines.");
                if (recorded.Any(input => input.Length != before.FileSizeBytes || input.LastWriteTimeUtcTicks != before.LastWriteUtc.Ticks))
                    return Changed(path, before);
            }

            WordlistStatistics statistics;
            if (cache.TryGetValue(path, out var cached) && SameFile(cached, before)) statistics = cached;
            else if (entries.TryGetValue(path, out var entry) && entry.LineCount is { } savedLines
                && (before.FileSizeBytes == 0 ? savedLines == 0 : savedLines > 0 && savedLines <= before.FileSizeBytes)
                && entry.FileSizeBytes == before.FileSizeBytes && entry.LastWriteUtc?.Ticks == before.LastWriteUtc.Ticks)
                statistics = new(before.FileSizeBytes, savedLines, before.LastWriteUtc);
            else statistics = await WordlistTools.InspectAsync(path, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (!SameFile(before, statistics) || !SameFile(before, Snapshot(path))) return Changed(path, before);
            cache[path] = statistics;
            return new(path, statistics.LineCount, statistics.FileSizeBytes, statistics.LastWriteUtc);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return Unavailable(path, before, "Wordlist not found. Reconnect its drive or restore the original file to count its lines."); }
        catch (UnauthorizedAccessException)
        { return Unavailable(path, before, "Wordlist cannot be read. Check file access or choose a readable copy for a new attempt."); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        { return Unavailable(path, before, "Wordlist could not be counted. Close programs editing it and retry, or choose a readable file for a new attempt."); }
    }

    private static bool UsesWordlists(string family) => family is AttackFamilies.Dictionary or AttackFamilies.Combinator
        or AttackFamilies.HybridWordlistMask or AttackFamilies.HybridMaskWordlist or AttackFamilies.WordlistPattern;

    private static WordlistStatistics Snapshot(string path)
    {
        var file = new FileInfo(path);
        return new(file.Length, 0, file.LastWriteTimeUtc);
    }

    private static bool SameFile(WordlistStatistics left, WordlistStatistics right) =>
        left.FileSizeBytes == right.FileSizeBytes && left.LastWriteUtc.Ticks == right.LastWriteUtc.Ticks;

    private static bool SamePath(string path, string recordedPath)
    {
        try { return System.IO.Path.IsPathFullyQualified(recordedPath) && string.Equals(path, System.IO.Path.GetFullPath(recordedPath), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static JobWordlistInfo Changed(string path, WordlistStatistics before) => Unavailable(path, before,
        "Wordlist changed since this job was recorded or while counting. Restore the original file or start a new attempt for an accurate line count.");

    private static JobWordlistInfo Unavailable(string path, WordlistStatistics? metadata, string status) =>
        new(path, null, metadata?.FileSizeBytes, metadata?.LastWriteUtc, status);
}
