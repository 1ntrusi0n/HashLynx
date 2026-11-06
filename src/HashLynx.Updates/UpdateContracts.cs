namespace HashLynx.Updates;

public sealed record UpdateAsset(string Name, Uri DownloadUrl, long Size, string Sha256);
public sealed record UpdateRelease(string Version, string ReleaseNotes, Uri ReleasePageUrl, UpdateAsset Package);
public sealed record UpdateCheckResult(UpdateRelease? Release, string StatusMessage);
public sealed record UpdateProgress(string Message, long BytesReceived = 0, long? TotalBytes = null);
public sealed record StagedApplicationUpdate(string StageDirectory, string HelperExecutablePath, string PlanPath, string Version);

public interface IApplicationUpdateService
{
    Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);
    Task<StagedApplicationUpdate> DownloadAsync(UpdateRelease release, string installationDirectory, string stagingRoot,
        IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class UpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = "";
    public List<UpdateManifestFile> Files { get; set; } = [];
}

public sealed record UpdateManifestFile(string Path, long Length, string Sha256);

public sealed class ApplicationUpdatePlan
{
    public int SchemaVersion { get; set; } = 1;
    public string InstallationDirectory { get; set; } = "";
    public string Version { get; set; } = "";
    public string PreviousManifestSha256 { get; set; } = "";
}

public sealed record UpdateInstallResult(bool Succeeded, bool RolledBack, string Message);
