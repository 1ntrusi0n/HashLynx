using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HashLynx.Updates;

/// <summary>Reads public release metadata. Package downloads happen only through an explicit DownloadAsync call.</summary>
public sealed class GitHubApplicationUpdateService : IApplicationUpdateService, IDisposable
{
    public const string RepositoryUrl = "https://github.com/1ntrusi0n/HashLynx";
    public const string LatestReleaseApi = "https://api.github.com/repos/1ntrusi0n/HashLynx/releases/latest";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public GitHubApplicationUpdateService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        if (!ApplicationVersion.TryParse(currentVersion, out var current))
            throw new InvalidOperationException("This application's version cannot be compared with published releases.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await GetAsync(new Uri(LatestReleaseApi), false, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(null, "No published stable release is available yet.");
            EnsureSuccess(response);
            var bytes = await ReadBoundedAsync(response.Content, 1024 * 1024, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var tag = Text(root, "tag_name");
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
                !ApplicationVersion.TryParse(tag, out var latest) || latest.IsPrerelease)
                throw new InvalidDataException("GitHub returned unsupported release information. Check the repository's releases page.");
            if (latest.CompareTo(current) <= 0) return new(null, "You have the latest published version of HashLynx.");
            var page = ReadRepositoryUrl(Text(root, "html_url"), "/releases/tag/" + tag);
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The latest release has no Windows update package.");
            var packages = assets.EnumerateArray().Where(asset => Text(asset, "name") is "HashLynx-win-x64.zip" or "HashLynx.zip").ToArray();
            if (packages.Length != 1) throw new InvalidDataException("The latest release needs exactly one supported Windows update package.");
            var package = packages[0];
            var name = Text(package, "name");
            var digest = Text(package, "digest");
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !UpdateFiles.IsSha256(digest[7..]) ||
                !package.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out var size) || size <= 0 || size > UpdateFiles.MaximumPackageBytes)
                throw new InvalidDataException("The release package has no valid SHA-256 checksum or supported download size.");
            var download = ReadRepositoryUrl(Text(package, "browser_download_url"), "/releases/download/" + tag + "/" + name);
            var notes = Text(root, "body");
            if (notes.Length > 100_000) notes = notes[..100_000];
            return new(new(tag.TrimStart('v'), notes, page, new(name, download, size, digest[7..].ToLowerInvariant())),
                $"HashLynx {tag.TrimStart('v')} is available. Download and restart when your recovery work is idle.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The update check timed out. Try again when your connection is available.", ex); }
        catch (JsonException ex) { throw new InvalidDataException("GitHub returned unreadable release information.", ex); }
    }

    public async Task<StagedApplicationUpdate> DownloadAsync(UpdateRelease release, string installationDirectory, string stagingRoot,
        IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ApplicationUpdateInstaller.ValidateInstallation(installationDirectory);
        var oldManifest = UpdateFiles.ReadManifest(installationDirectory);
        if (!ApplicationVersion.TryParse(release.Version, out var requested) || requested.IsPrerelease ||
            !ApplicationVersion.TryParse(oldManifest.Version, out var installed) || requested.CompareTo(installed) <= 0)
            throw new InvalidOperationException("The selected update must be newer than the installed application.");
        ValidateAsset(release);
        UpdateFiles.EnsureNoReparsePoints(stagingRoot);
        var root = Path.GetFullPath(stagingRoot);
        var installRoot = Path.GetFullPath(installationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (IsWithin(root, installRoot) || IsWithin(installRoot, root))
            throw new InvalidOperationException("Update downloads must be staged outside the application directory.");
        Directory.CreateDirectory(root);
        var stage = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var archivePath = Path.Combine(stage, "package.zip");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        try
        {
            progress?.Report(new("Downloading HashLynx…", 0, release.Package.Size));
            using (var response = await GetAsync(release.Package.DownloadUrl, true, ct).ConfigureAwait(false))
            {
                EnsureSuccess(response);
                if (response.Content.Headers.ContentLength is { } length && length != release.Package.Size)
                    throw new InvalidDataException("The update download size does not match GitHub's release metadata.");
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                var buffer = new byte[65536]; long received = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > release.Package.Size) throw new InvalidDataException("The update download exceeded its advertised size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    progress?.Report(new("Downloading HashLynx…", received, release.Package.Size));
                }
                if (received != release.Package.Size) throw new InvalidDataException("The update download was incomplete. Try again.");
            }
            progress?.Report(new("Checking the downloaded package…"));
            if (!string.Equals(await UpdateFiles.HashAsync(archivePath, ct).ConfigureAwait(false), release.Package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update failed its SHA-256 check. The installed application was not changed.");
            var packageDirectory = Path.Combine(stage, "package");
            await ExtractAsync(archivePath, packageDirectory, ct).ConfigureAwait(false);
            var manifest = UpdateFiles.ReadManifest(packageDirectory);
            if (!ApplicationVersion.TryParse(manifest.Version, out var packaged) || packaged.CompareTo(requested) != 0)
                throw new InvalidDataException("The update manifest version does not match the selected release.");
            await UpdateFiles.VerifyFilesAsync(packageDirectory, manifest, ct).ConfigureAwait(false);
            var installedHash = await UpdateFiles.HashAsync(Path.Combine(installationDirectory, UpdateFiles.ManifestName), ct).ConfigureAwait(false);
            // Execute the installed, known helper, never the newly downloaded helper, to perform replacement.
            var runner = Path.Combine(stage, "runner");
            Directory.CreateDirectory(runner);
            var helperFiles = oldManifest.Files.Where(file => file.Path.StartsWith("Updater/", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var file in helperFiles)
            {
                ct.ThrowIfCancellationRequested();
                var source = UpdateFiles.Child(installationDirectory, file.Path);
                if (!File.Exists(source) || new FileInfo(source).Length != file.Length ||
                    !string.Equals(await UpdateFiles.HashAsync(source, ct).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The installed updater is missing or changed. Download a fresh packaged application release.");
                var destination = UpdateFiles.Child(runner, file.Path[8..]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, false);
            }
            var planPath = Path.Combine(stage, "plan.json");
            var plan = new ApplicationUpdatePlan { InstallationDirectory = installRoot, Version = release.Version, PreviousManifestSha256 = installedHash };
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, UpdateFiles.JsonOptions), ct).ConfigureAwait(false);
            progress?.Report(new("Update verified. Ready to close and restart HashLynx."));
            return new(stage, Path.Combine(runner, "HashLynx.Updater.exe"), planPath, release.Version);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            TryRemoveStage(stage);
            throw new TimeoutException("The update download timed out. The installed application was not changed; try again when your connection is available.", ex);
        }
        catch
        {
            // Failed stages have no user data. Remove only files created here, without following any links.
            TryRemoveStage(stage);
            throw;
        }
    }

    private static bool IsWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void ValidateAsset(UpdateRelease release)
    {
        var asset = release.Package;
        if (asset is null || asset.Name is not ("HashLynx-win-x64.zip" or "HashLynx.zip") || asset.Size <= 0 ||
            asset.Size > UpdateFiles.MaximumPackageBytes || !UpdateFiles.IsSha256(asset.Sha256))
            throw new InvalidDataException("The selected release package is invalid.");
        var expected1 = "/releases/download/v" + release.Version + "/" + asset.Name;
        var expected2 = "/releases/download/" + release.Version + "/" + asset.Name;
        if (asset.DownloadUrl.AbsolutePath != "/1ntrusi0n/HashLynx" + expected1 && asset.DownloadUrl.AbsolutePath != "/1ntrusi0n/HashLynx" + expected2)
            throw new InvalidDataException("The update URL does not belong to the selected release.");
        ReadRepositoryUrl(asset.DownloadUrl.AbsoluteUri, asset.DownloadUrl.AbsolutePath["/1ntrusi0n/HashLynx".Length..]);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, bool download, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 4; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) ||
                (download ? !AllowedDownloadHost(uri.Host) : uri.AbsoluteUri != LatestReleaseApi))
                throw new InvalidDataException("The update server returned an unsupported download location.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("HashLynx-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(download ? "application/octet-stream" : "application/vnd.github+json"));
            if (!download) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects == 4) throw new InvalidDataException("The update server returned too many or invalid redirects.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidDataException("The update server returned too many redirects.");
    }

    private static bool AllowedDownloadHost(string host) => host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    private static Uri ReadRepositoryUrl(string value, string suffix)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/1ntrusi0n/HashLynx" + suffix)
            throw new InvalidDataException("The release URL is not from the configured HashLynx repository.");
        return uri;
    }

    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub temporarily limited update requests. Please try again later.", null, response.StatusCode);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub could not provide the update (HTTP {(int)response.StatusCode}). Try again later.", null, response.StatusCode);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("The release response is too large.");
        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > limit) throw new InvalidDataException("The release response is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task ExtractAsync(string archivePath, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > UpdateFiles.MaximumEntries) throw new InvalidDataException("The update archive has an unsupported number of files.");
        var fileEntries = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToArray();
        var hasFolder = fileEntries.All(entry => entry.FullName.StartsWith("win-x64/", StringComparison.Ordinal));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var unixKind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixKind is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Links and special files are not supported in update packages.");
            var name = entry.FullName;
            if (hasFolder)
            {
                if (name == "win-x64/") continue;
                if (!name.StartsWith("win-x64/", StringComparison.Ordinal)) throw new InvalidDataException("The update archive has an unexpected layout.");
                name = name[8..];
            }
            var isDirectory = name.EndsWith('/');
            if (isDirectory) name = name[..^1];
            var path = UpdateFiles.Child(destination, name);
            if (!seen.Add(name)) throw new InvalidDataException("The update archive contains duplicate paths.");
            if (isDirectory) { Directory.CreateDirectory(path); continue; }
            total = checked(total + entry.Length);
            if (entry.Length > UpdateFiles.MaximumFileBytes || total > UpdateFiles.MaximumExpandedBytes)
                throw new InvalidDataException("The expanded update is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            var buffer = new byte[65536]; long copied = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (count == 0) break;
                copied += count;
                if (copied > entry.Length || copied > UpdateFiles.MaximumFileBytes) throw new InvalidDataException("An update file exceeded its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            if (copied != entry.Length) throw new InvalidDataException("An update archive file is incomplete.");
        }
        var manifest = UpdateFiles.ReadManifest(destination);
        var declared = manifest.Files.Select(file => file.Path).Append(UpdateFiles.ManifestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (fileEntries.Length != declared.Count || Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .Any(path => !declared.Contains(Path.GetRelativePath(destination, path).Replace('\\', '/'))))
            throw new InvalidDataException("The update archive contains files not declared in its manifest.");
    }

    private static void TryRemoveStage(string stage)
    {
        try
        {
            UpdateFiles.EnsureNoReparsePoints(stage);
            var directories = Directory.GetDirectories(stage, "*", SearchOption.AllDirectories);
            foreach (var directory in directories) UpdateFiles.EnsureNoReparsePoints(directory);
            foreach (var file in Directory.GetFiles(stage, "*", SearchOption.AllDirectories)) { UpdateFiles.EnsureNoReparsePoints(file); File.Delete(file); }
            foreach (var directory in directories.OrderByDescending(path => path.Length)) Directory.Delete(directory);
            Directory.Delete(stage);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
