using System.Security.Cryptography;

namespace HashLynx.Persistence;

/// <summary>Provides the small app-owned starter list without copying user wordlists into app data or source control.</summary>
public sealed class BundledWordlists(AppPaths paths)
{
    public const string StarterId = "starter-v1";
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<string> GetStarterPathAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(paths.Root, "wordlists");
        var path = Path.Combine(directory, StarterId + ".txt");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            using var resource = typeof(BundledWordlists).Assembly.GetManifestResourceStream("HashLynx.StarterWordlist")
                ?? throw new InvalidOperationException("The starter wordlist is missing from this application build.");
            using var memory = new MemoryStream();
            await resource.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            var bytes = memory.ToArray();
            if (File.Exists(path))
            {
                await using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                if (CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false), SHA256.HashData(bytes))) return path;
            }
            Directory.CreateDirectory(directory);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            return path;
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }

}
