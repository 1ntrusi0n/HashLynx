param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not $Version) {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $Version = [string]$properties.Project.PropertyGroup.Version
}
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'A stable major.minor.patch version is required.' }
if ((Test-Path -LiteralPath (Join-Path $publishRoot '.git')) -or (Test-Path -LiteralPath (Join-Path $publishRoot 'src'))) { throw 'Refusing to package a source tree.' }
$files = [System.Collections.Generic.List[object]]::new()
$required = @('HashLynx.exe', 'HashLynx.dll', 'HashLynx.deps.json', 'HashLynx.runtimeconfig.json', 'Markdig.dll',
    'DriveReader/HashLynx.DriveReader.exe', 'DriveReader/HashLynx.DriveReader.dll', 'DriveReader/HashLynx.DriveReader.runtimeconfig.json',
    'Updater/HashLynx.Updater.exe', 'Updater/HashLynx.Updater.dll', 'Updater/HashLynx.Updater.deps.json',
    'Updater/HashLynx.Updater.runtimeconfig.json', 'Updater/HashLynx.Updates.dll', 'LICENSE', 'THIRD_PARTY_NOTICES.md')
# Exact product filenames are reserved for updates; a similarly named user file must never become updater-owned.
# Keep this inventory in sync when adding a new product assembly. The SDK also publishes helper apphosts at the root.
$inventory = @{
    '' = @('HashLynx.exe', 'HashLynx.dll', 'HashLynx.deps.json', 'HashLynx.runtimeconfig.json', 'Markdig.dll',
        'HashLynx.Core.dll', 'HashLynx.Hashcat.dll', 'HashLynx.Extractors.dll', 'HashLynx.Persistence.dll', 'HashLynx.Drives.dll', 'HashLynx.Updates.dll',
        'HashLynx.DriveReader.exe', 'HashLynx.DriveReader.dll', 'HashLynx.DriveReader.deps.json', 'HashLynx.DriveReader.runtimeconfig.json',
        'HashLynx.Updater.exe', 'HashLynx.Updater.dll', 'HashLynx.Updater.deps.json', 'HashLynx.Updater.runtimeconfig.json',
        'LICENSE', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md')
    'DriveReader' = @('HashLynx.DriveReader.exe', 'HashLynx.DriveReader.dll', 'HashLynx.DriveReader.deps.json', 'HashLynx.DriveReader.runtimeconfig.json',
        'HashLynx.Drives.dll', 'HashLynx.Extractors.dll')
    'Updater' = @('HashLynx.Updater.exe', 'HashLynx.Updater.dll', 'HashLynx.Updater.deps.json', 'HashLynx.Updater.runtimeconfig.json', 'HashLynx.Updates.dll')
}
# Adjacent user collections are never traversed.
foreach ($subdirectory in @('', 'DriveReader', 'Updater')) {
    $directory = if ($subdirectory) { Join-Path $publishRoot $subdirectory } else { $publishRoot }
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Missing published folder: $subdirectory" }
    if (((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Publish folders must not be junctions or symbolic links.' }
    foreach ($file in Get-ChildItem -LiteralPath $directory -File) {
        if ($file.Name -notin $inventory[$subdirectory]) { continue }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Published application files must not be symbolic links.' }
        $relative = if ($subdirectory) { "$subdirectory/$($file.Name)" } else { $file.Name }
        $files.Add([ordered]@{ path = $relative; length = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
}
foreach ($name in $required) { if ($name -notin $files.path) { throw "Required application file is missing: $name" } }
$manifest = [ordered]@{ schemaVersion = 1; version = $Version; files = @($files | Sort-Object path) }
$json = $manifest | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $publishRoot 'hashlynx-update.json'), $json, [Text.UTF8Encoding]::new($false))
Write-Host "Created update manifest for HashLynx $Version ($($files.Count) application files)."
