using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HashLynx.Updates.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HashLynx-update-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] RequiredFiles = ["HashLynx.exe", "HashLynx.dll", "HashLynx.deps.json", "HashLynx.runtimeconfig.json",
        "DriveReader/HashLynx.DriveReader.exe", "DriveReader/HashLynx.DriveReader.dll", "DriveReader/HashLynx.DriveReader.runtimeconfig.json",
        "Updater/HashLynx.Updater.exe", "Updater/HashLynx.Updater.dll", "Updater/HashLynx.Updater.deps.json",
        "Updater/HashLynx.Updater.runtimeconfig.json", "Updater/HashLynx.Updates.dll", "LICENSE", "THIRD_PARTY_NOTICES.md"];

    [Theory]
    [InlineData("0.9.9", "0.10.0", -1)]
    [InlineData("v1.2.3+commit", "1.2.3", 0)]
    [InlineData("1.2.3-rc.2", "1.2.3-rc.10", -1)]
    [InlineData("1.2.3-rc.10", "1.2.3", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    public void SemanticVersionComparison(string left, string right, int result)
    {
        Assert.True(ApplicationVersion.TryParse(left, out var a));
        Assert.True(ApplicationVersion.TryParse(right, out var b));
        Assert.Equal(result, Math.Sign(a.CompareTo(b)));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-abc..def")]
    [InlineData("1.2.3.4")]
    public void InvalidVersionsAreRejected(string value) => Assert.False(ApplicationVersion.TryParse(value, out _));

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("0.2.0", false)]
    [InlineData("0.3.0", false)]
    public async Task MetadataChecksCompareVersionsWithoutDownloading(string current, bool available)
    {
        var archive = CreateArchive();
        var requests = new List<Uri>();
        using var client = Client(request =>
        {
            requests.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization);
            Assert.Empty(request.RequestUri!.Query);
            Assert.Contains("HashLynx", request.Headers.UserAgent.ToString());
            return JsonResponse(Metadata(archive));
        });
        using var service = new GitHubApplicationUpdateService(client);
        var result = await service.CheckAsync(current);
        Assert.Equal(available, result.Release is not null);
        Assert.Equal(new Uri(GitHubApplicationUpdateService.LatestReleaseApi), Assert.Single(requests));
        if (available) Assert.Equal("0.2.0", result.Release!.Version);
    }

    [Theory]
    [InlineData("prerelease")]
    [InlineData("draft")]
    [InlineData("missing-digest")]
    [InlineData("wrong-host")]
    [InlineData("two-assets")]
    public async Task UnsupportedMetadataDoesNotOfferExecutableDownload(string problem)
    {
        var archive = CreateArchive();
        var metadata = Metadata(archive);
        if (problem == "prerelease") metadata["prerelease"] = true;
        if (problem == "draft") metadata["draft"] = true;
        var asset = ((Dictionary<string, object>[])metadata["assets"])[0];
        if (problem == "missing-digest") asset["digest"] = "";
        if (problem == "wrong-host") asset["browser_download_url"] = "https://example.org/HashLynx.zip";
        if (problem == "two-assets") metadata["assets"] = new[] { asset, asset };
        using var client = Client(_ => JsonResponse(metadata));
        using var service = new GitHubApplicationUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync("0.1.0"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task UnavailableGithubIsAnActionableFailure(HttpStatusCode status)
    {
        using var client = Client(_ => new(status));
        using var service = new GitHubApplicationUpdateService(client);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync("0.1.0"));
        Assert.Contains("GitHub", exception.Message);
        Assert.Equal(status, exception.StatusCode);
    }

    [Fact]
    public async Task MissingReleaseHasInformationalResult()
    {
        using var client = Client(_ => new(HttpStatusCode.NotFound));
        using var service = new GitHubApplicationUpdateService(client);
        Assert.Null((await service.CheckAsync("0.1.0")).Release);
    }

    [Fact]
    public async Task MalformedMetadataIsReportedWithoutChangingInstallation()
    {
        using var client = Client(_ => new(HttpStatusCode.OK) { Content = new StringContent("{broken") });
        using var service = new GitHubApplicationUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync("0.1.0"));
    }

    [Fact]
    public async Task CancelledMetadataCheckDoesNotSwallowCancellation()
    {
        using var client = Client(_ => throw new InvalidOperationException("Should not send a cancelled request."));
        using var service = new GitHubApplicationUpdateService(client);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync("0.1.0", cancellation.Token));
    }

    [Fact]
    public async Task DownloadStagesVerifiedFilesAndCopiesExistingHelperWithoutChangingInstall()
    {
        var installation = Install();
        var original = Snapshot(installation);
        var archive = CreateArchive();
        using var client = Client(_ => ZipResponse(archive));
        using var service = new GitHubApplicationUpdateService(client);
        var stage = await service.DownloadAsync(Release(archive), installation, Path.Combine(_root, "updates"));
        Assert.Equal("0.2.0", stage.Version);
        Assert.True(File.Exists(stage.PlanPath));
        Assert.Equal("0.1.0:Updater/HashLynx.Updater.exe", File.ReadAllText(stage.HelperExecutablePath));
        Assert.Equal("0.2.0:HashLynx.exe", File.ReadAllText(Path.Combine(stage.StageDirectory, "package", "HashLynx.exe")));
        AssertSnapshotsEqual(original, Snapshot(installation));
    }

    [Fact]
    public async Task SuccessfulUpdatePreservesUserCollectionsAndRemovesOnlyObsoleteOwnedFiles()
    {
        var installation = Install(new() { ["HashLynx.Obsolete.dll"] = "obsolete" });
        var userFiles = new[] { "hashcat/hashcat.exe", "hashcat/rules/user.rule", "rules/my.rule", "wordlist/my.txt", "notes.txt", "settings.json", "results/private.txt" };
        foreach (var file in userFiles) Write(installation, file, "user-" + file);
        var archive = CreateArchive(extra: new() { ["HashLynx.New.dll"] = "new" });
        var stage = await Stage(installation, archive);
        var result = await ApplicationUpdateInstaller.ApplyAsync(stage.PlanPath);
        Assert.True(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("0.2.0:HashLynx.exe", File.ReadAllText(Path.Combine(installation, "HashLynx.exe")));
        Assert.False(File.Exists(Path.Combine(installation, "HashLynx.Obsolete.dll")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(installation, "HashLynx.New.dll")));
        foreach (var file in userFiles) Assert.Equal("user-" + file, File.ReadAllText(Path.Combine(installation, file)));
        Assert.False(File.Exists(Path.Combine(installation, ".hashlynx-update.lock")));
        Assert.True(File.Exists(Path.Combine(stage.StageDirectory, "backup", "HashLynx.exe")));
    }

    [Fact]
    public async Task ExistingUnownedFileBlocksUpdateBeforeAnyApplicationChanges()
    {
        var installation = Install();
        Write(installation, "HashLynx.New.dll", "user file");
        var before = Snapshot(installation);
        var stage = await Stage(installation, CreateArchive(extra: new() { ["HashLynx.New.dll"] = "replacement" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplicationUpdateInstaller.ApplyAsync(stage.PlanPath));
        AssertSnapshotsEqual(before, Snapshot(installation));
    }

    [Fact]
    public async Task LockedOwnedFileRollsBackEarlierReplacements()
    {
        var installation = Install();
        var before = Snapshot(installation);
        var stage = await Stage(installation, CreateArchive());
        using var locked = new FileStream(Path.Combine(installation, "HashLynx.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await ApplicationUpdateInstaller.ApplyAsync(stage.PlanPath);
        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        AssertSnapshotsEqual(before, Snapshot(installation));
    }

    [Fact]
    public async Task ExistingUpdateLockBlocksWithoutOverwritingIt()
    {
        var installation = Install();
        var stage = await Stage(installation, CreateArchive());
        var path = Path.Combine(installation, ".hashlynx-update.lock");
        File.WriteAllText(path, "unknown or concurrent lock");
        var before = Snapshot(installation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplicationUpdateInstaller.ApplyAsync(stage.PlanPath));
        AssertSnapshotsEqual(before, Snapshot(installation));
    }

    [Fact]
    public async Task ConcurrentInstallationLockPreventsSecondUpdater()
    {
        var installation = Install();
        var stage = await Stage(installation, CreateArchive());
        var original = File.ReadAllText(Path.Combine(installation, "HashLynx.exe"));
        using var firstUpdate = new FileStream(Path.Combine(installation, ".hashlynx-update.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplicationUpdateInstaller.ApplyAsync(stage.PlanPath));
        Assert.Equal(original, File.ReadAllText(Path.Combine(installation, "HashLynx.exe")));
    }

    [Fact]
    public async Task InterruptedTransactionRestoresBackupsBeforeAllowingAnotherUpdate()
    {
        var installation = Install();
        var before = Snapshot(installation);
        var stage = await Stage(installation, CreateArchive(extra: new() { ["HashLynx.New.dll"] = "new" }));
        var originalFiles = Directory.GetFiles(installation, "*", SearchOption.AllDirectories);
        var journalFiles = new List<object>();
        foreach (var file in originalFiles)
        {
            var relative = Path.GetRelativePath(installation, file).Replace('\\', '/');
            var backup = Path.Combine(stage.StageDirectory, "backup", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(file, backup);
            journalFiles.Add(new { path = relative, existed = true });
        }
        journalFiles.Add(new { path = "HashLynx.New.dll", existed = false });
        File.WriteAllText(Path.Combine(installation, "HashLynx.exe"), "partially replaced");
        File.WriteAllText(Path.Combine(installation, "HashLynx.New.dll"), "new");
        File.WriteAllText(Path.Combine(stage.StageDirectory, "transaction.json"), JsonSerializer.Serialize(new
        { installationDirectory = installation, state = "applying", files = journalFiles }, JsonOptions));
        var result = await ApplicationUpdateInstaller.ApplyAsync(stage.PlanPath);
        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        AssertSnapshotsEqual(before, Snapshot(installation));
    }

    [Fact]
    public async Task DuplicateManifestEntriesAreRejected()
    {
        var installation = Install();
        var path = Path.Combine(installation, ApplicationUpdateInstaller.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(path), JsonOptions)!;
        manifest.Files.Add(manifest.Files[0] with { Path = manifest.Files[0].Path.ToUpperInvariant() });
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
        var archive = CreateArchive();
        using var client = Client(_ => ZipResponse(archive));
        using var service = new GitHubApplicationUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(Release(archive), installation, Path.Combine(_root, "updates")));
    }

    [Fact]
    public async Task CancelledInstallLeavesOriginalApplicationIntact()
    {
        var installation = Install();
        var stage = await Stage(installation, CreateArchive());
        var before = Snapshot(installation);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplicationUpdateInstaller.ApplyAsync(stage.PlanPath, cancellation.Token));
        AssertSnapshotsEqual(before, Snapshot(installation));
    }

    [Fact]
    public async Task ChangedStagedFileFailsFinalVerificationBeforeReplacement()
    {
        var installation = Install();
        var before = Snapshot(installation);
        var stage = await Stage(installation, CreateArchive());
        File.WriteAllText(Path.Combine(stage.StageDirectory, "package", "HashLynx.exe"), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => ApplicationUpdateInstaller.ApplyAsync(stage.PlanPath));
        AssertSnapshotsEqual(before, Snapshot(installation));
    }

    [Fact]
    public async Task ChangedStagedHelperIsRejectedWithoutLaunching()
    {
        var installation = Install();
        var stage = await Stage(installation, CreateArchive());
        File.WriteAllText(stage.HelperExecutablePath, "tampered");
        Assert.Throws<InvalidDataException>(() => ApplicationUpdateLauncher.Start(stage, Environment.ProcessId));
    }

    [Fact]
    public async Task UnexpectedHelperDllIsRejectedWithoutLaunching()
    {
        var installation = Install();
        var stage = await Stage(installation, CreateArchive());
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(stage.HelperExecutablePath)!, "unexpected.dll"), "unexpected");
        Assert.Throws<InvalidDataException>(() => ApplicationUpdateLauncher.Start(stage, Environment.ProcessId));
    }

    [Theory]
    [InlineData("hashcat/hashcat.exe")]
    [InlineData("rules/user.rule")]
    [InlineData("wordlist/user.txt")]
    [InlineData("settings.json")]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("NUL.txt")]
    [InlineData("trailing. /file.txt")]
    public async Task UnsafeArchivePathsNeverTouchInstallationOrOutsideFiles(string entry)
    {
        var installation = Install();
        var before = Snapshot(installation);
        var archive = CreateArchive(archiveEntry: entry);
        using var client = Client(_ => ZipResponse(archive));
        using var service = new GitHubApplicationUpdateService(client);
        await Assert.ThrowsAnyAsync<Exception>(() => service.DownloadAsync(Release(archive), installation, Path.Combine(_root, "updates")));
        AssertSnapshotsEqual(before, Snapshot(installation));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public async Task ArchiveSymlinkIsRejected()
    {
        var installation = Install();
        var archive = CreateArchive(archiveEntry: "link", symlink: true);
        using var client = Client(_ => ZipResponse(archive));
        using var service = new GitHubApplicationUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(Release(archive), installation, Path.Combine(_root, "updates")));
    }

    [Fact]
    public async Task UndeclaredArchiveFileIsRejected()
    {
        var installation = Install();
        var archive = CreateArchive(archiveEntry: "unexpected.dll");
        using var client = Client(_ => ZipResponse(archive));
        using var service = new GitHubApplicationUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(Release(archive), installation, Path.Combine(_root, "updates")));
    }

    [Fact]
    public async Task WrongPackageChecksumNeverChangesInstallation()
    {
        var installation = Install();
        var before = Snapshot(installation);
        var archive = CreateArchive();
        var release = Release(archive);
        release = release with { Package = release.Package with { Sha256 = new string('a', 64) } };
        using var client = Client(_ => ZipResponse(archive));
        using var service = new GitHubApplicationUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, installation, Path.Combine(_root, "updates")));
        AssertSnapshotsEqual(before, Snapshot(installation));
    }

    [Fact]
    public async Task InterruptedDownloadIsRejected()
    {
        var installation = Install();
        var archive = CreateArchive();
        using var client = Client(_ => ZipResponse(archive[..^10]));
        using var service = new GitHubApplicationUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(Release(archive), installation, Path.Combine(_root, "updates")));
    }

    [Fact]
    public async Task RedirectOutsideGithubAssetHostsIsRejected()
    {
        var installation = Install();
        var archive = CreateArchive();
        var requests = 0;
        using var client = Client(_ =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.org/update.zip");
            return response;
        });
        using var service = new GitHubApplicationUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(Release(archive), installation, Path.Combine(_root, "updates")));
        Assert.Equal(1, requests);
    }

    [Fact]
    public void DevelopmentFolderIsRejectedWithoutCreatingManifest()
    {
        var installation = Install();
        Directory.CreateDirectory(Path.Combine(installation, ".git"));
        Assert.Throws<InvalidOperationException>(() => ApplicationUpdateInstaller.ValidateInstallation(installation));
    }

    [Fact]
    public void UnpackagedFolderIsRejected()
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<InvalidDataException>(() => ApplicationUpdateInstaller.ValidateInstallation(_root));
    }

    [Fact]
    public async Task ManifestVersionMustMatchRelease()
    {
        var installation = Install();
        var archive = CreateArchive(version: "0.3.0");
        using var client = Client(_ => ZipResponse(archive));
        using var service = new GitHubApplicationUpdateService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(Release(archive), installation, Path.Combine(_root, "updates")));
    }

    [Fact]
    public async Task InstallationChangedAfterStagingRequiresNewDownload()
    {
        var installation = Install();
        var stage = await Stage(installation, CreateArchive());
        var manifestPath = Path.Combine(installation, ApplicationUpdateInstaller.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestPath), JsonOptions)!;
        manifest.Version = "0.1.1";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        var before = Snapshot(installation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplicationUpdateInstaller.ApplyAsync(stage.PlanPath));
        AssertSnapshotsEqual(before, Snapshot(installation));
    }

    private async Task<StagedApplicationUpdate> Stage(string installation, byte[] archive)
    {
        using var client = Client(_ => ZipResponse(archive));
        using var service = new GitHubApplicationUpdateService(client);
        return await service.DownloadAsync(Release(archive), installation, Path.Combine(_root, "updates"));
    }

    private string Install(Dictionary<string, string>? extra = null)
    {
        var installation = Path.Combine(_root, "application");
        var files = FileContents("0.1.0", extra);
        foreach (var (path, content) in files) Write(installation, path, content);
        File.WriteAllText(Path.Combine(installation, ApplicationUpdateInstaller.ManifestFileName), JsonSerializer.Serialize(Manifest("0.1.0", files), JsonOptions));
        return installation;
    }

    private static Dictionary<string, string> FileContents(string version, Dictionary<string, string>? extra)
    {
        var files = RequiredFiles.ToDictionary(path => path, path => version + ":" + path);
        if (extra is not null) foreach (var (path, content) in extra) files.Add(path, content);
        return files;
    }

    private static byte[] CreateArchive(string version = "0.2.0", Dictionary<string, string>? extra = null, string? archiveEntry = null, bool symlink = false)
    {
        var files = FileContents(version, extra);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (path, content) in files) WriteEntry(archive, "win-x64/" + path, content);
            WriteEntry(archive, "win-x64/" + ApplicationUpdateInstaller.ManifestFileName, JsonSerializer.Serialize(Manifest(version, files), JsonOptions));
            if (archiveEntry is not null)
            {
                var entry = WriteEntry(archive, "win-x64/" + archiveEntry, "unexpected");
                if (symlink) entry.ExternalAttributes = 0xA1FF << 16;
            }
        }
        return output.ToArray();
    }

    private static ZipArchiveEntry WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
        return entry;
    }

    private static UpdateManifest Manifest(string version, Dictionary<string, string> files) => new()
    {
        Version = version,
        Files = files.Select(pair => new UpdateManifestFile(pair.Key, Encoding.UTF8.GetByteCount(pair.Value), Hash(Encoding.UTF8.GetBytes(pair.Value)))).ToList()
    };

    private static UpdateRelease Release(byte[] archive) => new("0.2.0", "Release notes", new("https://github.com/1ntrusi0n/HashLynx/releases/tag/v0.2.0"),
        new("HashLynx-win-x64.zip", new("https://github.com/1ntrusi0n/HashLynx/releases/download/v0.2.0/HashLynx-win-x64.zip"), archive.Length, Hash(archive)));

    private static Dictionary<string, object> Metadata(byte[] archive) => new()
    {
        ["tag_name"] = "v0.2.0", ["draft"] = false, ["prerelease"] = false, ["body"] = "A useful release.",
        ["html_url"] = "https://github.com/1ntrusi0n/HashLynx/releases/tag/v0.2.0",
        ["assets"] = new[] { new Dictionary<string, object> { ["name"] = "HashLynx-win-x64.zip", ["size"] = archive.Length,
            ["digest"] = "sha256:" + Hash(archive), ["browser_download_url"] = "https://github.com/1ntrusi0n/HashLynx/releases/download/v0.2.0/HashLynx-win-x64.zip" } }
    };

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static Dictionary<string, string> Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(path, file), file => Hash(File.ReadAllBytes(file)));
    private static void AssertSnapshotsEqual(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (path, hash) in expected) Assert.Equal(hash, actual[path]);
    }
    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static HttpResponseMessage ZipResponse(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) => new(new FakeHandler(handler));
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(handler(request)); }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
