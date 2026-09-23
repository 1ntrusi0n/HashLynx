using System.Security.Cryptography;
using System.Text;

namespace HashLynx.Hashcat;

/// <summary>Hashcat creates caches even in help mode. Stage support assets into a user-owned runtime directory to preserve the configured release.</summary>
public sealed class HashcatRuntimeWorkspace(string cacheDirectory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly string[] SupportDirectories = ["bridges", "charsets", "layouts", "masks", "modules", "OpenCL", "Python", "Rust", "rules", "tunings", "extra"];

    public async Task<string> PrepareAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        var executable = new FileInfo(executablePath);
        var identity = $"{executable.FullName}|{executable.Length}|{executable.LastWriteTimeUtc.Ticks}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        var runtime = Path.Combine(cacheDirectory, "backend", key);
        var marker = Path.Combine(runtime, ".hashlynx-ready");
        if (File.Exists(marker)) return runtime;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(marker)) return runtime;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(runtime);
                var source = executable.DirectoryName!;
                foreach (var name in SupportDirectories)
                {
                    var input = Path.Combine(source, name);
                    if (Directory.Exists(input)) CopyDirectory(input, Path.Combine(runtime, name), cancellationToken);
                }
                // Companion DLLs may be loaded by bridge/runtime implementations from the working directory.
                foreach (var file in Directory.EnumerateFiles(source, "*.dll").Append(Path.Combine(source, "hashcat.hcstat2")))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(file)) File.Copy(file, Path.Combine(runtime, Path.GetFileName(file)), true);
                }
                File.WriteAllText(marker, "HashLynx runtime asset cache. Delete this cache directory to refresh supporting files.");
            }, cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        finally { _gate.Release(); }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);
        }
    }
}
