using System.Text;

namespace HashLynx.Persistence;

/// <summary>Mutable data lives under the current user's profile, independently of the installation.</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string JobsDirectory => Path.Combine(Root, "jobs");
    public string ResultsDirectory => Path.Combine(Root, "results");
    public string TargetsDirectory => Path.Combine(Root, "targets");
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string LogsDirectory => Path.Combine(Root, "logs");

    public AppPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HashLynx"));
        foreach (var directory in new[] { Root, JobsDirectory, ResultsDirectory, TargetsDirectory, CacheDirectory, LogsDirectory })
            Directory.CreateDirectory(directory);
    }

    public string GetJobDirectory(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id is "." or "..")
            throw new ArgumentException("The job identifier must be a valid file name.", nameof(id));
        var directory = Path.Combine(JobsDirectory, id);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string GetJobDirectory(Guid id) => GetJobDirectory(id.ToString("N"));

    public async Task<string> CreateTargetAsync(string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var path = Path.Combine(TargetsDirectory, $"{Guid.NewGuid():N}.hashes");
        await File.WriteAllTextAsync(path, content.TrimEnd() + Environment.NewLine, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return path;
    }
}
