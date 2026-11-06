using System.Security.Cryptography;
using System.Text.Json;

namespace HashLynx.Updates;

internal static class UpdateFiles
{
    internal const string ManifestName = "hashlynx-update.json";
    internal const long MaximumPackageBytes = 512L * 1024 * 1024;
    internal const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    internal const long MaximumFileBytes = 256L * 1024 * 1024;
    internal const int MaximumEntries = 4096;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "hashcat", "rules", "wordlist", "wordlists", "settings.json", "profiles.json", "jobs.json", "queue.json",
        "targets", "results", "jobs", "queue", "logs", "cache", "hints", "experiments", ".git", "src", "tests", "artifacts"
    };

    internal static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.StartsWith('/') ||
            path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part)))
            throw new InvalidDataException("The update contains an unsafe file path.");
        if (ReservedNames.Contains(path.Split('/')[0])) throw new InvalidDataException("The update contains a protected application data or backend path.");
        return path;
    }

    private static bool IsDeviceName(string value)
    {
        var name = value.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '0' and <= '9');
    }

    internal static string Child(string root, string relative)
    {
        Relative(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update path escapes its directory.");
        EnsureNoReparsePoints(full);
        return full;
    }

    internal static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Updates cannot use symbolic links or junctions. Use a regular application folder.");
        }
    }

    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    internal static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        EnsureNoReparsePoints(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    internal static UpdateManifest ReadManifest(string directory)
    {
        var path = Child(directory, ManifestName);
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException("This folder has no supported HashLynx update manifest. Download a packaged release to use in-app updates.");
        UpdateManifest manifest;
        try { manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(path), JsonOptions) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("The application's update manifest is invalid.", ex); }
        ValidateManifest(manifest);
        return manifest;
    }

    internal static void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || !ApplicationVersion.TryParse(manifest.Version, out var version) || version.IsPrerelease ||
            manifest.Files is null || manifest.Files.Count is 0 or > MaximumEntries)
            throw new InvalidDataException("The update manifest has an unsupported version or file list.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || !seen.Add(Relative(file.Path)) || file.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
                file.Length < 0 || file.Length > MaximumFileBytes || !IsSha256(file.Sha256))
                throw new InvalidDataException("The update manifest contains invalid or duplicate files.");
            total = checked(total + file.Length);
        }
        string[] required = ["HashLynx.exe", "HashLynx.dll", "HashLynx.deps.json", "HashLynx.runtimeconfig.json",
            "DriveReader/HashLynx.DriveReader.exe", "DriveReader/HashLynx.DriveReader.dll", "DriveReader/HashLynx.DriveReader.runtimeconfig.json",
            "Updater/HashLynx.Updater.exe", "Updater/HashLynx.Updater.dll", "Updater/HashLynx.Updater.deps.json",
            "Updater/HashLynx.Updater.runtimeconfig.json", "Updater/HashLynx.Updates.dll", "LICENSE", "THIRD_PARTY_NOTICES.md"];
        if (total > MaximumExpandedBytes || required.Any(path => !seen.Contains(path)))
            throw new InvalidDataException("The update is incomplete or exceeds the supported size.");
        foreach (var path in seen)
            for (var parent = path.LastIndexOf('/'); parent > 0; parent = path.LastIndexOf('/', parent - 1))
                if (seen.Contains(path[..parent])) throw new InvalidDataException("The update has conflicting file and directory paths.");
    }

    internal static async Task VerifyFilesAsync(string directory, UpdateManifest manifest, CancellationToken ct)
    {
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Child(directory, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length ||
                !string.Equals(await HashAsync(path, ct).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An update file failed its integrity check. Download the update again.");
        }
    }
}
