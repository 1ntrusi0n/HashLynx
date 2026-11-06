using System.Text.Json;
using HashLynx.Core;

namespace HashLynx.Persistence;

/// <summary>Versioned local JSON with serialized, atomic replacement of each file.</summary>
public sealed class PersistenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool _settingsWriteBlocked;
    public AppPaths Paths { get; }

    public PersistenceStore(AppPaths? paths = null) => Paths = paths ?? new AppPaths();

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        try
        {
        var settings = await ReadAsync("settings.json", () => new AppSettings(), ct).ConfigureAwait(false);
        if (settings.SchemaVersion > 1)
            throw new InvalidDataException("These settings were written by a newer HashLynx version. They have been preserved.");
        if (string.IsNullOrWhiteSpace(settings.DefaultOutputDirectory)) settings.DefaultOutputDirectory = Paths.ResultsDirectory;
        settings.DefaultWorkloadProfile = Math.Clamp(settings.DefaultWorkloadProfile, 1, 4);
        settings.StatusIntervalSeconds = Math.Clamp(settings.StatusIntervalSeconds, 1, 60);
        settings.ExtractorTools ??= new(StringComparer.OrdinalIgnoreCase);
        settings.DefaultDeviceIds ??= [];
        if (settings.DefaultDeviceIds.Any(id => id <= 0))
            throw new InvalidDataException("Saved recovery device IDs must be positive. Settings were preserved; inspect your data directory.");
        _settingsWriteBlocked = false;
        return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _settingsWriteBlocked = true;
            throw;
        }
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (_settingsWriteBlocked) throw new InvalidDataException("Settings could not be loaded and have been preserved. Repair the settings file and restart before saving preferences.");
        if (settings.SchemaVersion > 1) throw new InvalidDataException("Cannot overwrite settings from a newer version.");
        return WriteAsync("settings.json", settings, ct);
    }

    public async Task<UpdateCheckState> LoadUpdateCheckStateAsync(CancellationToken ct = default)
    {
        var state = await ReadAsync("update-check.json", () => new UpdateCheckState(), ct).ConfigureAwait(false);
        if (state.SchemaVersion != 1) throw new InvalidDataException("The update-check history is unsupported and has been preserved.");
        return state;
    }

    public Task SaveUpdateCheckStateAsync(UpdateCheckState state, CancellationToken ct = default)
    {
        if (state.SchemaVersion != 1) throw new InvalidDataException("Cannot overwrite unsupported update-check history.");
        return WriteAsync("update-check.json", state, ct);
    }

    public Task<List<JobRecord>> LoadJobsAsync(CancellationToken ct = default) => ReadAsync("jobs.json", () => new List<JobRecord>(), ct);
    public Task SaveJobsAsync(IReadOnlyList<JobRecord> jobs, CancellationToken ct = default) => WriteAsync("jobs.json", jobs, ct);
    public Task<List<AttackProfile>> LoadProfilesAsync(CancellationToken ct = default) => ReadAsync("profiles.json", () => new List<AttackProfile>(), ct);
    public Task SaveProfilesAsync(IReadOnlyList<AttackProfile> profiles, CancellationToken ct = default) => WriteAsync("profiles.json", profiles, ct);

    public async Task<RecoveryQueueDocument> LoadQueueAsync(CancellationToken ct = default)
    {
        var queue = await ReadAsync("queue.json", () => new RecoveryQueueDocument(), ct).ConfigureAwait(false);
        queue.Validate();
        return queue;
    }
    public Task SaveQueueAsync(RecoveryQueueDocument queue, CancellationToken ct = default)
    {
        queue.Validate();
        return WriteAsync("queue.json", queue, ct);
    }

    public async Task<WordlistLibrary> LoadWordlistLibraryAsync(CancellationToken ct = default)
    {
        var library = await ReadAsync("wordlists.json", () => new WordlistLibrary(), ct).ConfigureAwait(false);
        library.Validate();
        return library;
    }
    public Task SaveWordlistLibraryAsync(WordlistLibrary library, CancellationToken ct = default)
    {
        library.Validate();
        return WriteAsync("wordlists.json", library, ct);
    }

    private async Task<T> ReadAsync<T>(string fileName, Func<T> fallback, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(Paths.Root, fileName);
            if (!File.Exists(path)) return fallback();
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false)
                    ?? throw new JsonException("The JSON document was empty.");
            }
            catch (JsonException ex)
            {
                // Keep the original intact. Never replace malformed user data with defaults implicitly.
                throw new InvalidDataException($"Cannot read {fileName}. The original file was preserved; inspect your data directory.", ex);
            }
        }
        finally { gate.Release(); }
    }

    private async Task WriteAsync<T>(string fileName, T value, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var path = Path.Combine(Paths.Root, fileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }
}
