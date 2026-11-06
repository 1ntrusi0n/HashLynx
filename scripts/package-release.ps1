param([string]$Version, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $Version = [string]$properties.Project.PropertyGroup.Version
}
[xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
if ($Version -ne [string]$properties.Project.PropertyGroup.Version) { throw 'The release version must match Directory.Build.props.' }
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'A stable major.minor.patch version is required.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "artifacts/releases/$Version" }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$packagePath = Join-Path $outputRoot 'HashLynx-win-x64.zip'
if (Test-Path -LiteralPath $packagePath) { throw 'A release package already exists in this output folder. Choose a new output folder to preserve the earlier artifact.' }
# Always publish into a fresh staging directory, so unrelated files in a user's normal publish folder cannot enter a release.
$stageRoot = Join-Path $projectRoot ("artifacts/release-work/" + [Guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $stageRoot 'win-x64'
[IO.Directory]::CreateDirectory($publishRoot) | Out-Null
Push-Location -LiteralPath $projectRoot
try {
    dotnet publish src/HashLynx.UI -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    dotnet publish src/HashLynx.Updater -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $publishRoot 'Updater')
    if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }
    & (Join-Path $PSScriptRoot 'New-UpdateManifest.ps1') -PublishDirectory $publishRoot -Version $Version
    # Windows PowerShell 5 Compress-Archive emits backslash names. Emit portable forward-slash paths explicitly.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $manifest = Get-Content -LiteralPath (Join-Path $publishRoot 'hashlynx-update.json') -Raw | ConvertFrom-Json
    $relativeNames = @($manifest.files.path) + @('hashlynx-update.json')
    $expectedEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $publishPrefix = [IO.Path]::GetFullPath($publishRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $archive = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in $relativeNames) {
            if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('\') -or [IO.Path]::IsPathRooted($relative) -or
                @($relative.Split('/') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 }).Count -gt 0) {
                throw 'The manifest contains an unsafe ZIP path.'
            }
            $source = [IO.Path]::GetFullPath((Join-Path $publishRoot $relative))
            if (-not $source.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'A package file escapes the publish folder or is missing.' }
            for ($ancestor = $source; $ancestor -and $ancestor.StartsWith($publishPrefix.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase); $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
                if (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Release packages cannot follow symbolic links or junctions.' }
            }
            $entryName = 'win-x64/' + $relative
            if (-not $expectedEntries.Add($entryName)) { throw 'The manifest contains duplicate ZIP paths.' }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $source, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
    $verificationArchive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        if ($verificationArchive.Entries.Count -ne $expectedEntries.Count) { throw 'The ZIP inventory does not match the update manifest.' }
        foreach ($entry in $verificationArchive.Entries) {
            if (-not $expectedEntries.Contains($entry.FullName) -or $entry.FullName.Contains('\')) { throw 'The ZIP contains an unexpected or nonportable entry.' }
            $relative = $entry.FullName.Substring('win-x64/'.Length)
            if ($relative -eq 'hashlynx-update.json') { continue }
            $declared = @($manifest.files | Where-Object { $_.path -ceq $relative })
            if ($declared.Count -ne 1 -or $entry.Length -ne $declared[0].length) { throw 'A ZIP entry does not match its manifest record.' }
            $entryStream = $entry.Open()
            $entryHasher = [Security.Cryptography.SHA256]::Create()
            try { $entryHash = [BitConverter]::ToString($entryHasher.ComputeHash($entryStream)).Replace('-', '').ToLowerInvariant() }
            finally { $entryStream.Dispose(); $entryHasher.Dispose() }
            if ($entryHash -ne $declared[0].sha256) { throw 'A ZIP entry failed its manifest SHA-256 check.' }
        }
    }
    finally { $verificationArchive.Dispose() }
    $sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $outputRoot 'SHA256SUMS.txt'), "$sha256  HashLynx-win-x64.zip`n", [Text.UTF8Encoding]::new($false))
    $notes = @"
HashLynx $Version for Windows x64 (.NET 10 Desktop Runtime required).

Download HashLynx-win-x64.zip and extract the whole win-x64 folder. Keep Updater and DriveReader with the application.
Hashcat is obtained separately. Existing settings, recovery history and results remain under your local user profile.
In-app updates are optional; automatic checks are off by default and downloads require an explicit request.

See CHANGELOG.md inside the package for this release's changes.
"@
    [IO.File]::WriteAllText((Join-Path $outputRoot 'release-notes.md'), $notes, [Text.UTF8Encoding]::new($false))
    Write-Host "Release package: $packagePath"
    Write-Host "SHA-256: $sha256"
}
finally { Pop-Location }
